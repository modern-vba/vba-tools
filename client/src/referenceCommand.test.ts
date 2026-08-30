import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  ReferenceCommandOptions,
  ReferenceQuickPickWorkflowOptions,
  appendFormattedReferenceList,
  parseReferenceList,
  runReferenceListCommand,
  runReferenceQuickPickWorkflow
} from './referenceCommand';

test('ReferenceAddQuickPick retains one exact target through discovery and one atomic mutation', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const outputShows: boolean[] = [];
  const routeEvents: string[] = [];
  const targetScopes: string[] = [];
  const information: string[] = [];
  const warnings: string[] = [];
  const errors: string[] = [];
  let progressTitle: string | undefined;
  const base = createOptions({
    projectRoot,
    calls,
    output,
    outputShows,
    routeEvents,
    targetScopes,
    documentName: 'Book2',
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'available',
        complete: true,
        warnings: [],
        references: [
          {
            name: 'Alpha Library',
            status: 'resolved',
            identity: {
              guid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
              major: 1,
              minor: 0
            }
          },
          {
            name: 'Beta Library',
            status: 'resolved',
            identity: {
              guid: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
              major: 2,
              minor: 4
            }
          }
        ]
      })
      : JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        operation: 'add',
        complete: true,
        warnings: [],
        results: [
          {
            requestedName: 'Alpha Library',
            storedName: 'Alpha Library',
            status: 'added'
          },
          {
            requestedName: 'Beta Library',
            storedName: 'Beta Library',
            status: 'alreadyPresent'
          }
        ]
      })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      assert.deepEqual(calls, [], 'QuickPick must open before companion discovery starts');
      assert.equal(
        request.title,
        'VBA Tools: Add Reference — BookProject / Book2'
      );
      const items = await request.discover(notCancelledToken);
      assert.deepEqual(items, [
        {
          label: 'Alpha Library',
          description: 'TypeLib 1.0',
          canonicalName: 'Alpha Library'
        },
        {
          label: 'Beta Library',
          description: 'TypeLib 2.4',
          canonicalName: 'Beta Library'
        }
      ]);
      return {
        kind: 'accepted',
        names: ['Alpha Library', 'Beta Library']
      };
    },
    runMutationWithProgress: async (title, task) => {
      progressTitle = title;
      await task(notCancelledToken);
    },
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async (message) => {
      warnings.push(message);
      return undefined;
    },
    showReferenceErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'add');

  assert.deepEqual(targetScopes, ['document']);
  assert.equal(progressTitle, 'VBA Tools: Adding references — BookProject / Book2');
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    [
      'reference', 'list', '--available',
      '--project', projectRoot,
      '--document', 'Book2',
      '--format', 'json'
    ],
    [
      'reference', 'add', 'Alpha Library', 'Beta Library',
      '--project', projectRoot,
      '--document', 'Book2',
      '--format', 'json'
    ]
  ]);
  assert.deepEqual(information, [
    'References for Book2: 1 added, 0 promoted, 1 unchanged.'
  ]);
  assert.deepEqual(warnings, []);
  assert.deepEqual(errors, []);
  assert.deepEqual(outputShows, []);
  assert.deepEqual(routeEvents, [
    'basis:Reference Available:BookProject:Book2',
    'process:reference list',
    'mutation:start:Reference Add:BookProject:Book2',
    'process:reference add',
    'mutation:complete:Reference Add'
  ]);
});

