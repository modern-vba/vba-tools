import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  NewExcelProjectCommand,
  NewExcelProjectCommandOptions
} from './newExcelProjectCommand';
import type { CompanionExecutableResolution } from './devtool';
import type { VbaDevCommandRunResult } from './devtoolRuntime';

test('guided creation binds one resolution to preflight and one exact creation invocation', async () => {
  const resolution = createResolution();
  const parentPath = String.raw`C:\work`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  const events: string[] = [];
  const invocations: readonly string[][] = [];
  const mutableInvocations = invocations as string[][];
  const notifications: Array<{
    readonly kind: string;
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  let resolverCalls = 0;

  const options: NewExcelProjectCommandOptions = {
    resolveCompanionExecutable: async () => {
      resolverCalls += 1;
      events.push('resolve');
      return resolution;
    },
    runCommand: async (receivedResolution, args) => {
      assert.equal(receivedResolution, resolution);
      mutableInvocations.push([...args]);
      if (args[0] === 'doctor') {
        events.push('doctor');
        return commandResult(0, environmentDoctorJson());
      }

      events.push('new');
      return commandResult(0, newExcelReceiptJson(projectRoot, projectName));
    },
    showProjectNameInput: async (inputOptions) => {
      events.push('name');
      assert.equal(inputOptions.title, 'Create Excel VBA Project');
      assert.equal(inputOptions.value, undefined);
      assert.equal(inputOptions.validateInput(projectName), undefined);
      return projectName;
    },
    showParentFolder: async (folderOptions) => {
      events.push('parent');
      assert.equal(folderOptions.title, 'Select Parent Folder for "Sample"');
      assert.equal(folderOptions.openLabel, 'Create Here');
      return { scheme: 'file', fsPath: parentPath };
    },
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: parentPath }],
    getActiveResource: () => undefined,
    showInformationMessage: async (message, ...actions) => {
      notifications.push({ kind: 'information', message, actions });
      return undefined;
    },
    showWarningMessage: async (message, ...actions) => {
      notifications.push({ kind: 'warning', message, actions });
      return undefined;
    },
    showErrorMessage: async (message, _options, ...actions) => {
      notifications.push({ kind: 'error', message, actions });
      return undefined;
    },
    showOutput: () => undefined,
    appendOutput: () => undefined,
    openSetupInstructions: async () => undefined,
    openSettings: async () => undefined,
    openManifest: async () => undefined,
    openFolderInNewWindow: async () => undefined
  };

  await new NewExcelProjectCommand(options).run();

  assert.equal(resolverCalls, 1);
  assert.deepEqual(events, ['resolve', 'doctor', 'name', 'parent', 'new']);
  assert.deepEqual(mutableInvocations, [
    ['doctor', '--scope', 'environment', '--format', 'json'],
    ['new', 'excel', '--name', projectName, '--output', projectRoot, '--format', 'json']
  ]);
  assert.deepEqual(notifications, [{
    kind: 'information',
    message: 'Created Excel VBA project "Sample".',
    actions: ['Open Manifest']
  }]);
});

test('a second guided creation is rejected while the first preflight is running', async () => {
  const resolution = createResolution();
  const doctorStarted = createDeferred<void>();
  const finishDoctor = createDeferred<ReturnType<typeof commandResult>>();
  const informationMessages: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  let resolverCalls = 0;
  const options = createCommandOptions({
    resolveCompanionExecutable: async () => {
      resolverCalls += 1;
      return resolution;
    },
    runCommand: async () => {
      doctorStarted.resolve();
      return finishDoctor.promise;
    },
    showInformationMessage: async (message, ...actions) => {
      informationMessages.push({ message, actions });
      return undefined;
    }
  });
  const command = new NewExcelProjectCommand(options);

  const firstInvocation = command.run();
  await doctorStarted.promise;
  const secondInvocation = command.run();
  await new Promise<void>((resolve) => setImmediate(resolve));

  finishDoctor.resolve(commandResult(130));
  await Promise.all([firstInvocation, secondInvocation]);

  assert.equal(resolverCalls, 1);
  assert.deepEqual(informationMessages, [{
    message: 'Excel VBA project creation is already in progress in this window.',
    actions: []
  }]);

});

