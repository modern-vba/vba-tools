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
  const routeEvents: string[] = [];

  const result = await runReferenceListCommand(createOptions({
    projectRoot,
    calls,
    output,
    routeEvents,
    documentName: 'Book2',
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
    ['reference', 'list', '--project', projectRoot, '--document', 'Book2', '--format', 'json']
  ]);
  assert.equal(result.referenceList?.document, 'Book1');
  assert.match(output.join(''), /References for Book1/);
  assert.match(output.join(''), /Microsoft Scripting Runtime/);
  assert.deepEqual(routeEvents, [
    'basis:Reference List:BookProject:Book2',
    'process:reference list'
  ]);
});

test('Reference add command uses a human-visible description name', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const routeEvents: string[] = [];

  await runReferenceAddCommand(
    createOptions({
      projectRoot,
      calls,
      output,
      routeEvents,
      documentName: 'Book2',
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
    ['reference', 'add', 'Microsoft Scripting Runtime', '--project', projectRoot, '--document', 'Book2'],
    ['reference', 'list', '--project', projectRoot, '--document', 'Book2', '--format', 'json']
  ]);
  assert.match(output.join(''), /Added Book1\/Microsoft Scripting Runtime/);
  assert.match(output.join(''), /References for Book1/);
  assert.deepEqual(routeEvents, [
    'mutation:start:Reference Add:BookProject:Book2',
    'process:reference add',
    'mutation:complete:Reference Add',
    'basis:Reference List:BookProject:Book2',
    'process:reference list'
  ]);
});

test('Reference remove command targets manifest-defined reference entries', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];

  await runReferenceRemoveCommand(
    createOptions({
      projectRoot,
      calls,
      output: [],
      documentName: 'Book2',
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
    ['reference', 'remove', 'Microsoft Scripting Runtime', '--project', projectRoot, '--document', 'Book2'],
    ['reference', 'list', '--project', projectRoot, '--document', 'Book2', '--format', 'json']
  ]);
});

test('Reference mutation success remains authoritative after cancellation without starting list', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  let cancellationRequested = false;
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  const running = runReferenceAddCommand(createOptions({
    projectRoot,
    calls,
    output: [],
    startStdout: () => '',
    advertiseStdinCancellation: true,
    cancellationToken: {
      get isCancellationRequested() {
        return cancellationRequested;
      },
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    },
    startProcess: (file, args) => {
      calls.push({ file, args });
      signalStarted?.();
      return {
        onStdout: () => undefined,
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          closeListener = listener;
        },
        requestCancellation: async () => undefined,
        kill: () => undefined
      };
    }
  }), 'Microsoft Scripting Runtime');

  await started;
  cancellationRequested = true;
  cancelListener?.();
  closeListener?.(0, null);
  const result = await running;

  assert.ok(result);
  assert.equal(result.exitCode, 0);
  assert.equal(result.cancelled, false);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    [
      'reference',
      'add',
      'Microsoft Scripting Runtime',
      '--project',
      projectRoot,
      '--document',
      'Book2',
      '--cancellation-transport',
      'stdin-v1'
    ]
  ]);
});

test('Reference target selection cancellation starts no companion or mutation', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const targetScopes: string[] = [];

  const result = await runReferenceAddCommand(createOptions({
    projectRoot,
    calls,
    output: [],
    startStdout: () => {
      throw new Error('cancelled target selection must not start a command');
    },
    cancelTargetSelection: true,
    targetScopes
  }), 'Microsoft Scripting Runtime');

  assert.equal(result, undefined);
  assert.deepEqual(targetScopes, ['document']);
  assert.deepEqual(calls, []);
});

