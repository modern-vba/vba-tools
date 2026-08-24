import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  VbaDevCapabilities,
  VbaDevCompatibilityError,
  VbaDevSessionResolver
} from './devtool';
import {
  VscodeDebugIntegration,
  createVbaDebugConfigurationProvider,
  handleVbaDebugSessionTermination,
  stopVbaDebugSessionAfterLifecycleFailure,
  useVbaDebugConfigurationObserverForTest
} from './vscodeDebugIntegration';
import {
  VbaDebugCancellationError,
  VbaDebugConfiguration
} from './vscodeDebugConfiguration';

test('VBA debug provider normalizes an empty F5 configuration before variable substitution', () => {
  let hostWasTouched = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [],
      getActiveEditor: () => {
        hostWasTouched = true;
        return undefined;
      },
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [],
      readTextFile: async () => '',
      readSourceText: async () => '',
      findExportedSourceFiles: async () => []
    }
  });
  const provider = createVbaDebugConfigurationProvider(integration, () => undefined);

  assert.deepEqual(provider.resolveDebugConfiguration({}), {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure'
  });
  assert.equal(hostWasTouched, false);
});

test('VBA debug provider checks Workspace Trust before resolving substituted configuration', async () => {
  let configurationResolutions = 0;
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async () => {
      configurationResolutions += 1;
      throw new Error('Workspace Trust must be checked first');
    }
  }, () => undefined, async () => false);

  const configuration = await provider.resolveDebugConfigurationWithSubstitutedVariables({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure'
  });

  assert.equal(configuration, undefined);
  assert.equal(configurationResolutions, 0);
});

test('VBA debug provider exposes the post-substitution result to tests and aborts before adapter startup', async () => {
  const resolvedConfiguration = {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: path.join('C:', 'resolved', 'BookProject'),
    document: 'Book1',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: []
    }
  };
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async () => resolvedConfiguration
  }, () => undefined);
  let observed: unknown;
  const observer = useVbaDebugConfigurationObserverForTest((configuration) => {
    observed = configuration;
  });

  try {
    const result = await provider.resolveDebugConfigurationWithSubstitutedVariables({
      type: 'vba',
      request: 'launch',
      name: 'VBA: Active Procedure',
      project: path.join('C:', 'substituted', 'BookProject')
    });

    assert.equal(result, undefined);
    assert.equal(observed, resolvedConfiguration);
  } finally {
    observer.dispose();
  }
});

test('VBA debug provider forwards cancellation and does not report it as a setup error', async () => {
  const messages: string[] = [];
  let cancellationListener: (() => void) | undefined;
  const cancellationToken = {
    isCancellationRequested: false,
    onCancellationRequested: (listener: () => void) => {
      cancellationListener = listener;
      return { dispose: () => undefined };
    }
  };
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async (_configuration, token) => new Promise((_, reject) => {
      token?.onCancellationRequested(() => reject(new VbaDebugCancellationError()));
    })
  }, (message) => messages.push(message));

  const resolution = provider.resolveDebugConfigurationWithSubstitutedVariables(
    { type: 'vba', request: 'launch', name: 'VBA: Active Procedure' },
    undefined,
    cancellationToken
  );
  cancellationToken.isCancellationRequested = true;
  cancellationListener?.();

  assert.equal(await resolution, undefined);
  assert.deepEqual(messages, []);
});

test('VBA debug provider binds the resolved standalone-adapter launch for restart', async () => {
  const workspaceFolder = path.join('C:', 'work');
  const resolvedConfiguration = {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: path.join(workspaceFolder, 'BookProject'),
    document: 'Book1',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: []
    }
  };
  const restartPreparationCalls: Array<{
    configuration: VbaDebugConfiguration;
    workspaceFolderPath: string | undefined;
  }> = [];
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async () => resolvedConfiguration,
    prepareDebugConfigurationForRestart: (configuration, workspaceFolderPath) => {
      restartPreparationCalls.push({ configuration, workspaceFolderPath });
      return {
        ...configuration,
        __vbaRestartPreparation: {
          protocolVersion: 1,
          id: 'fedcba9876543210fedcba9876543210',
          generation: 0
        }
      };
    }
  }, () => undefined);

  const configuration = await provider.resolveDebugConfigurationWithSubstitutedVariables(
    resolvedConfiguration,
    workspaceFolder
  );

  assert.deepEqual(restartPreparationCalls, [{
    configuration: resolvedConfiguration,
    workspaceFolderPath: workspaceFolder
  }]);
  assert.deepEqual(configuration, {
    ...resolvedConfiguration,
    __vbaRestartPreparation: {
      protocolVersion: 1,
      id: 'fedcba9876543210fedcba9876543210',
      generation: 0
    }
  });
});

