import assert from 'node:assert/strict';
import test from 'node:test';
import {
  createSemanticReadinessPerformanceReport,
  selectUniqueSemanticRequestTiming
} from './semanticReadinessPerformanceEvidence';

function completeReportInput() {
  return {
    budgetMilliseconds: 30_000,
    timeline: {
      activateStartedAtUnixMilliseconds: 1_000,
      languageServerProcessStartedAtUnixMilliseconds: 1_100,
      initializationCompletedAtUnixMilliseconds: 1_500,
      didOpenCompletedAtUnixMilliseconds: 1_700,
      semanticRequestStartedAtUnixMilliseconds: 1_800,
      semanticSnapshotCompletedAtUnixMilliseconds: 2_200,
      tokenResponseAtUnixMilliseconds: 2_300
    },
    semanticTokenDataLength: 25,
    acceptedDocumentVersion: 7,
    responseDocumentVersion: 7,
    corpus: {
      manifestPath: String.raw`C:\CommonModules\vba-project.json`,
      activeSourcePath: String.raw`C:\CommonModules\src\CommonModules\Fx_Common.bas`,
      sourceFileCount: 94,
      sourceByteCount: 1_000_000,
      physicalLineCount: 50_000
    },
    sourceRevision: {
      repositoryCommit: '0123456789abcdef',
      repositoryDirty: false,
      activeSourceSha256: 'abcdef',
      activeDocumentVersion: 7,
      activeSourceLastWriteTimeUtc: '2026-09-05T00:00:00.000Z'
    },
    runtime: {
      operatingSystem: 'Windows',
      architecture: 'x64',
      cpu: 'Test CPU',
      logicalProcessorCount: 8,
      totalMemoryBytes: 16_000_000_000,
      freeMemoryBytes: 8_000_000_000,
      vscodeVersion: '1.125.0',
      electronVersion: 'test',
      nodeVersion: 'test',
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
      directoryPath: String.raw`C:\timing`,
      semanticRequest: capturedSchedulerPath(
        'textDocument/semanticTokens/full',
        5,
        '9',
        1_900,
        2_200,
        2_250
      ),
      companionPublication: schedulerPath(
        'vba/companionExecutable',
        6,
        'none',
        2_450,
        2_500
      ),
      userFormPublication: schedulerPath(
        'vba/intrinsicHostEventCatalog',
        7,
        'none',
        2_600,
        2_650
      )
    },
    lateReadiness: {
      extensionActivationAwaitedAtUnixMilliseconds: 2_350,
      capabilityBarrierReleasedAtUnixMilliseconds: 2_400,
      companionSettledAtUnixMilliseconds: 2_510,
      userFormInvocationStartedAtUnixMilliseconds: 2_430,
      userFormResultReleasedAtUnixMilliseconds: 2_550,
      userFormSettledAtUnixMilliseconds: 2_660,
      companionPendingInvocationCount: 0,
      userFormPendingInvocationCount: 0
    }
  } as const;
}

test('a complete exact nonempty semantic readiness timeline produces a passing phase report', () => {
  const report = createSemanticReadinessPerformanceReport(completeReportInput());

  assert.equal(report.result, 'pass');
  assert.deepEqual(report.scenario, {
    editorCount: 1,
    restoredEditor: 'visible-before-activate',
    activationStart: 'immediately-before-explicit-extension-activate',
    companionResolution: 'deterministically-blocked'
  });
  assert.equal(report.firstNonemptySemanticTokenResponseMilliseconds, 1_300);
  assert.equal(
    report.phaseEvidence.semanticSnapshot,
    'semantic-request-dispatch-to-scheduler-capture-completion'
  );
  assert.deepEqual(report.phaseMilliseconds, {
    activateToLanguageServerProcessStart: 100,
    languageServerProcessStartToInitializationComplete: 400,
    initializationCompleteToDidOpenComplete: 200,
    didOpenCompleteToSemanticRequest: 100,
    semanticSnapshot: 400,
    tokenResponseAfterSnapshot: 100
  });
  assert.equal(report.correctness.exactAcceptedDocumentRevision, true);
  assert.equal(report.correctness.nonemptySemanticTokenData, true);
  assert.equal(report.correctness.activationPromiseAwaited, true);
  assert.equal(report.correctness.lateReadinessSettled, true);
  assert.equal(
    report.schedulerTimingPath.semanticRequest.requestId,
    '9'
  );
  assert.deepEqual(report.lateReadiness, {
    extensionActivationAwaitedAtUnixMilliseconds: 2_350,
    capabilityBarrierReleasedAtUnixMilliseconds: 2_400,
    companionSettledAtUnixMilliseconds: 2_510,
    userFormInvocationStartedAtUnixMilliseconds: 2_430,
    userFormResultReleasedAtUnixMilliseconds: 2_550,
    userFormSettledAtUnixMilliseconds: 2_660,
    companionPendingInvocationCount: 0,
    userFormPendingInvocationCount: 0
  });
});