test('ReferenceRemoveQuickPick keeps broken stored names and removes them without resolution', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const information: string[] = [];
  const errors: string[] = [];
  const base = createOptions({
    projectRoot,
    calls,
    output: [],
    documentName: 'Book2',
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'selection',
        complete: true,
        warnings: [],
        references: [
          { name: 'MiXeD Broken Library' },
          { name: 'Already Missing Library' }
        ]
      })
      : JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        operation: 'remove',
        complete: true,
        warnings: [],
        results: [
          {
            requestedName: 'MiXeD Broken Library',
            storedName: 'MiXeD Broken Library',
            status: 'removed'
          },
          {
            requestedName: 'Already Missing Library',
            storedName: null,
            status: 'alreadyAbsent'
          }
        ]
      })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      assert.deepEqual(items, [
        {
          label: 'MiXeD Broken Library',
          canonicalName: 'MiXeD Broken Library'
        },
        {
          label: 'Already Missing Library',
          canonicalName: 'Already Missing Library'
        }
      ]);
      return {
        kind: 'accepted',
        names: items.map((item) => item.canonicalName)
      };
    },
    runMutationWithProgress: async (_title, task) => task(notCancelledToken),
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async () => undefined,
    showReferenceErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'remove');

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    [
      'reference', 'list', '--no-resolve',
      '--project', projectRoot,
      '--document', 'Book2',
      '--format', 'json'
    ],
    [
      'reference', 'remove', 'MiXeD Broken Library', 'Already Missing Library',
      '--project', projectRoot,
      '--document', 'Book2',
      '--format', 'json'
    ]
  ]);
  assert.deepEqual(information, [
    'References for Book2: 1 removed, 1 unchanged.'
  ]);
  assert.deepEqual(errors, []);
});

test('an exit-zero untrusted reference mutation warns that the manifest may have committed without retrying', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const information: string[] = [];
  const warnings: string[] = [];
  let showOutputCount = 0;
  const base = createOptions({
    projectRoot,
    calls,
    output: [],
    documentName: 'Book2',
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'available',
        complete: true,
        warnings: [],
        references: [{
          name: 'Alpha Library',
          status: 'resolved',
          identity: {
            guid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
            major: 1,
            minor: 0
          }
        }]
      })
      : '{"complete":true}'
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      return { kind: 'accepted', names: [items[0]!.canonicalName] };
    },
    runMutationWithProgress: async (_title, task) => task(notCancelledToken),
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async (message) => {
      warnings.push(message);
      return 'Show Output';
    },
    showReferenceErrorMessage: async () => undefined,
    showOutput: () => {
      showOutputCount += 1;
    }
  };

  await runReferenceQuickPickWorkflow(options, 'add');

  assert.deepEqual(information, []);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /manifest may already have committed/i);
  assert.equal(showOutputCount, 1);
  assert.equal(calls.filter((call) => call.args[0] === 'reference').length, 2);
  assert.equal(calls.filter((call) => call.args[1] === 'list').length, 1);
});

test('an untrusted available inventory offers Output without items, mutation, or automatic reveal', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const outputShows: boolean[] = [];
  const errors: string[] = [];
  let mutationProgressCount = 0;
  let showOutputCount = 0;
  const base = createOptions({
    projectRoot,
    calls,
    output: [],
    outputShows,
    documentName: 'Book2',
    startStdout: () => JSON.stringify({
      schemaVersion: '1.0',
      scope: 'project',
      project: projectRoot,
      document: 'DifferentBook',
      mode: 'available',
      complete: true,
      warnings: [],
      references: []
    })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      try {
        await request.discover(notCancelledToken);
        assert.fail('Mismatched discovery output must not publish picker items.');
      } catch (error) {
        return { kind: 'failed', error };
      }
    },
    runMutationWithProgress: async () => {
      mutationProgressCount += 1;
    },
    showInformationMessage: async () => undefined,
    showWarningMessage: async () => undefined,
    showReferenceErrorMessage: async (message) => {
      errors.push(message);
      return 'Show Output';
    },
    showOutput: () => {
      showOutputCount += 1;
    }
  };

  await runReferenceQuickPickWorkflow(options, 'add');

  assert.equal(errors.length, 1);
  assert.match(errors[0]!, /could not be loaded/i);
  assert.equal(showOutputCount, 1);
  assert.equal(mutationProgressCount, 0);
  assert.deepEqual(outputShows, []);
  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    [
      'reference', 'list', '--available',
      '--project', projectRoot,
      '--document', 'Book2',
      '--format', 'json'
    ]
  ]);
});

