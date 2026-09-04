export interface SemanticReadinessTimeline {
  readonly activateStartedAtUnixMilliseconds: number;
  readonly languageServerProcessStartedAtUnixMilliseconds: number;
  readonly initializationCompletedAtUnixMilliseconds: number;
  readonly didOpenCompletedAtUnixMilliseconds: number;
  readonly semanticRequestStartedAtUnixMilliseconds: number;
  readonly semanticSnapshotCompletedAtUnixMilliseconds: number;
  readonly tokenResponseAtUnixMilliseconds: number;
}

export interface SemanticReadinessCorpusEvidence {
  readonly manifestPath: string;
  readonly activeSourcePath: string;
  readonly sourceFileCount: number;
  readonly sourceByteCount: number;
  readonly physicalLineCount: number;
}

export interface SemanticReadinessSourceRevisionEvidence {
  readonly repositoryCommit: string;
  readonly repositoryDirty: boolean;
  readonly activeSourceSha256: string;
  readonly activeDocumentVersion: number;
  readonly activeSourceLastWriteTimeUtc: string;
}

export interface SemanticReadinessRuntimeEvidence {
  readonly operatingSystem: string;
  readonly architecture: string;
  readonly cpu: string;
  readonly logicalProcessorCount: number;
  readonly totalMemoryBytes: number;
  readonly freeMemoryBytes: number;
  readonly vscodeVersion: string;
  readonly electronVersion: string;
  readonly nodeVersion: string;
  readonly languageServerBuildConfiguration: 'Release';
  readonly languageServerTargetFramework: string;
}

export interface SemanticReadinessCachePolicyEvidence {
  readonly extensionUserData: 'fresh';
  readonly referenceCatalog: 'fresh';
  readonly operatingSystemFileCache: 'uncontrolled';
}

export interface SemanticReadinessCompetingLoadEvidence {
  readonly syntheticLoad: 'none';
  readonly ambientLoad: 'uncontrolled';
}

export interface SchedulerTimingFileEvidence {
  readonly fileName: string;
  readonly stage: 'admitted' | 'captured' | 'completed';
  readonly kind: string;
  readonly method: string;
  readonly inputSequence: number;
  readonly requestId: string;
  readonly recordedAtUnixMilliseconds: number;
  readonly cancelled?: boolean;
  readonly faulted?: boolean;
}

export interface CorrelatedSchedulerTiming {
  readonly kind: string;
  readonly inputSequence: number;
  readonly requestId: string;
  readonly admittedFileName: string;
  readonly completedFileName: string;
  readonly admittedAtUnixMilliseconds: number;
  readonly completedAtUnixMilliseconds: number;
}

export interface CorrelatedSchedulerRequestTiming
  extends CorrelatedSchedulerTiming {
  readonly capturedFileName: string;
  readonly capturedAtUnixMilliseconds: number;
}

export interface SchedulerTimingPathEvidence {
  readonly method: string;
  readonly kind: string;
  readonly inputSequence: number;
  readonly requestId: string;
  readonly admittedFilePath: string;
  readonly completedFilePath: string;
  readonly admittedAtUnixMilliseconds: number;
  readonly completedAtUnixMilliseconds: number;
}

export interface SchedulerCapturedTimingPathEvidence
  extends SchedulerTimingPathEvidence {
  readonly capturedFilePath: string;
  readonly capturedAtUnixMilliseconds: number;
}

export interface SemanticReadinessSchedulerTimingPath {
  readonly directoryPath: string;
  readonly semanticRequest: SchedulerCapturedTimingPathEvidence;
  readonly companionPublication: SchedulerTimingPathEvidence;
  readonly userFormPublication: SchedulerTimingPathEvidence;
}

export interface SemanticReadinessLateReadinessEvidence {
  readonly extensionActivationAwaitedAtUnixMilliseconds: number;
  readonly capabilityBarrierReleasedAtUnixMilliseconds: number;
  readonly companionSettledAtUnixMilliseconds: number;
  readonly userFormInvocationStartedAtUnixMilliseconds: number;
  readonly userFormResultReleasedAtUnixMilliseconds: number;
  readonly userFormSettledAtUnixMilliseconds: number;
  readonly companionPendingInvocationCount: number;
  readonly userFormPendingInvocationCount: number;
}

