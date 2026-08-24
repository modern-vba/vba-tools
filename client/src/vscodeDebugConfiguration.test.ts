import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  VscodeDebugIntegration,
  handleVbaDebugLifecycleRequest
} from './vscodeDebugIntegration';
import {
  VbaDebugCancellationError,
  VbaDebugSelectionError,
  type VbaDebugCancellationToken,
  type VbaDebugSourceBreakpoint
} from './vscodeDebugConfiguration';
import { type SnapshotSourceInventory } from './snapshotSourceInventory';

test('F5 from one active exported VBA source resolves a zero-configuration source snapshot', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sourceText = [
    'Attribute VB_Name = "DebugModule"',
    'Option Explicit',
    '',
    'Public Sub RunTarget()',
    'End Sub',
    ''
  ].join('\r\n');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 3, character: 12 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, sourceText]])
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration, {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: [{ path: sourcePath, text: sourceText }],
      activeSource: { path: sourcePath, line: 3, character: 12 },
      breakpoints: []
    }
  });
});

test('F5 transports an unsaved captured source as persistent base64 bytes', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourceSetPath = path.join(projectRoot, 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'DebugModule.bas');
  const sourceUri = pathToFileURL(sourcePath).href;
  const bytes = new TextEncoder().encode('Public Sub RunTarget()\r\nEnd Sub\r\n');
  let saveCalls = 0;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [path.join('C:', 'work')],
      getActiveEditor: () => ({ uriPath: sourcePath, line: 0, character: 11 }),
      getOpenTextDocuments: () => [{
        uriPath: sourcePath,
        isDirty: true,
        save: async () => {
          saveCalls += 1;
          return true;
        }
      }],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [manifestPath],
      readTextFile: async () => manifestJson('BookProject', ['Book1']),
      readSourceText: async () => {
        throw new Error('Transported snapshot must not re-read source text.');
      },
      findExportedSourceFiles: async () => {
        throw new Error('Transported snapshot must use its capture-start inventory.');
      },
      captureSourceInventory: async () => ({
        sourceSetPath,
        activeWindowsCodePage: 65001,
        entries: [{
          relativePath: 'DebugModule.bas',
          sourceUri,
          encoding: 'utf8',
          bytes
        }]
      })
    }
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.equal(saveCalls, 0);
  assert.deepEqual(configuration, {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: [{
        relativePath: 'DebugModule.bas',
        sourceUri,
        encoding: 'utf8',
        contentBase64: Buffer.from(bytes).toString('base64')
      }],
      activeSource: { sourceUri, line: 0, character: 11 },
      breakpoints: []
    }
  });
});

test('transported snapshot paths use portable raw UTF-16 ordinal order', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourceSetPath = path.join(projectRoot, 'src', 'Book1');
  const digitSourcePath = path.join(sourceSetPath, 'a0.bas');
  const nestedSourcePath = path.join(sourceSetPath, 'a', 'b.bas');
  const dottedISourcePath = path.join(sourceSetPath, 'İ.bas');
  const jSourcePath = path.join(sourceSetPath, 'j.bas');
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [path.join('C:', 'work')],
      getActiveEditor: () => ({ uriPath: digitSourcePath, line: 0, character: 0 }),
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [manifestPath],
      readTextFile: async () => manifestJson('BookProject', ['Book1']),
      readSourceText: async () => '',
      findExportedSourceFiles: async () => [],
      captureSourceInventory: async () => ({
        sourceSetPath,
        activeWindowsCodePage: 65001,
        entries: [
          {
            relativePath: 'a0.bas',
            sourceUri: pathToFileURL(digitSourcePath).href,
            encoding: 'utf8',
            bytes: new TextEncoder().encode('Option Explicit\r\n')
          },
          {
            relativePath: 'a\\b.bas',
            sourceUri: pathToFileURL(nestedSourcePath).href,
            encoding: 'utf8',
            bytes: new TextEncoder().encode('Option Explicit\r\n')
          },
          {
            relativePath: 'İ.bas',
            sourceUri: pathToFileURL(dottedISourcePath).href,
            encoding: 'utf8',
            bytes: new TextEncoder().encode('Option Explicit\r\n')
          },
          {
            relativePath: 'j.bas',
            sourceUri: pathToFileURL(jSourcePath).href,
            encoding: 'utf8',
            bytes: new TextEncoder().encode('Option Explicit\r\n')
          }
        ]
      })
    }
  });

  const configuration = await integration.resolveDebugConfiguration({});
  const snapshot = configuration.sourceSnapshot as {
    sources: readonly { readonly relativePath: string }[];
  };

  assert.deepEqual(
    snapshot.sources.map((source) => source.relativePath),
    ['a/b.bas', 'a0.bas', 'j.bas', 'İ.bas']
  );
});

test('source snapshots use UTF-16 ordinal canonical path order across punctuation and case', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const underscoreSource = path.join(projectRoot, 'src', 'Book1', 'A_B.bas');
  const digitSource = path.join(projectRoot, 'src', 'Book1', 'A0.bas');
  const lowerCaseSource = path.join(projectRoot, 'src', 'Book1', 'aZ.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: underscoreSource, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [lowerCaseSource, 'Public Sub LowerCaseTarget()\r\nEnd Sub\r\n'],
      [underscoreSource, 'Public Sub UnderscoreTarget()\r\nEnd Sub\r\n'],
      [digitSource, 'Public Sub DigitTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [
      { path: digitSource, text: 'Public Sub DigitTarget()\r\nEnd Sub\r\n' },
      { path: underscoreSource, text: 'Public Sub UnderscoreTarget()\r\nEnd Sub\r\n' },
      { path: lowerCaseSource, text: 'Public Sub LowerCaseTarget()\r\nEnd Sub\r\n' }
    ],
    activeSource: { path: underscoreSource, line: 0, character: 0 },
    breakpoints: []
  });
});