test('a passing environment preflight is reused for the same resolution after name cancellation', async () => {
  const resolution = createResolution();
  let doctorRuns = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => resolution,
    runCommand: async (_receivedResolution, args) => {
      assert.equal(args[0], 'doctor');
      doctorRuns += 1;
      return commandResult(0, environmentDoctorJson());
    }
  }));

  await command.run();
  await command.run();

  assert.equal(doctorRuns, 1);
});

test('invalidating the command cache requires a fresh environment preflight', async () => {
  const resolution = createResolution();
  let doctorRuns = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => resolution,
    runCommand: async () => {
      doctorRuns += 1;
      return commandResult(0, environmentDoctorJson());
    }
  }));

  await command.run();
  command.invalidatePreflight();
  await command.run();

  assert.equal(doctorRuns, 2);
});

test('an untrusted environment preflight blocks input and offers setup, retry, and output actions', async () => {
  const errors: Array<{
    readonly message: string;
    readonly modal: boolean;
    readonly actions: readonly string[];
  }> = [];
  let namePrompts = 0;
  let outputReveals = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async () => commandResult(0, '{not json'),
    showProjectNameInput: async () => {
      namePrompts += 1;
      return undefined;
    },
    showErrorMessage: async (message, options, ...actions) => {
      errors.push({ message, modal: options?.modal ?? false, actions });
      return 'Show Output';
    },
    showOutput: () => {
      outputReveals += 1;
    }
  }));

  await command.run();

  assert.deepEqual(errors, [{
    message: 'VBA Tools could not verify Excel VBA project prerequisites.',
    modal: false,
    actions: ['Open Setup Instructions', 'Retry', 'Show Output']
  }]);
  assert.equal(namePrompts, 0);
  assert.equal(outputReveals, 1);
});

test('a trusted complete warning preflight reports that prerequisites need attention', async () => {
  const warnings: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async () => commandResult(0, environmentDoctorWarningJson()),
    showWarningMessage: async (message, ...actions) => {
      warnings.push({ message, actions });
      return undefined;
    }
  }));

  await command.run();

  assert.deepEqual(warnings, [{
    message: 'Excel VBA project prerequisites need attention.',
    actions: ['Open Setup Instructions', 'Retry', 'Show Output']
  }]);
});

test('Retry reruns environment preflight with the same resolution before opening input', async () => {
  const resolution = createResolution();
  let resolverCalls = 0;
  let doctorRuns = 0;
  let namePrompts = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => {
      resolverCalls += 1;
      return resolution;
    },
    runCommand: async () => {
      doctorRuns += 1;
      return doctorRuns === 1
        ? commandResult(0, environmentDoctorWarningJson())
        : commandResult(0, environmentDoctorJson());
    },
    showWarningMessage: async () => 'Retry',
    showProjectNameInput: async () => {
      namePrompts += 1;
      return undefined;
    }
  }));

  await command.run();

  assert.equal(resolverCalls, 1);
  assert.equal(doctorRuns, 2);
  assert.equal(namePrompts, 1);
});

test('project name validation reports the contract-specific inline message without rewriting input', async () => {
  const expectedMessages = new Map<string, string>([
    ['', 'Enter a project name.'],
    ['\ud800', 'Project name contains an invalid Unicode sequence.'],
    ['.', 'Project name cannot be "." or "..".'],
    ['A/B', 'Project name cannot contain "/" or "\\".'],
    ['A:B', 'Project name contains a character that Windows does not allow in a file or folder name.'],
    ['A\u0001B', 'Project name cannot contain control characters.'],
    [' A', 'Project name cannot start or end with whitespace.'],
    ['A.', 'Project name cannot end with a dot.'],
    ['NUL.txt', 'Project name cannot use a reserved Windows device name, even with an extension.'],
    ['A[B', 'Project name cannot contain "[" or "]" because Excel does not reliably support them in workbook paths.']
  ]);
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async () => commandResult(0, environmentDoctorJson()),
    showProjectNameInput: async (options) => {
      for (const [candidate, expected] of expectedMessages) {
        assert.equal(options.validateInput(candidate), expected, candidate);
      }
      assert.equal(options.validateInput('  Internal Space  '.trim()), undefined);
      return undefined;
    }
  }));

  await command.run();
});

