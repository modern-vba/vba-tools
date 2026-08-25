import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { VbaDevSessionResolver } from './devtool';
import { createVbaLanguageServerOptions } from './languageServer';

import {
  ManagedToolingWorkspaceTrustGate,
  ManagedToolingCommandIds,
  ProjectCreationRestrictedModeMessage,
  WorkspaceTrustAction,
  createManagedToolingCommandHandler,
  createManagedToolingCommandHandlers,
  resolveCompanionExecutableForLanguageActivation
} from './workspaceTrust';

test('restricted language activation starts safe language assistance without managed tooling', async () => {
  let resolutions = 0;

  const resolution = await resolveCompanionExecutableForLanguageActivation(
    false,
    async () => {
      resolutions += 1;
      return { executablePath: 'vba-dev.exe' };
    }
  );
  const vbaDevExecutablePath = resolution?.executablePath;

  assert.equal(resolution, undefined);
  assert.equal(resolutions, 0);
  const languageServerOptions = createVbaLanguageServerOptions({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    platform: 'win32',
    vbaDevExecutablePath
  }) as {
    readonly run: { readonly args?: readonly string[] };
    readonly debug: { readonly args?: readonly string[] };
  };
  assert.equal(languageServerOptions.run.args, undefined);
  assert.equal(languageServerOptions.debug.args, undefined);
});

test('restricted managed command handler blocks before the operation and only offers trust management', async () => {
  const warnings: Array<{ message: string; actions: readonly string[] }> = [];
  const commands: string[] = [];
  let commandInvocations = 0;
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => false,
    invalidateManagedToolingState: () => undefined,
    showWarningMessage: async (message, ...actions) => {
      warnings.push({ message, actions });
      return undefined;
    },
    executeCommand: async (command) => {
      commands.push(command);
    }
  });
  const handler = createManagedToolingCommandHandler(
    gate,
    'managed-tooling',
    async () => {
      commandInvocations += 1;
    }
  );

  await handler();

  assert.equal(commandInvocations, 0);
  assert.deepEqual(warnings, [{
    message: 'VBA Tools cannot run managed VBA tooling in Restricted Mode. Trust this workspace to continue.',
    actions: [WorkspaceTrustAction.ManageWorkspaceTrust]
  }]);
  assert.deepEqual(commands, []);
});

test('granting trust never resumes a blocked command and a later explicit invocation runs once', async () => {
  let trusted = false;
  let invalidations = 0;
  let warnings = 0;
  let commandInvocations = 0;
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => trusted,
    invalidateManagedToolingState: () => {
      invalidations += 1;
    },
    showWarningMessage: async () => {
      warnings += 1;
      return WorkspaceTrustAction.ManageWorkspaceTrust;
    },
    executeCommand: async () => {
      trusted = true;
    }
  });
  const handler = createManagedToolingCommandHandler(
    gate,
    'managed-tooling',
    async () => {
      commandInvocations += 1;
    }
  );

  await handler();
  assert.equal(trusted, true);
  assert.equal(commandInvocations, 0);

  await handler();

  assert.equal(commandInvocations, 1);
  assert.equal(invalidations, 1);
  assert.equal(warnings, 1);
});

test('a later trusted command performs fresh companion resolution after the blocked boundary', async () => {
  const firstPath = path.join('D:', 'tools', 'vba-dev.exe');
  const secondPath = path.join('E:', 'tools', 'vba-dev.exe');
  let configuredPath = firstPath;
  const resolutionCalls: string[] = [];
  const resolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    configuredPathProvider: () => configuredPath,
    requiredContract: { contractVersion: '1.0', commandSchemaVersions: {} },
    runProcess: async (file) => {
      resolutionCalls.push(file);
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {}
        }),
        stderr: ''
      };
    }
  });
  await resolver.resolve();
  configuredPath = secondPath;
  let trusted = false;
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => trusted,
    invalidateManagedToolingState: () => resolver.invalidate(),
    showWarningMessage: async () => WorkspaceTrustAction.ManageWorkspaceTrust,
    executeCommand: async () => {
      trusted = true;
    }
  });
  const resolvedPaths: string[] = [];
  const command = createManagedToolingCommandHandler(
    gate,
    'managed-tooling',
    async () => {
      resolvedPaths.push((await resolver.resolve()).executablePath);
    }
  );

  await command();
  assert.deepEqual(resolvedPaths, []);
  assert.deepEqual(resolutionCalls, [firstPath]);

  await command();

  assert.deepEqual(resolvedPaths, [secondPath]);
  assert.deepEqual(resolutionCalls, [firstPath, secondPath]);
});

