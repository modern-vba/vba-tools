import test from 'node:test';
import assert from 'node:assert/strict';

import { runVbaDevCommandInvocation } from './devtoolRuntime';

test('common-module add is never caller-force-killed after cooperative cancellation', async () => {
  const outcome = await runProtectedCancellation(['common-module', 'add', 'Feature']);

  assert.equal(outcome.kills, 0);
  assert.ok(outcome.result);
  assert.equal(outcome.result.cancelled, true);
});

test('common-module update is never caller-force-killed after cooperative cancellation', async () => {
  const outcome = await runProtectedCancellation(['common-module', 'update']);

  assert.equal(outcome.kills, 0);
  assert.ok(outcome.result);
  assert.equal(outcome.result.cancelled, true);
});

test('new excel is never caller-force-killed after cooperative cancellation', async () => {
  const outcome = await runProtectedCancellation(['new', 'excel', '--name', 'BookProject']);

  assert.equal(outcome.kills, 0);
  assert.ok(outcome.result);
  assert.equal(outcome.result.cancelled, true);
});

test('protected CommonModules mutation is not caller-force-killed after EPIPE', async () => {
  const outcome = await runProtectedCancellation(
    ['common-module', 'add', 'Feature'],
    new Error('write EPIPE')
  );

  assert.equal(outcome.kills, 0);
  assert.ok(outcome.result);
  assert.equal(outcome.result.cancelled, true);
  assert.equal(outcome.result.cancellationRequestDelivered, false);
  assert.equal(outcome.result.cancellationRequestError, 'write EPIPE');
});

test('managed runtime retains failed cancellation delivery without replacing success', async () => {
  const progress: string[] = [];
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  const running = runVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: 'C:\\tools\\vba-dev.exe',
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {}
        },
        bundledPath: 'C:\\tools\\vba-dev.exe',
        source: 'bundled'
      })
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    reportCancellationProgress: (message) => progress.push(message),
    forceKillAfterCancellationMilliseconds: 100,
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => {
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => {
          throw new Error('write EPIPE');
        },
        kill: () => undefined
      };
    }
  }, ['build']);

  await started;
  cancelListener?.();
  await new Promise<void>((resolve) => setImmediate(resolve));
  closeListener?.(0, null);
  const result = await running;

  assert.ok(result);
  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequested, true);
  assert.equal(result.cancellationRequestDelivered, false);
  assert.equal(result.cancellationRequestError, 'write EPIPE');
  assert.deepEqual(progress, [
    'Cancellation requested; waiting for vba-dev to finish.',
    'Cancellation request could not be delivered; waiting for vba-dev to finish.'
  ]);
});

test('managed runtime default grace outlives CLI cleanup and observation', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  let kills = 0;
  const running = runVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: 'C:\\tools\\vba-dev.exe',
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {}
        },
        bundledPath: 'C:\\tools\\vba-dev.exe',
        source: 'bundled'
      })
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => {
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => undefined,
        kill: () => {
          kills += 1;
        }
      };
    }
  }, ['build']);

  await started;
  cancelListener?.();
  t.mock.timers.tick(6_000);
  assert.equal(kills, 0);
  t.mock.timers.tick(3_999);
  assert.equal(kills, 0);
  t.mock.timers.tick(1);
  assert.equal(kills, 1);

  closeListener?.(null, 'SIGTERM');
  const result = await running;
  assert.ok(result);
  assert.equal(result.exitCode, 1);
  assert.equal(result.cancelled, false);
});

async function runProtectedCancellation(
  args: readonly string[],
  cancellationRequestError?: Error
): Promise<{
  kills: number;
  result: Awaited<ReturnType<typeof runVbaDevCommandInvocation>>;
}> {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  let kills = 0;
  const running = runVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: 'C:\\tools\\vba-dev.exe',
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {}
        },
        bundledPath: 'C:\\tools\\vba-dev.exe',
        source: 'bundled'
      })
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    forceKillAfterCancellationMilliseconds: 0,
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => {
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => {
          if (cancellationRequestError !== undefined) {
            throw cancellationRequestError;
          }
        },
        kill: () => {
          kills += 1;
        }
      };
    }
  }, args);

  await started;
  cancelListener?.();
  await new Promise<void>((resolve) => setTimeout(resolve, 10));

  closeListener?.(130, null);
  const result = await running;
  return { kills, result };
}