test('a non-file parent retains the name and allows choosing another parent', async () => {
  const projectName = 'Sample';
  const localParent = String.raw`C:\work`;
  let folderPrompts = 0;
  let namePrompts = 0;
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(130),
    showProjectNameInput: async () => {
      namePrompts += 1;
      return projectName;
    },
    showParentFolder: async () => {
      folderPrompts += 1;
      return folderPrompts === 1
        ? { scheme: 'vscode-remote', fsPath: '/workspace' }
        : { scheme: 'file', fsPath: localParent };
    },
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Choose Another Parent';
    }
  }));

  await command.run();

  assert.equal(namePrompts, 1);
  assert.equal(folderPrompts, 2);
  assert.deepEqual(errors, [{
    message: 'Select a folder on the Windows file system. Remote and virtual workspace folders cannot be used to create an Excel VBA project.',
    actions: ['Choose Another Parent', 'Cancel']
  }]);
});

test('a bracketed parent can be replaced without asking for the name again', async () => {
  const rejectedParent = String.raw`C:\bad[parent]`;
  const acceptedParent = String.raw`C:\good`;
  const parentDefaults: Array<string | undefined> = [];
  let folderPrompts = 0;
  let namePrompts = 0;
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const outputEntries: string[] = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(130),
    showProjectNameInput: async () => {
      namePrompts += 1;
      return 'Sample';
    },
    showParentFolder: async (options) => {
      parentDefaults.push(options.defaultUri?.fsPath);
      folderPrompts += 1;
      return {
        scheme: 'file',
        fsPath: folderPrompts === 1 ? rejectedParent : acceptedParent
      };
    },
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Choose Another Parent';
    },
    appendOutput: (text) => outputEntries.push(text)
  }));

  await command.run();

  assert.equal(namePrompts, 1);
  assert.equal(folderPrompts, 2);
  assert.deepEqual(parentDefaults, [undefined, rejectedParent]);
  assert.deepEqual(errors, [{
    message: 'The selected parent folder cannot be used because its path contains "[" or "]", which Excel does not reliably support.',
    actions: ['Choose Another Parent', 'Cancel']
  }]);
  const firstWorkbookPath = path.win32.join(
    rejectedParent,
    'Sample',
    'src',
    'Sample',
    'Sample.xlsm'
  );
  assert.deepEqual(outputEntries, [
    `excelPathContainsUnsupportedCharacter: Excel workbook path contains "[" or "]", which Excel does not reliably support: "${firstWorkbookPath}".`
  ]);
});

test('an overlong derived workbook path can change the name while retaining the parent', async () => {
  const parentPath = `C:\\${'p'.repeat(180)}`;
  const firstName = 'LongProjectName';
  const secondName = 'A';
  const nameOptions: Array<{
    readonly value: string | undefined;
    readonly valueSelection: readonly [number, number] | undefined;
  }> = [];
  let folderPrompts = 0;
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(130),
    showProjectNameInput: async (options) => {
      nameOptions.push({
        value: options.value,
        valueSelection: options.valueSelection
      });
      return nameOptions.length === 1 ? firstName : secondName;
    },
    showParentFolder: async () => {
      folderPrompts += 1;
      return { scheme: 'file', fsPath: parentPath };
    },
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Change Name';
    }
  }));

  await command.run();

  assert.equal(folderPrompts, 1);
  assert.deepEqual(nameOptions, [
    { value: undefined, valueSelection: undefined },
    { value: firstName, valueSelection: [0, firstName.length] }
  ]);
  assert.deepEqual(errors, [{
    message: "One or more generated workbook paths exceed Excel's 218-character limit.",
    actions: ['Choose Another Parent', 'Change Name', 'Cancel']
  }]);
});