test('VBA debug lifecycle notification failure reports the error and stops the session', async () => {
  const events: string[] = [];

  await stopVbaDebugSessionAfterLifecycleFailure(
    new Error('Synthetic notification failure.'),
    (message) => events.push(`report:${message}`),
    async () => { events.push('stop'); },
    async () => { events.push('disconnect'); }
  );

  assert.equal(events.length, 2);
  assert.match(events[0], /Synthetic notification failure/);
  assert.equal(events[1], 'stop');
});

test('VBA debug lifecycle stop failure forces adapter disconnect and retries VS Code stop', async () => {
  const events: string[] = [];
  let stopAttempt = 0;

  await stopVbaDebugSessionAfterLifecycleFailure(
    new Error('Synthetic notification failure.'),
    (message) => events.push(`report:${message}`),
    async () => {
      stopAttempt += 1;
      events.push(`stop:${stopAttempt}`);
      if (stopAttempt === 1) {
        throw new Error('Synthetic VS Code stop failure.');
      }
    },
    async () => { events.push('disconnect'); }
  );

  assert.equal(stopAttempt, 2);
  assert.deepEqual(
    events.filter((event) => !event.startsWith('report:')),
    ['stop:1', 'disconnect', 'stop:2']
  );
  assert.equal(events.filter((event) => event.startsWith('report:')).length, 2);
  assert.match(events[0], /Synthetic notification failure/);
  assert.match(events[2], /Synthetic VS Code stop failure/);
});

test('VBA debug lifecycle reports every failed terminal fallback without rejecting', async () => {
  const reports: string[] = [];

  await stopVbaDebugSessionAfterLifecycleFailure(
    new Error('Synthetic notification failure.'),
    (message) => reports.push(message),
    async () => { throw new Error('Synthetic stop failure.'); },
    async () => { throw new Error('Synthetic disconnect failure.'); }
  );

  assert.equal(reports.length, 4);
  assert.match(reports[1], /Synthetic stop failure/);
  assert.match(reports[2], /Synthetic disconnect failure/);
  assert.match(reports[3], /Synthetic stop failure/);
});

test('unrelated debug session termination does not cancel VBA restart preparation', () => {
  const events: string[] = [];
  const integration = {
    cancelRestartPreparation: () => { events.push('cancel'); },
    releaseSession: (sessionId: string) => { events.push(`release:${sessionId}`); }
  };

  handleVbaDebugSessionTermination(integration, {
    id: 'node-session',
    type: 'node',
    configuration: {
      type: 'node',
      request: 'launch',
      name: 'Node'
    }
  });

  assert.deepEqual(events, []);

  handleVbaDebugSessionTermination(integration, {
    id: 'vba-session',
    type: 'vba',
    configuration: {
      type: 'vba',
      request: 'launch',
      name: 'VBA'
    }
  });

  assert.deepEqual(events, ['release:vba-session']);
});

test('VBA debug provider reports invalid saved launch selectors and aborts resolution', () => {
  const messages: string[] = [];
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async (configuration) => configuration
  }, (message) => messages.push(message));

  const result = provider.resolveDebugConfiguration({
    type: 'vba',
    request: 'launch',
    name: 'Invalid pair',
    module: 'Module1'
  });

  assert.equal(result, undefined);
  assert.deepEqual(messages, [
    'VBA debug launch selectors module and procedure must be supplied together as non-empty strings.'
  ]);
});

test('VBA debug provider resolves a relative saved project selector from its workspace folder', async () => {
  const workspaceFolder = path.join('C:', 'work', 'Workspace');
  let receivedConfiguration: unknown;
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async (configuration) => {
      receivedConfiguration = configuration;
      return configuration;
    }
  }, () => undefined);

  await provider.resolveDebugConfigurationWithSubstitutedVariables({
    type: 'vba',
    request: 'launch',
    name: 'Relative project',
    project: path.join('projects', 'BookProject'),
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget'
  }, workspaceFolder);

  assert.deepEqual(receivedConfiguration, {
    type: 'vba',
    request: 'launch',
    name: 'Relative project',
    project: path.join(workspaceFolder, 'projects', 'BookProject'),
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget'
  });
});

