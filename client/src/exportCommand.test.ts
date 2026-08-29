import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { runExportCommand } from './exportCommand';

test('cleanup export cancellation obtains exact consent before resolving vba-dev', async () => {
  const projectRoot = path.resolve('test-work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const destinationPath = path.resolve(projectRoot, 'src', 'Book2');
  const selectedTarget = createManifestTarget(projectRoot, 'Book2');
  let targetResolverCalls = 0;
  let resolverCalls = 0;
  let processStarts = 0;
  let progressStarts = 0;
  const output: string[] = [];
  const prompts: Array<{
    message: string;
    options: { modal: boolean; detail: string };
    items: string[];
  }> = [];
  const errors: string[] = [];

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => {
        resolverCalls += 1;
        throw new Error('vba-dev must not be resolved before consent');
      }
    },
    activeFilePath: path.join(destinationPath, 'Module1.bas'),
    workspaceRoots: [path.dirname(projectRoot)],
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'document');
      targetResolverCalls += 1;
      return selectedTarget;
    },
    fileExists: async (candidate) => path.normalize(candidate) === path.normalize(manifestPath),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    readTextFile: async () => {
      throw new Error('selected target must not be re-read');
    },
    showWarningMessage: async (message, options, ...items) => {
      prompts.push({ message, options, items });
      return undefined;
    },
    runWithProgress: async () => {
      progressStarts += 1;
      throw new Error('progress must not start after confirmation cancellation');
    },
    startProcess: () => {
      processStarts += 1;
      throw new Error('vba-dev must not start');
    },
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  assert.equal(result, undefined);
  assert.equal(targetResolverCalls, 1);
  assert.equal(resolverCalls, 0);
  assert.equal(processStarts, 0);
  assert.equal(progressStarts, 0);
  assert.deepEqual(output, []);
  assert.deepEqual(errors, []);
  const prompt = assertSingle(prompts);
  assert.equal(prompt.options.modal, true);
  assert.deepEqual(prompt.items, ['Export']);
  const renderedPrompt = `${prompt.message}\n${prompt.options.detail}`;
  assert.ok(renderedPrompt.includes(destinationPath));
  assert.match(renderedPrompt, /source may be overwritten/i);
  assert.match(
    renderedPrompt,
    /stale[\s\S]*\.bas[\s\S]*\.cls[\s\S]*\.frm[\s\S]*\.frx[\s\S]*deleted/i
  );
});

test('manifest target selection cancellation starts no consent, companion, progress, or process', async () => {
  let resolverCalls = 0;
  let promptCalls = 0;
  let progressStarts = 0;
  let processStarts = 0;

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => {
        resolverCalls += 1;
        throw new Error('cancelled target selection must not resolve vba-dev');
      }
    },
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'document');
      return undefined;
    },
    workspaceRoots: [],
    fileExists: async () => {
      throw new Error('palette resolver cancellation must not use legacy discovery');
    },
    findProjectManifests: async () => {
      throw new Error('palette resolver cancellation must not use legacy discovery');
    },
    chooseProject: async () => {
      throw new Error('palette resolver cancellation must not use legacy discovery');
    },
    readTextFile: async () => {
      throw new Error('palette resolver cancellation must not read a manifest');
    },
    showWarningMessage: async () => {
      promptCalls += 1;
      throw new Error('cancelled target selection must not request cleanup consent');
    },
    runWithProgress: async () => {
      progressStarts += 1;
      throw new Error('cancelled target selection must not start progress');
    },
    startProcess: () => {
      processStarts += 1;
      throw new Error('cancelled target selection must not start a process');
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });

  assert.equal(result, undefined);
  assert.equal(resolverCalls, 0);
  assert.equal(promptCalls, 0);
  assert.equal(progressStarts, 0);
  assert.equal(processStarts, 0);
});

