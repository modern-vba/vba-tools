import assert from 'node:assert/strict';
import test from 'node:test';

import { CommandPalettePathIdentity, CommandPaletteProjectTarget } from './commandPaletteTarget';
import {
  ProjectManifestComparisonContext,
  ProjectManifestEditorSnapshot
} from './projectManifestMutation';
import {
  ProjectManifestMutationVscodeAdapter,
  ProjectManifestMutationVscodeAdapterOptions,
  ProjectManifestSnapshotContentProvider,
  ProjectManifestVscodeDocumentChangeLike,
  ProjectManifestVscodeTextDocumentLike,
  createProjectManifestMutationVscodeAdapter
} from './projectManifestMutationVscodeAdapter';

const CanonicalManifestPath = 'C:\\canonical\\vba-project.json';
const ManifestIdentity: CommandPalettePathIdentity = {
  canonicalPath: CanonicalManifestPath,
  objectIdentity: '1:2',
  kind: 'file'
};

test('raw bytes are copied and every canonical manifest alias remains visible', async () => {
  const fixture = createFixture();
  const canonical = fixture.addDocument(CanonicalManifestPath, '{"one":1}');
  const alias = fixture.addDocument('C:\\alias\\vba-project.json', '{"one":1}');
  fixture.addDocument('untitled:vba-project.json', '{}', 'untitled');

  const firstRead = await fixture.adapter.readManifestBytes(CanonicalManifestPath);
  firstRead[0] = 0xff;
  const secondRead = await fixture.adapter.readManifestBytes(CanonicalManifestPath);
  assert.deepEqual([...secondRead], [1, 2, 3]);

  const buffers = await fixture.adapter.getOpenBuffers(ManifestIdentity);
  assert.ok(buffers.some((buffer) =>
    buffer.bufferId.startsWith(`${alias.uri.toString()}#buffer-`)
  ));
  assert.ok(buffers.some((buffer) =>
    buffer.bufferId.startsWith(`${canonical.uri.toString()}#buffer-`)
  ));
  assert.deepEqual(buffers.map((buffer) => buffer.text), ['{"one":1}', '{"one":1}']);
});

test('an unrelated unresolvable open manifest does not poison the selected identity', async () => {
  const fixture = createFixture({
    resolvePathIdentity: async (filePath) => {
      if (filePath.includes('deleted')) {
        throw new Error('missing unrelated file');
      }
      return filePath === CanonicalManifestPath
        ? ManifestIdentity
        : { canonicalPath: filePath };
    }
  });
  fixture.addDocument(CanonicalManifestPath, 'selected');
  fixture.addDocument('C:\\deleted\\vba-project.json', 'unrelated');

  const buffers = await fixture.adapter.getOpenBuffers(ManifestIdentity);

  assert.equal(buffers.length, 1);
  assert.equal(buffers[0]?.filePath, CanonicalManifestPath);
});

test('buffer observation preserves dirty intermediate revisions, Auto Save, and close', async () => {
  const fixture = createFixture();
  const document = fixture.addDocument(CanonicalManifestPath, 'baseline');
  const originalBufferId = (await fixture.adapter.getOpenBuffers(ManifestIdentity))[0]!.bufferId;
  const observations: Array<readonly ProjectManifestEditorSnapshot[]> = [];
  const subscription = fixture.adapter.observeBuffers(
    ManifestIdentity,
    (buffers) => observations.push(buffers)
  );

  document.change('competing', true);
  fixture.emitChange(document);
  document.change('competing', false);
  fixture.emitSave(document);
  await fixture.adapter.getOpenBuffers(ManifestIdentity);

  assert.equal(observations.length, 2);
  assert.deepEqual(
    observations.map((buffers) => ({
      text: buffers[0]?.text,
      dirty: buffers[0]?.isDirty,
      revision: buffers[0]?.revision
    })),
    [
      { text: 'competing', dirty: true, revision: 2 },
      { text: 'competing', dirty: false, revision: 3 }
    ]
  );

  fixture.closeDocument(document);
  fixture.emitClose(document);
  await fixture.adapter.getOpenBuffers(ManifestIdentity);
  assert.deepEqual(observations.at(-1), []);

  const reopened = fixture.addDocument(CanonicalManifestPath, 'reopened');
  await fixture.adapter.getOpenBuffers(ManifestIdentity);
  assert.ok(observations.at(-1)?.[0]?.bufferId.startsWith(
    `${reopened.uri.toString()}#buffer-`
  ));
  assert.notEqual(observations.at(-1)?.[0]?.bufferId, originalBufferId);
  assert.equal(observations.at(-1)?.[0]?.text, 'reopened');
  subscription.dispose();
});