test('a creation failure offers only output inspection and invalidates the preflight pass', async () => {
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  let doctorRuns = 0;
  let namePrompts = 0;
  let outputReveals = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => {
      if (args[0] === 'doctor') {
        doctorRuns += 1;
        return commandResult(0, environmentDoctorJson());
      }
      return commandResult(1, '', 'creation failed');
    },
    showProjectNameInput: async () => {
      namePrompts += 1;
      return namePrompts === 1 ? 'Sample' : undefined;
    },
    showParentFolder: async () => ({ scheme: 'file', fsPath: String.raw`C:\work` }),
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Show Output';
    },
    showOutput: () => {
      outputReveals += 1;
    }
  }));

  await command.run();
  await command.run();

  assert.equal(doctorRuns, 2);
  assert.deepEqual(errors, [{
    message: 'Excel VBA project creation failed for "Sample".',
    actions: ['Show Output']
  }]);
  assert.equal(outputReveals, 1);
});

test('exit 130 after creation starts is silent and preserves the passing preflight', async () => {
  const resolution = createResolution();
  const parentPath = String.raw`C:\work`;
  let doctorRuns = 0;
  let creationRuns = 0;
  let namePrompts = 0;
  let terminalNotifications = 0;
  let navigationCalls = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => resolution,
    runCommand: async (_resolution, args) => {
      if (args[0] === 'doctor') {
        doctorRuns += 1;
        return commandResult(0, environmentDoctorJson());
      }
      creationRuns += 1;
      return commandResult(130, '', 'Project creation was cancelled.');
    },
    showProjectNameInput: async () => {
      namePrompts += 1;
      return namePrompts === 1 ? 'Sample' : undefined;
    },
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    showInformationMessage: async () => {
      terminalNotifications += 1;
      return undefined;
    },
    showWarningMessage: async () => {
      terminalNotifications += 1;
      return undefined;
    },
    showErrorMessage: async () => {
      terminalNotifications += 1;
      return undefined;
    },
    openManifest: async () => { navigationCalls += 1; },
    openFolderInNewWindow: async () => { navigationCalls += 1; }
  }));

  await command.run();
  await command.run();

  assert.equal(doctorRuns, 1);
  assert.equal(creationRuns, 1);
  assert.equal(terminalNotifications, 0);
  assert.equal(navigationCalls, 0);
});

test('an untrusted exit-zero creation receipt reports uncertainty and invalidates preflight', async () => {
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  let doctorRuns = 0;
  let namePrompts = 0;
  let outputReveals = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => {
      if (args[0] === 'doctor') {
        doctorRuns += 1;
        return commandResult(0, environmentDoctorJson());
      }
      return commandResult(0, '{invalid receipt');
    },
    showProjectNameInput: async () => {
      namePrompts += 1;
      return namePrompts === 1 ? 'Sample' : undefined;
    },
    showParentFolder: async () => ({ scheme: 'file', fsPath: String.raw`C:\work` }),
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Show Output';
    },
    showOutput: () => {
      outputReveals += 1;
    }
  }));

  await command.run();
  await command.run();

  assert.equal(doctorRuns, 2);
  assert.deepEqual(errors, [{
    message: 'Excel VBA project creation may have completed, but its result could not be verified. Inspect the target and VBA Tools Output.',
    actions: ['Show Output']
  }]);
  assert.equal(outputReveals, 1);
});

