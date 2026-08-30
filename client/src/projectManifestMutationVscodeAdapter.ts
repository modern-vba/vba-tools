import * as path from 'node:path';

import {
  CommandPalettePathIdentity,
  CommandPaletteProjectTarget,
  sameCommandPalettePathIdentity
} from './commandPaletteTarget';
import {
  ProjectManifestComparisonContext,
  ProjectManifestEditorSnapshot,
  ProjectManifestMutationDisposable,
  ProjectManifestMutationPorts,
  ProjectManifestMutationReport,
  ProjectManifestMutationReportKind,
  ProjectManifestPreflightMismatchChoice,
  ProjectManifestRecoveryChoice,
  ProjectManifestReloadContext
} from './projectManifestMutation';

export const ProjectManifestSnapshotScheme = 'vba-tools-manifest-snapshot';

const SaveAndContinueLabel = 'Save and Continue';
const CancelLabel = 'Cancel';
const CompareChangesLabel = 'Compare Changes';
const ReloadFromDiskLabel = 'Reload from Disk';
const KeepEditingLabel = 'Keep Editing';

export interface ProjectManifestVscodeUriLike {
  readonly scheme: string;
  readonly fsPath: string;
  toString(): string;
}

export interface ProjectManifestVscodeTextDocumentLike {
  readonly uri: ProjectManifestVscodeUriLike;
  readonly version: number;
  readonly isDirty: boolean;
  getText(): string;
  save(): PromiseLike<boolean>;
}

export interface ProjectManifestVscodeTextEditorLike {
  readonly document: ProjectManifestVscodeTextDocumentLike;
}

export interface ProjectManifestVscodeDocumentChangeLike {
  readonly document: ProjectManifestVscodeTextDocumentLike;
}

export interface ProjectManifestVscodeOutputChannelLike {
  appendLine(value: string): void;
  show(preserveFocus?: boolean): void;
}

export interface ProjectManifestSnapshotUriLike {
  toString(): string;
}

export interface ProjectManifestSnapshotContentProvider<
  TSnapshotUri extends ProjectManifestSnapshotUriLike
> {
  provideTextDocumentContent(uri: TSnapshotUri): string | undefined;
}

export interface ProjectManifestMutationVscodeAdapterOptions<
  TSnapshotUri extends ProjectManifestSnapshotUriLike
> {
  readonly resolvePathIdentity: (
    filePath: string
  ) => Promise<CommandPalettePathIdentity>;
  readonly readFileBytes: (filePath: string) => PromiseLike<Uint8Array>;
  readonly decodeManifestBytes: (bytes: Uint8Array) => string;
  readonly loadProjectTarget: (
    manifestPath: string,
    bytes: Uint8Array
  ) => Promise<CommandPaletteProjectTarget | undefined>;
  readonly getOpenTextDocuments: () => readonly ProjectManifestVscodeTextDocumentLike[];
  readonly onDidOpenTextDocument: (
    listener: (document: ProjectManifestVscodeTextDocumentLike) => void
  ) => ProjectManifestMutationDisposable;
  readonly onDidChangeTextDocument: (
    listener: (event: ProjectManifestVscodeDocumentChangeLike) => void
  ) => ProjectManifestMutationDisposable;
  readonly onDidSaveTextDocument: (
    listener: (document: ProjectManifestVscodeTextDocumentLike) => void
  ) => ProjectManifestMutationDisposable;
  readonly onDidCloseTextDocument: (
    listener: (document: ProjectManifestVscodeTextDocumentLike) => void
  ) => ProjectManifestMutationDisposable;
  readonly getActiveTextEditor: () => ProjectManifestVscodeTextEditorLike | undefined;
  readonly showTextDocument: (
    document: ProjectManifestVscodeTextDocumentLike
  ) => PromiseLike<ProjectManifestVscodeTextEditorLike>;
  readonly executeRevertCommand: () => PromiseLike<unknown>;
  readonly showWarningMessage: (
    message: string,
    options: { readonly modal: boolean; readonly detail?: string | undefined },
    ...items: readonly string[]
  ) => PromiseLike<string | undefined>;
  readonly createSnapshotUri: (
    scheme: string,
    snapshotId: string,
    role: 'editor' | 'disk'
  ) => TSnapshotUri;
  readonly registerSnapshotContentProvider: (
    scheme: string,
    provider: ProjectManifestSnapshotContentProvider<TSnapshotUri>
  ) => ProjectManifestMutationDisposable;
  readonly showDiff: (
    editorSnapshot: TSnapshotUri,
    diskSnapshot: TSnapshotUri,
    title: string
  ) => PromiseLike<unknown>;
  readonly outputChannel: ProjectManifestVscodeOutputChannelLike;
  readonly snapshotScheme?: string | undefined;
  readonly now?: (() => number) | undefined;
  readonly wait?: ((milliseconds: number) => Promise<void>) | undefined;
}

