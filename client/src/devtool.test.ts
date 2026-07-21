import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  VbaDevCompatibilityError,
  loadRequiredVbaDevContract,
  resolveCompatibleVbaDev,
  resolveVbaDevPath
} from './devtool';

test('VbaDev resolution uses the bundled Windows executable by default', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');

  assert.equal(
    resolveVbaDevPath({ extensionRoot }),
    path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe')
  );
});

test('VbaDev resolution uses an explicit configured path override', () => {
  assert.equal(
    resolveVbaDevPath({
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      configuredPath: path.join('D:', 'tools', 'vba-dev.exe')
    }),
    path.join('D:', 'tools', 'vba-dev.exe')
  );
});

test('VbaDev compatibility rejects a relative configured path before starting a process', async () => {
  let processStarted = false;

  await assert.rejects(
    () => resolveCompatibleVbaDev({
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      configuredPath: path.join('tools', 'vba-dev.exe'),
      runProcess: async () => {
        processStarted = true;
        return { stdout: '', stderr: '' };
      },
      requiredContract: {
        contractVersion: '1.0',
        debugAdapterProtocolVersion: '1.0',
        commandSchemaVersions: {}
      }
    }),
    (error) => {
      assert.ok(error instanceof VbaDevCompatibilityError);
      assert.match(error.message, /configured VbaDev path/i);
      assert.match(error.message, /absolute path/i);
      return true;
    }
  );
  assert.equal(processStarted, false);
});

test('Packaged VbaDev contract requires the debug adapter protocol', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');

  assert.equal(
    loadRequiredVbaDevContract(extensionRoot).debugAdapterProtocolVersion,
    '1.0'
  );
});

test('VbaDev compatibility invokes capabilities JSON and returns parsed versions', async () => {
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');

  const resolved = await resolveCompatibleVbaDev({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredPath: executablePath,
    runProcess: async (file, args) => {
      calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            build: { outputSchemaVersion: '1.0' },
            test: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    },
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {
        build: '1.0',
        test: '1.0'
      }
    }
  });

  assert.deepEqual(calls, [
    {
      file: executablePath,
      args: ['capabilities', '--format', 'json']
    }
  ]);
  assert.equal(resolved.executablePath, executablePath);
  assert.equal(resolved.capabilities.toolVersion, '0.1.0');
  assert.equal(resolved.capabilities.contractVersion, '1.0');
  assert.equal(resolved.capabilities.commands.build.outputSchemaVersion, '1.0');
  assert.equal(resolved.capabilities.debugAdapter?.protocolVersion, '1.0');
});

test('VbaDev compatibility never falls back to PATH discovery', async () => {
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const extensionRoot = path.resolve(__dirname, '..', '..');

  await resolveCompatibleVbaDev({
    extensionRoot,
    runProcess: async (file, args) => {
      calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            doctor: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    },
    requiredContract: {
      contractVersion: '1.0',
      debugAdapterProtocolVersion: '1.0',
      commandSchemaVersions: {
        doctor: '1.0'
      }
    }
  });

  assert.equal(
    calls[0]?.file,
    path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe')
  );
  assert.notEqual(calls[0]?.file, 'vba-dev');
});

test('VbaDev compatibility rejects an incompatible contract before command use', async () => {
  const executablePath = path.join('D:', 'tools', 'old-vba-dev.exe');

  await assert.rejects(
    () =>
      resolveCompatibleVbaDev({
        extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
        configuredPath: executablePath,
        runProcess: async () => ({
          stdout: JSON.stringify({
            toolVersion: '0.1.0',
            contractVersion: '0.9',
            commands: {
              build: { outputSchemaVersion: '1.0' }
            }
          }),
          stderr: ''
        }),
        requiredContract: {
          contractVersion: '1.0',
          debugAdapterProtocolVersion: '1.0',
          commandSchemaVersions: {
            build: '1.0'
          }
        }
      }),
    (error) => {
      assert.ok(error instanceof VbaDevCompatibilityError);
      assert.match(error.message, /old-vba-dev\.exe/);
      assert.match(error.message, /contractVersion 0\.9/);
      assert.match(error.message, /requires 1\.0/);
      return true;
    }
  );
});

test('VbaDev compatibility rejects an incompatible debug adapter protocol before adapter use', async () => {
  const executablePath = path.join('D:', 'tools', 'old-vba-dev.exe');
  const requiredContract = {
    contractVersion: '1.0',
    debugAdapterProtocolVersion: '1.0',
    commandSchemaVersions: {}
  };

  await assert.rejects(
    () => resolveCompatibleVbaDev({
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      configuredPath: executablePath,
      runProcess: async () => ({
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {},
          debugAdapter: {
            protocolVersion: '0.9',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      }),
      requiredContract
    }),
    (error) => {
      assert.ok(error instanceof VbaDevCompatibilityError);
      assert.match(error.message, /old-vba-dev\.exe/);
      assert.match(error.message, /debug adapter protocolVersion 0\.9/);
      assert.match(error.message, /requires 1\.0/);
      return true;
    }
  );
});

test('VbaDev compatibility requires a stdio debug adapter capability', async () => {
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const requiredContract = {
    contractVersion: '1.0',
    debugAdapterProtocolVersion: '1.0',
    commandSchemaVersions: {}
  };
  const cases: Array<{
    debugAdapter?: Record<string, unknown> | undefined;
    expectedMessage: RegExp;
  }> = [
    {
      expectedMessage: /does not report the required debug adapter capability/
    },
    {
      debugAdapter: {
        protocolVersion: '1.0',
        transport: 'socket',
        command: 'debug-adapter'
      },
      expectedMessage: /transport socket.*requires stdio/
    }
  ];

  for (const testCase of cases) {
    await assert.rejects(
      () => resolveCompatibleVbaDev({
        extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
        configuredPath: executablePath,
        runProcess: async () => ({
          stdout: JSON.stringify({
            toolVersion: '0.1.0',
            contractVersion: '1.0',
            commands: {},
            debugAdapter: testCase.debugAdapter
          }),
          stderr: ''
        }),
        requiredContract
      }),
      testCase.expectedMessage
    );
  }
});