test('save targets only the exact selected manifest snapshot', async () => {
  const fixture = createFixture();
  const selected = fixture.addDocument(CanonicalManifestPath, 'selected', 'file', true);
  const unrelated = fixture.addDocument('C:\\other\\notes.json', 'unrelated', 'file', true);
  const snapshot = (await fixture.adapter.getOpenBuffers(ManifestIdentity))[0]!;

  assert.equal(await fixture.adapter.saveBuffer(snapshot), true);
  assert.equal(selected.saveCount, 1);
  assert.equal(unrelated.saveCount, 0);
  assert.equal(await fixture.adapter.saveBuffer(snapshot), false);
  assert.equal(selected.saveCount, 1);
});

test('same-URI close and reopen never reuses the prior buffer instance identity', async () => {
  const fixture = createFixture();
  const original = fixture.addDocument(CanonicalManifestPath, 'original', 'file', true);
  const originalSnapshot = (await fixture.adapter.getOpenBuffers(ManifestIdentity))[0]!;
  fixture.closeDocument(original);
  fixture.emitClose(original);
  const reopened = fixture.addDocument(CanonicalManifestPath, 'original', 'file', true);
  const reopenedSnapshot = (await fixture.adapter.getOpenBuffers(ManifestIdentity))[0]!;

  assert.notEqual(reopenedSnapshot.bufferId, originalSnapshot.bufferId);
  assert.equal(await fixture.adapter.saveBuffer(originalSnapshot), false);
  await assert.rejects(
    fixture.adapter.revealAndFocus(originalSnapshot),
    /no longer open/u
  );
  assert.equal(reopened.saveCount, 0);
});

test('preflight and recovery UI exposes only the specified choices', async () => {
  const fixture = createFixture();
  const context = comparisonContext();
  fixture.warningResponses.push(
    'Save and Continue',
    'Compare Changes',
    'Reload from Disk',
    'Keep Editing',
    'Reload from Disk',
    undefined
  );

  assert.equal(
    await fixture.adapter.chooseDirtyPreflight(context, context.buffer),
    'saveAndContinue'
  );
  assert.equal(await fixture.adapter.choosePreflightMismatch(context), 'compare');
  assert.equal(await fixture.adapter.choosePreflightMismatch(context), 'reload');
  assert.equal(await fixture.adapter.chooseRecovery(context), 'keepEditing');
  assert.equal(await fixture.adapter.confirmReload(context), true);
  assert.equal(
    await fixture.adapter.chooseDirtyPreflight(context, context.buffer),
    'cancel'
  );

  assert.deepEqual(fixture.warningCalls.map((call) => call.items), [
    ['Save and Continue', 'Cancel'],
    ['Compare Changes', 'Reload from Disk', 'Cancel'],
    ['Compare Changes', 'Reload from Disk', 'Cancel'],
    ['Compare Changes', 'Reload from Disk', 'Keep Editing'],
    ['Reload from Disk', 'Cancel'],
    ['Save and Continue', 'Cancel']
  ]);
  assert.ok(fixture.warningCalls.every((call) => call.modal));
});