test('a saved launch narrows project and document and resolves an explicit procedure pair without an active editor', async () => {
  const firstRoot = path.join('C:', 'work', 'FirstProject');
  const selectedRoot = path.join('C:', 'work', 'SelectedProject');
  const firstManifest = path.join(firstRoot, 'vba-project.json');
  const selectedManifest = path.join(selectedRoot, 'vba-project.json');
  const firstSource = path.join(firstRoot, 'src', 'Book1', 'First.bas');
  const selectedSource = path.join(selectedRoot, 'src', 'Book2', 'DebugModule.bas');
  const integration = createIntegration({
    manifests: new Map([
      [firstManifest, manifestJson('FirstProject', ['Book1'])],
      [selectedManifest, manifestJson('SelectedProject', ['Book1', 'Book2'])]
    ]),
    sources: new Map([
      [firstSource, 'Public Sub FirstTarget()\r\nEnd Sub\r\n'],
      [selectedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({
    type: 'vba',
    request: 'launch',
    name: 'Saved VBA target',
    project: selectedRoot,
    document: 'book2',
    module: 'DebugModule',
    procedure: 'RunTarget'
  });

  assert.deepEqual(configuration, {
    type: 'vba',
    request: 'launch',
    name: 'Saved VBA target',
    project: selectedRoot,
    document: 'Book2',
    module: 'DebugModule',
    procedure: 'RunTarget',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: [{
        path: selectedSource,
        text: 'Public Sub RunTarget()\r\nEnd Sub\r\n'
      }],
      breakpoints: []
    }
  });
});

test('a saved launch rejects module and procedure unless both selectors are supplied', async () => {
  const integration = createIntegration({
    manifests: new Map(),
    sources: new Map()
  });

  for (const configuration of [
    { module: 'DebugModule' },
    { procedure: 'RunTarget' },
    { module: '  ', procedure: 'RunTarget' }
  ]) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      /module.*procedure.*together/i
    );
  }
});

test('a saved launch rejects invalid project and document selectors instead of treating them as omitted', async () => {
  let hostWasTouched = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [],
      getActiveEditor: () => {
        hostWasTouched = true;
        return undefined;
      },
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [],
      readTextFile: async () => '',
      readSourceText: async () => '',
      findExportedSourceFiles: async () => []
    }
  });

  for (const [configuration, expectedError] of [
    [{ project: '  ' }, /project.*non-empty string/i],
    [{ document: 42 }, /document.*non-empty string/i]
  ] as const) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      expectedError
    );
  }
  assert.equal(hostWasTouched, false);
});

test('debug launch saves every dirty exported source in the selected project and leaves other projects untouched', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const otherRoot = path.join('C:', 'work', 'OtherProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const otherManifestPath = path.join(otherRoot, 'vba-project.json');
  const activeSource = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const peerSource = path.join(projectRoot, 'src', 'Book2', 'PeerModule.cls');
  const outsideSource = path.join(otherRoot, 'src', 'OtherBook', 'Outside.bas');
  const sources = new Map([
    [activeSource, 'Public Sub BeforeSave()\r\nEnd Sub\r\n'],
    [peerSource, 'Public Sub PeerBeforeSave()\r\nEnd Sub\r\n'],
    [outsideSource, 'Public Sub OutsideBeforeSave()\r\nEnd Sub\r\n']
  ]);
  const saved: string[] = [];
  const dirtyDocument = (uriPath: string, savedText: string) => ({
    uriPath,
    isDirty: true,
    save: async () => {
      saved.push(uriPath);
      sources.set(uriPath, savedText);
      return true;
    }
  });
  const integration = createIntegration({
    activeEditor: { uriPath: activeSource, line: 0, character: 11 },
    manifests: new Map([
      [manifestPath, manifestJson('BookProject', ['Book1', 'Book2'])],
      [otherManifestPath, manifestJson('OtherProject', ['OtherBook'])]
    ]),
    sources,
    openTextDocuments: () => [
      dirtyDocument(outsideSource, 'Public Sub OutsideAfterSave()\r\nEnd Sub\r\n'),
      dirtyDocument(peerSource, 'Public Sub PeerAfterSave()\r\nEnd Sub\r\n'),
      dirtyDocument(activeSource, 'Public Sub AfterSave()\r\nEnd Sub\r\n')
    ]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(saved, [activeSource, peerSource]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: activeSource,
      text: 'Public Sub AfterSave()\r\nEnd Sub\r\n'
    }],
    activeSource: { path: activeSource, line: 0, character: 11 },
    breakpoints: []
  });
  assert.equal(sources.get(outsideSource), 'Public Sub OutsideBeforeSave()\r\nEnd Sub\r\n');
});

test('debug launch awaits save participants and re-resolves membership and source position before snapshot capture', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sources = new Map([[sourcePath, 'Public Sub BeforeSave()\r\nEnd Sub\r\n']]);
  const events: string[] = [];
  let activeEditor = { uriPath: sourcePath, line: 0, character: 11 };
  let manifestReads = 0;
  const integration = createIntegration({
    getActiveEditor: () => {
      events.push(`active:${activeEditor.line}:${activeEditor.character}`);
      return activeEditor;
    },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources,
    readTextFile: async (filePath) => {
      if (filePath === manifestPath) {
        manifestReads += 1;
        events.push(`manifest:${manifestReads}`);
        return manifestJson('BookProject', ['Book1']);
      }
      return sources.get(filePath) ?? '';
    },
    readSourceText: async (filePath) => {
      events.push('snapshot-source');
      return sources.get(filePath) ?? '';
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => {
        events.push('save-start');
        await Promise.resolve();
        sources.set(sourcePath, 'Public Sub AfterSave()\r\nEnd Sub\r\n');
        activeEditor = { uriPath: sourcePath, line: 1, character: 7 };
        events.push('save-participants-finished');
        return true;
      }
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(events, [
    'active:0:11',
    'manifest:1',
    'save-start',
    'save-participants-finished',
    'active:1:7',
    'manifest:2',
    'snapshot-source'
  ]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: sourcePath,
      text: 'Public Sub AfterSave()\r\nEnd Sub\r\n'
    }],
    activeSource: { path: sourcePath, line: 1, character: 7 },
    breakpoints: []
  });
});