test('semantic timing correlation selects one new request identity after its checkpoint', () => {
  const selected = selectUniqueSemanticRequestTiming({
    checkpointFileNames: new Set(['old.admitted', 'old.completed']),
    method: 'textDocument/semanticTokens/full',
    requestStartedAtUnixMilliseconds: 2_000,
    tokenResponseAtUnixMilliseconds: 2_500,
    evidence: [
      timing('old.admitted', 'admitted', 4, '8', 2_100),
      timing('old.captured', 'captured', 4, '8', 2_150),
      timing('old.completed', 'completed', 4, '8', 2_200),
      timing('new.admitted', 'admitted', 5, '9', 2_300),
      timing('new.captured', 'captured', 5, '9', 2_400),
      timing('new.completed', 'completed', 5, '9', 2_510)
    ]
  });

  assert.deepEqual(selected, {
    kind: 'read',
    inputSequence: 5,
    requestId: '9',
    admittedFileName: 'new.admitted',
    capturedFileName: 'new.captured',
    completedFileName: 'new.completed',
    admittedAtUnixMilliseconds: 2_300,
    capturedAtUnixMilliseconds: 2_400,
    completedAtUnixMilliseconds: 2_510
  });
});

test('semantic timing correlation fails when the explicit request window is ambiguous', () => {
  assert.throws(() => selectUniqueSemanticRequestTiming({
    checkpointFileNames: new Set(),
    method: 'textDocument/semanticTokens/full',
    requestStartedAtUnixMilliseconds: 2_000,
    tokenResponseAtUnixMilliseconds: 2_500,
    evidence: [
      timing('first.admitted', 'admitted', 5, '9', 2_200),
      timing('first.captured', 'captured', 5, '9', 2_250),
      timing('first.completed', 'completed', 5, '9', 2_300),
      timing('second.admitted', 'admitted', 6, '10', 2_400),
      timing('second.captured', 'captured', 6, '10', 2_450),
      timing('second.completed', 'completed', 6, '10', 2_510)
    ]
  }), /observed 2/u);
});

test('semantic timing correlation waits for the matching snapshot capture', () => {
  const selected = selectUniqueSemanticRequestTiming({
    checkpointFileNames: new Set(),
    method: 'textDocument/semanticTokens/full',
    requestStartedAtUnixMilliseconds: 2_000,
    tokenResponseAtUnixMilliseconds: 2_500,
    evidence: [
      timing('new.admitted', 'admitted', 5, '9', 2_300),
      timing('new.completed', 'completed', 5, '9', 2_450)
    ]
  });

  assert.equal(selected, undefined);
});

test('semantic report rejects an admission timestamp presented as snapshot completion', () => {
  const input = completeReportInput();

  assert.throws(() => createSemanticReadinessPerformanceReport({
    ...input,
    timeline: {
      ...input.timeline,
      semanticSnapshotCompletedAtUnixMilliseconds:
        input.schedulerTimingPath.semanticRequest.admittedAtUnixMilliseconds
    }
  }), /must use the correlated scheduler capture/u);
});

function timing(
  fileName: string,
  stage: 'admitted' | 'captured' | 'completed',
  inputSequence: number,
  requestId: string,
  recordedAtUnixMilliseconds: number
) {
  return {
    fileName,
    stage,
    kind: 'read',
    method: 'textDocument/semanticTokens/full',
    inputSequence,
    requestId,
    recordedAtUnixMilliseconds
  } as const;
}

function schedulerPath(
  method: string,
  inputSequence: number,
  requestId: string,
  admittedAtUnixMilliseconds: number,
  completedAtUnixMilliseconds: number
) {
  const kind = requestId === 'none' ? 'background' : 'read';
  const sanitizedMethod = method.replaceAll('/', '_');
  const stem = `${String(inputSequence).padStart(20, '0')}-${kind}-${sanitizedMethod}-${requestId}`;
  return {
    method,
    kind,
    inputSequence,
    requestId,
    admittedFilePath: String.raw`C:\timing\${stem}.admitted`,
    completedFilePath: String.raw`C:\timing\${stem}.completed`,
    admittedAtUnixMilliseconds,
    completedAtUnixMilliseconds
  };
}

function capturedSchedulerPath(
  method: string,
  inputSequence: number,
  requestId: string,
  admittedAtUnixMilliseconds: number,
  capturedAtUnixMilliseconds: number,
  completedAtUnixMilliseconds: number
) {
  const base = schedulerPath(
    method,
    inputSequence,
    requestId,
    admittedAtUnixMilliseconds,
    completedAtUnixMilliseconds
  );
  return {
    ...base,
    capturedFilePath: base.admittedFilePath.replace(/\.admitted$/u, '.captured'),
    capturedAtUnixMilliseconds
  };
}