test('a trusted reference mutation with warnings emits one warning notification and no success duplicate', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const information: string[] = [];
  const warnings: string[] = [];
  let showOutputCount = 0;
  const base = createOptions({
    projectRoot,
    calls: [],
    output: [],
    documentName: 'Book2',
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'available',
        complete: true,
        warnings: [],
        references: [{
          name: 'Alpha Library',
          status: 'resolved',
          identity: {
            guid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
            major: 1,
            minor: 0
          }
        }]
      })
      : JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        operation: 'add',
        complete: true,
        warnings: [{
          code: 'leaseMarkerCleanupFailed',
          message: 'The stale lease marker could not be removed.'
        }],
        results: [{
          requestedName: 'Alpha Library',
          storedName: 'Alpha Library',
          status: 'promoted'
        }]
      })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      return { kind: 'accepted', names: [items[0]!.canonicalName] };
    },
    runMutationWithProgress: async (_title, task) => task(notCancelledToken),
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async (message) => {
      warnings.push(message);
      return 'Show Output';
    },
    showReferenceErrorMessage: async () => undefined,
    showOutput: () => {
      showOutputCount += 1;
    }
  };

  await runReferenceQuickPickWorkflow(options, 'add');

  assert.deepEqual(information, []);
  assert.deepEqual(warnings, [
    'References for Book2: 0 added, 1 promoted, 0 unchanged. 1 warning.'
  ]);
  assert.equal(showOutputCount, 1);
});

test('untrusted manifest coherence suppresses a trusted mutation success notification', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const information: string[] = [];
  const warnings: string[] = [];
  const base = createOptions({
    projectRoot,
    calls: [],
    output: [],
    documentName: 'Book2',
    mutationManifestOutcome: 'untrusted',
    mutationCoherence: 'untrusted',
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'selection',
        complete: true,
        warnings: [],
        references: [{ name: 'Broken Library' }]
      })
      : JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        operation: 'remove',
        complete: true,
        warnings: [],
        results: [{
          requestedName: 'Broken Library',
          storedName: 'Broken Library',
          status: 'removed'
        }]
      })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      return { kind: 'accepted', names: [items[0]!.canonicalName] };
    },
    runMutationWithProgress: async (_title, task) => task(notCancelledToken),
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async (message) => {
      warnings.push(message);
      return undefined;
    },
    showReferenceErrorMessage: async () => undefined,
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'remove');

  assert.deepEqual(information, []);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0]!, /manifest may already have committed/i);
});

test('an empty configured reference selection reports a non-error without opening mutation progress', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const information: string[] = [];
  let progressCount = 0;
  const base = createOptions({
    projectRoot,
    calls,
    output: [],
    documentName: 'Book2',
    startStdout: () => JSON.stringify({
      schemaVersion: '1.0',
      scope: 'project',
      project: projectRoot,
      document: 'Book2',
      mode: 'selection',
      complete: true,
      warnings: [],
      references: []
    })
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      assert.deepEqual(await request.discover(notCancelledToken), []);
      return { kind: 'empty' };
    },
    runMutationWithProgress: async () => {
      progressCount += 1;
    },
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async () => undefined,
    showReferenceErrorMessage: async () => undefined,
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'remove');

  assert.deepEqual(information, ['Book2 has no configured references to remove.']);
  assert.equal(progressCount, 0);
  assert.equal(calls.filter((call) => call.args[0] === 'reference').length, 1);
});