test('VBA debug provider rejects a relative project selector without workspace-folder context', async () => {
  const messages: string[] = [];
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async (configuration) => configuration
  }, (message) => messages.push(message));

  const result = await provider.resolveDebugConfigurationWithSubstitutedVariables({
    type: 'vba',
    request: 'launch',
    name: 'Relative project',
    project: path.join('projects', 'BookProject'),
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget'
  });

  assert.equal(result, undefined);
  assert.deepEqual(messages, [
    'A relative VBA debug project selector requires a workspace folder; '
    + 'use an absolute path or ${workspaceFolder}.'
  ]);
});

test('VBA debug startup pins independent adapter and CLI paths with a canonical session ID', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const workspaceRoot = path.join('C:', 'work', 'BookProject');
  const cliPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  let cliResolutions = 0;
  let adapterResolutions = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot,
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => {
        cliResolutions += 1;
        return {
          executablePath: cliPath,
          bundledPath: cliPath,
          source: 'configured',
          capabilities: compatibleCapabilities()
        };
      }
    },
    vbaDebugAdapterResolver: {
      resolve: async () => {
        adapterResolutions += 1;
        return {
          executablePath: adapterPath,
          capabilities: compatibleDebugAdapterCapabilities()
        };
      }
    },
    createDebugSessionId: () => '0123456789abcdef0123456789abcdef'
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    workspaceRoot,
    stop: () => undefined
  });
  assert.equal(cliResolutions, 1);
  assert.equal(adapterResolutions, 1);
  assert.deepEqual(descriptor, {
    command: adapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      cliPath,
      '--session',
      '0123456789abcdef0123456789abcdef'
    ],
    options: { cwd: workspaceRoot }
  });
});

test('VBA debug adapter startup checks Workspace Trust before resolving either executable', async () => {
  let cliResolutions = 0;
  let adapterResolutions = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    requireTrustedWorkspace: async () => false,
    vbaDevResolver: {
      resolve: async () => {
        cliResolutions += 1;
        throw new Error('Workspace Trust must be checked first');
      }
    },
    vbaDebugAdapterResolver: {
      resolve: async () => {
        adapterResolutions += 1;
        throw new Error('Workspace Trust must be checked first');
      }
    }
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    stop: () => undefined
  });

  assert.equal(descriptor, undefined);
  assert.equal(cliResolutions, 0);
  assert.equal(adapterResolutions, 0);
});

test('VBA debug startup strictly resolves the configured standalone adapter', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const cliPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const capabilityCalls: Array<{ file: string; args: readonly string[] }> = [];
  const integration = new VscodeDebugIntegration({
    extensionRoot,
    getConfiguredDevToolPath: () => undefined,
    getConfiguredDebugAdapterPath: () => adapterPath,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: cliPath,
        bundledPath: cliPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    capabilitiesProcess: async (file, args) => {
      capabilityCalls.push({ file, args });
      return {
        stdout: JSON.stringify(compatibleDebugAdapterCapabilities()),
        stderr: ''
      };
    },
    requiredDebugAdapterContract: requiredDebugAdapterContract(),
    createDebugSessionId: () => 'fedcba9876543210fedcba9876543210'
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    stop: () => undefined
  });

  assert.deepEqual(capabilityCalls, [{
    file: adapterPath,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.deepEqual(descriptor, {
    command: adapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      cliPath,
      '--session',
      'fedcba9876543210fedcba9876543210'
    ],
    options: undefined
  });
});

test('VBA debug startup always resolves the bundled standalone adapter instead of falling back to vba-dev', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const cliPath = path.join('D:', 'tools', 'vba-dev.exe');
  const bundledAdapterPath = path.join(
    extensionRoot,
    'bin',
    'vba-debug-adapter',
    'win-x64',
    'vba-debug-adapter.exe'
  );
  const capabilityCalls: Array<{ file: string; args: readonly string[] }> = [];
  const integration = new VscodeDebugIntegration({
    extensionRoot,
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: cliPath,
        bundledPath: cliPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    capabilitiesProcess: async (file, args) => {
      capabilityCalls.push({ file, args });
      return {
        stdout: JSON.stringify(compatibleDebugAdapterCapabilities()),
        stderr: ''
      };
    },
    requiredDebugAdapterContract: requiredDebugAdapterContract(),
    createDebugSessionId: () => '0123456789abcdef0123456789abcdef'
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    stop: () => undefined
  });

  assert.deepEqual(capabilityCalls, [{
    file: bundledAdapterPath,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.deepEqual(descriptor, {
    command: bundledAdapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      cliPath,
      '--session',
      '0123456789abcdef0123456789abcdef'
    ],
    options: undefined
  });
});

test('VBA debug startup rejects a second session until the active session terminates', async () => {
  let capabilityCallCount = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => path.join('D:', 'tools', 'vba-dev.exe'),
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: path.join('D:', 'tools', 'vba-debug-adapter.exe'),
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    capabilitiesProcess: async () => {
      capabilityCallCount += 1;
      return {
        stdout: JSON.stringify(compatibleCapabilities()),
        stderr: ''
      };
    },
    requiredContract: requiredContract()
  });

  await integration.createDebugAdapterExecutable({ id: 'session-1', stop: () => undefined });
  await assert.rejects(
    () => integration.createDebugAdapterExecutable({
      id: 'session-2',
      stop: () => undefined
    }),
    /already running in this VS Code window/
  );
  assert.equal(capabilityCallCount, 1);

  integration.releaseSession('session-1');
  await integration.createDebugAdapterExecutable({ id: 'session-2', stop: () => undefined });
  assert.equal(capabilityCallCount, 2);
});

