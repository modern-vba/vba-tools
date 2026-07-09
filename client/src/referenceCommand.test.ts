import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  ReferenceCommandOptions,
  appendFormattedReferenceList,
  parseReferenceList,
  runReferenceAddCommand,
  runReferenceListCommand,
  runReferenceRemoveCommand
} from './referenceCommand';

test('Reference list command invokes CLI list with explicit project root and maps output', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];

  const result = await runReferenceListCommand(createOptions({
    projectRoot,
    calls,
    output,
    startStdout: () => JSON.stringify({
      document: 'Book1',
      references: [
        { name: 'Microsoft Scripting Runtime' }
      ]
    })
  }));

  assert.ok(result);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['reference', 'list', '--project', projectRoot, '--format', 'json']
  ]);
  assert.equal(result.referenceList?.document, 'Book1');
  assert.match(output.join(''), /References for Book1/);
  assert.match(output.join(''), /Microsoft Scripting Runtime/);
});

test('Reference add command uses a human-visible description name', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];

  await runReferenceAddCommand(
    createOptions({
      projectRoot,
      calls,
      output,
      startStdout: (args) => args[1] === 'list'
        ? JSON.stringify({
          document: 'Book1',
          references: [
            { name: 'Microsoft Scripting Runtime' }
          ]
        })
        : 'Added Book1/Microsoft Scripting Runtime\n'
    }),
    'Microsoft Scripting Runtime'
  );

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['reference', 'add', 'Microsoft Scripting Runtime', '--project', projectRoot],
    ['reference', 'list', '--project', projectRoot, '--format', 'json']
  ]);
  assert.match(output.join(''), /Added Book1\/Microsoft Scripting Runtime/);
  assert.match(output.join(''), /References for Book1/);
});

test('Reference remove command targets manifest-defined reference entries', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];

  await runReferenceRemoveCommand(
    createOptions({
      projectRoot,
      calls,
      output: [],
      startStdout: (args) => args[1] === 'list'
        ? JSON.stringify({
          document: 'Book1',
          references: []
        })
        : 'Removed Book1/Microsoft Scripting Runtime\n'
    }),
    'Microsoft Scripting Runtime'
  );

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['reference', 'remove', 'Microsoft Scripting Runtime', '--project', projectRoot],
    ['reference', 'list', '--project', projectRoot, '--format', 'json']
  ]);
});

test('Reference commands report a missing input name before invoking CLI', async () => {
  const errors: string[] = [];
  const result = await runReferenceAddCommand(
    createOptions({
      projectRoot: path.join('C:', 'work', 'BookProject'),
      calls: [],
      output: [],
      startStdout: () => ''
    }, errors),
    '   '
  );

  assert.equal(result, undefined);
  assert.deepEqual(errors, ['Reference name is required.']);
});

test('Reference commands surface ambiguous or missing CLI resolution errors', async () => {
  const errors: string[] = [];
  const diagnosticRefreshes: Array<{ scopeKey: string; output: string }> = [];
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const stderr = "VbaProjectReference 'Ambiguous Library' is ambiguous.\n";

  await runReferenceAddCommand(
    createOptions({
      projectRoot,
      calls: [],
      output: [],
      startExitCode: () => 2,
      startStdout: () => '',
      startStderr: () => stderr,
      diagnosticRefreshes
    }, errors),
    'Ambiguous Library'
  );

  assert.deepEqual(errors, [
    'Reference command failed. See the VBA Tools output for details.'
  ]);
  assert.deepEqual(diagnosticRefreshes, [
    {
      scopeKey: `project:${projectRoot}`,
      output: stderr
    }
  ]);
});

test('Reference display includes the selected document scope', () => {
  const output: string[] = [];
  const list = parseReferenceList(JSON.stringify({
    document: 'Book1',
    references: [
      { name: 'Microsoft Scripting Runtime' }
    ]
  }));

  appendFormattedReferenceList({
    append: (value) => output.push(value),
    appendLine: (value) => output.push(`${value}\n`),
    show: () => undefined
  }, list);

  assert.match(output.join(''), /References for Book1/);
  assert.match(output.join(''), /Microsoft Scripting Runtime/);
});

function createOptions(
  options: {
    projectRoot: string;
    calls: Array<{ file: string; args: readonly string[] }>;
    output: string[];
    startStdout: (args: readonly string[]) => string;
    startStderr?: (args: readonly string[]) => string;
    startExitCode?: (args: readonly string[]) => number;
    diagnosticRefreshes?: Array<{ scopeKey: string; output: string }>;
  },
  errors: string[] = []
): ReferenceCommandOptions {
  return {
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-devtool.exe'),
    activeFilePath: path.join(options.projectRoot, 'src', 'Book1', 'Module1.bas'),
    workspaceRoots: [path.dirname(options.projectRoot)],
    fileExists: async (candidate) => candidate === path.join(options.projectRoot, 'project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    capabilitiesProcess: async (file, args) => {
      options.calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            'reference add': { outputSchemaVersion: '1.0' },
            'reference list': { outputSchemaVersion: '1.0' },
            'reference remove': { outputSchemaVersion: '1.0' }
          }
        }),
        stderr: ''
      };
    },
    startProcess: (file, args) => {
      options.calls.push({ file, args });
      return {
        onStdout: (listener) => listener(options.startStdout(args)),
        onStderr: (listener) => listener(options.startStderr?.(args) ?? ''),
        onExit: (listener) => listener(options.startExitCode?.(args) ?? 0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => options.output.push(value),
      appendLine: (value) => options.output.push(`${value}\n`),
      show: () => undefined
    },
    diagnosticReporter: options.diagnosticRefreshes
      ? {
        refresh: (scopeKey, value) => {
          options.diagnosticRefreshes?.push({ scopeKey, output: value });
          return [];
        }
      }
      : undefined,
    showErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        'reference add': '1.0',
        'reference list': '1.0',
        'reference remove': '1.0'
      }
    }
  };
}
