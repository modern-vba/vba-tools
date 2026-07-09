import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  CommonModulesCommandOptions,
  appendFormattedCommonModulesList,
  parseCommonModulesList,
  runCommonModulesAddCommand,
  runCommonModulesListCommand,
  runCommonModulesUpdateCommand
} from './commonModulesCommand';

test('CommonModules add command invokes CLI add and list with explicit project root', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];

  const result = await runCommonModulesAddCommand(
    createOptions({
      projectRoot,
      calls,
      output,
      startStdout: (args) => {
        if (args[1] === 'list') {
          return JSON.stringify({
            document: 'Book1',
            commonModules: [
              { name: 'Base', requested: false },
              { name: 'Feature', requested: true }
            ]
          });
        }

        return 'Copied Feature.bas\n';
      }
    }),
    ['Feature']
  );

  assert.ok(result);
  assert.equal(result.projectRoot, projectRoot);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['common-module', 'add', 'Feature', '--project', projectRoot],
    ['common-module', 'list', '--project', projectRoot, '--format', 'json']
  ]);
  assert.deepEqual(result.commonModulesList?.commonModules, [
    { name: 'Base', requested: false },
    { name: 'Feature', requested: true }
  ]);
  assert.match(output.join(''), /CommonModules for Book1/);
  assert.match(output.join(''), /Base \(dependency\)/);
  assert.match(output.join(''), /Feature \(requested\)/);
});

test('CommonModules update command invokes CLI update and displays installed modules', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];

  await runCommonModulesUpdateCommand(createOptions({
    projectRoot,
    calls,
    output,
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        document: 'Book1',
        commonModules: [
          { name: 'Feature', requested: true }
        ]
      })
      : 'Updated Book1/Feature.bas\n'
  }));

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['common-module', 'update', '--project', projectRoot],
    ['common-module', 'list', '--project', projectRoot, '--format', 'json']
  ]);
  assert.match(output.join(''), /Feature \(requested\)/);
});

test('CommonModules list command uses selected project arguments and output channel', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];

  await runCommonModulesListCommand(createOptions({
    projectRoot,
    calls,
    output,
    startStdout: () => JSON.stringify({
      document: 'Book1',
      commonModules: []
    })
  }));

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['common-module', 'list', '--project', projectRoot, '--format', 'json']
  ]);
  assert.match(output.join(''), /CommonModules for Book1/);
  assert.match(output.join(''), /\(none\)/);
});

test('CommonModules display formats requested roots separately from dependencies', () => {
  const output: string[] = [];
  const list = parseCommonModulesList(JSON.stringify({
    document: 'Book1',
    commonModules: [
      { name: 'Base', requested: false },
      { name: 'Feature', requested: true }
    ]
  }));

  appendFormattedCommonModulesList({
    append: (value) => output.push(value),
    appendLine: (value) => output.push(`${value}\n`),
    show: () => undefined
  }, list);

  assert.match(output.join(''), /Base \(dependency\)/);
  assert.match(output.join(''), /Feature \(requested\)/);
});

function createOptions(
  options: {
    projectRoot: string;
    calls: Array<{ file: string; args: readonly string[] }>;
    output: string[];
    startStdout: (args: readonly string[]) => string;
  }
): CommonModulesCommandOptions {
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
            'common-module add': { outputSchemaVersion: '1.0' },
            'common-module list': { outputSchemaVersion: '1.0' },
            'common-module update': { outputSchemaVersion: '1.0' }
          }
        }),
        stderr: ''
      };
    },
    startProcess: (file, args) => {
      options.calls.push({ file, args });
      return {
        onStdout: (listener) => listener(options.startStdout(args)),
        onStderr: (listener) => listener(''),
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: (value) => options.output.push(value),
      appendLine: (value) => options.output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async () => undefined,
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        'common-module add': '1.0',
        'common-module list': '1.0',
        'common-module update': '1.0'
      }
    }
  };
}
