import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  mkdir,
  readFile,
  readdir,
  stat,
  writeFile
} from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  commands,
  extensions,
  languages,
  Uri,
  version as vscodeVersion,
  window
} from 'vscode';
import {
  CorrelatedSchedulerTiming,
  CorrelatedSchedulerRequestTiming,
  createSemanticReadinessPerformanceReport,
  SchedulerCapturedTimingPathEvidence,
  SchedulerTimingFileEvidence,
  SchedulerTimingPathEvidence,
  selectUniqueSemanticRequestTiming
} from '../../semanticReadinessPerformanceEvidence';
import type {
  VbaToolsExtensionHostTestApi
} from '../../intrinsicHostEventCatalogExtensionHostProbe';

const MeasurementBudgetMilliseconds = 30_000;
const LateReadinessBudgetMilliseconds = 60_000;
const ExtensionIdentifier = 'modern-vba.vba-tools';
const ActiveSourceEnvironment = 'VBA_TOOLS_COMMON_MODULES_ACTIVE_SOURCE';
const TimingDirectoryEnvironment =
  'VBA_TOOLS_SEMANTIC_READINESS_TIMING_DIRECTORY';
const ResultPathEnvironment = 'VBA_TOOLS_SEMANTIC_READINESS_RESULT';
const CorpusRevisionEnvironment =
  'VBA_TOOLS_COMMON_MODULES_CORPUS_REVISION';
const CorpusDirtyEnvironment = 'VBA_TOOLS_COMMON_MODULES_CORPUS_DIRTY';