test('debug launch cancellation stops waiting for a pending save and retains completed saves', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const completedSource = path.join(projectRoot, 'src', 'Book1', 'A.bas');
  const pendingSource = path.join(projectRoot, 'src', 'Book1', 'B.cls');
  const sources = new Map([
    [completedSource, 'Public Sub BeforeSave()\r\nEnd Sub\r\n'],
    [pendingSource, 'Public Sub PendingSave()\r\nEnd Sub\r\n']
  ]);
  let finishPendingSave!: (saved: boolean) => void;
  let notifyPendingSaveStarted!: () => void;
  const pendingSaveStarted = new Promise<void>((resolve) => {
    notifyPendingSaveStarted = resolve;
  });
  let sourceWasRead = false;
  const integration = createIntegration({
    activeEditor: { uriPath: completedSource, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources,
    readSourceText: async () => {
      sourceWasRead = true;
      return '';
    },
    openTextDocuments: () => [{
      uriPath: completedSource,
      isDirty: true,
      save: async () => {
        sources.set(completedSource, 'Public Sub AfterSave()\r\nEnd Sub\r\n');
        return true;
      }
    }, {
      uriPath: pendingSource,
      isDirty: true,
      save: () => new Promise<boolean>((resolve) => {
        finishPendingSave = resolve;
        notifyPendingSaveStarted();
      })
    }]
  });
  let isCancellationRequested = false;
  const cancellationListeners = new Set<() => void>();
  const cancellationToken = {
    get isCancellationRequested() {
      return isCancellationRequested;
    },
    onCancellationRequested(listener: () => void) {
      cancellationListeners.add(listener);
      return {
        dispose: () => cancellationListeners.delete(listener)
      };
    }
  };
  const resolution = integration.resolveDebugConfiguration({}, cancellationToken);
  let outcome: unknown;
  let settled = false;
  void resolution.then(
    () => {
      outcome = 'resolved';
      settled = true;
    },
    (error: unknown) => {
      outcome = error;
      settled = true;
    }
  );

  await pendingSaveStarted;
  isCancellationRequested = true;
  for (const listener of cancellationListeners) {
    listener();
  }
  try {
    await new Promise<void>((resolve) => setImmediate(resolve));

    assert.equal(settled, true);
    assert.ok(outcome instanceof VbaDebugCancellationError);
    assert.equal(
      sources.get(completedSource),
      'Public Sub AfterSave()\r\nEnd Sub\r\n'
    );
    assert.equal(sourceWasRead, false);
  } finally {
    finishPendingSave(true);
    await resolution.catch(() => undefined);
  }
});

test('debug restart captures unsaved bytes from the bound document after the active editor changes', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const boundSource = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const activeSource = path.join(projectRoot, 'src', 'Book2', 'OtherModule.bas');
  const capturedSourceSets: string[] = [];
  const unsavedBytes = Buffer.from(
    'Attribute VB_Name = "DebugModule"\r\nPublic Sub RunTarget()\r\nEnd Sub\r\n',
    'utf8'
  );
  const integration = createIntegration({
    getActiveEditor: () => ({ uriPath: activeSource, line: 1, character: 4 }),
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1', 'Book2'])]]),
    sources: new Map([
      [boundSource, 'Public Sub SavedTarget()\r\nEnd Sub\r\n'],
      [activeSource, 'Public Sub OtherTarget()\r\nEnd Sub\r\n']
    ]),
    captureSourceInventory: async (sourceSetPath) => {
      capturedSourceSets.push(sourceSetPath);
      return {
        sourceSetPath,
        activeWindowsCodePage: 1252,
        entries: [{
          relativePath: 'DebugModule.bas',
          sourceUri: pathToFileURL(boundSource).href,
          encoding: 'utf8',
          bytes: unsavedBytes
        }]
      };
    }
  });

  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const restartPreparation = configuration.__vbaRestartPreparation as {
    protocolVersion: number;
    id: string;
    generation: number;
  };
  assert.equal(restartPreparation.protocolVersion, 1);
  assert.match(restartPreparation.id, /^[0-9a-f]{32}$/);
  assert.equal(restartPreparation.generation, 0);

  const captured = await integration.captureBoundRestartConfiguration(configuration);

  assert.deepEqual(capturedSourceSets, [path.join(projectRoot, 'src', 'Book1')]);
  const sourceSnapshot = captured.sourceSnapshot as {
    sources: readonly { relativePath: string; contentBase64: string }[];
  };
  assert.deepEqual(sourceSnapshot.sources, [{
    relativePath: 'DebugModule.bas',
    sourceUri: pathToFileURL(boundSource).href,
    encoding: 'utf8',
    contentBase64: unsavedBytes.toString('base64')
  }]);
  assert.equal(captured.document, 'Book1');
  assert.equal(captured.module, 'DebugModule');
  assert.equal(captured.procedure, 'RunTarget');
});

test('debug restart preparation notifies the bound adapter session with the next generation', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const bytes = Buffer.from(
    'Attribute VB_Name = "DebugModule"\r\nPublic Sub RunTarget()\r\nEnd Sub\r\n',
    'utf8'
  );
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, bytes.toString('utf8')]]),
    captureSourceInventory: async (sourceSetPath) => ({
      sourceSetPath,
      activeWindowsCodePage: 1252,
      entries: [{
        relativePath: 'DebugModule.bas',
        sourceUri: pathToFileURL(sourcePath).href,
        encoding: 'utf8',
        bytes
      }]
    })
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  });
  const marker = configuration.__vbaRestartPreparation as {
    protocolVersion: number;
    id: string;
    generation: number;
  };
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];

  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 41, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.ok(preparation);
  await preparation;

  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].command, 'vba/restartPrepared');
  assert.equal(notifications[0].arguments.sessionId, adapterSessionId);
  assert.equal(notifications[0].arguments.restartRequestSequence, 41);
  assert.equal(notifications[0].arguments.preparationId, marker.id);
  assert.equal(notifications[0].arguments.generation, 1);
  assert.equal(notifications[0].arguments.success, true);
  const launch = notifications[0].arguments.launch as Record<string, unknown>;
  assert.equal(launch.project, projectRoot);
  assert.equal(launch.document, 'Book1');
  assert.equal(launch.module, 'DebugModule');
  assert.equal(launch.procedure, 'RunTarget');
  assert.deepEqual(launch.__vbaRestartPreparation, {
    protocolVersion: 1,
    id: marker.id,
    generation: 1
  });
  const snapshot = launch.sourceSnapshot as {
    sources: readonly { contentBase64: string }[];
  };
  assert.equal(snapshot.sources[0].contentBase64, bytes.toString('base64'));
});