export interface ProjectManifestMutationVscodeAdapter
  extends ProjectManifestMutationPorts, ProjectManifestMutationDisposable {}

interface ObservationState {
  queue: Promise<void>;
  failure?: unknown;
}

/**
 * Translates VS Code documents and UI into the editor-neutral mutation ports.
 * All policy decisions remain in ProjectManifestMutationCoordinator.
 */
export function createProjectManifestMutationVscodeAdapter<
  TSnapshotUri extends ProjectManifestSnapshotUriLike
>(
  options: ProjectManifestMutationVscodeAdapterOptions<TSnapshotUri>
): ProjectManifestMutationVscodeAdapter {
  const snapshotContents = new Map<string, string>();
  const bufferIds = new WeakMap<ProjectManifestVscodeTextDocumentLike, string>();
  const activeObservations = new Set<ProjectManifestMutationDisposable>();
  const observationStates = new Map<string, ObservationState>();
  const snapshotScheme = options.snapshotScheme ?? ProjectManifestSnapshotScheme;
  let bufferSequence = 0;
  let snapshotSequence = 0;
  let disposed = false;

  const snapshotProvider = options.registerSnapshotContentProvider(
    snapshotScheme,
    {
      provideTextDocumentContent: (uri) => snapshotContents.get(uri.toString())
    }
  );

  const getBufferId = (
    document: ProjectManifestVscodeTextDocumentLike
  ): string => {
    let bufferId = bufferIds.get(document);
    if (bufferId === undefined) {
      bufferSequence += 1;
      bufferId = `${document.uri.toString()}#buffer-${bufferSequence.toString(36)}`;
      bufferIds.set(document, bufferId);
    }
    return bufferId;
  };

  const getObservationState = (
    identity: CommandPalettePathIdentity
  ): ObservationState => {
    const key = pathIdentityKey(identity);
    let state = observationStates.get(key);
    if (state === undefined) {
      state = { queue: Promise.resolve() };
      observationStates.set(key, state);
    }
    return state;
  };

  const waitForObservationDrain = async (
    state: ObservationState
  ): Promise<void> => {
    let pending = state.queue;
    await pending;
    while (pending !== state.queue) {
      pending = state.queue;
      await pending;
    }
    if (state.failure !== undefined) {
      throw state.failure;
    }
  };

  const filterMatchingSnapshots = async (
    snapshots: readonly ProjectManifestEditorSnapshot[],
    manifestIdentity: CommandPalettePathIdentity
  ): Promise<readonly ProjectManifestEditorSnapshot[]> => {
    const matching: ProjectManifestEditorSnapshot[] = [];
    for (const snapshot of snapshots) {
      let identity: CommandPalettePathIdentity;
      try {
        identity = await options.resolvePathIdentity(snapshot.filePath);
      } catch (error) {
        if (lexicallySameFilePath(snapshot.filePath, manifestIdentity.canonicalPath)) {
          throw error;
        }
        continue;
      }
      if (sameCommandPalettePathIdentity(manifestIdentity, identity)) {
        matching.push(cloneEditorSnapshot(snapshot));
      }
    }
    return matching.sort((left, right) => left.bufferId.localeCompare(right.bufferId));
  };

  const getOpenBuffers = async (
    manifestIdentity: CommandPalettePathIdentity
  ): Promise<readonly ProjectManifestEditorSnapshot[]> => {
    await waitForObservationDrain(getObservationState(manifestIdentity));
    return filterMatchingSnapshots(
      captureOpenDocumentSnapshots(options.getOpenTextDocuments(), getBufferId),
      manifestIdentity
    );
  };

  const observeBuffers = (
    manifestIdentity: CommandPalettePathIdentity,
    listener: (buffers: readonly ProjectManifestEditorSnapshot[]) => void
  ): ProjectManifestMutationDisposable => {
    if (disposed) {
      throw new Error('The project manifest mutation VS Code adapter is disposed.');
    }

    const observationState = getObservationState(manifestIdentity);
    const enqueue = (
      eventDocument: ProjectManifestVscodeTextDocumentLike,
      eventKind: 'open' | 'change' | 'save' | 'close'
    ): void => {
      const snapshots = captureDocumentEventState(
        options.getOpenTextDocuments(),
        eventDocument,
        eventKind,
        getBufferId
      );
      observationState.queue = observationState.queue
        .then(async () => {
          const matching = await filterMatchingSnapshots(snapshots, manifestIdentity);
          listener(matching.map(cloneEditorSnapshot));
        })
        .catch((error: unknown) => {
          observationState.failure = error;
        });
    };

    const subscriptions = [
      options.onDidOpenTextDocument((document) => enqueue(document, 'open')),
      options.onDidChangeTextDocument((event) => enqueue(event.document, 'change')),
      options.onDidSaveTextDocument((document) => enqueue(document, 'save')),
      options.onDidCloseTextDocument((document) => enqueue(document, 'close'))
    ];
    let observationDisposed = false;
    const observation: ProjectManifestMutationDisposable = {
      dispose(): void {
        if (observationDisposed) {
          return;
        }
        observationDisposed = true;
        for (const subscription of subscriptions) {
          subscription.dispose();
        }
        activeObservations.delete(observation);
      }
    };
    activeObservations.add(observation);
    return observation;
  };

  const findCurrentDocument = (
    expected: ProjectManifestEditorSnapshot,
    requireExactSnapshot: boolean
  ): ProjectManifestVscodeTextDocumentLike | undefined => {
    const document = options.getOpenTextDocuments().find((candidate) =>
      isFileBacked(candidate) && getBufferId(candidate) === expected.bufferId
    );
    if (document === undefined) {
      return undefined;
    }
    if (!requireExactSnapshot) {
      return document;
    }
    const current = captureEditorSnapshot(document, getBufferId);
    return current !== undefined && editorSnapshotsEqual(current, expected)
      ? document
      : undefined;
  };

  const choose = async <TChoice extends string>(
    message: string,
    detail: string,
    items: readonly string[],
    choices: ReadonlyMap<string, TChoice>,
    fallback: TChoice
  ): Promise<TChoice> => {
    const selected = await options.showWarningMessage(
      message,
      { modal: true, detail },
      ...items
    );
    return choices.get(selected ?? '') ?? fallback;
  };

  const showComparison = async (
    context: ProjectManifestComparisonContext
  ): Promise<void> => {
    snapshotSequence += 1;
    const snapshotId = `${Date.now().toString(36)}-${snapshotSequence.toString(36)}`;
    const editorUri = options.createSnapshotUri(
      snapshotScheme,
      `${snapshotId}-editor`,
      'editor'
    );
    const diskUri = options.createSnapshotUri(
      snapshotScheme,
      `${snapshotId}-disk`,
      'disk'
    );
    snapshotContents.set(editorUri.toString(), `${context.buffer.text}`);
    snapshotContents.set(diskUri.toString(), `${context.disk.text}`);
    await options.showDiff(
      editorUri,
      diskUri,
      comparisonTitle(context)
    );
  };

  const report = (event: ProjectManifestMutationReport): void => {
    const message = formatMutationReport(event);
    options.outputChannel.appendLine(message);
    if (event.reportPresentation !== 'logOnly') {
      options.outputChannel.show(true);
    }
    if (
      event.reportPresentation !== 'logOnly'
      && WarningReportKinds.has(event.kind)
    ) {
      void Promise.resolve(options.showWarningMessage(
        message,
        { modal: false }
      )).catch(() => undefined);
    }
  };

  return {
    resolvePathIdentity: options.resolvePathIdentity,
    readManifestBytes: async (manifestPath) =>
      Uint8Array.from(await options.readFileBytes(manifestPath)),
    decodeManifestBytes: options.decodeManifestBytes,
    loadProjectTarget: async (manifestPath, bytes) =>
      options.loadProjectTarget(manifestPath, Uint8Array.from(bytes)),
    getOpenBuffers,
    observeBuffers,
    saveBuffer: async (buffer) => {
      const document = findCurrentDocument(buffer, true);
      return document === undefined ? false : document.save();
    },
    chooseDirtyPreflight: (context) => choose(
      'The selected project manifest has unsaved changes.',
      contextDetail(context),
      [SaveAndContinueLabel, CancelLabel],
      new Map([[SaveAndContinueLabel, 'saveAndContinue']]),
      'cancel'
    ),
    choosePreflightMismatch: (context) => choose(
      'The open project manifest differs from the current disk file.',
      comparisonDetail(context),
      [CompareChangesLabel, ReloadFromDiskLabel, CancelLabel],
      new Map<string, ProjectManifestPreflightMismatchChoice>([
        [CompareChangesLabel, 'compare'],
        [ReloadFromDiskLabel, 'reload']
      ]),
      'cancel'
    ),
    chooseRecovery: async (context) => {
      const selected = await options.showWarningMessage(
        'Project manifest editor coherence is unresolved.',
        { modal: true, detail: comparisonDetail(context) },
        CompareChangesLabel,
        ReloadFromDiskLabel,
        KeepEditingLabel
      );
      return new Map<string, ProjectManifestRecoveryChoice>([
        [CompareChangesLabel, 'compare'],
        [ReloadFromDiskLabel, 'reload'],
        [KeepEditingLabel, 'keepEditing']
      ]).get(selected ?? '');
    },
    showComparison,
    confirmReload: async (context: ProjectManifestReloadContext) => {
      const selected = await options.showWarningMessage(
        'Reload the selected project manifest from disk?',
        {
          modal: true,
          detail: `${comparisonDetail(context)}\n` +
            'Unsaved or competing editor content will be discarded.'
        },
        ReloadFromDiskLabel,
        CancelLabel
      );
      return selected === ReloadFromDiskLabel;
    },
    revealAndFocus: async (buffer) => {
      const document = findCurrentDocument(buffer, false);
      if (document === undefined) {
        throw new Error('The selected project manifest buffer is no longer open.');
      }
      await options.showTextDocument(document);
    },
    getActiveFileIdentity: async () => {
      const document = options.getActiveTextEditor()?.document;
      if (document === undefined || !isFileBacked(document)) {
        return undefined;
      }
      try {
        return await options.resolvePathIdentity(document.uri.fsPath);
      } catch {
        return undefined;
      }
    },
    revertBuffer: async (buffer) => {
      const document = findCurrentDocument(buffer, true);
      const active = options.getActiveTextEditor()?.document;
      if (document === undefined || active !== document) {
        throw new Error('The selected project manifest is not the active unchanged buffer.');
      }
      await options.executeRevertCommand();
    },
    clock: {
      now: options.now ?? Date.now,
      wait: options.wait ?? wait
    },
    report,
    dispose(): void {
      if (disposed) {
        return;
      }
      disposed = true;
      for (const observation of [...activeObservations]) {
        observation.dispose();
      }
      snapshotContents.clear();
      snapshotProvider.dispose();
    }
  };
}

