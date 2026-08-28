import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  classifyHostClassTextDocumentChange,
  HostClassProjectionWorkspace,
  HostClassProjectionWorkspaceLifecycle
} from './hostClassProjectionWorkspace';
import {
  HostClassListInvocation,
  HostClassListRunResult,
  HostClassProjectionContext,
  HostClassProjectionLifecycle
} from './hostClassProjectionLifecycle';

test('HostClass workspace activation resolves every manifest document to canonical context', async () => {
  const firstManifest = path.resolve('workspace', 'Zulu', 'vba-project.json');
  const secondManifest = path.resolve('workspace', 'Alpha', 'vba-project.json');
  const activations: HostClassProjectionContext[] = [];
  const lifecycle = createRecordingLifecycle({ activations });
  const manifests = new Map([
    [firstManifest, createManifest({
      BookB: {
        sourcePath: 'src/BookB',
        templatePath: 'templates/BookB.xlsm'
      },
      BookA: {
        sourcePath: 'src/BookA',
        templatePath: 'templates/BookA.xlsm'
      }
    }, 'BookA')],
    [secondManifest, createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'src/Main/Main.xlsm'
      }
    }, 'Main')]
  ]);
  const workspace = new HostClassProjectionWorkspace({
    lifecycle,
    findProjectManifests: async () => [firstManifest, secondManifest],
    readManifestText: async (manifestPath) => manifests.get(manifestPath),
    collectHostClassSources: async () => []
  });

  await workspace.activate();

  assert.deepEqual(activations, [
    {
      project: path.dirname(secondManifest),
      document: 'Main',
      sourceTemplate: path.resolve(path.dirname(secondManifest), 'src/Main/Main.xlsm')
    },
    {
      project: path.dirname(firstManifest),
      document: 'BookA',
      sourceTemplate: path.resolve(path.dirname(firstManifest), 'templates/BookA.xlsm')
    },
    {
      project: path.dirname(firstManifest),
      document: 'BookB',
      sourceTemplate: path.resolve(path.dirname(firstManifest), 'templates/BookB.xlsm')
    }
  ]);
  assert.deepEqual(
    workspace.getActiveDocuments().map((document) => ({
      manifestPath: document.manifestPath,
      sourceSetPath: document.sourceSetPath,
      document: document.context.document
    })),
    [
      {
        manifestPath: secondManifest,
        sourceSetPath: path.resolve(path.dirname(secondManifest), 'src/Main'),
        document: 'Main'
      },
      {
        manifestPath: firstManifest,
        sourceSetPath: path.resolve(path.dirname(firstManifest), 'src/BookA'),
        document: 'BookA'
      },
      {
        manifestPath: firstManifest,
        sourceSetPath: path.resolve(path.dirname(firstManifest), 'src/BookB'),
        document: 'BookB'
      }
    ]
  );
});

test('HostClass workspace reevaluates every active source association without reactivation', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const activations: HostClassProjectionContext[] = [];
  const associationSources: string[][] = [];
  const collectedDocuments: string[] = [];
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({ activations, associationSources }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => createManifest({
      BookB: {
        sourcePath: 'src/BookB',
        templatePath: 'templates/BookB.xlsm'
      },
      BookA: {
        sourcePath: 'src/BookA',
        templatePath: 'templates/BookA.xlsm'
      }
    }, 'BookA'),
    collectHostClassSources: async (document) => {
      collectedDocuments.push(document.context.document);
      return [{
        sourceUri: `file:///workspace/Project/src/${document.context.document}/Form.frm`,
        kind: 'form',
        moduleIdentity: {
          state: 'authoritative',
          name: `${document.context.document}Form`
        }
      }];
    }
  });
  await workspace.activate();
  activations.length = 0;
  associationSources.length = 0;
  collectedDocuments.length = 0;

  await workspace.reevaluateAllSourceAssociations();

  assert.deepEqual(collectedDocuments, ['BookA', 'BookB']);
  assert.deepEqual(associationSources, [[
    'file:///workspace/Project/src/BookA/Form.frm'
  ], [
    'file:///workspace/Project/src/BookB/Form.frm'
  ]]);
  assert.deepEqual(activations, []);
});