export async function runSemanticReadinessPerformanceMeasurement(): Promise<void> {
  assert.equal(process.platform, 'win32', 'This Release measurement is Windows-only.');
  const activeSourcePath = requiredAbsolutePath(ActiveSourceEnvironment);
  const timingDirectory = requiredAbsolutePath(TimingDirectoryEnvironment);
  const resultPath = requiredAbsolutePath(ResultPathEnvironment);
  const activeSourceUri = Uri.file(activeSourcePath);
  const restoredEditor = await waitForRestoredEditor(activeSourcePath);
  assert.equal(
    window.visibleTextEditors.length,
    1,
    'The performance scenario requires exactly one restored editor.'
  );
  assert.equal(
    restoredEditor.document.languageId,
    'plaintext',
    'The restored editor must remain inert until the measured activate() call.'
  );

  const extension = extensions.getExtension<VbaToolsExtensionHostTestApi>(
    ExtensionIdentifier
  );
  assert.ok(extension, 'The VBA Tools development extension must be available.');
  assert.equal(
    extension.isActive,
    false,
    'The performance runner must start measuring before extension activation.'
  );

  const activateStartedAtUnixMilliseconds = Date.now();
  const activation = Promise.resolve(extension.activate());
  void Promise.resolve(activation).catch((error: unknown) => {
    console.error('Measured extension activation failed.', error);
  });
  const vbaDocument = await languages.setTextDocumentLanguage(
    restoredEditor.document,
    'vba'
  );
  assert.equal(vbaDocument.uri.fsPath.toLowerCase(), activeSourcePath.toLowerCase());
  const acceptedDocumentVersion = vbaDocument.version;

  const deadline = activateStartedAtUnixMilliseconds
    + MeasurementBudgetMilliseconds;
  const processStartedAtUnixMilliseconds = await waitForDirectoryBirthTime(
    timingDirectory,
    deadline
  );
  const initialization = await waitForSchedulerTiming(
    timingDirectory,
    'initialize',
    'completed',
    deadline
  );
  const didOpen = await waitForSchedulerTiming(
    timingDirectory,
    'textDocument/didOpen',
    'completed',
    deadline
  );

  const semanticTimingCheckpoint = await captureSchedulerTimingCheckpoint(
    timingDirectory
  );
  const semanticRequestStartedAtUnixMilliseconds = Date.now();
  const semanticTokens = await withDeadline(
    commands.executeCommand<unknown>(
      '_provideDocumentSemanticTokens',
      activeSourceUri
    ),
    deadline,
    'first semantic-token response'
  );
  const tokenResponseAtUnixMilliseconds = Date.now();
  const semanticTokenDataLength = getSemanticTokenDataLength(semanticTokens);
  const semanticRequest = await waitForUniqueSemanticRequestTiming(
    timingDirectory,
    semanticTimingCheckpoint,
    'textDocument/semanticTokens/full',
    deadline,
    semanticRequestStartedAtUnixMilliseconds,
    tokenResponseAtUnixMilliseconds
  );

  const lateReadinessDeadline = Date.now() + LateReadinessBudgetMilliseconds;
  const api = await awaitActivationWithNotificationDismissal(
    activation,
    lateReadinessDeadline
  );
  const extensionActivationAwaitedAtUnixMilliseconds = Date.now();
  assert.ok(api, 'The performance runner requires the extension-host Test API.');
  const companion = api.companionExecutable;
  const catalog = api.intrinsicHostEventCatalog;
  await waitForCondition(
    () => companion.snapshot().invocations.length === 1,
    lateReadinessDeadline,
    'blocked companion capabilities invocation'
  );
  assert.equal(companion.snapshot().pendingInvocationCount, 1);
  assert.deepEqual(
    companion.snapshot().invocations.map((invocation) => invocation.args),
    [['capabilities', '--format', 'json']]
  );
  assert.equal(catalog.snapshot().invocations.length, 0);

  const lateTimingCheckpoint = await captureSchedulerTimingCheckpoint(
    timingDirectory
  );
  const capabilityBarrierReleasedAtUnixMilliseconds = Date.now();
  companion.completeInvocation(
    0,
    await compatibleCapabilitiesResult(extension.extensionPath)
  );
  await waitForCondition(
    () => companion.snapshot().pendingInvocationCount === 0,
    lateReadinessDeadline,
    'companion capabilities settlement'
  );
  await waitForCondition(
    () => catalog.snapshot().invocations.length === 1,
    lateReadinessDeadline,
    'automatic UserForm catalog invocation'
  );
  const userFormInvocationStartedAtUnixMilliseconds = Date.now();
  assert.equal(catalog.snapshot().pendingInvocationCount, 1);
  assert.deepEqual(catalog.snapshot().invocations, [{
    trigger: 'activation',
    args: ['host-event', 'list', '--format', 'json']
  }]);

  const userFormResultReleasedAtUnixMilliseconds = Date.now();
  catalog.completeInvocation(0, successfulCatalogResult);
  await waitForCondition(
    () => catalog.snapshot().notifications.length === 1
      && catalog.snapshot().transitions.some((transition) =>
        transition.kind === 'committed'
        && transition.trigger === 'activation'
      ),
    lateReadinessDeadline,
    'automatic UserForm catalog publication'
  );
  assertSuccessfulUserFormNotification(catalog.snapshot().notifications[0]);

  const [companionPublication, userFormPublication] =
    await Promise.all([
      waitForUniqueSchedulerTimingAfterCheckpoint(
        timingDirectory,
        lateTimingCheckpoint,
        'vba/companionExecutable',
        lateReadinessDeadline
      ),
      waitForUniqueSchedulerTimingAfterCheckpoint(
        timingDirectory,
        lateTimingCheckpoint,
        'vba/intrinsicHostEventCatalog',
        lateReadinessDeadline
      )
    ]);
  await waitForCondition(
    () => companion.snapshot().pendingInvocationCount === 0
      && catalog.snapshot().pendingInvocationCount === 0,
    lateReadinessDeadline,
    'companion and UserForm probe settlement'
  );
  const lateReadinessSettledAtUnixMilliseconds = Date.now();

  const corpus = await captureCorpusEvidence(activeSourcePath);
  const activeSourceBytes = await readFile(activeSourcePath);
  const activeSourceStat = await stat(activeSourcePath);
  const cpu = os.cpus()[0];
  const report = createSemanticReadinessPerformanceReport({
    budgetMilliseconds: MeasurementBudgetMilliseconds,
    timeline: {
      activateStartedAtUnixMilliseconds,
      languageServerProcessStartedAtUnixMilliseconds:
        processStartedAtUnixMilliseconds,
      initializationCompletedAtUnixMilliseconds: initialization.recordedAt,
      didOpenCompletedAtUnixMilliseconds: didOpen.recordedAt,
      semanticRequestStartedAtUnixMilliseconds,
      semanticSnapshotCompletedAtUnixMilliseconds:
        semanticRequest.capturedAtUnixMilliseconds,
      tokenResponseAtUnixMilliseconds
    },
    semanticTokenDataLength,
    acceptedDocumentVersion,
    responseDocumentVersion: vbaDocument.version,
    corpus,
    sourceRevision: {
      repositoryCommit: requiredEnvironment(CorpusRevisionEnvironment),
      repositoryDirty: requiredEnvironment(CorpusDirtyEnvironment) === 'true',
      activeSourceSha256: createHash('sha256')
        .update(activeSourceBytes)
        .digest('hex'),
      activeDocumentVersion: acceptedDocumentVersion,
      activeSourceLastWriteTimeUtc: activeSourceStat.mtime.toISOString()
    },
    runtime: {
      operatingSystem: `${os.type()} ${os.release()}`,
      architecture: os.arch(),
      cpu: cpu?.model ?? 'unknown',
      logicalProcessorCount: os.cpus().length,
      totalMemoryBytes: os.totalmem(),
      freeMemoryBytes: os.freemem(),
      vscodeVersion,
      electronVersion: process.versions.electron ?? 'unknown',
      nodeVersion: process.versions.node,
      languageServerBuildConfiguration: 'Release',
      languageServerTargetFramework: 'net10.0'
    },
    cachePolicy: {
      extensionUserData: 'fresh',
      referenceCatalog: 'fresh',
      operatingSystemFileCache: 'uncontrolled'
    },
    competingLoad: {
      syntheticLoad: 'none',
      ambientLoad: 'uncontrolled'
    },
    schedulerTimingPath: {
      directoryPath: timingDirectory,
      semanticRequest: toCapturedSchedulerTimingPath(
        timingDirectory,
        'textDocument/semanticTokens/full',
        semanticRequest
      ),
      companionPublication: toSchedulerTimingPath(
        timingDirectory,
        'vba/companionExecutable',
        companionPublication
      ),
      userFormPublication: toSchedulerTimingPath(
        timingDirectory,
        'vba/intrinsicHostEventCatalog',
        userFormPublication
      )
    },
    lateReadiness: {
      extensionActivationAwaitedAtUnixMilliseconds,
      capabilityBarrierReleasedAtUnixMilliseconds,
      companionSettledAtUnixMilliseconds:
        lateReadinessSettledAtUnixMilliseconds,
      userFormInvocationStartedAtUnixMilliseconds,
      userFormResultReleasedAtUnixMilliseconds,
      userFormSettledAtUnixMilliseconds:
        lateReadinessSettledAtUnixMilliseconds,
      companionPendingInvocationCount:
        companion.snapshot().pendingInvocationCount,
      userFormPendingInvocationCount:
        catalog.snapshot().pendingInvocationCount
    }
  });

  await mkdir(path.dirname(resultPath), { recursive: true });
  await writeFile(resultPath, `${JSON.stringify(report, undefined, 2)}\n`, 'utf8');
  console.log(
    `PASS first nonempty semantic-token response `
      + `${report.firstNonemptySemanticTokenResponseMilliseconds} ms `
      + `(budget ${report.budgetMilliseconds} ms)`
  );
  console.log(`Measurement report: ${resultPath}`);
}

