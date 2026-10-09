import { spawn } from 'node:child_process';
import type { Writable } from 'node:stream';
import {
  classifyAbnormalProcessTermination,
  formatAbnormalProcessTermination
} from './companionProcessTermination';

export interface VbaToolsOutputChannel {
  append(value: string): void;
  appendLine(value: string): void;
  show(preserveFocus?: boolean): void;
}

export interface CancellationDisposable {
  dispose(): void;
}

export interface CommandCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(listener: () => void): CancellationDisposable;
}

export interface StartedVbaDevProcess {
  readonly started?: boolean | undefined;
  onStdout(listener: (value: string) => void): void;
  onStderr(listener: (value: string) => void): void;
  onSpawn?(listener: () => void): void;
  onExit(listener: (exitCode: number | null, signal: string | null) => void): void;
  onClose?(listener: (exitCode: number | null, signal: string | null) => void): void;
  onError?(listener: (error: Error) => void): void;
  requestCancellation?(): Promise<void>;
  respondToWorkbookConfirmation?(requestId: string, approved: boolean): Promise<void>;
  kill(): void;
}

export type StartVbaDevProcess = (
  executablePath: string,
  args: readonly string[]
) => StartedVbaDevProcess;

export interface VbaDevCommandRunOptions {
  executablePath: string;
  args: readonly string[];
  outputChannel: VbaToolsOutputChannel;
  displayName?: string | undefined;
  revealOutput?: boolean | undefined;
  cancellationTransport?: 'stdin-v1' | undefined;
  forceKillAfterCancellationMilliseconds?: number | undefined;
  reportCancellationProgress?: ((message: string) => void) | undefined;
  cancellationToken?: CommandCancellationToken | undefined;
  startProcess?: StartVbaDevProcess | undefined;
  processRole?: 'vba-dev' | 'vba-debug-adapter' | undefined;
  confirmWorkbookChanges?: ((message: string) => Promise<boolean>) | undefined;
}

export interface VbaDevCommandRunResult {
  exitCode: number;
  stdout: string;
  stderr: string;
  cancelled: boolean;
  cancellationRequested: boolean;
  cancellationRequestDelivered: boolean | undefined;
  cancellationRequestError: string | undefined;
  message: string;
  failureMessage?: string | undefined;
}

export function runVbaDevCommand(
  options: VbaDevCommandRunOptions
): Promise<VbaDevCommandRunResult> {
  return runCompanionCommand({
    ...options,
    processRole: 'vba-dev',
    displayName: options.displayName ?? 'VbaDev'
  });
}