test('HostClass workspace-folder reconciliation activates additions and removes departures once', async () => {
  const firstManifest = path.resolve('workspace', 'First', 'vba-project.json');
  const secondManifest = path.resolve('workspace', 'Second', 'vba-project.json');
  const activations: HostClassProjectionContext[] = [];
  const removals: HostClassProjectionContext[] = [];
  const activeDocumentSnapshots: string[][] = [];
  let manifestPaths = [firstManifest];
  const manifests = new Map([
    [firstManifest, createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'templates/Main.xlsm'
      }
    }, 'Main')],
    [secondManifest, createManifest({
      Added: {
        sourcePath: 'src/Added',
        templatePath: 'templates/Added.xlsm'
      }
    }, 'Added')]
  ]);
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({ activations, removals }),
    findProjectManifests: async () => manifestPaths,
    readManifestText: async (manifestPath) => manifests.get(manifestPath),
    collectHostClassSources: async () => [],
    onActiveDocumentsChanged: (documents) => {
      activeDocumentSnapshots.push(documents.map((document) => document.context.document));
    }
  });

  await workspace.activate();
  manifestPaths = [firstManifest, secondManifest];
  await workspace.reconcileWorkspaceFolders();
  manifestPaths = [secondManifest];
  const departure = workspace.reconcileWorkspaceFolders([
    path.dirname(secondManifest)
  ]);

  assert.deepEqual(removals.map((context) => context.document), ['Main']);
  assert.deepEqual(activeDocumentSnapshots, [
    ['Main'],
    ['Main', 'Added'],
    ['Added']
  ]);
  await departure;

  assert.deepEqual(activations.map((context) => context.document), ['Main', 'Added']);
  assert.deepEqual(removals.map((context) => context.document), ['Main']);
  assert.deepEqual(
    workspace.getActiveDocuments().map((document) => document.context.document),
    ['Added']
  );
});

test('HostClass text routing ignores manifests outside the active workspace scope', () => {
  const workspaceRoot = path.resolve('workspace', 'Active');
  const externalManifest = path.resolve('external', 'Project', 'vba-project.json');
  const externalDocument = {
    manifestPath: externalManifest,
    sourceSetPath: path.resolve('external', 'Project', 'src', 'Main'),
    context: {
      project: path.resolve('external', 'Project'),
      document: 'Main',
      sourceTemplate: path.resolve('external', 'Project', 'templates', 'Main.xlsm')
    }
  };

  assert.equal(classifyHostClassTextDocumentChange(
    'file',
    path.join(workspaceRoot, 'NewProject', 'vba-project.json'),
    [workspaceRoot],
    []
  ), 'manifest');
  assert.equal(classifyHostClassTextDocumentChange(
    'file',
    externalManifest,
    [workspaceRoot],
    [externalDocument]
  ), 'manifest');
  assert.equal(classifyHostClassTextDocumentChange(
    'file',
    externalManifest,
    [workspaceRoot],
    []
  ), undefined);
});

test('HostClass workspace schedules only newly added manifest documents for inspection', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const manifestChanges: HostClassProjectionContext[] = [];
  const manifests = new Map([
    [manifestPath, createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'templates/Main.xlsm'
      }
    }, 'Main')]
  ]);
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      manifestChanges
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async (candidate) => manifests.get(candidate),
    collectHostClassSources: async () => [],
    scheduleDelay: scheduleImmediately
  });
  await workspace.activate();
  manifests.set(manifestPath, createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Main.xlsm'
    },
    Added: {
      sourcePath: 'src/Added',
      templatePath: 'templates/Added.xlsm'
    }
  }, 'Main'));

  await workspace.manifestChanged(manifestPath);
  await workspace.flush();

  assert.deepEqual(manifestChanges, [{
    project: path.dirname(manifestPath),
    document: 'Added',
    sourceTemplate: path.resolve(path.dirname(manifestPath), 'templates/Added.xlsm')
  }]);
});

test('HostClass sourcePath-only manifest changes swap inventory without inspection', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const activations: HostClassProjectionContext[] = [];
  const manifestChanges: HostClassProjectionContext[] = [];
  const activeSourceSets: string[] = [];
  const collectedSourceSets: string[] = [];
  let manifestText = createManifest({
    Main: {
      sourcePath: 'src/Original',
      templatePath: 'templates/Main.xlsm'
    }
  }, 'Main');
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({ activations, manifestChanges }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => manifestText,
    collectHostClassSources: async (document) => {
      collectedSourceSets.push(document.sourceSetPath);
      return [];
    },
    onActiveDocumentsChanged: (documents) => {
      if (documents[0] !== undefined) {
        activeSourceSets.push(documents[0].sourceSetPath);
      }
    },
    scheduleDelay: scheduleImmediately
  });
  await workspace.activate();
  manifestText = createManifest({
    Main: {
      sourcePath: 'src/Replacement',
      templatePath: 'templates/Main.xlsm'
    }
  }, 'Main');

  await workspace.manifestChanged(manifestPath);
  await workspace.flush();

  const original = path.resolve(path.dirname(manifestPath), 'src/Original');
  const replacement = path.resolve(path.dirname(manifestPath), 'src/Replacement');
  assert.deepEqual(activeSourceSets, [original, replacement]);
  assert.deepEqual(collectedSourceSets, [original, replacement]);
  assert.deepEqual(activations.map((context) => context.document), ['Main']);
  assert.deepEqual(manifestChanges, []);
});