export function selectUniqueSemanticRequestTiming(options: {
  readonly checkpointFileNames: ReadonlySet<string>;
  readonly method: string;
  readonly requestStartedAtUnixMilliseconds: number;
  readonly tokenResponseAtUnixMilliseconds: number;
  readonly evidence: readonly SchedulerTimingFileEvidence[];
}): CorrelatedSchedulerRequestTiming | undefined {
  const newEvidence = options.evidence.filter((candidate) =>
    !options.checkpointFileNames.has(candidate.fileName)
    && candidate.method === options.method
  );
  const admitted = newEvidence.filter((candidate) =>
    candidate.stage === 'admitted'
    && candidate.requestId !== 'none'
    && candidate.recordedAtUnixMilliseconds
      >= options.requestStartedAtUnixMilliseconds
    && candidate.recordedAtUnixMilliseconds
      <= options.tokenResponseAtUnixMilliseconds
  );
  if (admitted.length === 0) {
    return undefined;
  }
  if (admitted.length !== 1) {
    throw new Error(
      `Expected one new '${options.method}' scheduler admission in the `
        + `explicit request window; observed ${admitted.length}.`
    );
  }

  const admission = admitted[0];
  const captured = newEvidence.filter((candidate) =>
    candidate.stage === 'captured'
    && candidate.kind === admission.kind
    && candidate.inputSequence === admission.inputSequence
    && candidate.requestId === admission.requestId
  );
  if (captured.length === 0) {
    return undefined;
  }
  if (captured.length !== 1) {
    throw new Error(
      `Expected one capture for scheduler request `
        + `${admission.inputSequence}/${admission.requestId}; `
        + `observed ${captured.length}.`
    );
  }
  const completed = newEvidence.filter((candidate) =>
    candidate.stage === 'completed'
    && candidate.kind === admission.kind
    && candidate.inputSequence === admission.inputSequence
    && candidate.requestId === admission.requestId
  );
  if (completed.length === 0) {
    return undefined;
  }
  if (completed.length !== 1) {
    throw new Error(
      `Expected one completion for scheduler request `
        + `${admission.inputSequence}/${admission.requestId}; `
        + `observed ${completed.length}.`
    );
  }
  if (completed[0].cancelled === true || completed[0].faulted === true) {
    throw new Error(
      `Scheduler request ${admission.inputSequence}/${admission.requestId} `
        + 'did not complete successfully.'
    );
  }
  if (captured[0].recordedAtUnixMilliseconds
        < admission.recordedAtUnixMilliseconds
      || completed[0].recordedAtUnixMilliseconds
        < captured[0].recordedAtUnixMilliseconds) {
    throw new Error(
      `Scheduler request ${admission.inputSequence}/${admission.requestId} `
        + 'recorded capture evidence out of order.'
    );
  }

  return {
    kind: admission.kind,
    inputSequence: admission.inputSequence,
    requestId: admission.requestId,
    admittedFileName: admission.fileName,
    capturedFileName: captured[0].fileName,
    completedFileName: completed[0].fileName,
    admittedAtUnixMilliseconds: admission.recordedAtUnixMilliseconds,
    capturedAtUnixMilliseconds: captured[0].recordedAtUnixMilliseconds,
    completedAtUnixMilliseconds: completed[0].recordedAtUnixMilliseconds
  };
}

export interface SemanticReadinessPerformanceInput {
  readonly budgetMilliseconds: number;
  readonly timeline: SemanticReadinessTimeline;
  readonly semanticTokenDataLength: number;
  readonly acceptedDocumentVersion: number;
  readonly responseDocumentVersion: number;
  readonly corpus: SemanticReadinessCorpusEvidence;
  readonly sourceRevision: SemanticReadinessSourceRevisionEvidence;
  readonly runtime: SemanticReadinessRuntimeEvidence;
  readonly cachePolicy: SemanticReadinessCachePolicyEvidence;
  readonly competingLoad: SemanticReadinessCompetingLoadEvidence;
  readonly schedulerTimingPath: SemanticReadinessSchedulerTimingPath;
  readonly lateReadiness: SemanticReadinessLateReadinessEvidence;
}