export function runCompanionCommand(
  options: VbaDevCommandRunOptions
): Promise<VbaDevCommandRunResult> {
  const startProcess = options.startProcess ?? ((executablePath, args) => startNodeProcess(
    executablePath,
    args,
    options.cancellationTransport
  ));
  const displayName = options.displayName ?? 'Companion';
  if (options.cancellationToken?.isCancellationRequested) {
    options.outputChannel.appendLine(`${displayName} command cancelled.`);
    return Promise.resolve({
      exitCode: 1,
      stdout: '',
      stderr: '',
      cancelled: true,
      cancellationRequested: true,
      cancellationRequestDelivered: undefined,
      cancellationRequestError: undefined,
      message: `${displayName} command was cancelled.`
    });
  }
  const child = startProcess(options.executablePath, options.args);
  let stdout = '';
  let stderr = '';
  let cancellationRequested = options.cancellationToken?.isCancellationRequested ?? false;
  let childCancellationRequested = false;
  let cancellationRequestDelivery: Promise<boolean> | undefined;
  let cancellationRequestError: string | undefined;
  let settleCancellationRequestDelivery: ((
    delivered: boolean,
    error?: unknown
  ) => void) | undefined;
  let forceKillTimer: NodeJS.Timeout | undefined;
  let forceKillRequested = false;
  let settled = false;
  let confirmationBuffer = '';
  const confirmationRequests = new Set<string>();

  const observeWorkbookConfirmation = (value: string): void => {
    if (options.confirmWorkbookChanges === undefined || options.args[0] !== 'build' ||
        options.args.includes('--source-snapshot')) return;
    confirmationBuffer += value;
    let newline: number;
    while ((newline = confirmationBuffer.indexOf('\n')) >= 0) {
      const line = confirmationBuffer.slice(0, newline);
      confirmationBuffer = confirmationBuffer.slice(newline + 1);
      let request: unknown;
      try { request = JSON.parse(line); } catch { continue; }
      if (typeof request !== 'object' || request === null) continue;
      const frame = request as Record<string, unknown>;
      if (frame.type !== 'workbookConfirmation' || frame.schemaVersion !== '1.0' ||
          typeof frame.requestId !== 'string' || !/^[a-f0-9]{32}$/.test(frame.requestId) ||
          typeof frame.message !== 'string' || frame.message.length === 0 ||
          confirmationRequests.has(frame.requestId)) continue;
      const requestId = frame.requestId;
      const message = frame.message;
      confirmationRequests.add(requestId);
      void Promise.resolve().then(async () => {
        if (settled || cancellationRequested) return;
        const approved = await options.confirmWorkbookChanges!(message);
        if (settled || cancellationRequested) return;
        if (child.respondToWorkbookConfirmation === undefined) {
          throw new Error('The workbook confirmation transport is unavailable.');
        }
        await child.respondToWorkbookConfirmation(requestId, approved === true);
      }).catch((error: unknown) => {
        options.outputChannel.appendLine('Workbook confirmation failed: ' +
          (error instanceof Error ? error.message : String(error)));
        requestChildCancellation();
      });
    }
    if (confirmationBuffer.length > 65_536) confirmationBuffer = '';
  };

  const scheduleForceKill = (): void => {
    const delay = options.forceKillAfterCancellationMilliseconds;
    if (delay === undefined || settled || forceKillTimer !== undefined) {
      return;
    }
    forceKillTimer = setTimeout(() => {
      forceKillTimer = undefined;
      if (settled) {
        return;
      }
      try {
        forceKillRequested = true;
        child.kill();
      } catch (error) {
        forceKillRequested = false;
        const message = error instanceof Error ? error.message : String(error);
        options.outputChannel.appendLine(
          `${displayName} command force termination failed: ${message}`
        );
      }
    }, delay);
    forceKillTimer.unref();
  };

  const requestChildCancellation = (): void => {
    if (childCancellationRequested) {
      return;
    }
    childCancellationRequested = true;
    if (options.cancellationTransport === 'stdin-v1') {
      options.reportCancellationProgress?.(
        'Cancellation requested; waiting for vba-dev to finish…'
      );
      scheduleForceKill();
      let deliverySettled = false;
      let resolveDelivery: ((delivered: boolean) => void) | undefined;
      cancellationRequestDelivery = new Promise<boolean>((resolve) => {
        resolveDelivery = resolve;
      });
      const settleDelivery = (delivered: boolean, error?: unknown): void => {
        if (deliverySettled) {
          return;
        }
        deliverySettled = true;
        if (!delivered) {
          cancellationRequestError = error instanceof Error
            ? error.message
            : error === undefined
              ? 'Cancellation delivery did not settle before the command closed.'
              : String(error);
          options.outputChannel.appendLine(
            `${displayName} cancellation request could not be delivered: ` +
            cancellationRequestError
          );
          options.reportCancellationProgress?.(
            'Cancellation request could not be delivered; waiting for vba-dev to finish.'
          );
        }
        resolveDelivery?.(delivered);
      };
      settleCancellationRequestDelivery = settleDelivery;
      if (child.requestCancellation === undefined) {
        settleDelivery(false, new Error('The command cancellation transport is unavailable.'));
        return;
      }
      try {
        void child.requestCancellation().then(
          () => settleDelivery(true),
          (error) => settleDelivery(false, error)
        );
      } catch (error) {
        settleDelivery(false, error);
      }
      return;
    }
    child.kill();
  };

  if (options.revealOutput !== false) {
    options.outputChannel.show(true);
  }
  options.outputChannel.appendLine(`> ${options.executablePath} ${options.args.join(' ')}`);

  child.onStdout((value) => {
    stdout += value;
    options.outputChannel.append(value);
  });
  child.onStderr((value) => {
    stderr += value;
    options.outputChannel.append(value);
    observeWorkbookConfirmation(value);
  });

  let cancellationSubscription: CancellationDisposable | undefined;
  let processStarted = child.started ?? child.onSpawn === undefined;
  const result = new Promise<VbaDevCommandRunResult>((resolve) => {
    const complete = (exitCode: number | null, signal: string | null): void => {
      if (settled) {
        return;
      }
      settled = true;
      cancellationSubscription?.dispose();
      if (forceKillTimer !== undefined) {
        clearTimeout(forceKillTimer);
        forceKillTimer = undefined;
      }
      const resolvedExitCode = exitCode ?? 1;
      const commandWasCancelled = resolvedExitCode === 130 ||
        (options.cancellationTransport === undefined && cancellationRequested);
      const closedAfterForceKillRequest = forceKillRequested && !commandWasCancelled
        && (signal !== null || resolvedExitCode !== 0);
      const abnormalTermination = commandWasCancelled || closedAfterForceKillRequest
        ? undefined
        : classifyAbnormalProcessTermination(exitCode, signal);
      const abnormalDiagnostic = abnormalTermination === undefined
        ? undefined
        : formatAbnormalProcessTermination(
            options.processRole ?? 'vba-debug-adapter',
            options.args[0] ?? 'unknown',
            options.executablePath,
            1,
            1,
            abnormalTermination,
            'not-retried'
          );
      const abnormalMessage = abnormalTermination === undefined
        ? undefined
        : `${displayName} terminated abnormally (` +
          (abnormalTermination.kind === 'exit'
            ? `exit code ${abnormalTermination.hexExitCode}`
            : `signal ${abnormalTermination.signal}`) +
          '); the command was not retried. See VBA Tools Output.';
      const forcedTerminationMessage = closedAfterForceKillRequest
        ? `${displayName} closed after a force-termination request following cancellation; the command outcome is uncertain. See VBA Tools Output.`
        : undefined;
      if (forcedTerminationMessage !== undefined) {
        const terminalStatus = signal !== null
          ? `signal=${signal}`
          : exitCode === null ? 'status=unknown'
            : `exitCode=${exitCode}` + (exitCode < 0 || exitCode >= 0x80000000
              ? ` exitCodeHex=0x${(exitCode >>> 0).toString(16).toUpperCase().padStart(8, '0')}`
              : '');
        const diagnostic = `Companion process closed after a force-termination request: ` +
          `role=${options.processRole ?? 'vba-debug-adapter'} stage=${options.args[0] ?? 'unknown'} ` +
          `executable=${JSON.stringify(options.executablePath)} ${terminalStatus} outcome=uncertain`;
        stderr = stderr.length > 0 ? `${diagnostic}\n${stderr}` : `${diagnostic}\n`;
        try {
          options.outputChannel.appendLine(diagnostic);
        } catch {
          // Output failure must not prevent the process outcome from settling.
        }
      }
      if (abnormalDiagnostic !== undefined) {
        stderr = stderr.length > 0 ? `${abnormalDiagnostic}\n${stderr}` : `${abnormalDiagnostic}\n`;
        try {
          options.outputChannel.appendLine(abnormalDiagnostic);
        } catch {
          // Output failure must not prevent the process outcome from settling.
        }
      }
      void (async () => {
        let cancellationRequestDelivered: boolean | undefined;
        if (cancellationRequestDelivery !== undefined) {
          await new Promise<void>((finishDeliveryTurn) => setImmediate(finishDeliveryTurn));
          settleCancellationRequestDelivery?.(false);
          cancellationRequestDelivered = await cancellationRequestDelivery;
        }
        resolve({
          exitCode: resolvedExitCode,
          stdout,
          stderr,
          cancelled: commandWasCancelled,
          cancellationRequested,
          cancellationRequestDelivered,
          cancellationRequestError,
          ...(abnormalMessage !== undefined || forcedTerminationMessage !== undefined
            ? { failureMessage: abnormalMessage ?? forcedTerminationMessage } : {}),
          message: commandWasCancelled
            ? `${displayName} command was cancelled.`
            : abnormalMessage ?? forcedTerminationMessage ?? `${displayName} exited with code ${resolvedExitCode}.`
        });
      })();
    };
    child.onSpawn?.(() => {
      processStarted = true;
    });
    child.onError?.((error) => {
      const failure = processStarted
        ? `${displayName} command process error: ${error.message}`
        : `${displayName} command failed to start: ${error.message}`;
      stderr += `${failure}\n`;
      options.outputChannel.appendLine(failure);
      if (!processStarted) {
        complete(1, null);
      }
    });
    if (child.onClose !== undefined) {
      child.onClose(complete);
    } else {
      child.onExit(complete);
    }
  });

  if (settled) {
    return result;
  }
  cancellationSubscription = options.cancellationToken?.onCancellationRequested(() => {
    cancellationRequested = true;
    options.outputChannel.appendLine(options.cancellationTransport === 'stdin-v1'
      ? `${displayName} cancellation requested; waiting for the command to close.`
      : `${displayName} command cancelled.`);
    requestChildCancellation();
  });
  if (settled) {
    cancellationSubscription?.dispose();
    cancellationSubscription = undefined;
    return result;
  }
  if (cancellationRequested) {
    requestChildCancellation();
  }

  return result;
}