const WarningReportKinds = new Set<ProjectManifestMutationReportKind>([
  'busy',
  'divergenceBlocked',
  'ambiguousBuffers',
  'preflightFailed',
  'preflightTargetChanged',
  'preflightMismatch',
  'reloadRefused',
  'abnormalManifestChange',
  'manifestUntrusted',
  'coherenceTimeout',
  'editorDivergence',
  'concurrentDiskChange',
  'keepEditingWarning',
  'manualRepairRequired',
  'readOnlyDiskBasis'
]);

function captureOpenDocumentSnapshots(
  documents: readonly ProjectManifestVscodeTextDocumentLike[],
  getBufferId: (document: ProjectManifestVscodeTextDocumentLike) => string
): readonly ProjectManifestEditorSnapshot[] {
  const snapshots: ProjectManifestEditorSnapshot[] = [];
  for (const document of documents) {
    const snapshot = captureEditorSnapshot(document, getBufferId);
    if (snapshot !== undefined) {
      snapshots.push(snapshot);
    }
  }
  return snapshots;
}

function captureDocumentEventState(
  documents: readonly ProjectManifestVscodeTextDocumentLike[],
  eventDocument: ProjectManifestVscodeTextDocumentLike,
  eventKind: 'open' | 'change' | 'save' | 'close',
  getBufferId: (document: ProjectManifestVscodeTextDocumentLike) => string
): readonly ProjectManifestEditorSnapshot[] {
  const snapshots = new Map(
    captureOpenDocumentSnapshots(documents, getBufferId).map((snapshot) => [
      snapshot.bufferId,
      snapshot
    ])
  );
  const eventBufferId = getBufferId(eventDocument);
  if (eventKind === 'close') {
    snapshots.delete(eventBufferId);
  } else {
    const eventSnapshot = captureEditorSnapshot(eventDocument, getBufferId);
    if (eventSnapshot !== undefined) {
      snapshots.set(eventBufferId, eventSnapshot);
    }
  }
  return [...snapshots.values()];
}