test('a trusted managed command handler forwards its request and result unchanged', async () => {
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => true,
    invalidateManagedToolingState: () => {
      throw new Error('Trusted invocation must not invalidate state');
    },
    showWarningMessage: async () => {
      throw new Error('Trusted invocation must not show a warning');
    },
    executeCommand: async () => {
      throw new Error('Trusted invocation must not navigate');
    }
  });
  const requests: string[] = [];
  const handler = createManagedToolingCommandHandler(
    gate,
    'managed-tooling',
    async (request: string) => {
      requests.push(request);
      return 'completed';
    }
  );

  const result = await handler('export-request');

  assert.equal(result, 'completed');
  assert.deepEqual(requests, ['export-request']);
});

test('the managed command registry gates every process-launching palette family', async () => {
  const operationCalls: string[] = [];
  const warnings: string[] = [];
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => false,
    invalidateManagedToolingState: () => undefined,
    showWarningMessage: async (message) => {
      warnings.push(message);
      return undefined;
    },
    executeCommand: async () => undefined
  });
  const operations = Object.fromEntries(
    ManagedToolingCommandIds.map((commandId) => [
      commandId,
      () => {
        operationCalls.push(commandId);
      }
    ])
  );

  const commands = createManagedToolingCommandHandlers(gate, operations);
  for (const command of commands) {
    await command.handler();
  }

  assert.deepEqual(commands.map((command) => command.commandId), [
    'vbaTools.doctor',
    'vbaTools.openVbaDevTerminal',
    'vbaTools.newExcel',
    'vbaTools.export',
    'vbaTools.build',
    'vbaTools.test',
    'vbaTools.publish',
    'vbaTools.hostClasses.refresh',
    'vbaTools.commonModules.add',
    'vbaTools.commonModules.list',
    'vbaTools.commonModules.update',
    'vbaTools.references.list',
    'vbaTools.references.add',
    'vbaTools.references.remove'
  ]);
  assert.deepEqual(operationCalls, []);
  assert.equal(warnings.length, ManagedToolingCommandIds.length);
  assert.equal(warnings.filter((message) => message === ProjectCreationRestrictedModeMessage).length, 1);
});

test('restricted project creation invalidates managed state and only opens trust management', async () => {
  let invalidations = 0;
  const warnings: Array<{ message: string; actions: readonly string[] }> = [];
  const commands: string[] = [];
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => false,
    invalidateManagedToolingState: () => {
      invalidations += 1;
    },
    showWarningMessage: async (message, ...actions) => {
      warnings.push({ message, actions });
      return WorkspaceTrustAction.ManageWorkspaceTrust;
    },
    executeCommand: async (command) => {
      commands.push(command);
    }
  });

  let commandInvocations = 0;
  const result = await gate.run('project-creation', async () => {
    commandInvocations += 1;
    return 'started';
  });

  assert.equal(result, undefined);
  assert.equal(commandInvocations, 0);
  assert.equal(invalidations, 1);
  assert.deepEqual(warnings, [{
    message: ProjectCreationRestrictedModeMessage,
    actions: [
      WorkspaceTrustAction.ManageWorkspaceTrust,
      WorkspaceTrustAction.OpenEmptyWindow
    ]
  }]);
  assert.deepEqual(commands, ['workbench.trust.manage']);
});

test('restricted project creation opens an Empty Window only after explicit selection', async () => {
  const commands: string[] = [];
  let commandInvocations = 0;
  const gate = new ManagedToolingWorkspaceTrustGate({
    isTrusted: () => false,
    invalidateManagedToolingState: () => undefined,
    showWarningMessage: async () => WorkspaceTrustAction.OpenEmptyWindow,
    executeCommand: async (command) => {
      commands.push(command);
    }
  });

  await gate.run('project-creation', () => {
    commandInvocations += 1;
  });

  assert.equal(commandInvocations, 0);
  assert.deepEqual(commands, ['vscode.newWindow']);
});