test('debug restart marker rejects a generation outside the adapter Int32 contract', () => {
  const integration = createIntegration({
    manifests: new Map(),
    sources: new Map()
  });

  assert.equal(integration.restartPreparationId({
    __vbaRestartPreparation: {
      protocolVersion: 1,
      id: '0123456789abcdef0123456789abcdef',
      generation: 0x80000000
    }
  }), undefined);
});

test('debug restart notification failure does not leave preparation state busy', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const bytes = Buffer.from('Public Sub RunTarget()\r\nEnd Sub\r\n', 'utf8');
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, bytes.toString('utf8')]]),
    captureSourceInventory: async (sourceSetPath) => ({
      sourceSetPath,
      activeWindowsCodePage: 1252,
      entries: [{
        relativePath: 'DebugModule.bas',
        sourceUri: pathToFileURL(sourcePath).href,
        encoding: 'utf8',
        bytes
      }]
    })
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const firstPreparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 48, type: 'request', command: 'restart' },
    async () => {
      throw new Error('Synthetic custom request transport failure.');
    }
  );
  assert.ok(firstPreparation);
  await assert.rejects(
    firstPreparation,
    /Synthetic custom request transport failure/
  );

  const notifications: string[] = [];
  const retry = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 49, type: 'request', command: 'restart' },
    async (command) => {
      notifications.push(command);
    }
  );
  assert.ok(retry);
  await retry;
  assert.deepEqual(notifications, ['vba/restartPrepared']);
});

test('debug restart preparation ignores fresh arguments and the active editor after session binding', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const oldProjectRoot = path.join(workspaceRoot, 'OldProject');
  const freshProjectRoot = path.join(workspaceRoot, 'FreshProject');
  const oldManifestPath = path.join(oldProjectRoot, 'vba-project.json');
  const freshManifestPath = path.join(freshProjectRoot, 'vba-project.json');
  const oldSource = path.join(oldProjectRoot, 'src', 'OldBook', 'OldModule.bas');
  const freshSource = path.join(freshProjectRoot, 'src', 'FreshBook', 'FreshModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const oldBytes = Buffer.from(
    'Attribute VB_Name = "OldModule"\r\nPublic Sub OldTarget()\r\nEnd Sub\r\n',
    'utf8'
  );
  const freshBytes = Buffer.from(
    'Attribute VB_Name = "FreshModule"\r\nPublic Sub FreshTarget()\r\nEnd Sub\r\n',
    'utf8'
  );
  const capturedSourceSets: string[] = [];
  const integration = createIntegration({
    adapterSessionId,
    activeEditor: { uriPath: freshSource, line: 1, character: 5 },
    manifests: new Map([
      [oldManifestPath, manifestJson('OldProject', ['OldBook'])],
      [freshManifestPath, manifestJson('FreshProject', ['FreshBook'])]
    ]),
    sources: new Map([
      [oldSource, oldBytes.toString('utf8')],
      [freshSource, freshBytes.toString('utf8')]
    ]),
    captureSourceInventory: async (sourceSetPath) => {
      capturedSourceSets.push(sourceSetPath);
      const isOld = path.normalize(sourceSetPath) === path.normalize(path.dirname(oldSource));
      return {
        sourceSetPath,
        activeWindowsCodePage: 1252,
        entries: [{
          relativePath: isOld ? 'OldModule.bas' : 'FreshModule.bas',
          sourceUri: pathToFileURL(isOld ? oldSource : freshSource).href,
          encoding: 'utf8',
          bytes: isOld ? oldBytes : freshBytes
        }]
      };
    }
  });
  const oldConfiguration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Old Procedure',
    project: oldProjectRoot,
    document: 'OldBook',
    module: 'OldModule',
    procedure: 'OldTarget',
    __vbaDebugWorkbookFileName: 'OldBook.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const freshConfiguration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Fresh Procedure',
    project: freshProjectRoot,
    document: 'FreshBook',
    module: 'FreshModule',
    procedure: 'FreshTarget',
    __vbaDebugWorkbookFileName: 'FreshBook.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const oldMarker = (
    oldConfiguration.__vbaRestartPreparation as { id: string }
  ).id;
  assert.notEqual(
    oldMarker,
    (freshConfiguration.__vbaRestartPreparation as { id: string }).id
  );
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration: oldConfiguration,
    stop: () => undefined
  });

  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];
  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    oldConfiguration,
    {
      seq: 44,
      type: 'request',
      command: 'restart',
      arguments: { arguments: freshConfiguration }
    },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.ok(preparation);
  await preparation;

  assert.deepEqual(capturedSourceSets, [path.dirname(oldSource)]);
  assert.equal(notifications.length, 1);
  assert.equal(notifications[0].command, 'vba/restartPrepared');
  assert.equal(notifications[0].arguments.sessionId, adapterSessionId);
  assert.equal(notifications[0].arguments.restartRequestSequence, 44);
  assert.equal(notifications[0].arguments.preparationId, oldMarker);
  assert.equal(notifications[0].arguments.generation, 1);
  assert.equal(notifications[0].arguments.success, true);
  const launch = notifications[0].arguments.launch as Record<string, unknown>;
  assert.equal(launch.project, oldProjectRoot);
  assert.equal(launch.document, 'OldBook');
  assert.equal(launch.module, 'OldModule');
  assert.equal(launch.procedure, 'OldTarget');
  const snapshot = launch.sourceSnapshot as {
    sources: readonly { contentBase64: string }[];
  };
  assert.equal(snapshot.sources[0].contentBase64, oldBytes.toString('base64'));
});

