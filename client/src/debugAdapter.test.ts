import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  RequiredVbaDebugAdapterContract,
  VbaDebugAdapterCompatibilityError,
  resolveCompatibleVbaDebugAdapter
} from './debugAdapter';

const requiredContract: RequiredVbaDebugAdapterContract = {
  contractVersion: '1.0',
  protocolVersion: '1.1',
  transports: ['stdio'],
  sessionIdFormat: 'lowercase-hex-32',
  commands: ['cleanup', 'doctor'],
  commandSchemaVersions: { doctor: '1.0' },
  requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
};

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