test('only the owning VBA debug session release cancels its pending restart preparation', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  let notifyCaptureStarted!: () => void;
  const captureStarted = new Promise<void>((resolve) => {
    notifyCaptureStarted = resolve;
  });
  const manifest = JSON.stringify({
    schemaVersion: 1,
    projectName: 'BookProject',
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        kind: 'excel',
        sourcePath: 'src/Book1',
        templatePath: 'src/Book1/Book1.xlsm',
        binPath: 'bin/Book1.xlsm',
        publishPath: 'publish/Book1.xlsm',
        commonModules: [],
        references: []
      }
    }
  });
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => path.join('D:', 'tools', 'vba-dev.exe'),
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: path.join('D:', 'tools', 'vba-debug-adapter.exe'),
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify(compatibleCapabilities()),
      stderr: ''
    }),
    requiredContract: requiredContract(),
    debugConfigurationHost: {
      workspaceRoots: [workspaceRoot],
      getActiveEditor: () => undefined,
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [manifestPath],
      readTextFile: async () => manifest,
      readSourceText: async () => 'Public Sub RunTarget()\r\nEnd Sub\r\n',
      findExportedSourceFiles: async () => [sourcePath],
      captureSourceInventory: async (sourceSetPath, cancellationToken) => {
        notifyCaptureStarted();
        return new Promise((_, reject) => {
          cancellationToken?.onCancellationRequested(() => {
            reject(new VbaDebugCancellationError());
          });
        });
      }
    }
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  });
  await integration.createDebugAdapterExecutable({
    id: 'session-1',
    configuration,
    stop: () => undefined
  });
  const preparation = integration.runRestartPreparation(configuration);

  await captureStarted;
  let preparationSettled = false;
  void preparation.then(
    () => { preparationSettled = true; },
    () => { preparationSettled = true; }
  );
  handleVbaDebugSessionTermination(integration, {
    id: 'rejected-session-2',
    type: 'vba',
    configuration
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(preparationSettled, false);

  integration.releaseSession('session-1');
  await assert.rejects(
    preparation,
    (error) => error instanceof VbaDebugCancellationError
  );
  await assert.rejects(
    () => integration.runRestartPreparation(configuration),
    /restart preparation is unavailable/
  );
});

test('VBA debug startup reuses the session-pinned CLI with the standalone adapter', async () => {
  const configuredPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const capabilityCalls: Array<{ file: string; args: readonly string[] }> = [];
  const capabilities = compatibleCapabilities();
  let adapterResolutions = 0;
  const adapterSessionIds = [
    '0123456789abcdef0123456789abcdef',
    'fedcba9876543210fedcba9876543210'
  ];
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    configuredPath,
    runProcess: async (file, args) => {
      capabilityCalls.push({ file, args });
      return { stdout: JSON.stringify(capabilities), stderr: '' };
    },
    requiredContract: requiredContract()
  });
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver,
    vbaDebugAdapterResolver: {
      resolve: async () => {
        adapterResolutions += 1;
        return {
          executablePath: adapterPath,
          capabilities: compatibleDebugAdapterCapabilities()
        };
      }
    },
    requiredContract: requiredContract(),
    createDebugSessionId: () => adapterSessionIds[adapterResolutions - 1]!
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    stop: () => undefined
  });
  integration.releaseSession('session-1');
  const secondDescriptor = await integration.createDebugAdapterExecutable({
    id: 'session-2',
    stop: () => undefined
  });

  assert.deepEqual(capabilityCalls, [{
    file: configuredPath,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.equal(adapterResolutions, 2);
  assert.deepEqual(descriptor, {
    command: adapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      configuredPath,
      '--session',
      adapterSessionIds[0]
    ],
    options: undefined
  });
  assert.deepEqual(secondDescriptor, {
    command: adapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      configuredPath,
      '--session',
      adapterSessionIds[1]
    ],
    options: undefined
  });
});

