import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  VbaDevSessionResolver,
  VbaDevCompatibilityError,
  VbaDevResolutionNoticeAction,
  configuredVbaDevFallbackMessage,
  formatVbaDevResolutionLog,
  loadRequiredVbaDevContract,
  noCompatibleVbaDevMessage,
  resolveCompatibleVbaDev,
  resolveVbaDevPath
} from './devtool';

const requiredContract = {
  contractVersion: '1.0',
  debugAdapterProtocolVersion: '1.0',
  commandSchemaVersions: {}
};

function compatibleCapabilities(): string {
  return JSON.stringify({
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    commands: {},
    debugAdapter: {
      protocolVersion: '1.0',
      transport: 'stdio',
      command: 'debug-adapter'
    }
  });
}

test('VbaDev session resolution falls back from a missing override and pins the bundled executable', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'missing', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const notices: unknown[] = [];
  const logs: unknown[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      if (file === configuredPath) {
        throw new Error('ENOENT');
      }

      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportNotice: (notice) => notices.push(notice),
    reportLog: (log) => logs.push(log)
  });

  const first = await resolver.resolve();
  const second = await resolver.resolve();

  assert.equal(first, second);
  assert.equal(first.configuredPath, configuredPath);
  assert.equal(first.executablePath, bundledPath);
  assert.equal(first.source, 'bundled');
  assert.deepEqual(calls, [configuredPath, bundledPath]);
  assert.deepEqual(notices, [{
    severity: 'warning',
    message: configuredVbaDevFallbackMessage,
    actions: [
      VbaDevResolutionNoticeAction.OpenSettings,
      VbaDevResolutionNoticeAction.ShowOutput
    ]
  }]);
  assert.deepEqual(logs, [{
    outcome: 'resolved',
    configuredPath,
    bundledPath,
    effectivePath: bundledPath,
    source: 'bundled',
    requiredContract,
    failures: [{
      source: 'configured',
      executablePath: configuredPath,
      message: 'ENOENT'
    }]
  }]);
});

test('VbaDev session resolution validates and pins the bundled executable when no override is configured', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const notices: unknown[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportNotice: (notice) => notices.push(notice)
  });

  const first = await resolver.resolve();
  const second = await resolver.resolve();

  assert.equal(first, second);
  assert.equal(first.source, 'bundled');
  assert.equal(first.executablePath, bundledPath);
  assert.deepEqual(calls, [bundledPath]);
  assert.deepEqual(notices, []);
});

test('VbaDev session resolution pins a valid override without probing the bundled executable', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'tools', 'vba-dev.exe');
  const calls: string[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      return { stdout: compatibleCapabilities(), stderr: '' };
    }
  });

  const resolution = await resolver.resolve();

  assert.equal(resolution.source, 'configured');
  assert.equal(resolution.executablePath, configuredPath);
  assert.deepEqual(calls, [configuredPath]);
});

test('VbaDev session resolution rejects a relative override before falling back to bundled', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('tools', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      return { stdout: compatibleCapabilities(), stderr: '' };
    }
  });

  const resolution = await resolver.resolve();

  assert.equal(resolution.executablePath, bundledPath);
  assert.match(resolution.configuredFailure ?? '', /absolute path/);
  assert.deepEqual(calls, [bundledPath]);
});

test('VbaDev session resolution rejects an incompatible override before falling back to bundled', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'old', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      return {
        stdout: file === configuredPath
          ? JSON.stringify({
              toolVersion: '0.1.0',
              contractVersion: '0.9',
              commands: {}
            })
          : compatibleCapabilities(),
        stderr: ''
      };
    }
  });

  const resolution = await resolver.resolve();

  assert.equal(resolution.executablePath, bundledPath);
  assert.match(resolution.configuredFailure ?? '', /contractVersion 0\.9/);
  assert.deepEqual(calls, [configuredPath, bundledPath]);
});

test('Concurrent VbaDev session consumers share one fallback and one warning', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'missing', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const notices: unknown[] = [];
  let releaseConfigured: (() => void) | undefined;
  const configuredMayFinish = new Promise<void>((resolve) => {
    releaseConfigured = resolve;
  });
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      if (file === configuredPath) {
        await configuredMayFinish;
        throw new Error('ENOENT');
      }

      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportNotice: (notice) => notices.push(notice)
  });

  const first = resolver.resolve();
  const second = resolver.resolve();
  releaseConfigured?.();
  const [firstResolution, secondResolution] = await Promise.all([first, second]);

  assert.equal(firstResolution, secondResolution);
  assert.equal(firstResolution.executablePath, bundledPath);
  assert.deepEqual(calls, [configuredPath, bundledPath]);
  assert.equal(notices.length, 1);
  assert.deepEqual(notices[0], {
    severity: 'warning',
    message: configuredVbaDevFallbackMessage,
    actions: [
      VbaDevResolutionNoticeAction.OpenSettings,
      VbaDevResolutionNoticeAction.ShowOutput
    ]
  });
});