test('debug restart preparation fails closed before adapter binding without saving dirty sources', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const oldProjectRoot = path.join(workspaceRoot, 'OldProject');
  const oldManifestPath = path.join(oldProjectRoot, 'vba-project.json');
  const oldSource = path.join(oldProjectRoot, 'src', 'OldBook', 'OldModule.bas');
  const saved: string[] = [];
  const integration = createIntegration({
    manifests: new Map([
      [oldManifestPath, manifestJson('OldProject', ['OldBook'])]
    ]),
    sources: new Map([
      [oldSource, 'Public Sub OldTarget()\r\nEnd Sub\r\n']
    ]),
    openTextDocuments: () => [{
      uriPath: oldSource,
      isDirty: true,
      save: async () => {
        saved.push(oldSource);
        return true;
      }
    }]
  });
  const oldConfiguration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Old Procedure',
    project: oldProjectRoot,
    document: 'OldBook',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const preparationId = (
    oldConfiguration.__vbaRestartPreparation as { id: string }
  ).id;
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];

  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    oldConfiguration,
    { seq: 47, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );

  assert.equal(preparation, undefined);
  assert.deepEqual(saved, []);
  assert.deepEqual(notifications, []);
});

test('debug restart preparation rejects a marker borrowed from another project', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const markerProjectRoot = path.join(workspaceRoot, 'MarkerProject');
  const freshProjectRoot = path.join(workspaceRoot, 'FreshProject');
  const markerManifestPath = path.join(markerProjectRoot, 'vba-project.json');
  const freshManifestPath = path.join(freshProjectRoot, 'vba-project.json');
  const markerSource = path.join(
    markerProjectRoot,
    'src',
    'MarkerBook',
    'MarkerModule.bas'
  );
  const freshSource = path.join(freshProjectRoot, 'src', 'FreshBook', 'FreshModule.bas');
  const saved: string[] = [];
  const integration = createIntegration({
    manifests: new Map([
      [markerManifestPath, manifestJson('MarkerProject', ['MarkerBook'])],
      [freshManifestPath, manifestJson('FreshProject', ['FreshBook'])]
    ]),
    sources: new Map([
      [markerSource, 'Public Sub MarkerTarget()\r\nEnd Sub\r\n'],
      [freshSource, 'Public Sub FreshTarget()\r\nEnd Sub\r\n']
    ]),
    openTextDocuments: () => [markerSource, freshSource].map((uriPath) => ({
      uriPath,
      isDirty: true,
      save: async () => {
        saved.push(uriPath);
        return true;
      }
    }))
  });
  const markerConfiguration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Marker Procedure',
    project: markerProjectRoot,
    document: 'MarkerBook',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const marker = markerConfiguration.__vbaRestartPreparation as {
    protocolVersion: number;
    id: string;
  };
  const borrowedMarkerConfiguration = {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Fresh Procedure',
    project: freshProjectRoot,
    document: 'FreshBook',
    sourceSnapshot: { schemaVersion: 1, sources: [] },
    __vbaRestartPreparation: marker
  };
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];

  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    borrowedMarkerConfiguration,
    {
      seq: 49,
      type: 'request',
      command: 'restart'
    },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.equal(preparation, undefined);
  assert.deepEqual(saved, []);
  assert.deepEqual(notifications, []);
});

test('debug disconnect cancels the bound snapshot capture without saving sources', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const source = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const saved: string[] = [];
  let notifyCaptureStarted!: () => void;
  const captureStarted = new Promise<void>((resolve) => {
    notifyCaptureStarted = resolve;
  });
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[source, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    openTextDocuments: () => [{
      uriPath: source,
      isDirty: true,
      save: async () => {
        saved.push(source);
        return true;
      }
    }],
    captureSourceInventory: (sourceSetPath, cancellationToken) => (
      new Promise<SnapshotSourceInventory>((_resolve, reject) => {
        assert.equal(path.normalize(sourceSetPath), path.normalize(path.dirname(source)));
        let registration: { dispose(): void } | undefined;
        registration = cancellationToken?.onCancellationRequested(() => {
          registration?.dispose();
          reject(new VbaDebugCancellationError());
        });
        notifyCaptureStarted();
      })
    )
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const preparationId = (
    configuration.__vbaRestartPreparation as { id: string }
  ).id;
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];
  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 45, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.ok(preparation);
  let settled = false;
  void preparation.finally(() => {
    settled = true;
  });

  await captureStarted;
  assert.equal(handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 46, type: 'request', command: 'disconnect' },
    async () => undefined
  ), undefined);
  await preparation;

  assert.equal(settled, true);
  assert.deepEqual(saved, []);
  assert.deepEqual(notifications, [{
    command: 'vba/restartPrepared',
    arguments: {
      sessionId: adapterSessionId,
      generation: 1,
      restartRequestSequence: 45,
      preparationId,
      success: false,
      message: 'VBA debug restart preparation was cancelled.'
    }
  }]);
});

test('debug concurrent restart does not send an unbound preparation result', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const source = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  let notifyCaptureStarted!: () => void;
  const captureStarted = new Promise<void>((resolve) => {
    notifyCaptureStarted = resolve;
  });
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[source, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    captureSourceInventory: (_sourceSetPath, cancellationToken) => (
      new Promise<SnapshotSourceInventory>((_resolve, reject) => {
        let registration: { dispose(): void } | undefined;
        registration = cancellationToken?.onCancellationRequested(() => {
          registration?.dispose();
          reject(new VbaDebugCancellationError());
        });
        notifyCaptureStarted();
      })
    )
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const preparationId = (
    configuration.__vbaRestartPreparation as { id: string }
  ).id;
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];
  const firstPreparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 42, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.ok(firstPreparation);

  await captureStarted;
  const concurrentPreparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 43, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.equal(handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 44, type: 'request', command: 'disconnect' },
    async () => undefined
  ), undefined);
  await firstPreparation;
  await concurrentPreparation;

  assert.equal(concurrentPreparation, undefined);
  assert.deepEqual(notifications, [{
    command: 'vba/restartPrepared',
    arguments: {
      sessionId: adapterSessionId,
      generation: 1,
      restartRequestSequence: 42,
      preparationId,
      success: false,
      message: 'VBA debug restart preparation was cancelled.'
    }
  }]);
});