export interface SemanticReadinessPerformanceReport
  extends SemanticReadinessPerformanceInput {
  readonly schemaVersion: 1;
  readonly result: 'pass';
  readonly scenario: {
    readonly editorCount: 1;
    readonly restoredEditor: 'visible-before-activate';
    readonly activationStart: 'immediately-before-explicit-extension-activate';
    readonly companionResolution: 'deterministically-blocked';
  };
  readonly phaseEvidence: {
    readonly languageServerProcessStart: 'server-created-timing-directory-birth-time';
    readonly initialization: 'initialize-scheduler-completion';
    readonly didOpen: 'didOpen-scheduler-completion';
    readonly semanticSnapshot: 'semantic-request-dispatch-to-scheduler-capture-completion';
    readonly tokenResponse: 'client-command-completion';
  };
  readonly firstNonemptySemanticTokenResponseMilliseconds: number;
  readonly phaseMilliseconds: {
    readonly activateToLanguageServerProcessStart: number;
    readonly languageServerProcessStartToInitializationComplete: number;
    readonly initializationCompleteToDidOpenComplete: number;
    readonly didOpenCompleteToSemanticRequest: number;
    readonly semanticSnapshot: number;
    readonly tokenResponseAfterSnapshot: number;
  };
  readonly correctness: {
    readonly exactAcceptedDocumentRevision: true;
    readonly nonemptySemanticTokenData: true;
    readonly activationPromiseAwaited: true;
    readonly lateReadinessSettled: true;
  };
}

export function createSemanticReadinessPerformanceReport(
  input: SemanticReadinessPerformanceInput
): SemanticReadinessPerformanceReport {
  const timelineEntries = Object.entries(input.timeline);
  for (let index = 1; index < timelineEntries.length; index += 1) {
    const [previousName, previousValue] = timelineEntries[index - 1];
    const [currentName, currentValue] = timelineEntries[index];
    if (currentValue < previousValue) {
      throw new Error(
        `Semantic readiness phase '${currentName}' preceded '${previousName}'.`
      );
    }
  }

  if (input.semanticTokenDataLength <= 0) {
    throw new Error('The first semantic-token response was empty.');
  }
  if (input.acceptedDocumentVersion !== input.responseDocumentVersion
      || input.sourceRevision.activeDocumentVersion
        !== input.acceptedDocumentVersion) {
    throw new Error(
      'The semantic-token response did not retain the accepted document revision.'
    );
  }

  validateSchedulerTimingPath(input.schedulerTimingPath);
  if (input.timeline.semanticSnapshotCompletedAtUnixMilliseconds
      !== input.schedulerTimingPath.semanticRequest
        .capturedAtUnixMilliseconds) {
    throw new Error(
      'The semantic snapshot timeline must use the correlated scheduler capture.'
    );
  }
  validateLateReadiness(input);

  const timeline = input.timeline;
  const elapsed = (
    end: keyof SemanticReadinessTimeline,
    start: keyof SemanticReadinessTimeline
  ): number => timeline[end] - timeline[start];
  const total = elapsed(
    'tokenResponseAtUnixMilliseconds',
    'activateStartedAtUnixMilliseconds'
  );
  if (total > input.budgetMilliseconds) {
    throw new Error(
      `First nonempty semantic-token response took ${total} ms; `
        + `required <= ${input.budgetMilliseconds} ms.`
    );
  }

  return {
    schemaVersion: 1,
    result: 'pass',
    scenario: {
      editorCount: 1,
      restoredEditor: 'visible-before-activate',
      activationStart: 'immediately-before-explicit-extension-activate',
      companionResolution: 'deterministically-blocked'
    },
    phaseEvidence: {
      languageServerProcessStart: 'server-created-timing-directory-birth-time',
      initialization: 'initialize-scheduler-completion',
      didOpen: 'didOpen-scheduler-completion',
      semanticSnapshot: 'semantic-request-dispatch-to-scheduler-capture-completion',
      tokenResponse: 'client-command-completion'
    },
    ...input,
    firstNonemptySemanticTokenResponseMilliseconds: total,
    phaseMilliseconds: {
      activateToLanguageServerProcessStart: elapsed(
        'languageServerProcessStartedAtUnixMilliseconds',
        'activateStartedAtUnixMilliseconds'
      ),
      languageServerProcessStartToInitializationComplete: elapsed(
        'initializationCompletedAtUnixMilliseconds',
        'languageServerProcessStartedAtUnixMilliseconds'
      ),
      initializationCompleteToDidOpenComplete: elapsed(
        'didOpenCompletedAtUnixMilliseconds',
        'initializationCompletedAtUnixMilliseconds'
      ),
      didOpenCompleteToSemanticRequest: elapsed(
        'semanticRequestStartedAtUnixMilliseconds',
        'didOpenCompletedAtUnixMilliseconds'
      ),
      semanticSnapshot: elapsed(
        'semanticSnapshotCompletedAtUnixMilliseconds',
        'semanticRequestStartedAtUnixMilliseconds'
      ),
      tokenResponseAfterSnapshot: elapsed(
        'tokenResponseAtUnixMilliseconds',
        'semanticSnapshotCompletedAtUnixMilliseconds'
      )
    },
    correctness: {
      exactAcceptedDocumentRevision: true,
      nonemptySemanticTokenData: true,
      activationPromiseAwaited: true,
      lateReadinessSettled: true
    }
  };
}