export function requestStdinCancellation(stdin: Writable): Promise<void> {
  if (stdin.destroyed) {
    return Promise.reject(
      new Error('The companion process standard input is unavailable.')
    );
  }
  return new Promise<void>((resolve, reject) => {
    let settled = false;
    const rejectDelivery = (error: Error): void => {
      if (settled) {
        return;
      }
      settled = true;
      reject(error);
    };
    const handleError = (error: Error): void => {
      rejectDelivery(error);
    };
    stdin.once('error', handleError);
    stdin.end('cancel\n', 'utf8', (error?: Error | null) => {
      if (error !== undefined && error !== null) {
        rejectDelivery(error);
        return;
      }
      if (settled) {
        return;
      }
      settled = true;
      stdin.off('error', handleError);
      resolve();
    });
  });
}

function startNodeProcess(
  executablePath: string,
  args: readonly string[],
  cancellationTransport?: 'stdin-v1' | undefined
): StartedVbaDevProcess {
  const child = spawn(executablePath, [...args], { windowsHide: true });

  return {
    started: child.pid !== undefined,
    onStdout: (listener) => {
      child.stdout?.on('data', (chunk: Buffer) => listener(chunk.toString('utf8')));
    },
    onStderr: (listener) => {
      child.stderr?.on('data', (chunk: Buffer) => listener(chunk.toString('utf8')));
    },
    onSpawn: (listener) => {
      child.once('spawn', listener);
    },
    onExit: (listener) => {
      child.once('exit', listener);
    },
    onClose: (listener) => {
      child.once('close', listener);
    },
    onError: (listener) => {
      child.once('error', listener);
    },
    respondToWorkbookConfirmation: (requestId, approved) => new Promise<void>((resolve, reject) => {
      if (child.stdin === null || child.stdin.destroyed) {
        reject(new Error('The companion process standard input is unavailable.'));
        return;
      }
      child.stdin.write(`confirm:${requestId}:${approved ? 'yes' : 'no'}\n`, 'utf8', error => {
        if (error) reject(error); else resolve();
      });
    }),
    ...(cancellationTransport === 'stdin-v1'
      ? {
          requestCancellation: () => child.stdin === null
            ? Promise.reject(
                new Error('The companion process standard input is unavailable.')
              )
            : requestStdinCancellation(child.stdin)
        }
      : {}),
    kill: () => {
      child.kill();
    }
  };
}