test('cleanup export pins the destination and document that received consent', async () => {
  const projectRoot = path.resolve('test-work', 'PinnedProject');
  const confirmedDestination = path.resolve(projectRoot, 'src', 'Book2');
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  let selectedTarget = createManifestTarget(projectRoot, 'Book2');
  let targetResolverCalls = 0;
  const processCalls: Array<{ file: string; args: readonly string[] }> = [];

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: { export: { outputSchemaVersion: '1.0' } }
        },
        configuredPath: executablePath,
        bundledPath: executablePath,
        source: 'configured'
      })
    },
    activeFilePath: path.join(confirmedDestination, 'Module1.bas'),
    workspaceRoots: [path.dirname(projectRoot)],
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'document');
      targetResolverCalls += 1;
      return selectedTarget;
    },
    fileExists: async () => false,
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    readTextFile: async () => {
      throw new Error('selected target must not be re-read');
    },
    showWarningMessage: async (_message, options, ..._items) => {
      assert.ok(options.detail.includes(confirmedDestination));
      selectedTarget = createManifestTarget(projectRoot, 'Book3');
      return 'Export';
    },
    runWithProgress: (task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    startProcess: (file, args) => {
      processCalls.push({ file, args });
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });

  assert.ok(result);
  assert.equal(targetResolverCalls, 1);
  assert.deepEqual(processCalls, [{
    file: executablePath,
    args: [
      'export',
      '--project', projectRoot,
      '--document', 'Book2',
      '--to', confirmedDestination
    ]
  }]);
});

test('manifest export retains a selected non-primary target and reports it before companion resolution', async () => {
  const projectRoot = path.resolve('test-work', 'SelectedProject');
  const destinationPath = path.join(projectRoot, 'src', 'Book2');
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  const events: string[] = [];
  const processCalls: Array<{ file: string; args: readonly string[] }> = [];
  const document = {
    name: 'Book2',
    sourcePath: 'src/Book2',
    sourceRoot: destinationPath,
    sourceRootIdentity: { canonicalPath: destinationPath }
  };
  const project = {
    projectRoot,
    manifestPath: path.join(projectRoot, 'vba-project.json'),
    projectName: 'SelectedProject',
    primaryDocument: 'Book1',
    documents: [document]
  };

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => {
        events.push('companion:resolve');
        return {
          executablePath,
          capabilities: {
            toolVersion: '0.1.0',
            contractVersion: '1.0',
            commands: { export: { outputSchemaVersion: '1.0' } }
          },
          bundledPath: executablePath,
          source: 'bundled'
        };
      }
    },
    resolveCommandPaletteTarget: async (scope) => {
      assert.equal(scope, 'document');
      return { project, document };
    },
    workspaceRoots: [],
    fileExists: async () => false,
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    readTextFile: async () => {
      throw new Error('selected target must not be re-read');
    },
    showWarningMessage: async (_message, options) => {
      assert.match(options.detail, new RegExp(destinationPath.replace(/[\\]/gu, '\\\\')));
      return 'Export';
    },
    runWithProgress: (task) => task(
      {
        isCancellationRequested: false,
        onCancellationRequested: () => ({ dispose: () => undefined })
      },
      (message) => events.push(`progress:${message}`)
    ),
    startProcess: (file, args) => {
      events.push('process:start');
      processCalls.push({ file, args });
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: (value) => events.push(`output:${value}`),
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });

  assert.ok(result);
  assert.deepEqual(processCalls, [{
    file: executablePath,
    args: [
      'export',
      '--project', projectRoot,
      '--document', 'Book2',
      '--to', destinationPath
    ]
  }]);
  assert.ok(events.indexOf('output:  Document: Book2') < events.indexOf('companion:resolve'));
  assert.ok(events.indexOf(`progress:Project: SelectedProject (${projectRoot}); Document: Book2`) <
    events.indexOf('companion:resolve'));
  assert.ok(events.indexOf('companion:resolve') < events.indexOf('process:start'));
});