interface SchedulerTimingEvidence {
  readonly recordedAt: number;
}

async function waitForSchedulerTiming(
  directory: string,
  method: string,
  stage: 'admitted' | 'completed',
  deadline: number,
  notBefore = Number.NEGATIVE_INFINITY
): Promise<SchedulerTimingEvidence> {
  while (Date.now() <= deadline) {
    const matching = (await readSchedulerTimingEvidence(directory))
      .filter((candidate) => candidate.stage === stage
        && candidate.method === method
        && candidate.recordedAtUnixMilliseconds >= notBefore)
      .sort((left, right) =>
        left.recordedAtUnixMilliseconds - right.recordedAtUnixMilliseconds
      )[0];
    if (matching !== undefined) {
      assertSuccessfulSchedulerCompletion(matching);
      return { recordedAt: matching.recordedAtUnixMilliseconds };
    }

    await delay(10);
  }

  throw new Error(
    `No ${stage} scheduler timing for '${method}' was recorded before the 30-second deadline.`
  );
}

async function captureSchedulerTimingCheckpoint(
  directory: string
): Promise<ReadonlySet<string>> {
  try {
    return new Set(await readdir(directory));
  } catch (error) {
    if (isMissingPathError(error)) {
      return new Set();
    }
    throw error;
  }
}