function captureEditorSnapshot(
  document: ProjectManifestVscodeTextDocumentLike,
  getBufferId: (document: ProjectManifestVscodeTextDocumentLike) => string
): ProjectManifestEditorSnapshot | undefined {
  if (!isFileBacked(document)) {
    return undefined;
  }
  return {
    filePath: document.uri.fsPath,
    bufferId: getBufferId(document),
    revision: document.version,
    text: document.getText(),
    isDirty: document.isDirty
  };
}

function isFileBacked(document: ProjectManifestVscodeTextDocumentLike): boolean {
  return document.uri.scheme.toLowerCase() === 'file';
}

function cloneEditorSnapshot(
  snapshot: ProjectManifestEditorSnapshot
): ProjectManifestEditorSnapshot {
  return { ...snapshot };
}

function editorSnapshotsEqual(
  left: ProjectManifestEditorSnapshot,
  right: ProjectManifestEditorSnapshot
): boolean {
  return left.filePath === right.filePath &&
    left.bufferId === right.bufferId &&
    left.revision === right.revision &&
    left.text === right.text &&
    left.isDirty === right.isDirty;
}

function contextDetail(context: {
  readonly command: string;
  readonly projectName: string;
  readonly documentName?: string | undefined;
  readonly manifestPath: string;
}): string {
  return [
    `Command: ${context.command}`,
    `Project: ${context.projectName}`,
    context.documentName === undefined ? undefined : `Document: ${context.documentName}`,
    `Manifest: ${context.manifestPath}`
  ].filter((value): value is string => value !== undefined).join('\n');
}