test('unexpected VBA debug adapter exit invokes cleanup with only its generated session ID', async () => {
  const vbaDevPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const cleanupCalls: Array<{ file: string; args: readonly string[] }> = [];
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: vbaDevPath,
        bundledPath: vbaDevPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: adapterPath,
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    createDebugSessionId: () => adapterSessionId,
    debugAdapterCleanupProcess: async (file, args) => {
      cleanupCalls.push({ file, args });
      return { stdout: '', stderr: '' };
    }
  });
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    workspaceRoot: path.join('C:', 'untrusted', 'workspace'),
    stop: () => undefined
  });

  await integration.handleAdapterExit('vscode-session-1');
  await integration.handleAdapterExit('vscode-session-1');
  await integration.handleAdapterExit('unknown-session');

  assert.deepEqual(cleanupCalls, [{
    file: adapterPath,
    args: ['cleanup', '--session', adapterSessionId]
  }]);
});

test('adapter cleanup identity survives an active shutdown attempt until confirmed exit', async () => {
  const vbaDevPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  let cleanupAttempts = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: vbaDevPath,
        bundledPath: vbaDevPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: adapterPath,
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    createDebugSessionId: () => adapterSessionId,
    debugAdapterCleanupProcess: async () => {
      cleanupAttempts += 1;
      if (cleanupAttempts === 1) {
        throw new Error('The adapter lease is still active.');
      }
      return { stdout: '', stderr: '' };
    }
  });
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    workspaceRoot: path.join('C:', 'work'),
    stop: () => undefined
  });

  await integration.shutdown();
  await integration.handleAdapterExit('vscode-session-1');

  assert.equal(cleanupAttempts, 2);
});

test('VBA debug integration shutdown cancels bound restart capture before cleanup', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const vbaDevPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const events: string[] = [];
  let notifyCaptureStarted!: () => void;
  const captureStarted = new Promise<void>((resolve) => {
    notifyCaptureStarted = resolve;
  });
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: vbaDevPath,
        bundledPath: vbaDevPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: adapterPath,
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    createDebugSessionId: () => adapterSessionId,
    debugAdapterCleanupProcess: async () => {
      events.push('cleanup');
      return { stdout: '', stderr: '' };
    },
    debugConfigurationHost: {
      workspaceRoots: [workspaceRoot],
      getActiveEditor: () => undefined,
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [manifestPath],
      readTextFile: async () => JSON.stringify({
        schemaVersion: 1,
        projectName: 'BookProject',
        primaryDocument: 'Book1',
        documents: {
          Book1: {
            kind: 'excel',
            sourcePath: 'src/Book1',
            templatePath: 'src/Book1/Book1.xlsm',
            binPath: 'bin/Book1.xlsm',
            publishPath: 'publish/Book1.xlsm',
            commonModules: [],
            references: []
          }
        }
      }),
      readSourceText: async () => 'Public Sub RunTarget()\r\nEnd Sub\r\n',
      findExportedSourceFiles: async () => [sourcePath],
      captureSourceInventory: async (_sourceSetPath, cancellationToken) => {
        notifyCaptureStarted();
        return new Promise((_, reject) => {
          cancellationToken?.onCancellationRequested(() => {
            events.push('capture:cancel');
            reject(new VbaDebugCancellationError());
          });
        });
      }
    }
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  });
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const preparation = integration.runRestartPreparation(configuration);
  const preparationCancelled = assert.rejects(
    preparation,
    (error) => error instanceof VbaDebugCancellationError
  );

  await captureStarted;
  await integration.shutdown();

  await preparationCancelled;
  assert.deepEqual(events, ['capture:cancel', 'cleanup']);
});