function validateSchedulerTimingPath(
  timingPath: SemanticReadinessSchedulerTimingPath
): void {
  if (timingPath.directoryPath.trim().length === 0) {
    throw new Error('The scheduler timing directory path is required.');
  }

  const expectedMethods = [
    ['semanticRequest', 'textDocument/semanticTokens/full'],
    ['companionPublication', 'vba/companionExecutable'],
    ['userFormPublication', 'vba/intrinsicHostEventCatalog']
  ] as const;
  for (const [name, expectedMethod] of expectedMethods) {
    const timing = timingPath[name];
    if (timing.method !== expectedMethod) {
      throw new Error(
        `Scheduler timing '${name}' used '${timing.method}', expected '${expectedMethod}'.`
      );
    }
    if (timing.inputSequence <= 0
        || timing.requestId.trim().length === 0
        || timing.admittedFilePath.trim().length === 0
        || timing.completedFilePath.trim().length === 0
        || timing.completedAtUnixMilliseconds
          < timing.admittedAtUnixMilliseconds) {
      throw new Error(`Scheduler timing '${name}' is incomplete or out of order.`);
    }
  }

  if (timingPath.semanticRequest.requestId === 'none') {
    throw new Error('The explicit semantic request must retain its request identity.');
  }
  if (timingPath.semanticRequest.capturedFilePath.trim().length === 0
      || timingPath.semanticRequest.capturedAtUnixMilliseconds
        < timingPath.semanticRequest.admittedAtUnixMilliseconds
      || timingPath.semanticRequest.completedAtUnixMilliseconds
        < timingPath.semanticRequest.capturedAtUnixMilliseconds) {
    throw new Error(
      'The explicit semantic request must retain ordered snapshot-capture evidence.'
    );
  }
  if (timingPath.companionPublication.requestId !== 'none'
      || timingPath.userFormPublication.requestId !== 'none') {
    throw new Error('Notification and background timing paths must not claim request IDs.');
  }
}

function validateLateReadiness(input: SemanticReadinessPerformanceInput): void {
  const readiness = input.lateReadiness;
  const timing = input.schedulerTimingPath;
  const tokenResponse = input.timeline.tokenResponseAtUnixMilliseconds;
  if (readiness.extensionActivationAwaitedAtUnixMilliseconds < tokenResponse
      || readiness.capabilityBarrierReleasedAtUnixMilliseconds
        < readiness.extensionActivationAwaitedAtUnixMilliseconds
      || readiness.companionSettledAtUnixMilliseconds
        < readiness.capabilityBarrierReleasedAtUnixMilliseconds
      || readiness.companionSettledAtUnixMilliseconds
        < timing.companionPublication.completedAtUnixMilliseconds) {
    throw new Error('Companion readiness did not settle after activation and publication.');
  }
  if (readiness.userFormInvocationStartedAtUnixMilliseconds
        < readiness.capabilityBarrierReleasedAtUnixMilliseconds
      || readiness.userFormResultReleasedAtUnixMilliseconds
        < readiness.userFormInvocationStartedAtUnixMilliseconds
      || readiness.userFormSettledAtUnixMilliseconds
        < readiness.userFormResultReleasedAtUnixMilliseconds
      || readiness.userFormSettledAtUnixMilliseconds
        < timing.userFormPublication.completedAtUnixMilliseconds) {
    throw new Error('Automatic UserForm readiness did not publish and settle in order.');
  }
  if (readiness.companionPendingInvocationCount !== 0
      || readiness.userFormPendingInvocationCount !== 0) {
    throw new Error('Late readiness retained a pending test invocation.');
  }
}
