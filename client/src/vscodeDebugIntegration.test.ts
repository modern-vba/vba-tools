import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  VscodeDebugIntegration,
  createVbaDebugConfigurationProvider,
  handleVbaDebugSessionTermination,
  stopVbaDebugSessionAfterLifecycleFailure,
  useVbaDebugConfigurationObserverForTest
} from './vscodeDebugIntegration';
import { VbaDebugCancellationError } from './vscodeDebugConfiguration';

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

test('VBA debug provider prepares the resolved configuration for the restart handshake', async () => {
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
  let preparedWorkspaceFolder: string | undefined;
  const provider = createVbaDebugConfigurationProvider({
    provideDynamicDebugConfigurations: () => [],
    resolveDebugConfiguration: async () => resolvedConfiguration,
    prepareDebugConfigurationForRestart: (configuration, workspaceFolderPath) => {
      preparedWorkspaceFolder = workspaceFolderPath;
      return {
        ...configuration,
        __vbaRestartPreparation: {
          protocolVersion: 1,
          id: 'restart-preparation'
        }
      };
    }
  }, () => undefined);

  const configuration = await provider.resolveDebugConfigurationWithSubstitutedVariables(
    resolvedConfiguration,
    workspaceFolder
  );

  assert.equal(preparedWorkspaceFolder, workspaceFolder);
  assert.deepEqual(configuration?.__vbaRestartPreparation, {
    protocolVersion: 1,
    id: 'restart-preparation'
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

test('VBA debug startup resolves the bundled compatible adapter over stdio', async () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const workspaceRoot = path.join('C:', 'work', 'BookProject');
  const capabilityCalls: Array<{ file: string; args: readonly string[] }> = [];
  const integration = new VscodeDebugIntegration({
    extensionRoot,
    getConfiguredDevToolPath: () => undefined,
    capabilitiesProcess: async (file, args) => {
      capabilityCalls.push({ file, args });
      return {
        stdout: JSON.stringify(compatibleCapabilities()),
        stderr: ''
      };
    },
    requiredContract: requiredContract()
  });

  const descriptor = await integration.createDebugAdapterExecutable({
    id: 'session-1',
    workspaceRoot
  });
  const expectedExecutable = path.join(
    extensionRoot,
    'bin',
    'vba-dev',
    'win-x64',
    'vba-dev.exe'
  );

  assert.deepEqual(capabilityCalls, [{
    file: expectedExecutable,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.deepEqual(descriptor, {
    command: expectedExecutable,
    args: ['debug-adapter', '--stdio'],
    options: { cwd: workspaceRoot }
  });
});

test('VBA debug startup rejects a second session until the active session terminates', async () => {
  let capabilityCallCount = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => path.join('D:', 'tools', 'vba-dev.exe'),
    capabilitiesProcess: async () => {
      capabilityCallCount += 1;
      return {
        stdout: JSON.stringify(compatibleCapabilities()),
        stderr: ''
      };
    },
    requiredContract: requiredContract()
  });

  await integration.createDebugAdapterExecutable({ id: 'session-1' });
  await assert.rejects(
    () => integration.createDebugAdapterExecutable({ id: 'session-2' }),
    /already running in this VS Code window/
  );
  assert.equal(capabilityCallCount, 1);

  integration.releaseSession('session-1');
  await integration.createDebugAdapterExecutable({ id: 'session-2' });
  assert.equal(capabilityCallCount, 2);
});

test('only the owning VBA debug session release cancels its pending restart preparation', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  let finishSave!: (saved: boolean) => void;
  let notifySaveStarted!: () => void;
  const saveStarted = new Promise<void>((resolve) => {
    notifySaveStarted = resolve;
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
        publishPath: 'publish/Book1.xlsm'
      }
    }
  });
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => path.join('D:', 'tools', 'vba-dev.exe'),
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify(compatibleCapabilities()),
      stderr: ''
    }),
    requiredContract: requiredContract(),
    debugConfigurationHost: {
      workspaceRoots: [workspaceRoot],
      getActiveEditor: () => undefined,
      getOpenTextDocuments: () => [{
        uriPath: sourcePath,
        isDirty: true,
        save: () => new Promise<boolean>((resolve) => {
          finishSave = resolve;
          notifySaveStarted();
        })
      }],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [manifestPath],
      readTextFile: async () => manifest,
      readSourceText: async () => 'Public Sub RunTarget()\r\nEnd Sub\r\n',
      findExportedSourceFiles: async () => [sourcePath]
    }
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  });
  await integration.createDebugAdapterExecutable({
    id: 'session-1',
    configuration
  });
  const preparation = integration.runRestartPreparation(configuration);

  await saveStarted;
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
  try {
    await assert.rejects(
      preparation,
      (error) => error instanceof VbaDebugCancellationError
    );
    await assert.rejects(
      () => integration.runRestartPreparation(configuration),
      /restart preparation is unavailable/
    );
  } finally {
    finishSave(true);
  }
});

test('VBA debug startup uses the configured compatible executable and advertised command', async () => {
  const configuredPath = path.join('D:', 'tools', 'vba-dev.exe');
  const capabilityCalls: Array<{ file: string; args: readonly string[] }> = [];
  const capabilities = compatibleCapabilities();
  capabilities.debugAdapter = {
    protocolVersion: '1.0',
    transport: 'stdio',
    command: 'vba-debug-adapter'
  };
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => configuredPath,
    capabilitiesProcess: async (file, args) => {
      capabilityCalls.push({ file, args });
      return { stdout: JSON.stringify(capabilities), stderr: '' };
    },
    requiredContract: requiredContract()
  });

  const descriptor = await integration.createDebugAdapterExecutable({ id: 'session-1' });

  assert.deepEqual(capabilityCalls, [{
    file: configuredPath,
    args: ['capabilities', '--format', 'json']
  }]);
  assert.deepEqual(descriptor, {
    command: configuredPath,
    args: ['vba-debug-adapter', '--stdio'],
    options: undefined
  });
});

test('VBA debug startup releases its session reservation after compatibility failure', async () => {
  let compatible = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    getConfiguredDevToolPath: () => path.join('D:', 'tools', 'vba-dev.exe'),
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify(compatible
        ? compatibleCapabilities()
        : {
            ...compatibleCapabilities(),
            debugAdapter: {
              protocolVersion: '0.9',
              transport: 'stdio',
              command: 'debug-adapter'
            }
          }),
      stderr: ''
    }),
    requiredContract: requiredContract()
  });

  await assert.rejects(
    () => integration.createDebugAdapterExecutable({ id: 'session-1' }),
    /debug adapter protocolVersion 0\.9/
  );

  compatible = true;
  await integration.createDebugAdapterExecutable({ id: 'session-2' });
});

function compatibleCapabilities(): Record<string, unknown> {
  return {
    toolVersion: '0.1.0',
    contractVersion: '1.0',
    commands: {},
    debugAdapter: {
      protocolVersion: '1.0',
      transport: 'stdio',
      command: 'debug-adapter'
    }
  };
}

function requiredContract() {
  return {
    contractVersion: '1.0',
    debugAdapterProtocolVersion: '1.0',
    commandSchemaVersions: {}
  };
}