test('VBA debug integration shutdown stops its owned session before cleanup', async () => {
  const vbaDevPath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const events: string[] = [];
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath: vbaDevPath,
        bundledPath: vbaDevPath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: adapterPath,
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    createDebugSessionId: () => adapterSessionId,
    debugAdapterCleanupProcess: async () => {
      events.push('cleanup');
      return { stdout: '', stderr: '' };
    }
  });
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    workspaceRoot: path.join('C:', 'work'),
    stop: async () => { events.push('stop'); }
  });

  await integration.shutdown();

  assert.deepEqual(events, ['stop', 'cleanup']);
});

test('VBA debug integration shutdown invalidates an in-flight adapter reservation', async () => {
  const vbaDevPath = path.join('D:', 'tools', 'vba-dev.exe');
  let finishDevtoolResolution!: () => void;
  let notifyDevtoolResolutionStarted!: () => void;
  const devtoolResolutionStarted = new Promise<void>((resolve) => {
    notifyDevtoolResolutionStarted = resolve;
  });
  const devtoolResolution = new Promise<void>((resolve) => {
    finishDevtoolResolution = resolve;
  });
  let adapterResolutionCalls = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => {
        notifyDevtoolResolutionStarted();
        await devtoolResolution;
        return {
          executablePath: vbaDevPath,
          bundledPath: vbaDevPath,
          source: 'configured',
          capabilities: compatibleCapabilities()
        };
      }
    },
    vbaDebugAdapterResolver: {
      resolve: async () => {
        adapterResolutionCalls += 1;
        return {
          executablePath: path.join('D:', 'tools', 'vba-debug-adapter.exe'),
          capabilities: compatibleDebugAdapterCapabilities()
        };
      }
    }
  });
  const executable = integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    workspaceRoot: path.join('C:', 'work'),
    stop: () => undefined
  });
  await devtoolResolutionStarted;

  await integration.shutdown();
  finishDevtoolResolution();

  assert.equal(await executable, undefined);
  assert.equal(adapterResolutionCalls, 0);
});

test('VBA debug startup suppresses an already reported resolution failure and releases the session', async () => {
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  let attempts = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => {
        attempts += 1;
        if (attempts === 1) {
          throw new VbaDevCompatibilityError('no compatible vba-dev', true);
        }

        return {
          executablePath,
          bundledPath: executablePath,
          source: 'bundled',
          capabilities: compatibleCapabilities()
        };
      }
    },
    vbaDebugAdapterResolver: {
      resolve: async () => ({
        executablePath: adapterPath,
        capabilities: compatibleDebugAdapterCapabilities()
      })
    },
    createDebugSessionId: () => '0123456789abcdef0123456789abcdef'
  });

  const suppressed = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    stop: () => undefined
  });
  const recovered = await integration.createDebugAdapterExecutable({
    id: 'session-2',
    stop: () => undefined
  });

  assert.equal(suppressed, undefined);
  assert.deepEqual(recovered, {
    command: adapterPath,
    args: [
      '--stdio',
      '--vba-dev',
      executablePath,
      '--session',
      '0123456789abcdef0123456789abcdef'
    ],
    options: undefined
  });
});

test('VBA debug startup releases its session reservation after standalone adapter compatibility failure', async () => {
  let compatible = false;
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const adapterPath = path.join('D:', 'tools', 'vba-debug-adapter.exe');
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => undefined,
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        bundledPath: executablePath,
        source: 'configured',
        capabilities: compatibleCapabilities()
      })
    },
    vbaDebugAdapterResolver: {
      resolve: async () => {
        if (!compatible) {
          throw new Error('debug adapter protocolVersion 0.9');
        }
        return {
          executablePath: adapterPath,
          capabilities: compatibleDebugAdapterCapabilities()
        };
      }
    }
  });

  await assert.rejects(
    () => integration.createDebugAdapterExecutable({
      id: 'session-1',
      stop: () => undefined
    }),
    /debug adapter protocolVersion 0\.9/
  );

  compatible = true;
  await integration.createDebugAdapterExecutable({ id: 'session-2', stop: () => undefined });
});

function compatibleCapabilities(): VbaDevCapabilities {
  return {
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    commands: {}
  };
}

function compatibleDebugAdapterCapabilities() {
  return {
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    protocolVersion: '1.1',
    transports: ['stdio'],
    sessionIdFormat: 'lowercase-hex-32',
    commands: ['cleanup', 'doctor'],
    commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0' },
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
  };
}

function requiredDebugAdapterContract() {
  const { toolVersion: _toolVersion, ...contract } = compatibleDebugAdapterCapabilities();
  return contract;
}

function requiredContract() {
  return {
    contractVersion: '1.0',
    commandSchemaVersions: {}
  };
}