test('debug restart remains busy until the adapter restart response completes replacement', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const bytes = Buffer.from('Public Sub RunTarget()\r\nEnd Sub\r\n', 'utf8');
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, bytes.toString('utf8')]]),
    captureSourceInventory: async (sourceSetPath) => ({
      sourceSetPath,
      activeWindowsCodePage: 1252,
      entries: [{
        relativePath: 'DebugModule.bas',
        sourceUri: pathToFileURL(sourcePath).href,
        encoding: 'utf8',
        bytes
      }]
    })
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const notifications: number[] = [];
  const first = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 50, type: 'request', command: 'restart' },
    async (_command, argumentsValue) => {
      notifications.push(argumentsValue.restartRequestSequence as number);
    }
  );
  assert.ok(first);
  await first;

  assert.equal(handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 51, type: 'request', command: 'restart' },
    async () => undefined
  ), undefined);
  integration.observeDebugAdapterMessage(configuration, {
    seq: 500,
    type: 'response',
    command: 'restart',
    request_seq: 50,
    success: true
  });
  const next = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 52, type: 'request', command: 'restart' },
    async (_command, argumentsValue) => {
      notifications.push(argumentsValue.restartRequestSequence as number);
    }
  );
  assert.ok(next);
  await next;
  integration.observeDebugAdapterMessage(configuration, {
    seq: 501,
    type: 'response',
    command: 'restart',
    request_seq: 52,
    success: true
  });

  assert.deepEqual(notifications, [50, 52]);
});

test('debug restart capture failure reports only restart failure with bound session identity', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const projectRoot = path.join(workspaceRoot, 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const source = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const adapterSessionId = '0123456789abcdef0123456789abcdef';
  const saved: string[] = [];
  const integration = createIntegration({
    adapterSessionId,
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[source, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    openTextDocuments: () => [{
      uriPath: source,
      isDirty: true,
      save: async () => {
        saved.push(source);
        return true;
      }
    }],
    captureSourceInventory: async () => {
      throw new VbaDebugSelectionError('The bound VBA debug document was removed.');
    }
  });
  const configuration = integration.prepareDebugConfigurationForRestart({
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'RunTarget',
    __vbaDebugWorkbookFileName: 'Book1.xlsm',
    sourceSnapshot: { schemaVersion: 1, sources: [] }
  }, workspaceRoot);
  const preparationId = (
    configuration.__vbaRestartPreparation as { id: string }
  ).id;
  await integration.createDebugAdapterExecutable({
    id: 'vscode-session-1',
    configuration,
    stop: () => undefined
  });
  const notifications: Array<{ command: string; arguments: Record<string, unknown> }> = [];

  const preparation = handleVbaDebugLifecycleRequest(
    integration,
    configuration,
    { seq: 47, type: 'request', command: 'restart' },
    async (command, argumentsValue) => {
      notifications.push({ command, arguments: argumentsValue });
    }
  );
  assert.ok(preparation);
  await preparation;

  assert.deepEqual(saved, []);
  assert.deepEqual(notifications, [{
    command: 'vba/restartPrepared',
    arguments: {
      sessionId: adapterSessionId,
      generation: 1,
      restartRequestSequence: 47,
      preparationId,
      success: false,
      message: 'The bound VBA debug document was removed.'
    }
  }]);
});

test('debug launch freezes one enabled ordinary BAS breakpoint after save participants finish', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sources = new Map([[sourcePath, 'Public Sub BeforeSave()\r\nEnd Sub\r\n']]);
  const events: string[] = [];
  let sourceBreakpoints = [{
    uriPath: sourcePath,
    line: 0,
    enabled: true
  }];
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources,
    readTextFile: async (filePath) => {
      events.push('manifest');
      return filePath === manifestPath
        ? manifestJson('BookProject', ['Book1'])
        : sources.get(filePath) ?? '';
    },
    getSourceBreakpoints: () => {
      events.push(`breakpoints:${sourceBreakpoints[0].line}`);
      return sourceBreakpoints;
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => {
        events.push('save-start');
        await Promise.resolve();
        sources.set(sourcePath, 'Public Sub AfterSave()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n');
        sourceBreakpoints = [{
          uriPath: sourcePath,
          line: 1,
          enabled: true
        }];
        events.push('save-participants-finished');
        return true;
      }
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(events, [
    'manifest',
    'save-start',
    'save-participants-finished',
    'manifest',
    'breakpoints:1'
  ]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: sourcePath,
      text: 'Public Sub AfterSave()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'
    }],
    activeSource: { path: sourcePath, line: 0, character: 11 },
    breakpoints: [{ path: sourcePath, line: 1 }]
  });
});

test('debug launch rejects an enabled in-scope conditional breakpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      condition: 'ready'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /conditional breakpoint.*unsupported/i
  );
});

test('debug launch rejects an enabled in-scope hit-count breakpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      hitCondition: '3'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /hit-count breakpoint.*unsupported/i
  );
});

test('debug launch rejects an enabled in-scope logpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      logMessage: 'hit'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /logpoint.*unsupported/i
  );
});

test('debug launch serializes enabled ordinary exported-source breakpoints in canonical order', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const moduleSource = path.join(projectRoot, 'src', 'Book1', 'A_Module.bas');
  const classSource = path.join(projectRoot, 'src', 'Book1', 'B_Class.cls');
  const formSource = path.join(projectRoot, 'src', 'Book1', 'C_Form.frm');
  const integration = createIntegration({
    activeEditor: { uriPath: moduleSource, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [formSource, 'Begin VB.UserForm C_Form\r\nEnd\r\nPublic Sub FormTarget()\r\nEnd Sub\r\n'],
      [classSource, 'Public Sub ClassTarget()\r\nEnd Sub\r\n'],
      [moduleSource, 'Public Sub ModuleTarget()\r\n  Debug.Print "one"\r\nEnd Sub\r\n']
    ]),
    getSourceBreakpoints: () => [{
      uriPath: formSource,
      line: 2,
      enabled: true
    }, {
      uriPath: moduleSource,
      line: 1,
      enabled: true
    }, {
      uriPath: classSource,
      line: 0,
      enabled: true
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(
    (configuration.sourceSnapshot as { breakpoints: unknown }).breakpoints,
    [
      { path: moduleSource, line: 1 },
      { path: classSource, line: 0 },
      { path: formSource, line: 2 }
    ]
  );
});

test('debug launch rejects duplicate enabled breakpoints at one canonical source line', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[
      sourcePath,
      'Public Sub RunTarget()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'
    ]]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 1,
      enabled: true
    }, {
      uriPath: sourcePath.toUpperCase(),
      line: 1,
      enabled: true
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /duplicate enabled VBA breakpoint.*DebugModule\.bas:2/i
  );
});