test('comparison content is immutable and labeled before VS Code diff opens', async () => {
  const fixture = createFixture({ snapshotScheme: 'vba-tools-manifest-snapshot-test' });
  const context = comparisonContext();
  await fixture.adapter.showComparison(context);

  (context.buffer as { text: string }).text = 'mutated editor';
  context.disk.bytes[0] = 0xff;
  (context.disk as { text: string }).text = 'mutated disk';

  assert.equal(fixture.diffCalls.length, 1);
  const diff = fixture.diffCalls[0]!;
  assert.equal(fixture.registeredSnapshotScheme, 'vba-tools-manifest-snapshot-test');
  assert.match(diff.editor.toString(), /^vba-tools-manifest-snapshot-test:/u);
  assert.match(diff.disk.toString(), /^vba-tools-manifest-snapshot-test:/u);
  assert.equal(fixture.snapshotProvider?.provideTextDocumentContent(diff.editor), 'editor snapshot');
  assert.equal(fixture.snapshotProvider?.provideTextDocumentContent(diff.disk), 'disk snapshot');
  assert.match(diff.title, /FixtureProject \/ Book1/u);
  assert.ok(diff.title.includes(CanonicalManifestPath));
  assert.match(diff.title, /process=0/u);
  assert.match(diff.title, /Editor Snapshot ↔ Disk Snapshot/u);
});

test('explicit reveal focuses the exact buffer and revert refuses stale state', async () => {
  const fixture = createFixture();
  const document = fixture.addDocument(CanonicalManifestPath, 'editor snapshot', 'file', true);
  const snapshot = (await fixture.adapter.getOpenBuffers(ManifestIdentity))[0]!;

  await fixture.adapter.revealAndFocus(snapshot);
  assert.equal(fixture.activeDocument, document);
  assert.deepEqual(await fixture.adapter.getActiveFileIdentity(), ManifestIdentity);
  await fixture.adapter.revertBuffer(snapshot);
  assert.equal(fixture.revertCount, 1);

  document.change('new revision', true);
  await assert.rejects(
    fixture.adapter.revertBuffer(snapshot),
    /active unchanged buffer/u
  );
  assert.equal(fixture.revertCount, 1);
});

test('reports identify the disk basis without moving focus and clock wait is injectable', async () => {
  const waits: number[] = [];
  const fixture = createFixture({
    now: () => 42,
    wait: async (milliseconds) => {
      waits.push(milliseconds);
    }
  });
  fixture.adapter.report({
    kind: 'readOnlyDiskBasis',
    command: 'Reference List',
    projectName: 'FixtureProject',
    documentName: 'Book1',
    manifestPath: CanonicalManifestPath
  });
  fixture.adapter.report({
    kind: 'busy',
    command: 'Common Module Add',
    projectName: 'FixtureProject',
    documentName: 'Book1',
    manifestPath: CanonicalManifestPath,
    runningCommand: 'Reference Remove',
    runningProjectName: 'OtherProject',
    runningDocumentName: 'Book2',
    runningManifestPath: 'C:\\other\\vba-project.json'
  });
  fixture.adapter.report({
    kind: 'abnormalManifestChange',
    command: 'Reference Add',
    projectName: 'FixtureProject',
    manifestPath: CanonicalManifestPath,
    process: { exitCode: 9, cancelled: false, threw: false }
  });
  fixture.adapter.report({
    kind: 'manifestUntrusted',
    command: 'Common Module Update',
    projectName: 'FixtureProject',
    manifestPath: CanonicalManifestPath,
    process: { cancelled: true, threw: false }
  });
  await fixture.adapter.clock.wait(2_000);
  await Promise.resolve();

  assert.equal(fixture.adapter.clock.now(), 42);
  assert.deepEqual(waits, [2_000]);
  assert.match(fixture.outputLines[0]!, /readOnlyDiskBasis/u);
  assert.match(fixture.outputLines[0]!, /using the on-disk manifest/u);
  assert.match(fixture.outputLines[0]!, /manifest=C:\\canonical\\vba-project\.json/u);
  assert.match(fixture.outputLines[1]!, /runningCommand=Reference Remove/u);
  assert.match(fixture.outputLines[1]!, /runningProject=OtherProject/u);
  assert.match(fixture.outputLines[1]!, /runningDocument=Book2/u);
  assert.match(fixture.outputLines[1]!, /runningManifest=C:\\other\\vba-project\.json/u);
  assert.match(fixture.outputLines[2]!, /abnormalManifestChange/u);
  assert.match(fixture.outputLines[2]!, /process=9/u);
  assert.match(fixture.outputLines[3]!, /manifestUntrusted/u);
  assert.match(fixture.outputLines[3]!, /process=cancelled/u);
  assert.equal(fixture.outputShows[0], true);
  assert.equal(fixture.warningCalls[0]?.modal, false);
  assert.equal(fixture.activeDocument, undefined);
});

