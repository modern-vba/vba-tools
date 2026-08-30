import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { TextDecoder } from 'node:util';
import {
  Position,
  TextDocument,
  Uri,
  ViewColumn,
  WorkspaceEdit,
  commands,
  window,
  workspace
} from 'vscode';

import {
  CommandPaletteTarget,
  resolveCommandPaletteProjectTargetFromManifestText
} from '../../commandPaletteTarget';
import { resolveCommandPalettePathIdentity } from '../../commandPaletteTargetAdapter';
import { ProjectManifestMutationCoordinator } from '../../projectManifestMutation';
import {
  createProjectManifestMutationVscodeAdapter
} from '../../projectManifestMutationVscodeAdapter';

interface ManifestFixture {
  root: string;
  manifestPath: string;
  unrelatedManifestPath: string;
  sentinelPath: string;
  initialText: string;
  unrelatedInitialText: string;
  target: CommandPaletteTarget;
}

export interface HarnessOptions {
  decodeManifestBytes?: (bytes: Uint8Array) => string;
  choose?: (
    message: string,
    items: readonly string[]
  ) => string | undefined | Promise<string | undefined>;
  showTextDocument?: (document: TextDocument) => Promise<void>;
  executeRevert?: () => Promise<void>;
  showDiff?: (
    editorSnapshot: Uri,
    diskSnapshot: Uri,
    title: string
  ) => Promise<void>;
}

export interface MutationHarness {
  coordinator: ProjectManifestMutationCoordinator;
  reports: string[];
  outputShows: boolean[];
  prompts: string[];
  savedPaths: string[];
  focusCalls: number;
  revertCalls: number;
  dispose(): void;
}

let harnessSequence = 0;