test('debug launch ignores disabled, out-of-scope, and non-source breakpoints before unsupported-feature checks', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const otherRoot = path.join('C:', 'work', 'OtherProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const formSidecarPath = path.join(projectRoot, 'src', 'Book1', 'Dialog.frx');
  const outsidePath = path.join(otherRoot, 'src', 'Book2', 'Outside.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [sourcePath, 'Public Sub RunTarget()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'],
      [formSidecarPath, 'binary form sidecar placeholder'],
      [outsidePath, 'Public Sub OutsideTarget()\r\nEnd Sub\r\n']
    ]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 1,
      enabled: true
    }, {
      uriPath: sourcePath,
      line: 0,
      enabled: false,
      condition: 'unsupported but disabled'
    }, {
      uriPath: outsidePath,
      line: 0,
      enabled: true,
      logMessage: 'unsupported but outside the selected source set'
    }, {
      uriPath: formSidecarPath,
      line: 0,
      enabled: true,
      condition: 'unsupported but not an exported source'
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(
    (configuration.sourceSnapshot as { breakpoints: unknown }).breakpoints,
    [{ path: sourcePath, line: 1 }]
  );
});

test('debug launch aborts before re-resolution and snapshot capture when a selected source cannot be saved', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  let manifestReadCount = 0;
  let sourceWasRead = false;
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    readTextFile: async () => {
      manifestReadCount += 1;
      return manifestJson('BookProject', ['Book1']);
    },
    readSourceText: async () => {
      sourceWasRead = true;
      return '';
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => false
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    (error: unknown) => (
      error instanceof Error
      && /could not save exported VBA source/i.test(error.message)
      && error.message.includes(sourcePath)
    )
  );
  assert.equal(manifestReadCount, 1);
  assert.equal(sourceWasRead, false);
});

test('an active source belonging to more than one workbook-backed project reports project ambiguity', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const firstRoot = path.join(workspaceRoot, 'FirstProject');
  const secondRoot = path.join(workspaceRoot, 'SecondProject');
  const sharedSource = path.join(workspaceRoot, 'Shared', 'DebugModule.bas');
  const manifests = new Map([
    [path.join(firstRoot, 'vba-project.json'), manifestJsonWithSourcePath(
      'FirstProject',
      'Book1',
      path.relative(firstRoot, path.dirname(sharedSource)))],
    [path.join(secondRoot, 'vba-project.json'), manifestJsonWithSourcePath(
      'SecondProject',
      'Book2',
      path.relative(secondRoot, path.dirname(sharedSource)))]
  ]);
  const integration = createIntegration({
    activeEditor: { uriPath: sharedSource, line: 0, character: 0 },
    manifests,
    sources: new Map([[sharedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']])
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /project selection is ambiguous/i
  );
});

test('an active source belonging to more than one document source set reports document ambiguity', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sharedSourceRoot = path.join(projectRoot, 'src', 'Shared');
  const activeSource = path.join(sharedSourceRoot, 'DebugModule.bas');
  const manifest = JSON.stringify({
    schemaVersion: 1,
    projectName: 'BookProject',
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        kind: 'excel',
        sourcePath: 'src/Shared',
        templatePath: 'Book1.xlsm',
        binPath: 'bin/Book1.xlsm',
        publishPath: 'publish/Book1.xlsm',
        commonModules: [],
        references: []
      },
      Book2: {
        kind: 'excel',
        sourcePath: 'src/Shared',
        templatePath: 'Book2.xlsm',
        binPath: 'bin/Book2.xlsm',
        publishPath: 'publish/Book2.xlsm',
        commonModules: [],
        references: []
      }
    }
  });
  const integration = createIntegration({
    activeEditor: { uriPath: activeSource, line: 0, character: 0 },
    manifests: new Map([[path.join(projectRoot, 'vba-project.json'), manifest]]),
    sources: new Map([[activeSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']])
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /document selection is ambiguous/i
  );
});

test('an explicit procedure pair uses active source membership to narrow omitted project and document selectors', async () => {
  const firstRoot = path.join('C:', 'work', 'FirstProject');
  const selectedRoot = path.join('C:', 'work', 'SelectedProject');
  const firstSource = path.join(firstRoot, 'src', 'Book1', 'First.bas');
  const selectedSource = path.join(selectedRoot, 'src', 'Book2', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: selectedSource, line: 0, character: 0 },
    manifests: new Map([
      [path.join(firstRoot, 'vba-project.json'), manifestJson('FirstProject', ['Book1'])],
      [path.join(selectedRoot, 'vba-project.json'), manifestJson('SelectedProject', ['Book2'])]
    ]),
    sources: new Map([
      [firstSource, 'Public Sub FirstTarget()\r\nEnd Sub\r\n'],
      [selectedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({
    module: 'DebugModule',
    procedure: 'RunTarget'
  });

  assert.equal(configuration.project, selectedRoot);
  assert.equal(configuration.document, 'Book2');
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: selectedSource,
      text: 'Public Sub RunTarget()\r\nEnd Sub\r\n'
    }],
    breakpoints: []
  });
});

test('source snapshots preserve decoded CP932 and UTF-16 text through the dedicated source-text host port', async () => {
  const projectRoot = path.join('C:', 'work', 'EncodedProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const cp932Source = path.join(projectRoot, 'src', 'Book1', 'Cp932.bas');
  const utf16Source = path.join(projectRoot, 'src', 'Book1', 'Utf16.cls');
  const decodedSources = new Map([
    [cp932Source, 'Public Sub 実行()\r\nEnd Sub\r\n'],
    [utf16Source, 'Public Sub 検証()\r\nEnd Sub\r\n']
  ]);
  const integration = createIntegration({
    activeEditor: { uriPath: cp932Source, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('EncodedProject', ['Book1'])]]),
    sources: decodedSources,
    readSourceText: async (sourcePath) => decodedSources.get(sourcePath) ?? ''
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [
      { path: cp932Source, text: 'Public Sub 実行()\r\nEnd Sub\r\n' },
      { path: utf16Source, text: 'Public Sub 検証()\r\nEnd Sub\r\n' }
    ],
    activeSource: { path: cp932Source, line: 0, character: 11 },
    breakpoints: []
  });
});

test('unsupported launch fields and request modes fail closed before project discovery or save', async () => {
  let hostTouched = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [],
      getActiveEditor: () => {
        hostTouched = true;
        return undefined;
      },
      getOpenTextDocuments: () => {
        hostTouched = true;
        return [];
      },
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => {
        hostTouched = true;
        return [];
      },
      readTextFile: async () => '',
      readSourceText: async () => '',
      findExportedSourceFiles: async () => []
    }
  });
  const unsupportedConfigurations: Array<[Record<string, unknown>, RegExp]> = [
    [{ args: ['value'] }, /unsupported.*args/i],
    [{ arguments: ['value'] }, /unsupported.*arguments/i],
    [{ noBuild: true }, /unsupported.*noBuild/i],
    [{ stopOnEntry: true }, /unsupported.*stopOnEntry/i],
    [{ request: 'attach' }, /only.*launch/i],
    [{ compound: ['one', 'two'] }, /unsupported.*compound/i],
    [{ concurrent: true }, /unsupported.*concurrent/i],
    [{ compilerConstants: { VBA7: true } }, /unsupported.*compilerConstants/i]
  ];

  for (const [configuration, expectedError] of unsupportedConfigurations) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      expectedError
    );
  }
  assert.equal(hostTouched, false);
});