async function waitForUniqueSemanticRequestTiming(
  directory: string,
  checkpointFileNames: ReadonlySet<string>,
  method: string,
  deadline: number,
  requestStartedAtUnixMilliseconds: number,
  tokenResponseAtUnixMilliseconds: number
): Promise<CorrelatedSchedulerRequestTiming> {
  while (Date.now() <= deadline) {
    const selected = selectUniqueSemanticRequestTiming({
      checkpointFileNames,
      method,
      requestStartedAtUnixMilliseconds,
      tokenResponseAtUnixMilliseconds,
      evidence: await readSchedulerTimingEvidence(directory)
    });
    if (selected !== undefined) {
      return selected;
    }
    await delay(10);
  }
  throw new Error(
    `No unique completed scheduler request for '${method}' was recorded `
      + 'before the semantic-readiness deadline.'
  );
}

async function waitForUniqueSchedulerTimingAfterCheckpoint(
  directory: string,
  checkpointFileNames: ReadonlySet<string>,
  method: string,
  deadline: number
): Promise<CorrelatedSchedulerTiming> {
  while (Date.now() <= deadline) {
    const evidence = await readSchedulerTimingEvidence(directory);
    const admissions = evidence.filter((candidate) =>
      candidate.stage === 'admitted'
      && candidate.method === method
      && !checkpointFileNames.has(candidate.fileName)
    );
    if (admissions.length > 1) {
      throw new Error(
        `Expected one new '${method}' scheduler admission after its checkpoint; `
          + `observed ${admissions.length}.`
      );
    }
    const admission = admissions[0];
    if (admission !== undefined) {
      const completions = evidence.filter((candidate) =>
        candidate.stage === 'completed'
        && candidate.kind === admission.kind
        && candidate.method === admission.method
        && candidate.inputSequence === admission.inputSequence
        && candidate.requestId === admission.requestId
      );
      if (completions.length > 1) {
        throw new Error(
          `Expected one completion for scheduler input ${admission.inputSequence}; `
            + `observed ${completions.length}.`
        );
      }
      if (completions.length === 1) {
        assertSuccessfulSchedulerCompletion(completions[0]);
        return {
          kind: admission.kind,
          inputSequence: admission.inputSequence,
          requestId: admission.requestId,
          admittedFileName: admission.fileName,
          completedFileName: completions[0].fileName,
          admittedAtUnixMilliseconds: admission.recordedAtUnixMilliseconds,
          completedAtUnixMilliseconds:
            completions[0].recordedAtUnixMilliseconds
        };
      }
    }
    await delay(10);
  }
  throw new Error(
    `No unique completed scheduler timing for '${method}' was recorded `
      + 'before the late-readiness deadline.'
  );
}

