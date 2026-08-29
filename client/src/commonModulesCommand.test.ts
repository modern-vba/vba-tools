import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  CommonModulesCommandOptions,
  appendFormattedCommonModulesList,
  parseCommonModuleNamesInput,
  parseCommonModulesList,
  runCommonModulesAddCommand,
  runCommonModulesListCommand,
  runCommonModulesUpdateCommand
} from './commonModulesCommand';

test('CommonModules prompt parsing uses exact MS-VBAL whitespace and preserves CP2 names', () => {
  assert.deepEqual(parseCommonModuleNamesInput('\u00A0\u3000Feature'), ['\u00A0', 'Feature']);
});

test('CommonModules add command preserves exact CP2 names through the CLI boundary', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const routeEvents: string[] = [];

  const result = await runCommonModulesAddCommand(
    createOptions({
      projectRoot,
      calls,
      output,
      routeEvents,
      documentName: 'Book2',
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
    ['Feature', '\u00A0']
  );

  assert.ok(result);
  assert.equal(result.projectRoot, projectRoot);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['common-module', 'add', 'Feature', '\u00A0', '--project', projectRoot, '--document', 'Book2'],
    ['common-module', 'list', '--project', projectRoot, '--document', 'Book2', '--format', 'json']
  ]);
  assert.deepEqual(result.commonModulesList?.commonModules, [
    { name: 'Base', requested: false },
    { name: 'Feature', requested: true }
  ]);
  assert.match(output.join(''), /CommonModules for Book1/);
  assert.match(output.join(''), /Base \(dependency\)/);
  assert.match(output.join(''), /Feature \(requested\)/);
  assert.deepEqual(routeEvents, [
    'mutation:start:Common Module Add:BookProject:Book2',
    'process:common-module add',
    'mutation:complete:Common Module Add',
    'basis:Common Module List:BookProject:Book2',
    'process:common-module list'
  ]);
});

test('CommonModules update command remains project-scoped without an implicit document list', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const routeEvents: string[] = [];

  await runCommonModulesUpdateCommand(createOptions({
    projectRoot,
    calls,
    output,
    routeEvents,
    documentName: 'Book2',
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
    ['common-module', 'update', '--project', projectRoot]
  ]);
  assert.doesNotMatch(output.join(''), /CommonModules for/);
  assert.deepEqual(routeEvents, [
    'mutation:start:Common Module Update:BookProject:(project)',
    'process:common-module update',
    'mutation:complete:Common Module Update'
  ]);
});

test('CommonModules mutation success remains authoritative after cancellation without starting list', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  let cancellationRequested = false;
  let cancelListener: (() => void) | undefined;
  let closeListener: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    signalStarted = resolve;
  });
  const running = runCommonModulesAddCommand(createOptions({
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
  }), ['Feature']);

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
      'common-module',
      'add',
      'Feature',
      '--project',
      projectRoot,
      '--document',
      'Book2',
      '--cancellation-transport',
      'stdin-v1'
    ]
  ]);
});

test('CommonModules target selection cancellation starts no companion or mutation', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const targetScopes: string[] = [];

  const result = await runCommonModulesAddCommand(createOptions({
    projectRoot,
    calls,
    output: [],
    startStdout: () => {
      throw new Error('cancelled target selection must not start a command');
    },
    cancelTargetSelection: true,
    targetScopes
  }), ['Feature']);

  assert.equal(result, undefined);
  assert.deepEqual(targetScopes, ['document']);
  assert.deepEqual(calls, []);
});

test('CommonModules mutation rejection launches no mutation or follow-up list', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const routeEvents: string[] = [];

  const result = await runCommonModulesAddCommand(createOptions({
    projectRoot,
    calls,
    output: [],
    startStdout: () => '',
    rejectMutation: true,
    routeEvents
  }), ['Feature']);

  assert.equal(result, undefined);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json']
  ]);
  assert.deepEqual(routeEvents, [
    'mutation:start:Common Module Add:BookProject:Book2',
    'mutation:rejected:Common Module Add'
  ]);
});

test('CommonModules list command uses selected project arguments and output channel', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const routeEvents: string[] = [];

  await runCommonModulesListCommand(createOptions({
    projectRoot,
    calls,
    output,
    routeEvents,
    documentName: 'Book2',
    startStdout: () => JSON.stringify({
      document: 'Book1',
      commonModules: []
    })
  }));

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['common-module', 'list', '--project', projectRoot, '--document', 'Book2', '--format', 'json']
  ]);
  assert.match(output.join(''), /CommonModules for Book1/);
  assert.match(output.join(''), /\(none\)/);
  assert.deepEqual(routeEvents, [
    'basis:Common Module List:BookProject:Book2',
    'process:common-module list'
  ]);
});

test('CommonModules command refreshes project diagnostics from failed command output', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const diagnosticRefreshes: Array<{ scopeKey: string; output: string }> = [];
  const stderr = JSON.stringify({
    type: 'diagnostic',
    owner: 'vba-dev',
    severity: 'error',
    uri: path.join(projectRoot, 'vba-project.json'),
    range: {
      start: { line: 0, character: 0 },
      end: { line: 0, character: 1 }
    },
    message: 'CommonModuleName is unknown.',
    code: 'VBACOMMON001'
  });

  await runCommonModulesUpdateCommand(createOptions({
    projectRoot,
    calls: [],
    output: [],
    startStdout: () => '',
    startStderr: () => stderr,
    startExitCode: () => 2,
    diagnosticRefreshes
  }));

  assert.deepEqual(diagnosticRefreshes, [
    {
      scopeKey: `project:${projectRoot}`,
      output: stderr
    }
  ]);
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
    startStderr?: (args: readonly string[]) => string;
    startExitCode?: (args: readonly string[]) => number;
    diagnosticRefreshes?: Array<{ scopeKey: string; output: string }>;
    advertiseStdinCancellation?: boolean;
    documentName?: string;
    cancelTargetSelection?: boolean;
    targetScopes?: string[];
    rejectMutation?: boolean;
    routeEvents?: string[];
    cancellationToken?: CommonModulesCommandOptions['cancellationToken'];
    startProcess?: NonNullable<CommonModulesCommandOptions['startProcess']>;
  }
): CommonModulesCommandOptions {
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
            'common-module add': { outputSchemaVersion: '1.0' },
            'common-module list': { outputSchemaVersion: '1.0' },
            'common-module update': { outputSchemaVersion: '1.0' }
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
    showErrorMessage: async () => undefined,
    cancellationToken: options.cancellationToken,
    requiredContract: {
      contractVersion: '1.0',
      featureVersions: options.advertiseStdinCancellation
        ? { 'invocation.stdinCancellation': '1.0' }
        : undefined,
      commandSchemaVersions: {
        'common-module add': '1.0',
        'common-module list': '1.0',
        'common-module update': '1.0'
      }
    }
  };
}