test('HostClass workspace debounces manifest resolution and reconciles only the final context', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const removals: HostClassProjectionContext[] = [];
  const manifestChanges: HostClassProjectionContext[] = [];
  const timers: Array<{
    delayMilliseconds: number;
    cancelled: boolean;
    callback: () => void;
  }> = [];
  let manifestReads = 0;
  let manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Main.xlsm'
    }
  }, 'Main');
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      removals,
      manifestChanges
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => {
      manifestReads += 1;
      return manifestText;
    },
    collectHostClassSources: async () => [],
    scheduleDelay: (delayMilliseconds, callback) => {
      const timer = { delayMilliseconds, cancelled: false, callback };
      timers.push(timer);
      return {
        dispose: () => {
          timer.cancelled = true;
        }
      };
    }
  });
  await workspace.activate();
  manifestReads = 0;

  manifestText = '{';
  await workspace.manifestChanged(manifestPath);
  manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Replacement.xlsm'
    }
  }, 'Main');
  await workspace.manifestChanged(manifestPath);

  assert.equal(manifestReads, 0);
  assert.equal(workspace.getActiveDocuments()[0]?.context.sourceTemplate,
    path.resolve(path.dirname(manifestPath), 'templates/Main.xlsm'));
  assert.deepEqual(
    timers.map((timer) => [timer.delayMilliseconds, timer.cancelled]),
    [[1000, true], [1000, false]]
  );

  timers[1]?.callback();
  await workspace.flush();

  assert.equal(manifestReads, 1);
  assert.deepEqual(removals.map((context) => context.sourceTemplate), [
    path.resolve(path.dirname(manifestPath), 'templates/Main.xlsm')
  ]);
  assert.deepEqual(manifestChanges.map((context) => context.sourceTemplate), [
    path.resolve(path.dirname(manifestPath), 'templates/Replacement.xlsm')
  ]);
});

test('HostClass pending manifest resolution fences an old-context invocation result', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const originalTemplate = path.resolve(
    path.dirname(manifestPath),
    'templates/Main.xlsm'
  );
  const replacementTemplate = path.resolve(
    path.dirname(manifestPath),
    'templates/Replacement.xlsm'
  );
  let manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Main.xlsm'
    }
  }, 'Main');
  const timers: Array<() => void> = [];
  const invocations: HostClassListInvocation[] = [];
  const notifications: unknown[] = [];
  let markFirstStarted: (() => void) | undefined;
  const firstStarted = new Promise<void>((resolve) => {
    markFirstStarted = resolve;
  });
  let completeFirst: ((result: HostClassListRunResult) => void) | undefined;
  const firstResult = new Promise<HostClassListRunResult>((resolve) => {
    completeFirst = resolve;
  });
  const completedResult = (
    context: HostClassProjectionContext
  ): HostClassListRunResult => ({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      if (invocations.length === 1) {
        markFirstStarted?.();
        return firstResult;
      }

      return completedResult(invocation.context);
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });
  const workspace = new HostClassProjectionWorkspace({
    lifecycle,
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => manifestText,
    collectHostClassSources: async () => [],
    scheduleDelay: (_delayMilliseconds, callback) => {
      timers.push(callback);
      return { dispose: () => undefined };
    }
  });

  await workspace.activate();
  await firstStarted;
  manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Replacement.xlsm'
    }
  }, 'Main');
  await workspace.manifestChanged(manifestPath);
  completeFirst?.(completedResult({
    project: path.dirname(manifestPath),
    document: 'Main',
    sourceTemplate: originalTemplate
  }));
  await Promise.resolve();
  await Promise.resolve();

  assert.equal(notifications.length, 0);

  timers[0]?.();
  await workspace.flush();
  await lifecycle.flush();

  assert.deepEqual(invocations.map((invocation) =>
    invocation.context.sourceTemplate), [
    originalTemplate,
    replacementTemplate
  ]);
  assert.deepEqual(notifications.map((value) =>
    (value as { sourceTemplate: string }).sourceTemplate), [
    replacementTemplate
  ]);
});