test('a trusted success with one CLI warning uses one warning notification', async () => {
  const parentPath = String.raw`C:\work`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  const informationMessages: string[] = [];
  const warnings: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(0, newExcelReceiptJson(projectRoot, projectName, [{
        code: 'futureWarning',
        message: 'A future compatible warning.'
      }])),
    showProjectNameInput: async () => projectName,
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: parentPath }],
    showInformationMessage: async (message) => {
      informationMessages.push(message);
      return undefined;
    },
    showWarningMessage: async (message, ...actions) => {
      warnings.push({ message, actions });
      return undefined;
    }
  }));

  await command.run();

  assert.deepEqual(informationMessages, []);
  assert.deepEqual(warnings, [{
    message: 'Created Excel VBA project "Sample". 1 warning.',
    actions: ['Open Manifest', 'Show Output']
  }]);
});

test('workspace containment uses .NET OrdinalIgnoreCase path semantics', async () => {
  const selectedParent = String.raw`C:\µ`;
  const workspaceRoot = String.raw`C:\Μ`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(selectedParent, projectName);
  const actions: Array<readonly string[]> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(0, newExcelReceiptJson(projectRoot, projectName)),
    showProjectNameInput: async () => projectName,
    showParentFolder: async () => ({ scheme: 'file', fsPath: selectedParent }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: workspaceRoot }],
    showInformationMessage: async (_message, ...receivedActions) => {
      actions.push(receivedActions);
      return undefined;
    }
  }));

  await command.run();

  assert.deepEqual(actions, [['Open Manifest']]);
});

test('a trusted success folds cancellation-delivery failure into one warning notification', async () => {
  const parentPath = String.raw`C:\work`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  const informationMessages: string[] = [];
  const warnings: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(0, newExcelReceiptJson(projectRoot, projectName), '', {
        cancellationRequested: true,
        cancellationRequestDelivered: false,
        cancellationRequestError: 'broken pipe'
      }),
    showProjectNameInput: async () => projectName,
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: parentPath }],
    showInformationMessage: async (message) => {
      informationMessages.push(message);
      return undefined;
    },
    showWarningMessage: async (message, ...actions) => {
      warnings.push({ message, actions });
      return undefined;
    }
  }));

  await command.run();

  assert.deepEqual(informationMessages, []);
  assert.deepEqual(warnings, [{
    message: 'Created Excel VBA project "Sample". Cancellation request could not be delivered.',
    actions: ['Open Manifest', 'Show Output']
  }]);
});

test('manifest navigation failure retries only the same navigation request', async () => {
  const parentPath = String.raw`C:\work`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  let creationRuns = 0;
  let manifestOpens = 0;
  const outputEntries: string[] = [];
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => {
      if (args[0] === 'doctor') {
        return commandResult(0, environmentDoctorJson());
      }
      creationRuns += 1;
      return commandResult(0, newExcelReceiptJson(projectRoot, projectName));
    },
    showProjectNameInput: async () => projectName,
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: parentPath }],
    showInformationMessage: async () => 'Open Manifest',
    openManifest: async () => {
      manifestOpens += 1;
      if (manifestOpens === 1) {
        throw new Error('editor failed');
      }
    },
    appendOutput: (text) => outputEntries.push(text),
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Retry';
    }
  }));

  await command.run();

  assert.equal(creationRuns, 1);
  assert.equal(manifestOpens, 2);
  assert.deepEqual(errors, [{
    message: 'Excel VBA project was created, but its manifest could not be opened.',
    actions: ['Retry', 'Show Output']
  }]);
  assert.deepEqual(outputEntries, [
    `Post-creation navigation failed. Project: "${projectRoot}". ` +
    `Target: "${path.win32.join(projectRoot, 'vba-project.json')}". ` +
    'Error: Error: editor failed'
  ]);
});

