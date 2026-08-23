import { spawn } from 'node:child_process';
import type { Writable } from 'node:stream';

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
  cancellationTransport?: 'stdin-v1' | undefined;
  cancellationToken?: CommandCancellationToken | undefined;
  startProcess?: StartVbaDevProcess | undefined;
}

export interface VbaDevCommandRunResult {
  exitCode: number;
  stdout: string;
  stderr: string;
  cancelled: boolean;
  cancellationRequestDelivered: boolean | undefined;
  message: string;
}

export function runVbaDevCommand(
  options: VbaDevCommandRunOptions
): Promise<VbaDevCommandRunResult> {
  return runCompanionCommand({
    ...options,
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
      cancellationRequestDelivered: undefined,
      message: `${displayName} command was cancelled.`
    });
  }
  const child = startProcess(options.executablePath, options.args);
  let stdout = '';
  let stderr = '';
  let cancelled = options.cancellationToken?.isCancellationRequested ?? false;
  let childCancellationRequested = false;
  let cancellationRequestDelivery: Promise<boolean> | undefined;

  const requestChildCancellation = (): void => {
    if (childCancellationRequested) {
      return;
    }
    childCancellationRequested = true;
    if (options.cancellationTransport === 'stdin-v1') {
      let deliverySettled = false;
      let resolveDelivery: ((delivered: boolean) => void) | undefined;
      cancellationRequestDelivery = new Promise<boolean>((resolve) => {
        resolveDelivery = resolve;
      });
      const settleDelivery = (delivered: boolean): void => {
        if (deliverySettled) {
          return;
        }
        deliverySettled = true;
        if (!delivered) {
          options.outputChannel.appendLine(
            `${displayName} cancellation request could not be delivered; ` +
            'waiting for the command to close.'
          );
        }
        resolveDelivery?.(delivered);
      };
      if (child.requestCancellation === undefined) {
        settleDelivery(false);
        return;
      }
      try {
        void child.requestCancellation().then(
          () => settleDelivery(true),
          () => settleDelivery(false)
        );
      } catch {
        settleDelivery(false);
      }
      return;
    }
    child.kill();
  };

  options.outputChannel.show(true);
  options.outputChannel.appendLine(`> ${options.executablePath} ${options.args.join(' ')}`);

  child.onStdout((value) => {
    stdout += value;
    options.outputChannel.append(value);
  });
  child.onStderr((value) => {
    stderr += value;
    options.outputChannel.append(value);
  });

  let cancellationSubscription: CancellationDisposable | undefined;
  let settled = false;
  let processStarted = child.started ?? child.onSpawn === undefined;
  const result = new Promise<VbaDevCommandRunResult>((resolve) => {
    const complete = (exitCode: number | null, signal: string | null): void => {
      if (settled) {
        return;
      }
      settled = true;
      cancellationSubscription?.dispose();
      const commandWasCancelled = cancelled;
      const resolvedExitCode = exitCode ?? 1;
      void (async () => {
        const cancellationRequestDelivered = cancellationRequestDelivery === undefined
          ? undefined
          : await cancellationRequestDelivery;
        resolve({
          exitCode: resolvedExitCode,
          stdout,
          stderr,
          cancelled: commandWasCancelled,
          cancellationRequestDelivered,
          message: commandWasCancelled
            ? `${displayName} command was cancelled.`
            : `${displayName} exited with code ${resolvedExitCode}.`
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
    cancelled = true;
    options.outputChannel.appendLine(`${displayName} command cancelled.`);
    requestChildCancellation();
  });
  if (settled) {
    cancellationSubscription?.dispose();
    cancellationSubscription = undefined;
    return result;
  }
  if (cancelled) {
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