test('HostClass workspace removes every document when its manifest is deleted', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const removals: HostClassProjectionContext[] = [];
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      removals
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'templates/Main.xlsm'
      },
      Secondary: {
        sourcePath: 'src/Secondary',
        templatePath: 'templates/Secondary.xlsm'
      }
    }, 'Main'),
    collectHostClassSources: async () => [],
    scheduleDelay: scheduleImmediately
  });
  await workspace.activate();

  await workspace.manifestRemoved(manifestPath);
  await workspace.flush();

  assert.deepEqual(removals.map((context) => context.document), [
    'Main',
    'Secondary'
  ]);
  assert.deepEqual(workspace.getActiveDocuments(), []);
});

test('HostClass workspace routes only the exact selected template to inspection', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const selectedTemplate = path.resolve(
    path.dirname(manifestPath),
    'templates/Main.xlsm'
  );
  const templateChanges: HostClassProjectionContext[] = [];
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      templateChanges
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'templates/Main.xlsm'
      }
    }, 'Main'),
    collectHostClassSources: async () => []
  });
  await workspace.activate();

  await workspace.templateFileChanged(path.resolve(
    path.dirname(manifestPath),
    'bin/Main.xlsm'
  ));
  const selectedChange = workspace.templateFileChanged(selectedTemplate.toUpperCase());

  assert.deepEqual(templateChanges, [{
    project: path.dirname(manifestPath),
    document: 'Main',
    sourceTemplate: selectedTemplate
  }]);
  await selectedChange;
});

test('HostClass workspace lets final manifest refresh own a newly selected template event', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const originalTemplate = path.resolve(
    path.dirname(manifestPath),
    'templates/Main.xlsm'
  );
  const replacementTemplate = path.resolve(
    path.dirname(manifestPath),
    'templates/Replacement.xlsm'
  );
  const manifestChanges: HostClassProjectionContext[] = [];
  const templateChanges: HostClassProjectionContext[] = [];
  const timers: Array<{ cancelled: boolean; callback: () => void }> = [];
  let manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Main.xlsm'
    }
  }, 'Main');
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      manifestChanges,
      templateChanges
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => manifestText,
    collectHostClassSources: async () => [],
    scheduleDelay: (_delayMilliseconds, callback) => {
      const timer = { cancelled: false, callback };
      timers.push(timer);
      return {
        dispose: () => {
          timer.cancelled = true;
        }
      };
    }
  });
  await workspace.activate();
  manifestText = createManifest({
    Main: {
      sourcePath: 'src/Main',
      templatePath: 'templates/Replacement.xlsm'
    }
  }, 'Main');

  await workspace.manifestChanged(manifestPath);
  await workspace.templateFileChanged(replacementTemplate);

  assert.equal(manifestChanges.length, 0);
  assert.equal(templateChanges.length, 0);
  assert.equal(workspace.getActiveDocuments()[0]?.context.sourceTemplate, originalTemplate);

  timers[0]?.callback();
  await workspace.flush();
  assert.deepEqual(manifestChanges.map((context) => context.sourceTemplate), [
    replacementTemplate
  ]);
  assert.deepEqual(templateChanges, []);
});

test('HostClass workspace source changes only reevaluate associations in the owning source set', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const associationSources: string[][] = [];
  let collections = 0;
  const sourceUri = 'file:///workspace/Project/src/Main/InvoiceForm.frm';
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      associationSources
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => createManifest({
      Main: {
        sourcePath: 'src/Main',
        templatePath: 'templates/Main.xlsm'
      }
    }, 'Main'),
    collectHostClassSources: async () => {
      collections += 1;
      return [{
        sourceUri,
        kind: 'form',
        moduleIdentity: { state: 'authoritative', name: 'InvoiceForm' }
      }];
    }
  });
  await workspace.activate();
  collections = 0;
  associationSources.length = 0;

  await workspace.sourceFileChanged(path.resolve(
    path.dirname(manifestPath),
    'src/Main/InvoiceForm.frm'
  ));
  await workspace.sourceFileChanged(path.resolve(
    path.dirname(manifestPath),
    'src/MainSibling/InvoiceForm.frm'
  ));

  assert.equal(collections, 1);
  assert.deepEqual(associationSources, [[sourceUri]]);
});