test('outside-workspace folder navigation failure retries only that navigation request', async () => {
  const parentPath = String.raw`C:\outside`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  let creationRuns = 0;
  let folderOpens = 0;
  const outputEntries: string[] = [];
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => {
      if (args[0] === 'doctor') {
        return commandResult(0, environmentDoctorJson());
      }
      creationRuns += 1;
      return commandResult(0, newExcelReceiptJson(projectRoot, projectName));
    },
    showProjectNameInput: async () => projectName,
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: String.raw`C:\workspace` }],
    showInformationMessage: async () => 'Open Folder in New Window',
    openFolderInNewWindow: async () => {
      folderOpens += 1;
      if (folderOpens === 1) {
        throw new Error('window failed');
      }
    },
    appendOutput: (text) => outputEntries.push(text),
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return 'Retry';
    }
  }));

  await command.run();

  assert.equal(creationRuns, 1);
  assert.equal(folderOpens, 2);
  assert.deepEqual(errors, [{
    message: 'Excel VBA project was created, but its folder could not be opened in a new window.',
    actions: ['Retry', 'Show Output']
  }]);
  assert.deepEqual(outputEntries, [
    `Post-creation navigation failed. Project: "${projectRoot}". ` +
    `Target: "${projectRoot}". Error: Error: window failed`
  ]);
});

test('single-flight ownership ends before the trusted-success notification is answered', async () => {
  const resolution = createResolution();
  const parentPath = String.raw`C:\work`;
  const projectName = 'Sample';
  const projectRoot = path.win32.join(parentPath, projectName);
  const notificationStarted = createDeferred<void>();
  const finishNotification = createDeferred<string | undefined>();
  let resolverCalls = 0;
  let namePrompts = 0;
  let informationCalls = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => {
      resolverCalls += 1;
      return resolution;
    },
    runCommand: async (_receivedResolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(0, newExcelReceiptJson(projectRoot, projectName)),
    showProjectNameInput: async () => {
      namePrompts += 1;
      return namePrompts === 1 ? projectName : undefined;
    },
    showParentFolder: async () => ({ scheme: 'file', fsPath: parentPath }),
    getWorkspaceFolders: () => [{ scheme: 'file', fsPath: parentPath }],
    showInformationMessage: async () => {
      informationCalls += 1;
      notificationStarted.resolve();
      return finishNotification.promise;
    }
  }));

  const firstInvocation = command.run();
  await notificationStarted.promise;
  const secondInvocation = command.run();
  await new Promise<void>((resolve) => setImmediate(resolve));

  finishNotification.resolve(undefined);
  await Promise.all([firstInvocation, secondInvocation]);

  assert.equal(resolverCalls, 2);
  assert.equal(namePrompts, 2);
  assert.equal(informationCalls, 1);
});

test('a file URI must still identify a Windows drive or UNC parent', async () => {
  let folderPrompts = 0;
  const errors: string[] = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(130),
    showProjectNameInput: async () => 'Sample',
    showParentFolder: async () => {
      folderPrompts += 1;
      return folderPrompts === 1
        ? { scheme: 'file', fsPath: '/tmp' }
        : { scheme: 'file', fsPath: String.raw`C:\work` };
    },
    showErrorMessage: async (message) => {
      errors.push(message);
      return 'Choose Another Parent';
    }
  }));

  await command.run();

  assert.equal(folderPrompts, 2);
  assert.deepEqual(errors, [
    'Select a folder on the Windows file system. Remote and virtual workspace folders cannot be used to create an Excel VBA project.'
  ]);
});

test('an extended device path is not accepted as a guided parent', async () => {
  let folderPrompts = 0;
  let errors = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async (_resolution, args) => args[0] === 'doctor'
      ? commandResult(0, environmentDoctorJson())
      : commandResult(130),
    showProjectNameInput: async () => 'Sample',
    showParentFolder: async () => {
      folderPrompts += 1;
      return folderPrompts === 1
        ? { scheme: 'file', fsPath: String.raw`\\?\C:\work` }
        : { scheme: 'file', fsPath: String.raw`C:\work` };
    },
    showErrorMessage: async () => {
      errors += 1;
      return 'Choose Another Parent';
    }
  }));

  await command.run();

  assert.equal(folderPrompts, 2);
  assert.equal(errors, 1);
});