test('Reference mutation rejection launches no mutation or follow-up list', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const routeEvents: string[] = [];

  const result = await runReferenceRemoveCommand(createOptions({
    projectRoot,
    calls,
    output: [],
    startStdout: () => '',
    rejectMutation: true,
    routeEvents
  }), 'Microsoft Scripting Runtime');

  assert.equal(result, undefined);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json']
  ]);
  assert.deepEqual(routeEvents, [
    'mutation:start:Reference Remove:BookProject:Book2',
    'mutation:rejected:Reference Remove'
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
    advertiseStdinCancellation?: boolean;
    documentName?: string;
    cancelTargetSelection?: boolean;
    targetScopes?: string[];
    rejectMutation?: boolean;
    routeEvents?: string[];
    cancellationToken?: ReferenceCommandOptions['cancellationToken'];
    startProcess?: NonNullable<ReferenceCommandOptions['startProcess']>;
  },
  errors: string[] = []
): ReferenceCommandOptions {
  const documentName = options.documentName ?? 'Book2';
  return {
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
    activeFilePath: path.join(options.projectRoot, 'src', 'Book1', 'Module1.bas'),
    workspaceRoots: [path.dirname(options.projectRoot)],
    fileExists: async (candidate) => candidate === path.join(options.projectRoot, 'vba-project.json'),
    findProjectManifests: async () => [],
    chooseProject: async () => undefined,
    resolveCommandPaletteTarget: async (scope) => {
      options.targetScopes?.push(scope);
      if (options.cancelTargetSelection === true) {
        return undefined;
      }
      const document = {
        name: documentName,
        sourcePath: `src/${documentName}`,
        sourceRoot: path.join(options.projectRoot, 'src', documentName),
        sourceRootIdentity: {
          canonicalPath: path.join(options.projectRoot, 'src', documentName)
        }
      };
      const project = {
        projectRoot: options.projectRoot,
        manifestPath: path.join(options.projectRoot, 'vba-project.json'),
        projectName: 'BookProject',
        primaryDocument: 'Book1',
        documents: [document]
      };
      return scope === 'document' ? { project, document } : { project };
    },
    projectManifestMutationCoordinator: {
      run: async ({ command, target, run }) => {
        options.routeEvents?.push(
          `mutation:start:${command}:${target.project.projectName}:${target.document?.name ?? '(project)'}`
        );
        if (options.rejectMutation === true) {
          options.routeEvents?.push(`mutation:rejected:${command}`);
          return { status: 'rejected', reason: 'preflight' };
        }
        const processResult = await run();
        options.routeEvents?.push(`mutation:complete:${command}`);
        return {
          status: 'completed',
          manifestOutcome: 'unchanged',
          coherence: 'notRequired',
          processResult
        };
      },
      reportReadOnlyDiskBasis: async ({ command, target }) => {
        options.routeEvents?.push(
          `basis:${command}:${target.project.projectName}:${target.document?.name ?? '(project)'}`
        );
        return false;
      }
    },
    capabilitiesProcess: async (file, args) => {
      options.calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          featureVersions: options.advertiseStdinCancellation
            ? { 'invocation.stdinCancellation': '1.0' }
            : undefined,
          commands: {
            'reference add': { outputSchemaVersion: '1.0' },
            'reference list': { outputSchemaVersion: '1.0' },
            'reference remove': { outputSchemaVersion: '1.0' }
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
    startProcess: options.startProcess ?? ((file, args) => {
      options.calls.push({ file, args });
      options.routeEvents?.push(`process:${args[0]} ${args[1]}`);
      return {
        onStdout: (listener) => listener(options.startStdout(args)),
        onStderr: (listener) => listener(options.startStderr?.(args) ?? ''),
        onExit: (listener) => listener(options.startExitCode?.(args) ?? 0, null),
        kill: () => undefined
      };
    }),
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
    cancellationToken: options.cancellationToken,
    forceKillAfterCancellationMilliseconds: 100,
    requiredContract: {
      contractVersion: '1.0',
      featureVersions: options.advertiseStdinCancellation
        ? { 'invocation.stdinCancellation': '1.0' }
        : undefined,
      commandSchemaVersions: {
        'reference add': '1.0',
        'reference list': '1.0',
        'reference remove': '1.0'
      }
    }
  };
}