async function readSchedulerTimingEvidence(
  directory: string
): Promise<SchedulerTimingFileEvidence[]> {
  let fileNames: string[];
  try {
    fileNames = await readdir(directory);
  } catch (error) {
    if (isMissingPathError(error)) {
      return [];
    }
    throw error;
  }

  const parsed = await Promise.all(fileNames
    .filter((fileName) => /\.(?:admitted|captured|completed)$/u.test(fileName))
    .map(async (fileName) => {
      const stage = fileName.endsWith('.admitted')
        ? 'admitted' as const
        : fileName.endsWith('.captured')
          ? 'captured' as const
          : 'completed' as const;
      const filePath = path.join(directory, fileName);
      try {
        const [content, fileStat] = await Promise.all([
          readFile(filePath, 'utf8'),
          stat(filePath)
        ]);
        const fields = new Map(content.split(/\r?\n/u).map((line) => {
          const separator = line.indexOf('=');
          return separator < 0
            ? [line, ''] as const
            : [line.slice(0, separator), line.slice(separator + 1)] as const;
        }));
        const kind = fields.get('kind');
        const method = fields.get('method');
        const requestId = fields.get('requestId');
        const inputSequence = Number(fields.get('inputSequence'));
        const cancelled = parseTimingBoolean(fields.get('cancelled'));
        const faulted = parseTimingBoolean(fields.get('faulted'));
        if (kind === undefined
            || method === undefined
            || requestId === undefined
            || !Number.isSafeInteger(inputSequence)
            || (stage === 'completed'
              && (cancelled === undefined || faulted === undefined))) {
          return undefined;
        }
        return {
          fileName,
          stage,
          kind,
          method,
          inputSequence,
          requestId,
          recordedAtUnixMilliseconds: fileStat.mtimeMs,
          ...(stage === 'completed' ? { cancelled, faulted } : {})
        } satisfies SchedulerTimingFileEvidence;
      } catch (error) {
        if (isMissingPathError(error)) {
          return undefined;
        }
        throw error;
      }
    }));
  return parsed.filter((candidate): candidate is SchedulerTimingFileEvidence =>
    candidate !== undefined
  );
}

function parseTimingBoolean(value: string | undefined): boolean | undefined {
  return value === 'True'
    ? true
    : value === 'False'
      ? false
      : undefined;
}

function assertSuccessfulSchedulerCompletion(
  evidence: SchedulerTimingFileEvidence
): void {
  if (evidence.stage === 'completed'
      && (evidence.cancelled !== false || evidence.faulted !== false)) {
    throw new Error(
      `Scheduler timing '${evidence.fileName}' recorded an unsuccessful completion.`
    );
  }
}

function toSchedulerTimingPath(
  directory: string,
  method: string,
  timing: CorrelatedSchedulerTiming
): SchedulerTimingPathEvidence {
  return {
    method,
    kind: timing.kind,
    inputSequence: timing.inputSequence,
    requestId: timing.requestId,
    admittedFilePath: path.join(directory, timing.admittedFileName),
    completedFilePath: path.join(directory, timing.completedFileName),
    admittedAtUnixMilliseconds: timing.admittedAtUnixMilliseconds,
    completedAtUnixMilliseconds: timing.completedAtUnixMilliseconds
  };
}

function toCapturedSchedulerTimingPath(
  directory: string,
  method: string,
  timing: CorrelatedSchedulerRequestTiming
): SchedulerCapturedTimingPathEvidence {
  return {
    ...toSchedulerTimingPath(directory, method, timing),
    capturedFilePath: path.join(directory, timing.capturedFileName),
    capturedAtUnixMilliseconds: timing.capturedAtUnixMilliseconds
  };
}

async function waitForDirectoryBirthTime(
  directory: string,
  deadline: number
): Promise<number> {
  while (Date.now() <= deadline) {
    try {
      const directoryStat = await stat(directory);
      return directoryStat.birthtimeMs > 0
        ? directoryStat.birthtimeMs
        : directoryStat.ctimeMs;
    } catch (error) {
      if (!isMissingPathError(error)) {
        throw error;
      }
    }
    await delay(10);
  }
  throw new Error(
    'The language-server timing directory was not created before the 30-second deadline.'
  );
}