test('observing a new resolution invalidates an older passing preflight identity', async () => {
  const firstResolution = createResolution();
  const secondResolution = createResolution();
  const resolutions = [firstResolution, secondResolution, firstResolution];
  let resolverCalls = 0;
  let doctorRuns = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => resolutions[resolverCalls++]!,
    runCommand: async (resolution) => {
      doctorRuns += 1;
      return resolution === secondResolution
        ? commandResult(0, environmentDoctorWarningJson())
        : commandResult(0, environmentDoctorJson());
    }
  }));

  await command.run();
  await command.run();
  await command.run();

  assert.equal(doctorRuns, 3);
});

test('a trusted failed preflight reports that prerequisites are not ready', async () => {
  const errors: Array<{
    readonly message: string;
    readonly actions: readonly string[];
  }> = [];
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async () => commandResult(1, environmentDoctorFailJson()),
    showErrorMessage: async (message, _options, ...actions) => {
      errors.push({ message, actions });
      return undefined;
    }
  }));

  await command.run();

  assert.deepEqual(errors, [{
    message: 'Excel VBA project prerequisites are not ready.',
    actions: ['Open Setup Instructions', 'Retry', 'Show Output']
  }]);
});

test('a locally cancelled late passing preflight stays silent and is not cached', async () => {
  let doctorRuns = 0;
  let namePrompts = 0;
  let notifications = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    runCommand: async () => {
      doctorRuns += 1;
      return doctorRuns === 1
        ? commandResult(0, environmentDoctorJson(), '', {
          cancellationRequested: true,
          cancellationRequestDelivered: false
        })
        : commandResult(130);
    },
    showProjectNameInput: async () => {
      namePrompts += 1;
      return undefined;
    },
    showInformationMessage: async () => {
      notifications += 1;
      return undefined;
    },
    showWarningMessage: async () => {
      notifications += 1;
      return undefined;
    },
    showErrorMessage: async () => {
      notifications += 1;
      return undefined;
    }
  }));

  await command.run();
  await command.run();

  assert.equal(doctorRuns, 2);
  assert.equal(namePrompts, 0);
  assert.equal(notifications, 0);
});

test('single-flight ownership ends before setup-instructions navigation completes', async () => {
  const resolution = createResolution();
  const setupStarted = createDeferred<void>();
  const finishSetup = createDeferred<void>();
  let doctorRuns = 0;
  let inProgressMessages = 0;
  const command = new NewExcelProjectCommand(createCommandOptions({
    resolveCompanionExecutable: async () => resolution,
    runCommand: async () => {
      doctorRuns += 1;
      return doctorRuns === 1
        ? commandResult(0, environmentDoctorWarningJson())
        : commandResult(130);
    },
    showWarningMessage: async () => 'Open Setup Instructions',
    openSetupInstructions: async () => {
      setupStarted.resolve();
      await finishSetup.promise;
    },
    showInformationMessage: async (message) => {
      if (message === 'Excel VBA project creation is already in progress in this window.') {
        inProgressMessages += 1;
      }
      return undefined;
    }
  }));

  const firstInvocation = command.run();
  await setupStarted.promise;
  const secondInvocation = command.run();
  await new Promise<void>((resolve) => setImmediate(resolve));
  finishSetup.resolve();
  await Promise.all([firstInvocation, secondInvocation]);

  assert.equal(doctorRuns, 2);
  assert.equal(inProgressMessages, 0);
});

function createResolution(): CompanionExecutableResolution {
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  return Object.freeze({
    executablePath,
    capabilities: {
      toolVersion: '0.1.0',
      contractVersion: '1.0',
      commands: {
        doctor: { outputSchemaVersion: '1.0' },
        'new excel': { outputSchemaVersion: '1.0' }
      },
      featureVersions: {
        'invocation.stdinCancellation': '1.0',
        'projectCreation.pathValidation': '1.0'
      }
    },
    configuredPath: executablePath,
    bundledPath: executablePath,
    source: 'configured' as const
  });
}