function comparisonDetail(context: ProjectManifestComparisonContext): string {
  const process = context.process.exitCode === undefined
    ? context.process.threw ? 'threw' : context.process.cancelled ? 'cancelled' : 'not started'
    : `exit ${context.process.exitCode}${context.process.cancelled ? ', cancelled' : ''}`;
  return `${contextDetail(context)}\nProcess: ${process}\n` +
    'Editor and disk snapshots are immutable comparison inputs.';
}

function comparisonTitle(context: ProjectManifestComparisonContext): string {
  const document = context.documentName === undefined
    ? ''
    : ` / ${context.documentName}`;
  const phase = context.phase === 'preflight' ? 'Preflight' : 'Post-mutation';
  return `VBA Tools: ${context.projectName}${document} — ${context.manifestPath} ` +
    `— ${phase} — process=${formatProcess(context.process)} ` +
    '— Editor Snapshot ↔ Disk Snapshot';
}

function formatMutationReport(report: ProjectManifestMutationReport): string {
  const document = report.documentName === undefined
    ? ''
    : `; document=${report.documentName}`;
  const running = report.runningCommand === undefined
    ? ''
    : `; runningCommand=${report.runningCommand}`;
  const runningProject = report.runningProjectName === undefined
    ? ''
    : `; runningProject=${report.runningProjectName}`;
  const runningDocument = report.runningDocumentName === undefined
    ? ''
    : `; runningDocument=${report.runningDocumentName}`;
  const runningManifest = report.runningManifestPath === undefined
    ? ''
    : `; runningManifest=${report.runningManifestPath}`;
  const process = report.process === undefined
    ? ''
    : `; process=${formatProcess(report.process)}`;
  const detail = report.detail === undefined ? '' : `; detail=${report.detail}`;
  return `${reportSummary(report.kind)} [manifest:${report.kind}] ` +
    `command=${report.command}; project=${report.projectName}` +
    `${document}; manifest=${report.manifestPath}${running}${runningProject}` +
    `${runningDocument}${runningManifest}${process}${detail}`;
}