async function waitForRestoredEditor(activeSourcePath: string) {
  const deadline = Date.now() + 10_000;
  while (Date.now() <= deadline) {
    const editor = window.visibleTextEditors.find((candidate) =>
      candidate.document.uri.scheme === 'file'
      && candidate.document.uri.fsPath.toLowerCase()
        === activeSourcePath.toLowerCase()
    );
    if (editor !== undefined) {
      return editor;
    }
    await delay(10);
  }
  throw new Error(`The restored CommonModules editor was not visible: ${activeSourcePath}`);
}

async function captureCorpusEvidence(activeSourcePath: string): Promise<{
  readonly manifestPath: string;
  readonly activeSourcePath: string;
  readonly sourceFileCount: number;
  readonly sourceByteCount: number;
  readonly physicalLineCount: number;
}> {
  const manifestPath = await findAncestorManifest(activeSourcePath);
  const manifestText = (await readFile(manifestPath, 'utf8')).replace(/^\uFEFF/u, '');
  const manifest = JSON.parse(manifestText) as {
    readonly projectName: string;
    readonly documents: Readonly<Record<string, { readonly sourcePath: string }>>;
  };
  assert.equal(
    manifest.projectName,
    'CommonModules',
    'The performance corpus must be the CommonModules project.'
  );
  const root = path.dirname(manifestPath);
  const sourceDirectories = Object.values(manifest.documents).map((document) =>
    path.resolve(root, document.sourcePath)
  );
  const sourcePaths = (await Promise.all(sourceDirectories.map(
    (directory) => enumerateVbaSources(directory)
  ))).flat();
  const sources = await Promise.all(sourcePaths.map(async (sourcePath) => {
    const bytes = await readFile(sourcePath);
    let physicalLineCount = 0;
    if (bytes.length > 0) {
      physicalLineCount = bytes.reduce(
        (count, byte) => count + (byte === 0x0a ? 1 : 0),
        bytes[bytes.length - 1] === 0x0a ? 0 : 1
      );
    }
    return { byteCount: bytes.length, physicalLineCount };
  }));
  return {
    manifestPath,
    activeSourcePath,
    sourceFileCount: sources.length,
    sourceByteCount: sources.reduce((total, source) => total + source.byteCount, 0),
    physicalLineCount: sources.reduce(
      (total, source) => total + source.physicalLineCount,
      0
    )
  };
}

async function findAncestorManifest(activeSourcePath: string): Promise<string> {
  for (
    let directory = path.dirname(activeSourcePath);
    ;
    directory = path.dirname(directory)
  ) {
    const candidate = path.join(directory, 'vba-project.json');
    try {
      const candidateStat = await stat(candidate);
      if (candidateStat.isFile()) {
        return candidate;
      }
    } catch (error) {
      if (!isMissingPathError(error)) {
        throw error;
      }
    }
    const parent = path.dirname(directory);
    if (parent === directory) {
      break;
    }
  }
  throw new Error(`No ancestor vba-project.json owns ${activeSourcePath}.`);
}

async function enumerateVbaSources(directory: string): Promise<string[]> {
  const paths: string[] = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      paths.push(...await enumerateVbaSources(entryPath));
    } else if (entry.isFile()
        && ['.bas', '.cls', '.frm'].includes(path.extname(entry.name).toLowerCase())) {
      paths.push(entryPath);
    }
  }
  return paths;
}

function getSemanticTokenDataLength(value: unknown): number {
  if (Array.isArray(value)) {
    return value.length;
  }
  if (ArrayBuffer.isView(value)) {
    return value.byteLength;
  }
  if (typeof value === 'object' && value !== null && 'data' in value) {
    return getSemanticTokenDataLength((value as { readonly data: unknown }).data);
  }
  if (typeof value === 'object' && value !== null && 'buffer' in value) {
    return getSemanticTokenDataLength((value as { readonly buffer: unknown }).buffer);
  }
  return 0;
}

