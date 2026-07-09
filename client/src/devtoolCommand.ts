import { spawn } from 'node:child_process';

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
  onStdout(listener: (value: string) => void): void;
  onStderr(listener: (value: string) => void): void;
  onExit(listener: (exitCode: number | null, signal: string | null) => void): void;
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
  cancellationToken?: CommandCancellationToken | undefined;
  startProcess?: StartVbaDevProcess | undefined;
}

export interface VbaDevCommandRunResult {
  exitCode: number;
  stdout: string;
  stderr: string;
  cancelled: boolean;
  message: string;
}

export function runVbaDevCommand(
  options: VbaDevCommandRunOptions
): Promise<VbaDevCommandRunResult> {
  const startProcess = options.startProcess ?? startNodeProcess;
  const child = startProcess(options.executablePath, options.args);
  let stdout = '';
  let stderr = '';
  let cancelled = options.cancellationToken?.isCancellationRequested ?? false;

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

  const cancellationSubscription = options.cancellationToken?.onCancellationRequested(() => {
    cancelled = true;
    options.outputChannel.appendLine('VbaDev command cancelled.');
    child.kill();
  });

  if (cancelled) {
    child.kill();
  }

  return new Promise((resolve) => {
    child.onExit((exitCode, signal) => {
      cancellationSubscription?.dispose();
      const commandWasCancelled = cancelled || signal !== null;
      resolve({
        exitCode: exitCode ?? (commandWasCancelled ? 1 : 0),
        stdout,
        stderr,
        cancelled: commandWasCancelled,
        message: commandWasCancelled
          ? 'VbaDev command was cancelled.'
          : `VbaDev exited with code ${exitCode ?? 0}.`
      });
    });
  });
}

function startNodeProcess(executablePath: string, args: readonly string[]): StartedVbaDevProcess {
  const child = spawn(executablePath, [...args], { windowsHide: true });

  return {
    onStdout: (listener) => {
      child.stdout?.on('data', (chunk: Buffer) => listener(chunk.toString('utf8')));
    },
    onStderr: (listener) => {
      child.stderr?.on('data', (chunk: Buffer) => listener(chunk.toString('utf8')));
    },
    onExit: (listener) => {
      child.once('exit', listener);
    },
    kill: () => {
      child.kill();
    }
  };
}
