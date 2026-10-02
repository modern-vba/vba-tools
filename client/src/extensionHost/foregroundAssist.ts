import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';

export interface ForegroundAssistCommand {
  readonly executable: string;
  readonly arguments: readonly string[];
}

export async function runWithForegroundAssist<T>(
  command: ForegroundAssistCommand,
  runDebugLaunch: () => Promise<T>
): Promise<T> {
  const child = spawn(command.executable, [...command.arguments], {
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe']
  });
  let standardError = '';
  child.stderr.on('data', (data: Buffer) => {
    standardError = (standardError + data.toString('utf8')).slice(-4096);
  });
  let closedBeforeStop = false;
  const closed = new Promise<{ code: number | null; signal: NodeJS.Signals | null }>(resolve => {
    child.once('close', (code, signal) => {
      closedBeforeStop = true;
      resolve({ code, signal });
    });
  });

  try {
    await waitForReady(child);
  } catch (error) {
    const exit = await terminateChild(child.kill.bind(child), closed);
    const reason = error instanceof Error ? error.message : String(error);
    throw new Error(
      `Test foreground assist ${JSON.stringify(command.executable)} did not become ready: ` +
      `${reason}; ${exit === undefined ? 'exit was not confirmed' :
        `exit code ${exit.code}, signal ${exit.signal}`}; stderr: ${standardError}`,
      { cause: error }
    );
  }

  let result: T | undefined;
  let launchFailure: unknown;
  let launchFailed = false;
  try {
    result = await runDebugLaunch();
  } catch (error) {
    launchFailed = true;
    launchFailure = error;
  }

  let assistFailure: unknown;
  let assistFailed = false;
  try {
    const exitedEarly = closedBeforeStop;
    child.stdin.end();
    const exit = await waitForExit(closed, child.kill.bind(child));
    if (exitedEarly || exit.code !== 0) {
      throw new Error(
        `Test foreground assist exited ${exitedEarly ? 'before debug launch completed' :
          `with code ${exit.code} and signal ${exit.signal}`}: ${standardError}`
      );
    }
  } catch (error) {
    assistFailed = true;
    assistFailure = error;
  }

  if (launchFailed && assistFailed) {
    throw new AggregateError([launchFailure, assistFailure],
      'Debug launch and test foreground assist both failed', { cause: launchFailure });
  }
  if (launchFailed) throw launchFailure;
  if (assistFailed) throw assistFailure;
  return result as T;
}

async function waitForReady(child: ChildProcessWithoutNullStreams): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    let output = '';
    const timeout = setTimeout(() => finish(new Error('Ready signal timed out.')), 30_000);
    const onData = (data: Buffer): void => {
      output += data.toString('utf8');
      if (output.split(/\r?\n/).includes('READY')) finish();
      if (output.length > 4096) output = output.slice(-4096);
    };
    const onError = (error: Error): void => finish(error);
    const onClose = (code: number | null, signal: NodeJS.Signals | null): void =>
      finish(new Error(`Process closed before ready with code ${code} and signal ${signal}.`));
    function finish(error?: Error): void {
      clearTimeout(timeout);
      child.stdout.off('data', onData);
      child.off('error', onError);
      child.off('close', onClose);
      if (error) reject(error);
      else resolve();
    }
    child.stdout.on('data', onData);
    child.once('error', onError);
    child.once('close', onClose);
  });
}

async function waitForExit(
  closed: Promise<{ code: number | null; signal: NodeJS.Signals | null }>,
  kill: () => boolean
): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
  let timeout: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      closed,
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(() => reject(new Error('Test foreground assist did not exit.')), 5_000);
      })
    ]);
  } catch (error) {
    await terminateChild(kill, closed);
    throw error;
  } finally {
    clearTimeout(timeout);
  }
}

async function terminateChild(
  kill: () => boolean,
  closed: Promise<{ code: number | null; signal: NodeJS.Signals | null }>
): Promise<{ code: number | null; signal: NodeJS.Signals | null } | undefined> {
  kill();
  let timeout: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      closed,
      new Promise<undefined>(resolve => {
        timeout = setTimeout(() => resolve(undefined), 2_000);
      })
    ]);
  } finally {
    clearTimeout(timeout);
  }
}