test('a committed late mutation success remains success after progress cancellation', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const information: string[] = [];
  const errors: string[] = [];
  let cancellationRequested = false;
  let cancelListener: (() => void) | undefined;
  let mutationStdout: ((value: string) => void) | undefined;
  let mutationClose: ((exitCode: number | null, signal: string | null) => void) | undefined;
  let signalMutationStarted: (() => void) | undefined;
  const mutationStarted = new Promise<void>((resolve) => {
    signalMutationStarted = resolve;
  });
  const availableOutput = JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: 'Book2',
    mode: 'available',
    complete: true,
    warnings: [],
    references: [{
      name: 'Alpha Library',
      status: 'resolved',
      identity: {
        guid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
        major: 1,
        minor: 0
      }
    }]
  });
  const mutationOutput = JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: 'Book2',
    operation: 'add',
    complete: true,
    warnings: [],
    results: [{
      requestedName: 'Alpha Library',
      storedName: 'Alpha Library',
      status: 'added'
    }]
  });
  const base = createOptions({
    projectRoot,
    calls,
    output: [],
    advertiseStdinCancellation: true,
    startStdout: () => '',
    startProcess: (file, args) => {
      calls.push({ file, args });
      if (args[1] === 'list') {
        return {
          onStdout: (listener) => listener(availableOutput),
          onStderr: () => undefined,
          onExit: (listener) => listener(0, null),
          kill: () => undefined
        };
      }

      signalMutationStarted?.();
      return {
        onStdout: (listener) => {
          mutationStdout = listener;
        },
        onStderr: () => undefined,
        onExit: () => undefined,
        onClose: (listener) => {
          mutationClose = listener;
        },
        requestCancellation: async () => undefined,
        kill: () => undefined
      };
    }
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      return { kind: 'accepted', names: [items[0]!.canonicalName] };
    },
    runMutationWithProgress: async (_title, task) => {
      const running = task({
        get isCancellationRequested() {
          return cancellationRequested;
        },
        onCancellationRequested: (listener) => {
          cancelListener = listener;
          return { dispose: () => undefined };
        }
      });
      await mutationStarted;
      cancellationRequested = true;
      cancelListener?.();
      mutationStdout?.(mutationOutput);
      mutationClose?.(0, null);
      await running;
    },
    showInformationMessage: async (message) => {
      information.push(message);
    },
    showWarningMessage: async () => undefined,
    showReferenceErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'add');

  assert.deepEqual(information, [
    'References for Book2: 1 added, 0 promoted, 0 unchanged.'
  ]);
  assert.deepEqual(errors, []);
});

test('a reference mutation cancelled before commit remains silent', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const notifications: string[] = [];
  const base = createOptions({
    projectRoot,
    calls: [],
    output: [],
    documentName: 'Book2',
    startExitCode: (args) => args[1] === 'remove' ? 130 : 0,
    startStdout: (args) => args[1] === 'list'
      ? JSON.stringify({
        schemaVersion: '1.0',
        scope: 'project',
        project: projectRoot,
        document: 'Book2',
        mode: 'selection',
        complete: true,
        warnings: [],
        references: [{ name: 'Broken Library' }]
      })
      : ''
  });
  const options: ReferenceQuickPickWorkflowOptions = {
    ...base,
    selectReferences: async (request) => {
      const items = await request.discover(notCancelledToken);
      return { kind: 'accepted', names: [items[0]!.canonicalName] };
    },
    runMutationWithProgress: async (_title, task) => task(notCancelledToken),
    showInformationMessage: async (message) => {
      notifications.push(message);
    },
    showWarningMessage: async (message) => {
      notifications.push(message);
      return undefined;
    },
    showReferenceErrorMessage: async (message) => {
      notifications.push(message);
      return undefined;
    },
    showOutput: () => undefined
  };

  await runReferenceQuickPickWorkflow(options, 'remove');

  assert.deepEqual(notifications, []);
});

const notCancelledToken = {
  isCancellationRequested: false,
  onCancellationRequested: () => ({ dispose: () => undefined })
};

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
    outputShows?: boolean[];
    startStdout: (args: readonly string[]) => string;
    startStderr?: (args: readonly string[]) => string;
    startExitCode?: (args: readonly string[]) => number;
    diagnosticRefreshes?: Array<{ scopeKey: string; output: string }>;
    advertiseStdinCancellation?: boolean;
    documentName?: string;
    cancelTargetSelection?: boolean;
    targetScopes?: string[];
    rejectMutation?: boolean;
    mutationManifestOutcome?: 'unchanged' | 'changed' | 'untrusted';
    mutationCoherence?: 'notRequired' | 'coherent' | 'diverged' | 'untrusted';
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
          manifestOutcome: options.mutationManifestOutcome ?? 'unchanged',
          coherence: options.mutationCoherence ?? 'notRequired',
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
      show: (preserveFocus) => options.outputShows?.push(preserveFocus ?? false)
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