test('VbaDev session resolution clears a total failure so a later invocation can recover', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'tools', 'vba-dev.exe');
  const calls: string[] = [];
  const notices: unknown[] = [];
  let repaired = false;
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      if (!repaired) {
        throw new Error(`${path.basename(file)} unavailable`);
      }

      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportNotice: (notice) => notices.push(notice)
  });

  await assert.rejects(
    () => resolver.resolve(),
    (error) => {
      assert.ok(error instanceof VbaDevCompatibilityError);
      assert.match(error.message, /configured .* unavailable/);
      assert.match(error.message, /bundled .* unavailable/);
      return true;
    }
  );
  assert.equal(calls.length, 2, 'resolution must not search for a third executable');
  repaired = true;

  const recovered = await resolver.resolve();
  const pinned = await resolver.resolve();

  assert.equal(recovered, pinned);
  assert.equal(recovered.executablePath, configuredPath);
  assert.equal(calls.length, 3);
  assert.deepEqual(notices[0], {
    severity: 'error',
    message: noCompatibleVbaDevMessage,
    actions: [
      VbaDevResolutionNoticeAction.OpenSettings,
      VbaDevResolutionNoticeAction.ShowOutput
    ]
  });
  assert.equal(notices.length, 1);
});

test('VbaDev session resolution reads a corrected configured path after total failure and then pins it', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const firstConfiguredPath = path.join('D:', 'missing', 'vba-dev.exe');
  const correctedConfiguredPath = path.join('E:', 'tools', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  let configuredPath = firstConfiguredPath;
  let configuredPathReads = 0;
  const calls: string[] = [];
  const notices: unknown[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPathProvider: () => {
      configuredPathReads += 1;
      return configuredPath;
    },
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      if (file !== correctedConfiguredPath) {
        throw new Error('unavailable');
      }

      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportNotice: (notice) => notices.push(notice)
  });

  await assert.rejects(
    () => resolver.resolve(),
    (error) => error instanceof VbaDevCompatibilityError
      && error.resolutionNoticeReported
  );
  configuredPath = correctedConfiguredPath;

  const recovered = await resolver.resolve();
  const pinned = await resolver.resolve();

  assert.equal(recovered, pinned);
  assert.equal(recovered.executablePath, correctedConfiguredPath);
  assert.equal(recovered.source, 'configured');
  assert.equal(configuredPathReads, 2);
  assert.deepEqual(calls, [firstConfiguredPath, bundledPath, correctedConfiguredPath]);
  assert.equal(notices.length, 1);
});

test('VbaDev session resolution keeps a bundled fallback when reporting callbacks throw', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const configuredPath = path.join('D:', 'missing', 'vba-dev.exe');
  const bundledPath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const calls: string[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot,
    configuredPath,
    requiredContract,
    runProcess: async (file) => {
      calls.push(file);
      if (file === configuredPath) {
        throw new Error('ENOENT');
      }

      return { stdout: compatibleCapabilities(), stderr: '' };
    },
    reportLog: () => {
      throw new Error('output channel disposed');
    },
    reportNotice: () => {
      throw new Error('notification host disposed');
    }
  });

  const resolution = await resolver.resolve();

  assert.equal(resolution.executablePath, bundledPath);
  assert.equal(resolution.source, 'bundled');
  assert.deepEqual(calls, [configuredPath, bundledPath]);
});

test('VbaDev session resolution leaves a total failure unreported when the notice callback throws', async () => {
  const resolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    requiredContract,
    runProcess: async () => {
      throw new Error('unavailable');
    },
    reportLog: () => {
      throw new Error('output channel disposed');
    },
    reportNotice: () => {
      throw new Error('notification host disposed');
    }
  });

  await assert.rejects(
    () => resolver.resolve(),
    (error) => error instanceof VbaDevCompatibilityError
      && !error.resolutionNoticeReported
  );
});

test('VbaDev resolution log exposes configured and effective paths with the required contract', () => {
  const configuredPath = path.join('D:', 'old', 'vba-dev.exe');
  const bundledPath = path.join('C:', 'extension', 'bin', 'vba-dev.exe');

  assert.deepEqual(formatVbaDevResolutionLog({
    outcome: 'resolved',
    configuredPath,
    bundledPath,
    effectivePath: bundledPath,
    source: 'bundled',
    requiredContract,
    failures: [{
      source: 'configured',
      executablePath: configuredPath,
      message: 'contractVersion 0.9 is incompatible'
    }]
  }), [
    'vba-dev companion resolution: resolved',
    `  Configured candidate: ${configuredPath}`,
    `  Configured failure: contractVersion 0.9 is incompatible`,
    `  Bundled candidate: ${bundledPath}`,
    `  Effective executable: ${bundledPath}`,
    '  Required contract: {"contractVersion":"1.0","debugAdapterProtocolVersion":"1.0","commandSchemaVersions":{}}'
  ]);
});

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
    '1.1'
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