test('explicit workbook export without a destination skips cleanup consent', async () => {
  const workingDirectory = path.resolve('test-work', 'ExplicitExport');
  const workbookPath = path.resolve(workingDirectory, 'Book1.xlsm');
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  const processCalls: Array<{ file: string; args: readonly string[] }> = [];
  let promptCalls = 0;

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: { export: { outputSchemaVersion: '1.0' } }
        },
        configuredPath: executablePath,
        bundledPath: executablePath,
        source: 'configured'
      })
    },
    resolveCommandPaletteTarget: async () => {
      throw new Error('explicit export must not resolve a manifest target');
    },
    workspaceRoots: [],
    fileExists: async () => {
      throw new Error('explicit export must not discover a project');
    },
    findProjectManifests: async () => {
      throw new Error('explicit export must not discover a project');
    },
    chooseProject: async () => {
      throw new Error('explicit export must not choose a project');
    },
    readTextFile: async () => {
      throw new Error('explicit export must not read a project manifest');
    },
    showWarningMessage: async () => {
      promptCalls += 1;
      throw new Error('non-cleanup export must not request cleanup consent');
    },
    runWithProgress: (task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    startProcess: (file, args) => {
      processCalls.push({ file, args });
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  }, {
    mode: 'explicit',
    workingDirectory,
    workbookPath: 'Book1.xlsm'
  });

  assert.ok(result);
  assert.equal(promptCalls, 0);
  assert.deepEqual(processCalls, [{
    file: executablePath,
    args: ['export', '--from', workbookPath]
  }]);
});

test('explicit workbook export with a destination obtains exact cleanup consent', async () => {
  const workingDirectory = path.resolve('test-work', 'ExplicitCleanupExport');
  const workbookPath = path.resolve(workingDirectory, 'Book1.xlsm');
  const destinationPath = path.resolve(workingDirectory, 'snapshot');
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  const processCalls: Array<{ file: string; args: readonly string[] }> = [];
  const prompts: Array<{
    message: string;
    options: { modal: boolean; detail: string };
    items: string[];
  }> = [];

  const result = await runExportCommand({
    extensionRoot: path.resolve('extension'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: { export: { outputSchemaVersion: '1.0' } }
        },
        configuredPath: executablePath,
        bundledPath: executablePath,
        source: 'configured'
      })
    },
    resolveCommandPaletteTarget: async () => {
      throw new Error('explicit export must not resolve a manifest target');
    },
    workspaceRoots: [],
    fileExists: async () => {
      throw new Error('explicit export must not discover a project');
    },
    findProjectManifests: async () => {
      throw new Error('explicit export must not discover a project');
    },
    chooseProject: async () => {
      throw new Error('explicit export must not choose a project');
    },
    readTextFile: async () => {
      throw new Error('explicit export must not read a project manifest');
    },
    showWarningMessage: async (message, options, ...items) => {
      prompts.push({ message, options, items });
      return 'Export';
    },
    runWithProgress: (task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    startProcess: (file, args) => {
      processCalls.push({ file, args });
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  }, {
    mode: 'explicit',
    workingDirectory,
    workbookPath: 'Book1.xlsm',
    destinationPath: 'snapshot'
  });

  assert.ok(result);
  const prompt = assertSingle(prompts);
  assert.equal(prompt.options.modal, true);
  assert.deepEqual(prompt.items, ['Export']);
  const renderedPrompt = `${prompt.message}\n${prompt.options.detail}`;
  assert.ok(renderedPrompt.includes(destinationPath));
  assert.match(renderedPrompt, /source may be overwritten/i);
  assert.match(renderedPrompt, /stale[\s\S]*deleted/i);
  assert.deepEqual(processCalls, [{
    file: executablePath,
    args: ['export', '--from', workbookPath, '--to', destinationPath]
  }]);
});

function assertSingle<T>(values: readonly T[]): T {
  assert.equal(values.length, 1);
  return values[0];
}

function createManifestTarget(projectRoot: string, documentName: string) {
  const sourceRoot = path.join(projectRoot, 'src', documentName);
  const document = {
    name: documentName,
    sourcePath: `src/${documentName}`,
    sourceRoot,
    sourceRootIdentity: { canonicalPath: sourceRoot }
  };
  return {
    project: {
      projectRoot,
      manifestPath: path.join(projectRoot, 'vba-project.json'),
      projectName: path.basename(projectRoot),
      primaryDocument: 'Book1',
      documents: [document]
    },
    document
  };
}