async function awaitActivationWithNotificationDismissal(
  activation: Promise<VbaToolsExtensionHostTestApi>,
  deadline: number
): Promise<VbaToolsExtensionHostTestApi> {
  type ActivationOutcome =
    | { readonly status: 'fulfilled'; readonly value: VbaToolsExtensionHostTestApi }
    | { readonly status: 'rejected'; readonly error: unknown };
  let outcome: ActivationOutcome | undefined;
  void activation.then(
    (value) => {
      outcome = { status: 'fulfilled', value };
    },
    (error: unknown) => {
      outcome = { status: 'rejected', error };
    }
  );

  while (outcome === undefined && Date.now() <= deadline) {
    await commands.executeCommand('notifications.clearAll');
    await delay(20);
  }
  if (outcome === undefined) {
    throw new Error(
      'Extension activation did not settle before the late-readiness deadline.'
    );
  }
  if (outcome.status === 'rejected') {
    throw outcome.error;
  }
  return outcome.value;
}

async function waitForCondition(
  condition: () => boolean,
  deadline: number,
  description: string
): Promise<void> {
  while (!condition()) {
    if (Date.now() > deadline) {
      throw new Error(
        `${description} did not complete before the late-readiness deadline.`
      );
    }
    await delay(10);
  }
}

async function compatibleCapabilitiesResult(
  extensionPath: string
): Promise<{ readonly stdout: string; readonly stderr: string }> {
  const contract = JSON.parse(await readFile(
    path.join(extensionPath, 'vba-dev-contract.json'),
    'utf8'
  )) as {
    readonly contractVersion: string;
    readonly featureVersions: Readonly<Record<string, string>>;
    readonly commandSchemaVersions: Readonly<Record<string, string>>;
  };
  return {
    stdout: JSON.stringify({
      toolVersion: 'semantic-readiness-performance-test',
      contractVersion: contract.contractVersion,
      featureVersions: contract.featureVersions,
      activeWindowsCodePage: 932,
      commands: Object.fromEntries(Object.entries(
        contract.commandSchemaVersions
      ).map(([command, outputSchemaVersion]) => [
        command,
        { outputSchemaVersion }
      ]))
    }),
    stderr: ''
  };
}

function assertSuccessfulUserFormNotification(notification: {
  readonly method: string;
  readonly parameters: unknown;
}): void {
  assert.equal(notification.method, 'vba/intrinsicHostEventCatalog');
  assert.ok(
    typeof notification.parameters === 'object'
      && notification.parameters !== null
      && 'catalog' in notification.parameters
      && notification.parameters.catalog !== null,
    'Automatic UserForm acquisition must publish a successful catalog.'
  );
}

async function withDeadline<T>(
  operation: Thenable<T> | PromiseLike<T>,
  deadline: number,
  description: string
): Promise<T> {
  const remainingMilliseconds = Math.max(1, deadline - Date.now());
  return new Promise<T>((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error(
      `${description} exceeded the 30-second activation budget.`
    )), remainingMilliseconds);
    void Promise.resolve(operation).then(
      (value) => {
        clearTimeout(timeout);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timeout);
        reject(error);
      }
    );
  });
}

function requiredAbsolutePath(name: string): string {
  const value = requiredEnvironment(name);
  assert.ok(path.isAbsolute(value), `${name} must be an absolute path.`);
  return path.normalize(value);
}

function requiredEnvironment(name: string): string {
  const value = process.env[name];
  assert.ok(value, `${name} must be provided.`);
  return value;
}

function isMissingPathError(error: unknown): boolean {
  return error instanceof Error
    && 'code' in error
    && (error as NodeJS.ErrnoException).code === 'ENOENT';
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, milliseconds);
  });
}

const successfulCatalogResult = {
  exitCode: 0,
  stderr: '',
  cancelled: false,
  stdout: JSON.stringify({
    schemaVersion: '1.0',
    sourceKind: 'userForm',
    intrinsicEventSourceName: 'UserForm',
    events: [{
      identity: { sourceName: 'UserForm', name: 'Initialize' },
      signature: {
        parameters: [],
        documentation: 'Occurs when the form is initialized.'
      },
      authoringAvailable: true,
      existingHandlerRecognizable: true
    }]
  })
} as const;
