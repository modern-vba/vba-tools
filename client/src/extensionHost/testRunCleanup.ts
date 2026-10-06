import { rm } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';

const retryableRemovalCodes = new Set(['EBUSY', 'EMFILE', 'ENFILE', 'ENOTEMPTY', 'EPERM']);

export async function runWithExtensionHostCleanup(
  directories: readonly string[],
  run: () => Promise<void>,
  onRunFailure?: () => Promise<void>
): Promise<void> {
  let runFailure: { error: unknown } | undefined;
  let evidenceFailure: { error: unknown } | undefined;
  try {
    await run();
  } catch (error) {
    runFailure = { error };
    try {
      await onRunFailure?.();
    } catch (error) {
      evidenceFailure = { error };
    }
  }
  const cleanupFailures: Error[] = [];
  for (const directory of directories) {
    try {
      await removeTemporaryDirectory(directory);
    } catch (error) {
      cleanupFailures.push(new Error(
        `Could not remove Extension Host test directory: ${directory}`, { cause: error }
      ));
    }
  }
  const secondaryFailures = [
    ...(evidenceFailure ? [evidenceFailure.error] : []),
    ...cleanupFailures
  ];
  if (secondaryFailures.length > 0) {
    if (!runFailure && cleanupFailures.length === 1) {
      throw cleanupFailures[0].cause;
    }
    throw new AggregateError(
      runFailure ? [runFailure.error, ...secondaryFailures] : secondaryFailures,
      runFailure ? 'Extension Host tests and finalization failed.' : 'Extension Host cleanup failed.',
      runFailure ? { cause: runFailure.error } : undefined
    );
  }
  if (runFailure) { throw runFailure.error; }
}

async function removeTemporaryDirectory(directory: string): Promise<void> {
  // Retry at the root, not inside recursive rm: nested native retry budgets
  // multiply. Ten linear waits add at most 5.5 seconds of backoff per root.
  for (let attempt = 0; ; attempt += 1) {
    try {
      await rm(directory, { recursive: true, force: true, maxRetries: 0 });
      return;
    } catch (error) {
      if (attempt >= 10 || !retryableRemovalCodes.has(
        (error as NodeJS.ErrnoException | null)?.code ?? ''
      )) {
        throw error;
      }
      await delay((attempt + 1) * 100);
    }
  }
}
