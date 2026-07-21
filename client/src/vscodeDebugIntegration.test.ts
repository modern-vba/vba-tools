import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  VscodeDebugIntegration,
  createVbaDebugConfigurationProvider,
  useVbaDebugConfigurationObserverForTest
} from './vscodeDebugIntegration';

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
