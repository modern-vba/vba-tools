import test from 'node:test';
import assert from 'node:assert/strict';

import type { CompanionExecutableResolution } from './devtool';
import {
  runResolvedVbaDevCommandInvocation,
  runVbaDevCommandInvocation
} from './devtoolRuntime';

test('resolved managed invocation starts the already selected companion executable', async () => {
  const invocations: Array<{ file: string; args: readonly string[] }> = [];
  let resolutionRequests = 0;
  const resolution = createResolution();

  const result = await runResolvedVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    vbaDevResolver: {
      resolve: async () => {
        resolutionRequests += 1;
        throw new Error('The already selected executable must not be resolved again.');
      }
    },
    outputChannel: silentOutputChannel(),
    startProcess: (file, args) => {
      invocations.push({ file, args });
      return {
        onStdout: (listener) => listener('{"complete":true}\n'),
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    }
  }, resolution, ['doctor', '--scope', 'environment', '--format', 'json']);

  assert.equal(result.executablePath, resolution.executablePath);
  assert.equal(result.exitCode, 0);
  assert.equal(resolutionRequests, 0);
  assert.deepEqual(invocations, [{
    file: resolution.executablePath,
    args: ['doctor', '--scope', 'environment', '--format', 'json']
  }]);
});

test('resolved managed invocation adds stdin-v1 exactly once', async () => {
  let processArgs: readonly string[] = [];
  const resolution = createResolution({
    'invocation.stdinCancellation': '1.0'
  });

  await runResolvedVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    outputChannel: silentOutputChannel(),
    startProcess: (_file, args) => {
      processArgs = args;
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        requestCancellation: async () => undefined,
        kill: () => undefined
      };
    }
  }, resolution, [
    'doctor',
    '--scope', 'environment',
    '--format', 'json',
    '--cancellation-transport', 'stdin-v1'
  ]);

  assert.equal(
    processArgs.filter((argument) => argument === '--cancellation-transport').length,
    1
  );
  assert.deepEqual(processArgs.slice(-2), ['--cancellation-transport', 'stdin-v1']);
});

test('resolved new excel invocation is never caller-force-killed after cancellation', async () => {
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  let cancellationRequests = 0;
  let kills = 0;
  const resolution = createResolution({
    'invocation.stdinCancellation': '1.0'
  });

  const running = runResolvedVbaDevCommandInvocation({
    extensionRoot: 'C:\\extensions\\vba-tools',
    outputChannel: silentOutputChannel(),
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
          cancellationRequests += 1;
        },
        kill: () => {
          kills += 1;
        }
      };
    }
  }, resolution, [
    'new', 'excel',
    '--name', 'BookProject',
    '--output', 'C:\\work\\BookProject',
    '--format', 'json'
  ]);

  await started;
  cancelListener?.();
  await new Promise<void>((resolve) => setTimeout(resolve, 10));

  assert.equal(cancellationRequests, 1);
  assert.equal(kills, 0);
  closeListener?.(130, null);
  const result = await running;
  assert.equal(result.exitCode, 130);
  assert.equal(result.cancelled, true);
});

test('managed background invocation preserves output without revealing it', async () => {
  let reveals = 0;
  const output: string[] = [];
  const result = await runVbaDevCommandInvocation({
    extensionRoot: String.raw`C:\extensions\vba-tools`,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: String.raw`C:\tools\vba-dev.exe`,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {},
          commands: {}
        },
        bundledPath: String.raw`C:\tools\vba-dev.exe`,
        source: 'bundled'
      })
    },
    revealOutput: false,
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => { reveals += 1; }
    },
    startProcess: () => ({
      onStdout: (listener) => listener('{"complete":true}\n'),
      onStderr: (listener) => listener(''),
      onExit: (listener) => listener(0, null),
      kill: () => undefined
    })
  }, ['host-class', 'list']);

  assert.equal(result?.exitCode, 0);
  assert.equal(reveals, 0);
  assert.match(output.join(''), /complete/);
});

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

test('host-class list is never caller-force-killed before owned Excel cleanup completes', async () => {
  const outcome = await runProtectedCancellation(['host-class', 'list']);

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
    'Cancellation requested; waiting for vba-dev to finish…',
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

function createResolution(
  featureVersions: Record<string, string> = {}
): CompanionExecutableResolution {
  return {
    executablePath: 'C:\\tools\\vba-dev.exe',
    capabilities: {
      toolVersion: '0.1.0',
      contractVersion: '1.0',
      featureVersions,
      commands: {}
    },
    bundledPath: 'C:\\tools\\vba-dev.exe',
    source: 'bundled'
  };
}

function silentOutputChannel() {
  return {
    append: () => undefined,
    appendLine: () => undefined,
    show: () => undefined
  };
}
