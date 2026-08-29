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
      resolveCommandPaletteTarget: async (scope) => {
        assert.equal(scope, 'document');
        const document = {
          name: 'Book2',
          sourcePath: 'src/Book2',
          sourceRoot: path.join(projectRoot, 'src', 'Book2'),
          sourceRootIdentity: {
            canonicalPath: path.join(projectRoot, 'src', 'Book2')
          }
        };
        return {
          project: {
            projectRoot,
            manifestPath: path.join(projectRoot, 'vba-project.json'),
            projectName: 'BookProject',
            primaryDocument: 'Book1',
            documents: [document]
          },
          document
        };
      },
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
      showWarningMessage: async () => undefined,
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
      [commandName, '--project', projectRoot, '--document', 'Book2']
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

test('managed build opts into the verified stdin cancellation transport', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  const processArguments: Array<readonly string[]> = [];

  const result = await runWorkbookBackedProjectCommand({
    toolCommandName: 'build',
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {
            build: { outputSchemaVersion: '1.0' }
          }
        },
        bundledPath: executablePath,
        source: 'bundled'
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
    startProcess: (_file, args) => {
      processArguments.push(args);
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
    showWarningMessage: async () => undefined,
    showErrorMessage: async () => undefined
  });

  assert.ok(result);
  assert.deepEqual(processArguments, [[
    'build',
    '--project',
    projectRoot,
    '--document',
    'Book2',
    '--cancellation-transport',
    'stdin-v1'
  ]]);
});

test('trusted successful build reports one cancellation delivery warning', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  let resolveWarningSelection: ((action: string | undefined) => void) | undefined;
  const warningSelection = new Promise<string | undefined>((resolve) => {
    resolveWarningSelection = resolve;
  });
  const warnings: Array<{ message: string; items: readonly string[] }> = [];
  const outputShows: Array<boolean | undefined> = [];
  const running = runWorkbookBackedProjectCommand({
    toolCommandName: 'build',
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {
            build: { outputSchemaVersion: '1.0' }
          }
        },
        bundledPath: executablePath,
        source: 'bundled'
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => {
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => {
          throw new Error('write EPIPE');
        },
        kill: () => undefined
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: (preserveFocus) => outputShows.push(preserveFocus)
    },
    showWarningMessage: (message, ...items) => {
      warnings.push({ message, items });
      return warningSelection;
    },
    showErrorMessage: async () => undefined
  });

  await started;
  cancelListener?.();
  await new Promise<void>((resolve) => setImmediate(resolve));
  closeListener?.(0, null);
  let commandSettled = false;
  void running.then(() => {
    commandSettled = true;
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  const settledBeforeWarningSelection = commandSettled;
  resolveWarningSelection?.('Show Output');
  const result = await running;
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.ok(result);
  assert.equal(settledBeforeWarningSelection, true);
  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.equal(result.cancellationRequestDelivered, false);
  assert.deepEqual(warnings, [{
    message: 'Build completed. Cancellation request could not be delivered.',
    items: ['Show Output']
  }]);
  assert.deepEqual(outputShows, [true, undefined]);
});

test('managed build escalates after its cooperative cancellation grace period', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const executablePath = path.join('D:', 'tools', 'vba-dev.exe');
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  let cancellationRequests = 0;
  let kills = 0;
  const errors: string[] = [];
  const running = runWorkbookBackedProjectCommand({
    toolCommandName: 'build',
    title: 'VBA Tools: Build',
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    vbaDevResolver: {
      resolve: async () => ({
        executablePath,
        capabilities: {
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: {
            'invocation.stdinCancellation': '1.0'
          },
          commands: {
            build: { outputSchemaVersion: '1.0' }
          }
        },
        bundledPath: executablePath,
        source: 'bundled'
      })
    },
    activeFilePath: path.join(projectRoot, 'vba-project.json'),
    workspaceRoots: [path.dirname(projectRoot)],
    fileExists: async (candidate) => candidate === path.join(projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
    forceKillAfterCancellationMilliseconds: 0,
    cancellationToken: {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: () => {
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => {
          cancellationRequests += 1;
        },
        kill: () => {
          kills += 1;
        }
      };
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showWarningMessage: async () => undefined,
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  await started;
  cancelListener?.();
  assert.equal(cancellationRequests, 1);
  assert.equal(kills, 0);
  await new Promise<void>((resolve) => setTimeout(resolve, 10));
  assert.equal(kills, 1);

  closeListener?.(null, 'SIGTERM');
  const result = await running;
  assert.ok(result);
  assert.equal(result.exitCode, 1);
  assert.equal(result.cancelled, false);
  assert.deepEqual(errors, [
    'Build failed. See the VBA Tools output for details.'
  ]);
});

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
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
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
    showWarningMessage: async () => undefined,
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
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
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
    showWarningMessage: async () => undefined,
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
    resolveCommandPaletteTarget: createDocumentTargetResolver(projectRoot),
    startProcess: () => {
      processStarts += 1;
      throw new Error('Build must not start');
    },
    outputChannel: {
      append: () => undefined,
      appendLine: () => undefined,
      show: () => undefined
    },
    showWarningMessage: async () => undefined,
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

function createDocumentTargetResolver(projectRoot: string) {
  return async () => {
    const document = {
      name: 'Book2',
      sourcePath: 'src/Book2',
      sourceRoot: path.join(projectRoot, 'src', 'Book2'),
      sourceRootIdentity: {
        canonicalPath: path.join(projectRoot, 'src', 'Book2')
      }
    };
    return {
      project: {
        projectRoot,
        manifestPath: path.join(projectRoot, 'vba-project.json'),
        projectName: 'BookProject',
        primaryDocument: 'Book1',
        documents: [document]
      },
      document
    };
  };
}