interface FakeSnapshotUri {
  readonly value: string;
  toString(): string;
}

interface WarningCall {
  readonly message: string;
  readonly modal: boolean;
  readonly detail: string | undefined;
  readonly items: readonly string[];
}

class FakeDocument implements ProjectManifestVscodeTextDocumentLike {
  public version = 1;
  public saveCount = 0;

  public constructor(
    public readonly uri: FakeDocumentUri,
    private text: string,
    public isDirty: boolean
  ) {}

  public getText(): string {
    return this.text;
  }

  public change(text: string, isDirty: boolean): void {
    this.text = text;
    this.isDirty = isDirty;
    this.version += 1;
  }

  public async save(): Promise<boolean> {
    this.saveCount += 1;
    this.isDirty = false;
    return true;
  }
}

class FakeDocumentUri {
  public constructor(
    public readonly fsPath: string,
    public readonly scheme: string
  ) {}

  public toString(): string {
    return `${this.scheme}:${this.fsPath}`;
  }
}

function createFixture(
  overrides: Partial<ProjectManifestMutationVscodeAdapterOptions<FakeSnapshotUri>> = {}
): {
  readonly adapter: ProjectManifestMutationVscodeAdapter;
  readonly warningResponses: Array<string | undefined>;
  readonly warningCalls: WarningCall[];
  readonly diffCalls: Array<{
    editor: FakeSnapshotUri;
    disk: FakeSnapshotUri;
    title: string;
  }>;
  readonly outputLines: string[];
  readonly outputShows: boolean[];
  readonly snapshotProvider: ProjectManifestSnapshotContentProvider<FakeSnapshotUri> | undefined;
  readonly registeredSnapshotScheme: string | undefined;
  readonly activeDocument: FakeDocument | undefined;
  readonly revertCount: number;
  addDocument(
    filePath: string,
    text: string,
    scheme?: string,
    isDirty?: boolean
  ): FakeDocument;
  closeDocument(document: FakeDocument): void;
  emitChange(document: FakeDocument): void;
  emitSave(document: FakeDocument): void;
  emitClose(document: FakeDocument): void;
} {
  const documents: FakeDocument[] = [];
  const openListeners: Array<(document: FakeDocument) => void> = [];
  const changeListeners: Array<(event: ProjectManifestVscodeDocumentChangeLike) => void> = [];
  const saveListeners: Array<(document: FakeDocument) => void> = [];
  const closeListeners: Array<(document: FakeDocument) => void> = [];
  const warningResponses: Array<string | undefined> = [];
  const warningCalls: WarningCall[] = [];
  const diffCalls: Array<{
    editor: FakeSnapshotUri;
    disk: FakeSnapshotUri;
    title: string;
  }> = [];
  const outputLines: string[] = [];
  const outputShows: boolean[] = [];
  let snapshotProvider: ProjectManifestSnapshotContentProvider<FakeSnapshotUri> | undefined;
  let registeredSnapshotScheme: string | undefined;
  let activeDocument: FakeDocument | undefined;
  let revertCount = 0;
  const identity = async (filePath: string): Promise<CommandPalettePathIdentity> => {
    if (filePath.toLowerCase().endsWith('vba-project.json')) {
      return ManifestIdentity;
    }
    return { canonicalPath: filePath };
  };
  const subscribe = <T>(listeners: T[], listener: T) => {
    listeners.push(listener);
    return {
      dispose(): void {
        const index = listeners.indexOf(listener);
        if (index >= 0) {
          listeners.splice(index, 1);
        }
      }
    };
  };
  const project: CommandPaletteProjectTarget = {
    projectRoot: 'C:\\canonical',
    manifestPath: CanonicalManifestPath,
    projectName: 'FixtureProject',
    primaryDocument: 'Book1',
    documents: []
  };
  const options: ProjectManifestMutationVscodeAdapterOptions<FakeSnapshotUri> = {
    resolvePathIdentity: identity,
    readFileBytes: async () => Uint8Array.from([1, 2, 3]),
    decodeManifestBytes: () => '{}',
    loadProjectTarget: async () => project,
    getOpenTextDocuments: () => documents,
    onDidOpenTextDocument: (listener) => subscribe(openListeners, listener as (document: FakeDocument) => void),
    onDidChangeTextDocument: (listener) => subscribe(changeListeners, listener),
    onDidSaveTextDocument: (listener) => subscribe(saveListeners, listener as (document: FakeDocument) => void),
    onDidCloseTextDocument: (listener) => subscribe(closeListeners, listener as (document: FakeDocument) => void),
    getActiveTextEditor: () => activeDocument === undefined
      ? undefined
      : { document: activeDocument },
    showTextDocument: async (document) => {
      activeDocument = document as FakeDocument;
      return { document };
    },
    executeRevertCommand: async () => {
      revertCount += 1;
    },
    showWarningMessage: async (message, options, ...items) => {
      warningCalls.push({
        message,
        modal: options.modal,
        detail: options.detail,
        items
      });
      return warningResponses.shift();
    },
    createSnapshotUri: (scheme, snapshotId, role) => ({
      value: `${scheme}:${snapshotId}:${role}`,
      toString() {
        return this.value;
      }
    }),
    registerSnapshotContentProvider: (scheme, provider) => {
      registeredSnapshotScheme = scheme;
      snapshotProvider = provider;
      return { dispose: () => undefined };
    },
    showDiff: async (editor, disk, title) => {
      diffCalls.push({ editor, disk, title });
    },
    outputChannel: {
      appendLine: (line) => outputLines.push(line),
      show: (preserveFocus) => outputShows.push(preserveFocus ?? false)
    },
    ...overrides
  };
  const adapter = createProjectManifestMutationVscodeAdapter(options);
  return {
    adapter,
    warningResponses,
    warningCalls,
    diffCalls,
    outputLines,
    outputShows,
    get snapshotProvider() {
      return snapshotProvider;
    },
    get registeredSnapshotScheme() {
      return registeredSnapshotScheme;
    },
    get activeDocument() {
      return activeDocument;
    },
    get revertCount() {
      return revertCount;
    },
    addDocument(filePath, text, scheme = 'file', isDirty = false) {
      const document = new FakeDocument(
        new FakeDocumentUri(filePath, scheme),
        text,
        isDirty
      );
      documents.push(document);
      for (const listener of openListeners) {
        listener(document);
      }
      return document;
    },
    closeDocument(document) {
      const index = documents.indexOf(document);
      if (index >= 0) {
        documents.splice(index, 1);
      }
    },
    emitChange(document) {
      for (const listener of changeListeners) {
        listener({ document });
      }
    },
    emitSave(document) {
      for (const listener of saveListeners) {
        listener(document);
      }
    },
    emitClose(document) {
      for (const listener of closeListeners) {
        listener(document);
      }
    }
  };
}

function comparisonContext(): ProjectManifestComparisonContext {
  return {
    command: 'Reference Add',
    projectName: 'FixtureProject',
    documentName: 'Book1',
    manifestPath: CanonicalManifestPath,
    phase: 'preflight',
    buffer: {
      filePath: CanonicalManifestPath,
      bufferId: `file:${CanonicalManifestPath}`,
      revision: 1,
      text: 'editor snapshot',
      isDirty: true
    },
    disk: {
      bytes: Uint8Array.from([1, 2, 3]),
      text: 'disk snapshot'
    },
    process: {
      exitCode: 0,
      cancelled: false,
      threw: false
    }
  };
}