test('dynamic debug configurations expose one transient active-procedure launch only for an exported VBA editor', () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const activeIntegration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map(),
    sources: new Map()
  });
  const inactiveIntegration = createIntegration({
    activeEditor: {
      uriPath: path.join(projectRoot, 'README.md'),
      line: 0,
      character: 0
    },
    manifests: new Map(),
    sources: new Map()
  });

  assert.deepEqual(activeIntegration.provideDynamicDebugConfigurations(), [{
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure'
  }]);
  assert.deepEqual(inactiveIntegration.provideDynamicDebugConfigurations(), []);
});

function createIntegration(options: {
  adapterSessionId?: string | undefined;
  activeEditor?: { uriPath: string; line: number; character: number } | undefined;
  getActiveEditor?: () => { uriPath: string; line: number; character: number } | undefined;
  manifests: ReadonlyMap<string, string>;
  sources: ReadonlyMap<string, string>;
  readTextFile?: (filePath: string) => Promise<string>;
  readSourceText?: (filePath: string) => Promise<string>;
  getSourceBreakpoints?: () => readonly VbaDebugSourceBreakpoint[];
  openTextDocuments?: () => readonly {
    uriPath: string;
    isDirty: boolean;
    save(): Promise<boolean>;
  }[];
  captureSourceInventory?: (
    sourceSetPath: string,
    cancellationToken?: VbaDebugCancellationToken
  ) => Promise<SnapshotSourceInventory>;
}): VscodeDebugIntegration {
  return new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    ...(options.adapterSessionId === undefined
      ? {}
      : {
          createDebugSessionId: () => options.adapterSessionId!,
          vbaDevResolver: {
            resolve: async () => ({
              executablePath: path.join('C:', 'tools', 'vba-dev.exe'),
              bundledPath: path.join('C:', 'tools', 'vba-dev.exe'),
              source: 'configured' as const,
              capabilities: { toolVersion: '0.1.0', contractVersion: '1.0', commands: {} }
            })
          },
          vbaDebugAdapterResolver: {
            resolve: async () => ({
              executablePath: path.join('C:', 'tools', 'vba-debug-adapter.exe'),
              capabilities: {
                toolVersion: '0.1.0',
                contractVersion: '1.0',
                protocolVersion: '1.1',
                transports: ['stdio'],
                sessionIdFormat: 'lowercase-hex-32',
                commands: ['cleanup', 'doctor'],
                commandSchemaVersions: { doctor: '1.0' },
                featureVersions: { 'doctor.stdinCancellation': '1.0' },
                requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
              }
            })
          }
        }),
    debugConfigurationHost: {
      workspaceRoots: [path.join('C:', 'work')],
      getActiveEditor: options.getActiveEditor ?? (() => options.activeEditor),
      getOpenTextDocuments: options.openTextDocuments ?? (() => []),
      getSourceBreakpoints: options.getSourceBreakpoints ?? (() => []),
      findProjectManifests: async () => [...options.manifests.keys()],
      readTextFile: options.readTextFile ?? (async (filePath) => {
        const text = options.manifests.get(filePath) ?? options.sources.get(filePath);
        if (text === undefined) {
          throw new Error(`Missing fake file: ${filePath}`);
        }
        return text;
      }),
      readSourceText: options.readSourceText ?? (async (filePath) => {
        const text = options.sources.get(filePath);
        if (text === undefined) {
          throw new Error(`Missing fake source: ${filePath}`);
        }
        return text;
      }),
      findExportedSourceFiles: async (sourceSetPath) => (
        [...options.sources.keys()].filter((sourcePath) => isWithin(sourcePath, sourceSetPath))
      ),
      captureSourceInventory: options.captureSourceInventory
    }
  });
}

function manifestJson(projectName: string, documentNames: readonly string[]): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: documentNames[0],
    documents: Object.fromEntries(documentNames.map((documentName) => [
      documentName,
      {
        kind: 'excel',
        sourcePath: `src/${documentName}`,
        templatePath: `src/${documentName}/${documentName}.xlsm`,
        binPath: `bin/${documentName}.xlsm`,
        publishPath: `publish/${documentName}.xlsm`,
        commonModules: [],
        references: []
      }
    ]))
  });
}

function manifestJsonWithSourcePath(
  projectName: string,
  documentName: string,
  sourcePath: string
): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: documentName,
    documents: {
      [documentName]: {
        kind: 'excel',
        sourcePath,
        templatePath: `${documentName}.xlsm`,
        binPath: `bin/${documentName}.xlsm`,
        publishPath: `publish/${documentName}.xlsm`,
        commonModules: [],
        references: []
      }
    }
  });
}

function isWithin(filePath: string, directoryPath: string): boolean {
  const relative = path.relative(path.resolve(directoryPath), path.resolve(filePath));
  return relative.length > 0
    && relative !== '..'
    && !relative.startsWith(`..${path.sep}`)
    && !path.isAbsolute(relative);
}