export async function runProjectManifestMutationIntegrationTests(): Promise<void> {
  await runTest(
    'log-only real coordinator reports never reveal Output or show warning prompts',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({});
      try {
        const noOp = await harness.coordinator.run({
          command: 'Common Module Add',
          target: fixture.target,
          reportPresentation: 'logOnly',
          run: async () => ({ exitCode: 0, cancelled: false })
        });
        assert.equal(noOp.status, 'completed');
        assert.equal(noOp.manifestOutcome, 'unchanged');

        const changed = await harness.coordinator.run({
          command: 'Common Module Update',
          target: fixture.target,
          reportPresentation: 'logOnly',
          run: async () => {
            await writeFile(
              fixture.manifestPath,
              manifestText('MutationFixture', 'changed'),
              'utf8'
            );
            return { exitCode: 0, cancelled: false };
          }
        });
        assert.equal(changed.status, 'completed');
        assert.equal(changed.manifestOutcome, 'changed');
        assert.equal(changed.coherence, 'coherent');

        const untrusted = await harness.coordinator.run({
          command: 'Common Module Update',
          target: fixture.target,
          reportPresentation: 'logOnly',
          run: async () => {
            await writeFile(fixture.manifestPath, '{ invalid json\n', 'utf8');
            return { exitCode: 0, cancelled: false };
          }
        });
        assert.equal(untrusted.status, 'completed');
        assert.equal(untrusted.manifestOutcome, 'untrusted');
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestUnchanged]')));
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestChanged]')));
        assert.ok(harness.reports.some((line) =>
          line.includes('[manifest:manifestUntrusted]')));
        assert.deepEqual(harness.outputShows, []);
        assert.deepEqual(harness.prompts, []);
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'manifest preflight saves only the selected real dirty VS Code document',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({
        choose: (message) => message.includes('unsaved changes')
          ? 'Save and Continue'
          : undefined
      });
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        const unrelated = await openFileDocument(fixture.unrelatedManifestPath);
        await appendTrailingSpace(selected);
        await appendTrailingSpace(unrelated);
        assert.equal(selected.isDirty, true);
        assert.equal(unrelated.isDirty, true);

        let launches = 0;
        const result = await harness.coordinator.run({
          command: 'Reference Add',
          target: fixture.target,
          run: async () => {
            launches += 1;
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'completed');
        assert.equal(result.manifestOutcome, 'unchanged');
        assert.equal(launches, 1);
        assert.equal(selected.isDirty, false);
        assert.equal(unrelated.isDirty, true);
        assert.deepEqual(
          harness.savedPaths.map(normalizePath),
          [normalizePath(fixture.manifestPath)]
        );
        assert.equal(
          await readFile(fixture.unrelatedManifestPath, 'utf8'),
          fixture.unrelatedInitialText
        );
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'dirty-manifest cancellation preserves the buffer and starts no fake process',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({
        choose: (message) => message.includes('unsaved changes') ? 'Cancel' : undefined
      });
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        await appendTrailingSpace(selected);
        const dirtyText = selected.getText();
        let launches = 0;

        const result = await harness.coordinator.run({
          command: 'Common Module Add',
          target: fixture.target,
          run: async () => {
            launches += 1;
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'rejected');
        assert.equal(result.reason, 'preflight');
        assert.equal(launches, 0);
        assert.equal(selected.isDirty, true);
        assert.equal(selected.getText(), dirtyText);
        assert.deepEqual(harness.savedPaths, []);
        assert.equal(await readFile(fixture.manifestPath, 'utf8'), fixture.initialText);
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'native clean external synchronization converges without focus, save, or revert',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({});
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        await window.showTextDocument(selected, {
          viewColumn: ViewColumn.One,
          preserveFocus: false,
          preview: false
        });
        const sentinel = await showSentinel(fixture.sentinelPath);
        const changedText = manifestText('MutationFixture', 'native-sync');

        const result = await harness.coordinator.run({
          command: 'Reference Remove',
          target: fixture.target,
          run: async () => {
            await writeFile(fixture.manifestPath, changedText, 'utf8');
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'completed');
        assert.equal(result.manifestOutcome, 'changed');
        assert.equal(
          result.coherence,
          'coherent',
          JSON.stringify({ result, reports: harness.reports }, undefined, 2)
        );
        assert.equal(window.activeTextEditor?.document.uri.toString(), sentinel.uri.toString());
        assert.equal(harness.focusCalls, 0);
        assert.equal(harness.revertCalls, 0);
        assert.deepEqual(harness.savedPaths, []);
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'competing edits are preserved and immutable snapshots are wired to a real diff',
    async () => {
      const fixture = await createFixture();
      let recoveryActiveUri: string | undefined;
      let comparedEditorText: string | undefined;
      let comparedDiskText: string | undefined;
      let comparedTitle: string | undefined;
      const harness = createHarness({
        choose: (message) => {
          if (message === 'Project manifest editor coherence is unresolved.') {
            recoveryActiveUri = window.activeTextEditor?.document.uri.toString();
            return 'Compare Changes';
          }
          return undefined;
        },
        showDiff: async (editorUri, diskUri, title) => {
          comparedEditorText = (await workspace.openTextDocument(editorUri)).getText();
          comparedDiskText = (await workspace.openTextDocument(diskUri)).getText();
          comparedTitle = title;
          await commands.executeCommand(
            'vscode.diff',
            editorUri,
            diskUri,
            title,
            { preview: false }
          );
        }
      });
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        const sentinel = await showSentinel(fixture.sentinelPath);
        const changedText = manifestText('MutationFixture', 'disk-snapshot');
        let competingText = '';

        const result = await harness.coordinator.run({
          command: 'Common Module Update',
          target: { project: fixture.target.project },
          run: async () => {
            await appendTrailingSpace(selected);
            competingText = selected.getText();
            await writeFile(fixture.manifestPath, changedText, 'utf8');
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'completed');
        assert.equal(result.manifestOutcome, 'changed');
        assert.equal(result.coherence, 'diverged');
        assert.equal(recoveryActiveUri, sentinel.uri.toString());
        assert.equal(selected.isDirty, true);
        assert.equal(selected.getText(), competingText);
        assert.equal(comparedEditorText, competingText);
        assert.equal(comparedDiskText, changedText);
        assert.match(comparedTitle ?? '', /Editor Snapshot ↔ Disk Snapshot/);
        assert.equal(harness.focusCalls, 0);
        assert.equal(harness.revertCalls, 0);
        assert.deepEqual(harness.savedPaths, []);
        assert.ok(harness.reports.some((line) => line.includes('[manifest:comparisonShown]')));
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'explicit recovery focuses and reverts the selected manifest only after confirmation',
    async () => {
      const fixture = await createFixture();
      const harness = createHarness({
        choose: reloadChoice,
        executeRevert: async () => {
          await commands.executeCommand('workbench.action.files.revert');
        }
      });
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        await showSentinel(fixture.sentinelPath);
        const changedText = manifestText('MutationFixture', 'reload-snapshot');

        const result = await harness.coordinator.run({
          command: 'Reference Add',
          target: fixture.target,
          run: async () => {
            await appendTrailingSpace(selected);
            await writeFile(fixture.manifestPath, changedText, 'utf8');
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'completed');
        assert.equal(result.coherence, 'coherent');
        assert.equal(harness.focusCalls, 1);
        assert.equal(harness.revertCalls, 1);
        assert.equal(
          normalizePath(window.activeTextEditor?.document.uri.fsPath ?? ''),
          normalizePath(fixture.manifestPath)
        );
        assert.equal(selected.isDirty, false);
        assert.equal(selected.getText(), changedText);
        assert.deepEqual(harness.savedPaths, []);
        assert.ok(harness.reports.some((line) => line.includes('[manifest:reloadCompleted]')));
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );

  await runTest(
    'reload refuses a stale post-snapshot disk state before invoking revert',
    async () => {
      const fixture = await createFixture();
      const staleText = manifestText('MutationFixture', 'stale-after-focus');
      const harness = createHarness({
        choose: reloadChoice,
        showTextDocument: async (document) => {
          await window.showTextDocument(document, {
            viewColumn: ViewColumn.One,
            preserveFocus: false,
            preview: false
          });
          await writeFile(fixture.manifestPath, staleText, 'utf8');
        },
        executeRevert: async () => {
          throw new Error('Revert must not run after the stale disk precheck fails.');
        }
      });
      try {
        const selected = await openFileDocument(fixture.manifestPath);
        await showSentinel(fixture.sentinelPath);
        const changedText = manifestText('MutationFixture', 'reload-authority');
        let competingText = '';

        const result = await harness.coordinator.run({
          command: 'Reference Remove',
          target: fixture.target,
          run: async () => {
            await appendTrailingSpace(selected);
            competingText = selected.getText();
            await writeFile(fixture.manifestPath, changedText, 'utf8');
            return { exitCode: 0, cancelled: false };
          }
        });

        assert.equal(result.status, 'completed');
        assert.equal(result.coherence, 'diverged');
        assert.equal(harness.focusCalls, 1);
        assert.equal(harness.revertCalls, 0);
        assert.equal(selected.isDirty, true);
        assert.equal(selected.getText(), competingText);
        assert.equal(await readFile(fixture.manifestPath, 'utf8'), staleText);
        assert.deepEqual(harness.savedPaths, []);
        assert.ok(harness.reports.some((line) => line.includes('[manifest:reloadRefused]')));
      } finally {
        await cleanupHarness(harness, fixture.root);
      }
    }
  );
}

export function createHarness(options: HarnessOptions): MutationHarness {
  const harnessId = ++harnessSequence;
  const reports: string[] = [];
  const outputShows: boolean[] = [];
  const prompts: string[] = [];
  const savedPaths: string[] = [];
  let focusCalls = 0;
  let revertCalls = 0;
  const output = window.createOutputChannel(
    `VBA Tools Manifest Mutation Integration ${harnessId}`
  );
  const saveSubscription = workspace.onDidSaveTextDocument((document) => {
    if (document.uri.scheme === 'file') {
      savedPaths.push(document.uri.fsPath);
    }
  });
  const decodeManifestBytes = options.decodeManifestBytes
    ?? ((bytes: Uint8Array): string =>
      new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  const adapter = createProjectManifestMutationVscodeAdapter({
    snapshotScheme: `vba-tools-manifest-mutation-test-${harnessId}`,
    resolvePathIdentity: resolveCommandPalettePathIdentity,
    readFileBytes: (filePath) => workspace.fs.readFile(Uri.file(filePath)),
    decodeManifestBytes,
    loadProjectTarget: (manifestPath, bytes) =>
      resolveCommandPaletteProjectTargetFromManifestText(
        manifestPath,
        decodeManifestBytes(bytes),
        resolveCommandPalettePathIdentity
      ),
    getOpenTextDocuments: () => workspace.textDocuments,
    onDidOpenTextDocument: (listener) => workspace.onDidOpenTextDocument(listener),
    onDidChangeTextDocument: (listener) => workspace.onDidChangeTextDocument(listener),
    onDidSaveTextDocument: (listener) => workspace.onDidSaveTextDocument(listener),
    onDidCloseTextDocument: (listener) => workspace.onDidCloseTextDocument(listener),
    getActiveTextEditor: () => window.activeTextEditor,
    showTextDocument: async (document) => {
      focusCalls += 1;
      if (options.showTextDocument !== undefined) {
        await options.showTextDocument(document as TextDocument);
        return window.activeTextEditor!;
      }
      return window.showTextDocument(document as TextDocument, {
        viewColumn: ViewColumn.One,
        preserveFocus: false,
        preview: false
      });
    },
    executeRevertCommand: async () => {
      revertCalls += 1;
      if (options.executeRevert !== undefined) {
        await options.executeRevert();
        return;
      }
      await commands.executeCommand('workbench.action.files.revert');
    },
    showWarningMessage: async (message, _dialogOptions, ...items) => {
      prompts.push(message);
      return options.choose?.(message, items);
    },
    createSnapshotUri: (scheme, snapshotId, role) => Uri.from({
      scheme,
      path: `/${snapshotId}-${role}.json`
    }),
    registerSnapshotContentProvider: (scheme, provider) =>
      workspace.registerTextDocumentContentProvider(scheme, {
        provideTextDocumentContent: (uri) => provider.provideTextDocumentContent(uri)
      }),
    showDiff: async (editorSnapshot, diskSnapshot, title) => {
      if (options.showDiff !== undefined) {
        await options.showDiff(editorSnapshot, diskSnapshot, title);
        return;
      }
      await commands.executeCommand(
        'vscode.diff',
        editorSnapshot,
        diskSnapshot,
        title,
        { preview: false }
      );
    },
    outputChannel: {
      appendLine: (value) => {
        reports.push(value);
        output.appendLine(value);
      },
      show: (preserveFocus) => {
        outputShows.push(preserveFocus ?? false);
        output.show(preserveFocus);
      }
    }
  });
  const coordinator = new ProjectManifestMutationCoordinator(adapter);

  return {
    coordinator,
    reports,
    outputShows,
    prompts,
    savedPaths,
    get focusCalls() {
      return focusCalls;
    },
    get revertCalls() {
      return revertCalls;
    },
    dispose: () => {
      saveSubscription.dispose();
      adapter.dispose();
      output.dispose();
    }
  };
}

async function createFixture(): Promise<ManifestFixture> {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_MUTATION_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'Manifest mutation fixture root was not configured.');
  const root = await mkdtemp(path.join(fixtureRoot, 'case-'));
  const projectRoot = path.join(root, 'selected');
  const unrelatedRoot = path.join(root, 'unrelated');
  await mkdir(path.join(projectRoot, 'src', 'Book1'), { recursive: true });
  await mkdir(path.join(unrelatedRoot, 'src', 'Book1'), { recursive: true });
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const unrelatedManifestPath = path.join(unrelatedRoot, 'vba-project.json');
  const sentinelPath = path.join(root, 'sentinel.txt');
  const initialText = manifestText('MutationFixture', 'initial');
  const unrelatedInitialText = manifestText('UnrelatedFixture', 'initial');
  await writeFile(manifestPath, initialText, 'utf8');
  await writeFile(unrelatedManifestPath, unrelatedInitialText, 'utf8');
  await writeFile(sentinelPath, 'sentinel\n', 'utf8');
  const project = await resolveCommandPaletteProjectTargetFromManifestText(
    manifestPath,
    initialText,
    resolveCommandPalettePathIdentity
  );
  assert.ok(project);
  assert.equal(project.documents.length, 1);

  return {
    root,
    manifestPath,
    unrelatedManifestPath,
    sentinelPath,
    initialText,
    unrelatedInitialText,
    target: {
      project,
      document: project.documents[0]!
    }
  };
}

function manifestText(projectName: string, marker: string): string {
  return `${JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        kind: 'excel',
        sourcePath: 'src/Book1',
        templatePath: 'src/Book1/Book1.xlsm',
        binPath: 'bin/Book1.xlsm',
        publishPath: 'publish/Book1.xlsm',
        commonModules: [],
        references: []
      }
    },
    mutationIntegrationMarker: marker
  }, undefined, 2)}\n`;
}

async function openFileDocument(filePath: string): Promise<TextDocument> {
  return workspace.openTextDocument(Uri.file(filePath));
}

async function showSentinel(filePath: string): Promise<TextDocument> {
  const document = await openFileDocument(filePath);
  await window.showTextDocument(document, {
    viewColumn: ViewColumn.Beside,
    preserveFocus: false,
    preview: false
  });
  return document;
}

async function appendTrailingSpace(document: TextDocument): Promise<void> {
  const edit = new WorkspaceEdit();
  const lastLine = document.lineAt(document.lineCount - 1);
  edit.insert(document.uri, new Position(lastLine.lineNumber, lastLine.text.length), ' ');
  assert.equal(await workspace.applyEdit(edit), true);
  await waitFor(() => document.isDirty, 1_000, 'VS Code did not mark the manifest dirty.');
}

function reloadChoice(message: string): string | undefined {
  if (message === 'Project manifest editor coherence is unresolved.' ||
      message === 'Reload the selected project manifest from disk?') {
    return 'Reload from Disk';
  }
  return undefined;
}

async function cleanupFixture(root: string): Promise<void> {
  const documents = workspace.textDocuments.filter((document) =>
    document.uri.scheme === 'file' && isInside(root, document.uri.fsPath));
  for (const document of documents) {
    await window.showTextDocument(document, {
      viewColumn: ViewColumn.One,
      preserveFocus: false,
      preview: false
    });
    if (document.isDirty) {
      await commands.executeCommand('workbench.action.files.revert');
    }
  }
  await commands.executeCommand('workbench.action.closeAllEditors');
}

async function cleanupHarness(
  harness: MutationHarness,
  fixtureRoot: string
): Promise<void> {
  try {
    await cleanupFixture(fixtureRoot);
  } finally {
    harness.dispose();
  }
}

function isInside(root: string, candidate: string): boolean {
  const relative = path.relative(root, candidate);
  return relative.length === 0 || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function normalizePath(value: string): string {
  const normalized = path.normalize(value);
  return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

async function waitFor(
  predicate: () => boolean,
  timeoutMilliseconds: number,
  failureMessage: string
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (!predicate() && Date.now() < deadline) {
    await new Promise<void>((resolve) => setTimeout(resolve, 25));
  }
  assert.equal(predicate(), true, failureMessage);
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