test('HostClass workspace treats manifest document casing as an exact context identity change', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const removals: HostClassProjectionContext[] = [];
  const manifestChanges: HostClassProjectionContext[] = [];
  let manifest = createManifest({
    Book1: {
      sourcePath: 'src/Book1',
      templatePath: 'templates/Book1.xlsm'
    }
  }, 'Book1');
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      removals,
      manifestChanges
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => manifest,
    collectHostClassSources: async () => [],
    scheduleDelay: scheduleImmediately
  });
  await workspace.activate();
  manifest = createManifest({
    book1: {
      sourcePath: 'src/Book1',
      templatePath: 'templates/Book1.xlsm'
    }
  }, 'book1');

  await workspace.manifestChanged(manifestPath);
  await workspace.flush();

  assert.deepEqual(removals.map((context) => context.document), ['Book1']);
  assert.deepEqual(manifestChanges.map((context) => context.document), ['book1']);
});

test('HostClass workspace shutdown cancels delayed manifest resolution and releases its fence', async () => {
  const manifestPath = path.resolve('workspace', 'Project', 'vba-project.json');
  const timers: Array<{ cancelled: boolean; callback: () => void }> = [];
  const manifestChanges: HostClassProjectionContext[] = [];
  const manifestResolutionBegins: HostClassProjectionContext[] = [];
  const manifestResolutionCompletions: HostClassProjectionContext[] = [];
  let manifestReads = 0;
  const workspace = new HostClassProjectionWorkspace({
    lifecycle: createRecordingLifecycle({
      activations: [],
      manifestChanges,
      manifestResolutionBegins,
      manifestResolutionCompletions
    }),
    findProjectManifests: async () => [manifestPath],
    readManifestText: async () => {
      manifestReads += 1;
      return createManifest({
        Main: {
          sourcePath: 'src/Main',
          templatePath: 'templates/Main.xlsm'
        }
      }, 'Main');
    },
    collectHostClassSources: async () => [],
    scheduleDelay: (_delayMilliseconds, callback) => {
      const timer = { cancelled: false, callback };
      timers.push(timer);
      return {
        dispose: () => {
          timer.cancelled = true;
        }
      };
    }
  });
  await workspace.activate();
  manifestReads = 0;

  await workspace.manifestChanged(manifestPath);
  workspace.shutdown();
  timers[0]?.callback();
  await workspace.flush();

  assert.equal(timers[0]?.cancelled, true);
  assert.equal(manifestReads, 0);
  assert.equal(manifestChanges.length, 0);
  assert.equal(manifestResolutionBegins.length, 1);
  assert.equal(manifestResolutionCompletions.length, 1);
});

function createRecordingLifecycle(recording: {
  activations: HostClassProjectionContext[];
  manifestChanges?: HostClassProjectionContext[];
  removals?: HostClassProjectionContext[];
  templateChanges?: HostClassProjectionContext[];
  associationSources?: string[][];
  manifestResolutionBegins?: HostClassProjectionContext[];
  manifestResolutionCompletions?: HostClassProjectionContext[];
}): HostClassProjectionWorkspaceLifecycle {
  return {
    activateDocument: (context) => recording.activations.push(context),
    templateChanged: (context) => recording.templateChanges?.push(context),
    beginManifestResolution: (context) =>
      recording.manifestResolutionBegins?.push(context),
    completeManifestResolution: (context) =>
      recording.manifestResolutionCompletions?.push(context),
    scheduleResolvedAutomaticRefresh: (context, trigger) => {
      if (trigger === 'manifestChanged') {
        recording.manifestChanges?.push(context);
      }
    },
    reevaluateSourceAssociations: (_context, sources) => {
      recording.associationSources?.push(sources.map((source) => source.sourceUri));
      return undefined;
    },
    removeDocument: (context) => recording.removals?.push(context)
  };
}

function createManifest(
  documents: Readonly<Record<string, {
    sourcePath: string;
    templatePath: string;
  }>>,
  primaryDocument: string
): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName: 'HostProjectionProject',
    primaryDocument,
    documents: Object.fromEntries(Object.entries(documents).map(([name, document]) => [
      name,
      {
        kind: 'excel',
        sourcePath: document.sourcePath,
        templatePath: document.templatePath,
        binPath: `bin/${name}.xlsm`,
        publishPath: `publish/${name}.xlsm`,
        commonModules: [],
        references: []
      }
    ]))
  });
}

function scheduleImmediately(
  _delayMilliseconds: number,
  callback: () => void
): { dispose(): void } {
  let cancelled = false;
  queueMicrotask(() => {
    if (!cancelled) {
      callback();
    }
  });
  return {
    dispose: () => {
      cancelled = true;
    }
  };
}
