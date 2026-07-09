import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { runWorkbookBackedProjectCommand } from './projectCommand';

for (const commandName of ['build', 'test', 'publish', 'export'] as const) {
  test(`WorkbookBackedProject command invokes ${commandName} with explicit project root`, async () => {
    const projectRoot = path.join('C:', 'work', 'BookProject');
    const calls: Array<{ file: string; args: readonly string[] }> = [];
    const output: string[] = [];
    const diagnosticRefreshes: Array<{ scopeKey: string; output: string }> = [];

    const result = await runWorkbookBackedProjectCommand({
      toolCommandName: commandName,
      title: `VBA Tools: ${toTitle(commandName)}`,
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
      activeFilePath: path.join(projectRoot, 'src', 'Book1', 'Module1.bas'),
      workspaceRoots: [path.dirname(projectRoot)],
      fileExists: async (candidate) => candidate === path.join(projectRoot, 'project.json'),
      findProjectManifests: async () => [],
      chooseProject: async () => undefined,
      capabilitiesProcess: async (file, args) => {
        calls.push({ file, args });
        return {
          stdout: JSON.stringify({
            toolVersion: '0.1.0',
            contractVersion: '1.0',
            commands: {
              [commandName]: { outputSchemaVersion: '1.0' }
            }
          }),
          stderr: ''
        };
      },
      startProcess: (file, args) => {
        calls.push({ file, args });
        return {
          onStdout: (listener) => listener(`${commandName} output\n`),
          onStderr: (listener) => listener(''),
          onExit: (listener) => listener(0, null),
          kill: () => undefined
        };
      },
      outputChannel: {
        append: (value) => output.push(value),
        appendLine: (value) => output.push(`${value}\n`),
        show: () => undefined
      },
      diagnosticReporter: {
        refresh: (scopeKey, value) => {
          diagnosticRefreshes.push({ scopeKey, output: value });
          return [];
        }
      },
      showErrorMessage: async () => undefined,
      requiredContract: {
        contractVersion: '1.0',
        commandSchemaVersions: {
          [commandName]: '1.0'
        }
      }
    });

    assert.ok(result);
    assert.equal(result.projectRoot, projectRoot);
    assert.deepEqual(calls.map((call) => call.args), [
      ['capabilities', '--format', 'json'],
      [commandName, '--project', projectRoot]
    ]);
    assert.equal(calls.some((call) => call.args.includes('common-module') || call.args.includes('restore')), false);
    assert.match(output.join(''), new RegExp(`${commandName} output`));
    assert.deepEqual(diagnosticRefreshes, [
      {
        scopeKey: `project:${projectRoot}`,
        output: `${commandName} output\n`
      }
    ]);
  });
}

test('WorkbookBackedProject command failure is surfaced to the user', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const errors: string[] = [];

  await runWorkbookBackedProjectCommand({
    toolCommandName: 'build',
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    activeFilePath: path.join(projectRoot, 'project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '1.0',
        commands: {
          build: { outputSchemaVersion: '1.0' }
        }
      }),
      stderr: ''
    }),
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: (listener) => listener('build failed\n'),
      onExit: (listener) => listener(1, null),
      kill: () => undefined
    }),
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        build: '1.0'
      }
    }
  });

  assert.match(errors[0], /Build failed/);
});

function toTitle(commandName: string): string {
  return commandName[0].toUpperCase() + commandName.slice(1);
}
