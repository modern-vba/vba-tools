import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { runExportCommand } from './exportCommand';

test('cleanup export cancellation obtains exact consent before resolving vba-dev', async () => {
  const projectRoot = path.resolve('test-work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const destinationPath = path.resolve(projectRoot, 'src', 'Book1');
  let resolverCalls = 0;
  let processStarts = 0;
  let progressStarts = 0;
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
    fileExists: async (candidate) => path.normalize(candidate) === path.normalize(manifestPath),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    readTextFile: async (candidate) => {
      assert.equal(path.normalize(candidate), path.normalize(manifestPath));
      return JSON.stringify({
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
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  assert.equal(result, undefined);
  assert.equal(resolverCalls, 0);
  assert.equal(processStarts, 0);
  assert.equal(progressStarts, 0);
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

test('cleanup export pins the destination and document that received consent', async () => {
  const projectRoot = path.resolve('test-work', 'PinnedProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const confirmedDestination = path.resolve(projectRoot, 'src', 'Book1');
  const executablePath = path.resolve('tools', 'vba-dev.exe');
  let manifestSourcePath = path.join('src', 'Book1');
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
    fileExists: async (candidate) => path.normalize(candidate) === path.normalize(manifestPath),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    readTextFile: async () => JSON.stringify({
      schemaVersion: 1,
      projectName: 'PinnedProject',
      primaryDocument: 'book1',
      documents: {
        Book1: {
          kind: 'excel',
          sourcePath: manifestSourcePath,
          templatePath: 'src/Book1/Book1.xlsm',
          binPath: 'bin/Book1.xlsm',
          publishPath: 'publish/Book1.xlsm',
          commonModules: [],
          references: []
        }
      }
    }),
    showWarningMessage: async (_message, _options, ..._items) => {
      manifestSourcePath = path.join('other', 'ChangedAfterConsent');
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
  assert.deepEqual(processCalls, [{
    file: executablePath,
    args: [
      'export',
      '--project', projectRoot,
      '--document', 'Book1',
      '--to', confirmedDestination
    ]
  }]);
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
