import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  RequiredVbaDebugAdapterContract,
  VbaDebugAdapterCompatibilityError,
  loadRequiredVbaDebugAdapterContract,
  runDebugAdapterProcess,
  resolveCompatibleVbaDebugAdapter
} from './debugAdapter';

const requiredContract: RequiredVbaDebugAdapterContract = {
  contractVersion: '1.0',
  protocolVersion: '1.1',
  transports: ['stdio'],
  sessionIdFormat: 'lowercase-hex-32',
  commands: ['cleanup', 'doctor'],
  commandSchemaVersions: { doctor: '1.0' },
  featureVersions: { 'doctor.stdinCancellation': '1.0' },
  requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
};

test('the extension contract requires Doctor stdin cancellation 1.0', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');

  const contract = loadRequiredVbaDebugAdapterContract(extensionRoot);

  assert.equal(contract.featureVersions['doctor.stdinCancellation'], '1.0');
});

test('a missing configured debug adapter fails without bundled fallback', async () => {
  const extensionRoot = path.resolve('extension-root');
  const configuredPath = path.resolve('missing-vba-debug-adapter.exe');
  const bundledPath = path.join(
    extensionRoot,
    'bin',
    'vba-debug-adapter',
    'win-x64',
    'vba-debug-adapter.exe'
  );
  const calls: Array<{ file: string; args: readonly string[] }> = [];

  await assert.rejects(
    () => resolveCompatibleVbaDebugAdapter({
      extensionRoot,
      configuredPath,
      requiredContract,
      runProcess: async (file, args) => {
        calls.push({ file, args });
        if (file === configuredPath) {
          throw new Error('spawn ENOENT');
        }
        return {
          stdout: JSON.stringify({ toolVersion: '0.1.0', ...requiredContract }),
          stderr: ''
        };
      }
    }),
    (error: unknown) => {
      assert.ok(error instanceof VbaDebugAdapterCompatibilityError);
      assert.match(error.message, new RegExp(configuredPath.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
      assert.match(error.message, /unavailable or incompatible/i);
      assert.match(error.message, /ENOENT/);
      return true;
    }
  );
  assert.deepEqual(calls, [{
    file: configuredPath,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.ok(calls.every(({ file }) => file !== bundledPath));
});

test('an incompatible configured debug adapter fails without bundled fallback', async () => {
  const extensionRoot = path.resolve('extension-root');
  const configuredPath = path.resolve('configured-vba-debug-adapter.exe');
  const bundledPath = path.join(
    extensionRoot,
    'bin',
    'vba-debug-adapter',
    'win-x64',
    'vba-debug-adapter.exe'
  );
  const calls: string[] = [];

  await assert.rejects(
    () => resolveCompatibleVbaDebugAdapter({
      extensionRoot,
      configuredPath,
      requiredContract,
      runProcess: async (file) => {
        calls.push(file);
        return {
          stdout: JSON.stringify({
            toolVersion: '0.1.0',
            ...requiredContract,
            protocolVersion: file === configuredPath ? '0.9' : '1.1'
          }),
          stderr: ''
        };
      }
    }),
    /protocolVersion 0\.9/
  );
  assert.deepEqual(calls, [configuredPath]);
  assert.notEqual(configuredPath, bundledPath);
});

test('a debug adapter missing required Doctor stdin cancellation is incompatible', async () => {
  const extensionRoot = path.resolve('extension-root');
  const configuredPath = path.resolve('configured-vba-debug-adapter.exe');
  const requiredWithCancellation = {
    ...requiredContract,
    featureVersions: { 'doctor.stdinCancellation': '1.0' }
  };

  await assert.rejects(
    () => resolveCompatibleVbaDebugAdapter({
      extensionRoot,
      configuredPath,
      requiredContract: requiredWithCancellation,
      runProcess: async () => ({
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          ...requiredWithCancellation,
          featureVersions: {}
        }),
        stderr: ''
      })
    }),
    /doctor\.stdinCancellation.*1\.0/
  );
});

test('cancelling adapter capabilities kills its process and rejects promptly', async () => {
  let cancelled = false;
  let cancellationListener = (): void => undefined;
  let killCount = 0;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  const cancellationToken = {
    get isCancellationRequested(): boolean {
      return cancelled;
    },
    onCancellationRequested: (listener: () => void) => {
      cancellationListener = listener;
      return { dispose: () => undefined };
    }
  };

  const running = runDebugAdapterProcess(
    path.resolve('vba-debug-adapter.exe'),
    ['capabilities', '--format', 'json'],
    cancellationToken,
    () => {
      signalStarted?.();
      return {
        kill: () => {
          killCount += 1;
        }
      };
    }
  );
  await started;
  cancelled = true;
  cancellationListener();
  const outcome = await Promise.race([
    running.then(
      () => 'resolved' as const,
      () => 'rejected' as const
    ),
    new Promise<'timeout'>((resolve) => {
      setTimeout(() => resolve('timeout'), 25);
    })
  ]);

  assert.equal(outcome, 'rejected');
  assert.equal(killCount, 1);
});