function reportSummary(kind: ProjectManifestMutationReportKind): string {
  switch (kind) {
    case 'busy':
      return 'Another mutation is already running for this project manifest.';
    case 'divergenceBlocked':
      return 'The project manifest has unresolved editor divergence; no mutation was started.';
    case 'ambiguousBuffers':
      return 'More than one open file-backed buffer identifies the same project manifest.';
    case 'preflightFailed':
    case 'preflightTargetChanged':
      return 'Project manifest preflight could not prove a stable retained target; no mutation was started.';
    case 'preflightMismatch':
      return 'The clean editor buffer differs from the on-disk project manifest.';
    case 'reloadRefused':
      return 'Reload was refused because the disk, editor revision, or active editor changed.';
    case 'abnormalManifestChange':
      return 'The project manifest changed after an abnormal or cancelled process result; rollback is not proven.';
    case 'manifestUntrusted':
    case 'manualRepairRequired':
      return 'The on-disk project manifest is missing, unreadable, or unusable; repair it before another mutation.';
    case 'coherenceTimeout':
      return 'VS Code did not prove native project manifest synchronization within two seconds.';
    case 'editorDivergence':
      return 'Competing project manifest editor content was preserved for explicit recovery.';
    case 'concurrentDiskChange':
      return 'The on-disk project manifest changed after the immutable process snapshot.';
    case 'keepEditingWarning':
      return 'Keep Editing preserves the buffer; a later save can intentionally replace the observed CLI-era disk state.';
    case 'readOnlyDiskBasis':
      return 'This read-only command is using the on-disk manifest while editor coherence is unresolved.';
    default:
      return 'Project manifest mutation state changed.';
  }
}

function lexicallySameFilePath(left: string, right: string): boolean {
  const normalizedLeft = path.normalize(path.resolve(left));
  const normalizedRight = path.normalize(path.resolve(right));
  return process.platform === 'win32'
    ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
    : normalizedLeft === normalizedRight;
}

function pathIdentityKey(identity: CommandPalettePathIdentity): string {
  if (identity.objectIdentity !== undefined) {
    return `object:${identity.objectIdentity.toLowerCase()}`;
  }
  const canonicalPath = path.normalize(path.resolve(identity.canonicalPath));
  return `path:${process.platform === 'win32' ? canonicalPath.toLowerCase() : canonicalPath}`;
}

function formatProcess(process: {
  readonly exitCode?: number | undefined;
  readonly cancelled: boolean;
  readonly threw: boolean;
}): string {
  if (process.threw) {
    return 'threw';
  }
  if (process.exitCode === undefined) {
    return process.cancelled ? 'cancelled' : 'not-started';
  }
  return `${process.exitCode}${process.cancelled ? '-cancelled' : ''}`;
}

async function wait(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}