function commandResult(
  exitCode: number,
  stdout = '',
  stderr = '',
  overrides: Partial<VbaDevCommandRunResult> = {}
): VbaDevCommandRunResult {
  return { ...baseCommandResult(exitCode, stdout, stderr), ...overrides };
}

function baseCommandResult(
  exitCode: number,
  stdout = '',
  stderr = ''
): VbaDevCommandRunResult {
  return {
    executablePath: path.resolve('tools', 'vba-dev.exe'),
    stdout,
    stderr,
    exitCode,
    cancelled: false,
    cancellationRequested: false,
    cancellationRequestDelivered: undefined,
    cancellationRequestError: undefined
  };
}

function environmentDoctorJson(): string {
  const ids = [
    ['platform.windows', 'isWindows'],
    ['excel.comStartup', 'dedicatedInstanceStarted'],
    ['excel.processOwnership', 'ownedByInvocation'],
    ['excel.vbideProjectAccess', 'projectAccessSucceeded'],
    ['excel.processCleanup', 'ownedProcessReleased']
  ] as const;
  return JSON.stringify({
    schemaVersion: '1.0',
    toolVersion: '0.1.0',
    scope: 'environment',
    project: null,
    status: 'pass',
    complete: true,
    checks: ids.map(([id, detail]) => ({
      id,
      status: 'pass',
      message: `${id} passed.`,
      durationMilliseconds: 1,
      details: { [detail]: true }
    }))
  });
}

function environmentDoctorWarningJson(): string {
  const report = JSON.parse(environmentDoctorJson()) as {
    status: string;
    checks: Array<{ status: string; details: Record<string, unknown> }>;
  };
  report.status = 'warning';
  report.checks[0]!.status = 'warning';
  report.checks[0]!.details.isWindows = null;
  return JSON.stringify(report);
}

function environmentDoctorFailJson(): string {
  const report = JSON.parse(environmentDoctorJson()) as {
    status: string;
    checks: Array<{ status: string; details: Record<string, unknown> }>;
  };
  report.status = 'fail';
  report.checks[0]!.status = 'fail';
  report.checks[0]!.details.isWindows = false;
  return JSON.stringify(report);
}

function newExcelReceiptJson(
  projectRoot: string,
  name: string,
  warnings: readonly { readonly code: string; readonly message: string }[] = []
): string {
  return JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: name,
    operation: 'new',
    template: 'excel',
    complete: true,
    warnings,
    manifestPath: path.win32.join(projectRoot, 'vba-project.json'),
    manifest: {
      schemaVersion: 1,
      projectName: name,
      primaryDocument: name,
      documents: {
        [name]: {
          kind: 'excel',
          sourcePath: `src/${name}`,
          templatePath: `src/${name}/${name}.xlsm`,
          binPath: `bin/${name}.xlsm`,
          publishPath: `publish/${name}.xlsm`,
          commonModules: [],
          references: []
        }
      },
      commonModulesRepository: '../common_modules_repo'
    }
  });
}

function createCommandOptions(
  overrides: Partial<NewExcelProjectCommandOptions> = {}
): NewExcelProjectCommandOptions {
  return {
    resolveCompanionExecutable: async () => createResolution(),
    runCommand: async () => commandResult(130),
    showProjectNameInput: async () => undefined,
    showParentFolder: async () => undefined,
    getWorkspaceFolders: () => [],
    getActiveResource: () => undefined,
    showInformationMessage: async () => undefined,
    showWarningMessage: async () => undefined,
    showErrorMessage: async () => undefined,
    showOutput: () => undefined,
    appendOutput: () => undefined,
    openSetupInstructions: async () => undefined,
    openSettings: async () => undefined,
    openManifest: async () => undefined,
    openFolderInNewWindow: async () => undefined,
    ...overrides
  };
}

function createDeferred<T>() {
  let resolvePromise: (value: T | PromiseLike<T>) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    resolvePromise = resolve;
  });
  return { promise, resolve: resolvePromise };
}
