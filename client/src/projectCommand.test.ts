import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { VbaDevCompatibilityError, VbaDevSessionResolver } from './devtool';
import { runWorkbookBackedProjectCommand } from './projectCommand';

for (const commandName of ['build', 'test', 'publish'] as const) {
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
      fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
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
            },
            debugAdapter: {
              protocolVersion: '1.0',
              transport: 'stdio',
              command: 'debug-adapter'
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
    configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    capabilitiesProcess: async () => ({
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '1.0',
        commands: {
          build: { outputSchemaVersion: '1.0' }
        },
        debugAdapter: {
          protocolVersion: '1.0',
          transport: 'stdio',
          command: 'debug-adapter'
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

test('WorkbookBackedProject commands reuse one session-pinned vba-dev executable', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const capabilityCalls: string[] = [];
  const commandCalls: string[] = [];
  const requiredContract = {
    contractVersion: '1.0',
    commandSchemaVersions: {
      build: '1.0'
    }
  };
  const vbaDevResolver = new VbaDevSessionResolver({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    configuredPath: executablePath,
    requiredContract,
    runProcess: async (file) => {
      capabilityCalls.push(file);
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            build: { outputSchemaVersion: '1.0' }
          },
          debugAdapter: {
            protocolVersion: '1.0',
            transport: 'stdio',
            command: 'debug-adapter'
          }
        }),
        stderr: ''
      };
    }
  });
  const options = {
    toolCommandName: 'build' as const,
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver,
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate: string) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    startProcess: (file: string) => {
      commandCalls.push(file);
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: (listener: (exitCode: number, signal: string | null) => void) => listener(0, null),
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async () => undefined,
    requiredContract
  };

  await runWorkbookBackedProjectCommand(options);
  await runWorkbookBackedProjectCommand(options);

  assert.deepEqual(capabilityCalls, [executablePath]);
  assert.deepEqual(commandCalls, [executablePath, executablePath]);
});

test('WorkbookBackedProject command stops without another notification after a reported resolution failure', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  let processStarts = 0;
  const notifications: string[] = [];

  const result = await runWorkbookBackedProjectCommand({
    toolCommandName: 'build',
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => {
        throw new VbaDevCompatibilityError('no compatible vba-dev', true);
      }
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    startProcess: () => {
      processStarts += 1;
      throw new Error('Build must not start');
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    }
  });

  assert.equal(result, undefined);
  assert.equal(processStarts, 0);
  assert.deepEqual(notifications, []);
});

function toTitle(commandName: string): string {
  return commandName[0].toUpperCase() + commandName.slice(1);
}
