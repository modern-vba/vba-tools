import {
  HostClassSourceAssociationResult,
  HostClassSourceCandidate,
  associateHostClassSources
} from './hostClassSourceAssociation';

export const HostClassProjectionSnapshotMethod = 'vba/hostClassProjectionSnapshot';

export interface HostClassProjectionContext {
  readonly project: string;
  readonly document: string;
  readonly sourceTemplate: string;
}

export interface HostClassListInvocation {
  readonly context: HostClassProjectionContext;
  readonly generation: number;
  readonly trigger:
    | 'activation'
    | 'manifestChanged'
    | 'templateChanged'
    | 'explicitRefresh';
  readonly args: readonly string[];
  readonly cancellationToken: HostClassCancellationToken;
}

export interface HostClassCancellationDisposable {
  dispose(): void;
}

export interface HostClassCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(
    listener: () => void
  ): HostClassCancellationDisposable;
}

export interface HostClassListRunResult {
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  readonly cancelled: boolean;
}

export interface HostClassProjectionLifecycleOptions {
  readonly runHostClassList: (
    invocation: HostClassListInvocation
  ) => Promise<HostClassListRunResult>;
  readonly sendNotification: (method: string, parameters: unknown) => Promise<void>;
  readonly scheduleDelay?: (
    delayMilliseconds: number,
    callback: () => void
  ) => HostClassCancellationDisposable;
  readonly onSourceAssociationChanged?: (
    context: HostClassProjectionContext,
    result: HostClassSourceAssociationResult
  ) => void;
  readonly onTransition?: (
    transition: HostClassProjectionLifecycleTransition
  ) => void;
}

export interface HostClassProjectionLifecycleTransition {
  readonly kind:
    | 'queued'
    | 'started'
    | 'cancellationRequested'
    | 'cancelled'
    | 'cleared'
    | 'committed'
    | 'replayed'
    | 'discarded'
    | 'sourceAssociationChanged';
  readonly context: HostClassProjectionContext;
  readonly generation: number;
  readonly revision: number;
  readonly trigger?: HostClassListInvocation['trigger'];
  readonly reasonCode?: string;
  readonly message?: string;
  readonly resolvedCount?: number;
  readonly unverifiedCount?: number;
  readonly lastKnownGoodCount?: number;
  readonly indeterminateCount?: number;
  readonly authoritativeDeletionCount?: number;
  readonly associationFailureCount?: number;
  readonly associationResult?: HostClassSourceAssociationResult;
  readonly classEnumerationComplete?: boolean;
  readonly diagnostics?: readonly HostClassProjectionMessage[];
  readonly warnings?: readonly HostClassProjectionMessage[];
  readonly resolvedIdentities?: readonly HostClassIdentity[];
  readonly unverifiedClasses?: readonly HostClassUnverifiedTransitionDetail[];
  readonly lastKnownGoodIdentities?: readonly HostClassIdentity[];
  readonly indeterminateIdentities?: readonly HostClassIdentity[];
  readonly authoritativeDeletions?: readonly HostClassIdentity[];
}

export interface HostClassProjectionMessage {
  readonly code: string;
  readonly message: string;
}

export interface HostClassUnverifiedTransitionDetail {
  readonly identity: HostClassIdentity;
  readonly reasonCode: HostClassUnverifiedReasonCode;
  readonly message: string;
  readonly authorityAfter: 'lastKnownGood' | 'indeterminate';
}

export type HostClassExplicitRefreshOutcome =
  | {
      readonly status: 'succeeded';
      readonly revision: number;
      readonly associationFailureCount: number;
    }
  | {
      readonly status: 'cancelled' | 'superseded';
    }
  | {
      readonly status: 'failed';
      readonly reason: 'executionFailed' | 'commandFailed' | 'invalidResult' | 'notificationFailed';
      readonly exitCode?: number;
    };

export interface HostClassExplicitRefreshHandle {
  readonly completion: Promise<HostClassExplicitRefreshOutcome>;
  cancel(): void;
}

export interface HostClassIdentity {
  readonly name: string;
  readonly kind: 'form' | 'document';
}

export interface HostClassProjection {
  readonly intrinsicEventSourceName: string;
  readonly events: readonly HostEventSignature[];
  readonly baseTypeProvenance?: HostClassBaseTypeProvenance;
}

export interface HostClassBaseTypeProvenance {
  readonly name: string;
  readonly libraryGuid: string;
  readonly majorVersion: number;
  readonly minorVersion: number;
  readonly lcid: number;
}

export type HostEventParameterType =
  | {
      readonly kind: 'intrinsic';
      readonly name: string;
    }
  | {
      readonly kind: 'typeLib';
      readonly name: string;
      readonly libraryGuid: string;
      readonly majorVersion: number;
      readonly minorVersion: number;
      readonly lcid: number;
    }
  | {
      readonly kind: 'unresolved';
      readonly displayName: string;
    };

export interface HostEventParameter {
  readonly name: string;
  readonly type: HostEventParameterType;
  readonly passing: 'byVal' | 'byRef';
  readonly arrayShape: 'scalar' | 'array';
  readonly optional: boolean;
  readonly paramArray: boolean;
}

export interface HostEventSignature {
  readonly name: string;
  readonly parameters: readonly HostEventParameter[];
  readonly documentation?: string;
  readonly authoringAvailable: boolean;
  readonly existingHandlerRecognizable: boolean;
}

export interface CurrentHostClassProjectionEntry {
  readonly identity: HostClassIdentity;
  readonly authority: 'current';
  readonly projection: HostClassProjection;
}

export interface LastKnownGoodHostClassProjectionEntry {
  readonly identity: HostClassIdentity;
  readonly authority: 'lastKnownGood';
  readonly projection: HostClassProjection;
}

export interface IndeterminateHostClassProjectionEntry {
  readonly identity: HostClassIdentity;
  readonly authority: 'indeterminate';
}

export type HostClassProjectionSnapshotEntry =
  | CurrentHostClassProjectionEntry
  | LastKnownGoodHostClassProjectionEntry
  | IndeterminateHostClassProjectionEntry;

export interface PresentHostClassProjectionSnapshot extends HostClassProjectionContext {
  readonly schemaVersion: 2;
  readonly revision: number;
  readonly state: 'present';
  readonly vbaProjectName?: string;
  readonly sourceTemplateFingerprint?: string;
  readonly classEnumerationComplete: boolean;
  readonly classes: readonly HostClassProjectionSnapshotEntry[];
}

export interface ClearedHostClassProjectionSnapshot extends HostClassProjectionContext {
  readonly schemaVersion: 2;
  readonly revision: number;
  readonly state: 'cleared';
}

export type HostClassProjectionSnapshot =
  | PresentHostClassProjectionSnapshot
  | ClearedHostClassProjectionSnapshot;

interface ParsedHostClassProjectionResult extends HostClassProjectionContext {
  readonly vbaProjectName?: string;
  readonly sourceTemplateFingerprint?: string;
  readonly classEnumerationComplete: boolean;
  readonly complete: boolean;
  readonly classes: readonly ParsedHostClassEntry[];
  readonly diagnostics: readonly HostClassProjectionMessage[];
  readonly warnings: readonly HostClassProjectionMessage[];
}

interface ParsedResolvedHostClassEntry {
  readonly identity: HostClassIdentity;
  readonly status: 'resolved';
  readonly projection: HostClassProjection;
}

interface ParsedUnverifiedHostClassEntry {
  readonly identity: HostClassIdentity;
  readonly status: 'unverified';
  readonly reasonCode: HostClassUnverifiedReasonCode;
  readonly message: string;
}

type ParsedHostClassEntry =
  | ParsedResolvedHostClassEntry
  | ParsedUnverifiedHostClassEntry;

export type HostClassUnverifiedReasonCode =
  | 'eventEnumerationFailure'
  | 'intrinsicEventSourceNameReadFailure'
  | 'signatureReadFailure'
  | 'availabilityReadFailure'
  | 'inspectionTimeout'
  | 'inspectionAborted'
  | 'cancelled'
  | 'inspectionFailure';

interface DocumentLifecycleState {
  context: HostClassProjectionContext;
  active: boolean;
  generation: number;
  revision: number;
  hasProjection: boolean;
  vbaProjectName: string | undefined;
  sourceTemplateFingerprint: string | undefined;
  classEnumerationComplete: boolean;
  classes: readonly HostClassProjectionSnapshotEntry[];
  sourceCandidates: readonly HostClassSourceCandidate[];
  associationResult: HostClassSourceAssociationResult | undefined;
}

interface ScheduledHostClassListInvocation extends HostClassListInvocation {
  readonly cancellationSource: HostClassCancellationSource;
  readonly completeExplicitRefresh?: (
    outcome: HostClassExplicitRefreshOutcome
  ) => void;
}

interface PendingHostClassInspection {
  readonly key: string;
  readonly invocation: ScheduledHostClassListInvocation;
  readonly clearBeforeStart: ClearedHostClassProjectionSnapshot | undefined;
}

interface DelayedHostClassInspection {
  readonly generation: number;
  readonly work: PendingHostClassInspection;
  readonly disposable: HostClassCancellationDisposable;
}

export class HostClassProjectionLifecycle {
  private readonly states = new Map<string, DocumentLifecycleState>();
  private readonly runningInvocations = new Map<string, ScheduledHostClassListInvocation>();
  private readonly pendingByKey = new Map<string, PendingHostClassInspection>();
  private readonly pendingOrder: string[] = [];
  private readonly delayedByKey = new Map<string, DelayedHostClassInspection>();
  private readonly manifestResolutionBarriers = new Map<
    string,
    { readonly completion: Promise<void>; readonly release: () => void }
  >();
  private pump: Promise<void> | undefined;
  private controlNotifications: Promise<void> = Promise.resolve();
  private shutdownRequested = false;

  public constructor(private readonly options: HostClassProjectionLifecycleOptions) {
  }

  public activateDocument(context: HostClassProjectionContext): void {
    this.schedule(context, 'activation');
  }

  public templateChanged(context: HostClassProjectionContext): void {
    this.schedule(context, 'templateChanged');
  }

  public manifestChanged(context: HostClassProjectionContext): void {
    this.schedule(context, 'manifestChanged');
  }

  public beginManifestResolution(context: HostClassProjectionContext): void {
    if (this.shutdownRequested) {
      return;
    }

    const key = documentKey(context);
    if (this.manifestResolutionBarriers.has(key)) {
      return;
    }

    let release: (() => void) | undefined;
    const completion = new Promise<void>((resolve) => {
      release = resolve;
    });
    this.manifestResolutionBarriers.set(key, {
      completion,
      release: () => release?.()
    });
  }

  public completeManifestResolution(context: HostClassProjectionContext): void {
    const key = documentKey(context);
    const barrier = this.manifestResolutionBarriers.get(key);
    if (barrier === undefined) {
      return;
    }

    this.manifestResolutionBarriers.delete(key);
    barrier.release();
  }

  public scheduleResolvedAutomaticRefresh(
    context: HostClassProjectionContext,
    trigger: 'manifestChanged' | 'templateChanged'
  ): void {
    this.schedule(context, trigger, undefined, false);
  }

  public refreshDocument(context: HostClassProjectionContext): HostClassExplicitRefreshHandle {
    let settle: ((outcome: HostClassExplicitRefreshOutcome) => void) | undefined;
    let settled = false;
    const completion = new Promise<HostClassExplicitRefreshOutcome>((resolve) => {
      settle = (outcome) => {
        if (!settled) {
          settled = true;
          resolve(outcome);
        }
      };
    });
    const invocation = this.schedule(context, 'explicitRefresh', settle);
    if (invocation === undefined) {
      settle?.({ status: 'cancelled' });
    }

    return {
      completion,
      cancel: () => {
        if (invocation !== undefined) {
          this.cancelExplicitRefresh(invocation);
        }
      }
    };
  }

  public reevaluateSourceAssociations(
    context: HostClassProjectionContext,
    sources: readonly HostClassSourceCandidate[]
  ): HostClassSourceAssociationResult | undefined {
    const state = this.states.get(documentKey(context));
    if (state === undefined || !state.active || !contextsEqual(state.context, context)) {
      return undefined;
    }

    state.sourceCandidates = sources.map((source) => ({
      ...source,
      moduleIdentity: { ...source.moduleIdentity }
    }));
    if (!state.hasProjection) {
      return undefined;
    }

    const result = associateHostClassSources(
      state.sourceCandidates,
      state.classes,
      state.classEnumerationComplete
    );
    state.associationResult = result;
    this.publishSourceAssociation(state.context, result);
    return result;
  }

  public removeDocument(context: HostClassProjectionContext): void {
    if (this.shutdownRequested) {
      return;
    }

    const key = documentKey(context);
    const state = this.states.get(key);
    if (state === undefined || !state.active || !contextsEqual(state.context, context)) {
      return;
    }

    state.generation += 1;
    state.active = false;
    const running = this.runningInvocations.get(key);
    if (running !== undefined) {
      this.requestCancellation(
        running,
        'documentRemoved',
        'Removing the manifest document cancelled its running host-class inspection.'
      );
    }
    const delayed = this.delayedByKey.get(key);
    const pending = this.pendingByKey.get(key);
    const terminalPendingClear = delayed?.work.clearBeforeStart ??
      this.pendingByKey.get(key)?.clearBeforeStart;
    delayed?.disposable.dispose();
    if (delayed !== undefined) {
      this.requestCancellation(
        delayed.work.invocation,
        'documentRemoved',
        'Removing the manifest document dropped its delayed host-class inspection.'
      );
      this.publishDiscarded(
        delayed.work.invocation,
        'documentRemoved',
        'The delayed host-class inspection was discarded because its document was removed.'
      );
    }
    this.delayedByKey.delete(key);
    if (pending !== undefined) {
      this.requestCancellation(
        pending.invocation,
        'documentRemoved',
        'Removing the manifest document dropped its queued host-class inspection.'
      );
      this.publishDiscarded(
        pending.invocation,
        'documentRemoved',
        'The queued host-class inspection was discarded because its document was removed.'
      );
    }
    pending?.invocation.completeExplicitRefresh?.({
      status: 'superseded'
    });
    this.pendingByKey.delete(key);
    for (let index = this.pendingOrder.length - 1; index >= 0; index -= 1) {
      if (this.pendingOrder[index] === key) {
        this.pendingOrder.splice(index, 1);
      }
    }

    if (!state.hasProjection) {
      this.publishTransition({
        kind: 'cleared',
        context: state.context,
        generation: state.generation,
        revision: state.revision,
        reasonCode: 'documentRemoved',
        message: 'The manifest project document was removed.'
      });
      if (terminalPendingClear !== undefined) {
        this.controlNotifications = this.controlNotifications
          .then(() => this.options.sendNotification(
            HostClassProjectionSnapshotMethod,
            terminalPendingClear
          ))
          .catch(() => undefined);
      }
      return;
    }

    state.revision += 1;
    state.hasProjection = false;
    state.vbaProjectName = undefined;
    state.sourceTemplateFingerprint = undefined;
    state.classEnumerationComplete = false;
    state.classes = [];
    state.associationResult = undefined;
    const cleared: ClearedHostClassProjectionSnapshot = {
      schemaVersion: 2,
      revision: state.revision,
      project: state.context.project,
      document: state.context.document,
      sourceTemplate: state.context.sourceTemplate,
      state: 'cleared'
    };
    this.publishTransition({
      kind: 'cleared',
      context: state.context,
      generation: state.generation,
      revision: state.revision,
      reasonCode: 'documentRemoved',
      message: 'The manifest project document was removed and its projection was cleared.'
    });
    this.controlNotifications = this.controlNotifications
      .then(() => this.options.sendNotification(
        HostClassProjectionSnapshotMethod,
        cleared
      ))
      .catch(() => undefined);
  }

  public replayDesiredSnapshots(): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }

    const desired = [...this.states.values()]
      .filter((state) => state.active)
      .map((state) => {
        const snapshot = desiredSnapshot(state);
        return snapshot === undefined
          ? undefined
          : { snapshot, generation: state.generation };
      })
      .filter((entry): entry is {
        readonly snapshot: HostClassProjectionSnapshot;
        readonly generation: number;
      } => entry !== undefined);
    const replay = this.controlNotifications.then(async () => {
      for (const entry of desired) {
        const { snapshot, generation } = entry;
        try {
          await this.options.sendNotification(
            HostClassProjectionSnapshotMethod,
            snapshot
          );
          const state = this.states.get(documentKey(snapshot));
          const currentDesired = state === undefined
            ? undefined
            : desiredSnapshot(state);
          if (state?.active === true &&
            state.generation === generation &&
            currentDesired?.revision === snapshot.revision &&
            contextsEqual(currentDesired, snapshot)) {
            this.publishTransition({
              kind: 'replayed',
              context: snapshot,
              generation,
              revision: snapshot.revision,
              reasonCode: 'desiredSnapshotReplayed',
              message: 'The desired host-class snapshot was replayed to the language server.'
            });
          }
        } catch {
          const state = this.states.get(documentKey(snapshot));
          const currentDesired = state === undefined
            ? undefined
            : desiredSnapshot(state);
          if (state?.active === true &&
            state.generation === generation &&
            currentDesired?.revision === snapshot.revision &&
            contextsEqual(currentDesired, snapshot)) {
            this.publishTransition({
              kind: 'discarded',
              context: snapshot,
              generation,
              revision: snapshot.revision,
              reasonCode: 'replayNotificationFailure',
              message: 'The desired host-class snapshot replay could not be delivered to the language server.'
            });
          }
        }
      }
    });
    this.controlNotifications = replay.catch(() => undefined);
    return replay;
  }

  public shutdown(): void {
    if (this.shutdownRequested) {
      return;
    }

    this.shutdownRequested = true;
    for (const barrier of this.manifestResolutionBarriers.values()) {
      barrier.release();
    }
    this.manifestResolutionBarriers.clear();
    for (const invocation of this.runningInvocations.values()) {
      this.requestCancellation(
        invocation,
        'shutdown',
        'Extension shutdown requested cancellation of the running host-class inspection.'
      );
    }
    for (const delayed of this.delayedByKey.values()) {
      delayed.disposable.dispose();
      this.requestCancellation(
        delayed.work.invocation,
        'shutdown',
        'Extension shutdown dropped the delayed host-class inspection.'
      );
      this.publishDiscarded(
        delayed.work.invocation,
        'shutdown',
        'The delayed host-class inspection was dropped during extension shutdown.'
      );
      delayed.work.invocation.completeExplicitRefresh?.({ status: 'cancelled' });
    }
    for (const pending of this.pendingByKey.values()) {
      this.requestCancellation(
        pending.invocation,
        'shutdown',
        'Extension shutdown dropped the queued host-class inspection.'
      );
      this.publishDiscarded(
        pending.invocation,
        'shutdown',
        'The queued host-class inspection was dropped during extension shutdown.'
      );
      pending.invocation.completeExplicitRefresh?.({ status: 'cancelled' });
    }
    this.delayedByKey.clear();
    this.pendingByKey.clear();
    this.pendingOrder.length = 0;
  }

  public async flush(): Promise<void> {
    while (true) {
      this.ensurePump();
      const pump = this.pump;
      const notifications = this.controlNotifications;
      await Promise.all([
        pump ?? Promise.resolve(),
        notifications
      ]);
      if (this.pump === undefined &&
          this.pendingOrder.length === 0 &&
          notifications === this.controlNotifications) {
        return;
      }
    }
  }

  private schedule(
    context: HostClassProjectionContext,
    trigger: HostClassListInvocation['trigger'],
    completeExplicitRefresh?: (
      outcome: HostClassExplicitRefreshOutcome
    ) => void,
    debounceAutomaticRefresh = true
  ): ScheduledHostClassListInvocation | undefined {
    if (this.shutdownRequested) {
      return undefined;
    }

    const key = documentKey(context);
    const running = this.runningInvocations.get(key);
    if (running !== undefined) {
      this.requestCancellation(
        running,
        'superseded',
        'A newer host-class refresh generation superseded the running invocation.'
      );
    }
    const existingDelay = this.delayedByKey.get(key);
    if (existingDelay !== undefined) {
      this.requestCancellation(
        existingDelay.work.invocation,
        'superseded',
        'A newer host-class refresh generation superseded the delayed invocation.'
      );
      this.publishDiscarded(
        existingDelay.work.invocation,
        'supersededGeneration',
        'The delayed host-class inspection was discarded before it started.'
      );
    }
    existingDelay?.disposable.dispose();
    existingDelay?.work.invocation.completeExplicitRefresh?.({
      status: 'superseded'
    });
    this.delayedByKey.delete(key);
    const delayBeforeEnqueue = debounceAutomaticRefresh &&
      (trigger === 'templateChanged' || trigger === 'manifestChanged');
    const existingPending = delayBeforeEnqueue
      ? this.pendingByKey.get(key)
      : undefined;
    if (existingPending !== undefined) {
      this.requestCancellation(
        existingPending.invocation,
        'superseded',
        'A newer host-class refresh generation superseded the queued invocation.'
      );
      this.publishDiscarded(
        existingPending.invocation,
        'supersededGeneration',
        'The queued host-class inspection was discarded before it started.'
      );
      existingPending.invocation.completeExplicitRefresh?.({ status: 'superseded' });
      this.pendingByKey.delete(key);
      for (let index = this.pendingOrder.length - 1; index >= 0; index -= 1) {
        if (this.pendingOrder[index] === key) {
          this.pendingOrder.splice(index, 1);
        }
      }
    }
    const previous = this.states.get(key);
    const state: DocumentLifecycleState = {
      context: { ...context },
      active: true,
      generation: (previous?.generation ?? 0) + 1,
      revision: previous?.revision ?? 0,
      hasProjection: previous?.hasProjection ?? false,
      vbaProjectName: previous?.vbaProjectName,
      sourceTemplateFingerprint: previous?.sourceTemplateFingerprint,
      classEnumerationComplete: previous?.classEnumerationComplete ?? false,
      classes: previous?.classes ?? [],
      sourceCandidates: previous?.active === true && contextsEqual(previous.context, context)
        ? previous.sourceCandidates
        : [],
      associationResult: previous?.associationResult
    };
    this.states.set(key, state);

    let clearBeforeStart: ClearedHostClassProjectionSnapshot | undefined;
    if (previous?.hasProjection === true &&
        !contextsEqual(previous.context, state.context)) {
      state.revision += 1;
      state.hasProjection = false;
      state.vbaProjectName = undefined;
      state.sourceTemplateFingerprint = undefined;
      state.classEnumerationComplete = false;
      clearBeforeStart = {
        schemaVersion: 2,
        revision: state.revision,
        project: state.context.project,
        document: state.context.document,
        sourceTemplate: state.context.sourceTemplate,
        state: 'cleared'
      };
      state.classes = [];
      state.associationResult = undefined;
    }

    const cancellationSource = new HostClassCancellationSource();
    const invocation: ScheduledHostClassListInvocation = {
      context: state.context,
      generation: state.generation,
      trigger,
      cancellationToken: cancellationSource.token,
      cancellationSource,
      completeExplicitRefresh,
      args: [
        'host-class',
        'list',
        '--project',
        state.context.project,
        '--document',
        state.context.document,
        '--format',
        'json'
      ]
    };

    const work: PendingHostClassInspection = {
      key,
      invocation,
      clearBeforeStart: clearBeforeStart ??
        existingDelay?.work.clearBeforeStart ??
        existingPending?.clearBeforeStart
    };
    this.publishTransition({
      kind: 'queued',
      context: state.context,
      generation: state.generation,
      revision: state.revision,
      trigger
    });
    if (delayBeforeEnqueue) {
      const scheduleDelay = this.options.scheduleDelay ?? scheduleHostClassDelay;
      const disposable = scheduleDelay(1000, () => {
        const delayed = this.delayedByKey.get(key);
        if (delayed?.generation !== invocation.generation || this.shutdownRequested) {
          return;
        }

        this.delayedByKey.delete(key);
        this.enqueue(delayed.work);
      });
      this.delayedByKey.set(key, {
        generation: invocation.generation,
        work,
        disposable
      });
      return invocation;
    }

    this.enqueue(work);
    return invocation;
  }

  private enqueue(work: PendingHostClassInspection): void {
    const existing = this.pendingByKey.get(work.key);
    if (existing !== undefined) {
      this.requestCancellation(
        existing.invocation,
        'superseded',
        'A newer host-class refresh generation superseded the queued invocation.'
      );
      this.publishDiscarded(
        existing.invocation,
        'supersededGeneration',
        'The queued host-class inspection was discarded before it started.'
      );
      const existingOrderIndex = this.pendingOrder.indexOf(work.key);
      if (existingOrderIndex >= 0) {
        this.pendingOrder.splice(existingOrderIndex, 1);
      }
    }
    existing?.invocation.completeExplicitRefresh?.({ status: 'superseded' });
    this.pendingOrder.push(work.key);

    const retainedClear = work.clearBeforeStart ?? existing?.clearBeforeStart;
    this.pendingByKey.set(work.key, {
      ...work,
      clearBeforeStart: retainedClear === undefined
        ? undefined
        : {
            ...retainedClear,
            project: work.invocation.context.project,
            document: work.invocation.context.document,
            sourceTemplate: work.invocation.context.sourceTemplate
          }
    });
    this.ensurePump();
  }

  private cancelExplicitRefresh(
    invocation: ScheduledHostClassListInvocation
  ): void {
    if (invocation.cancellationToken.isCancellationRequested) {
      return;
    }

    const key = documentKey(invocation.context);
    this.publishTransition({
      kind: 'cancellationRequested',
      context: invocation.context,
      generation: invocation.generation,
      revision: this.states.get(key)?.revision ?? 0,
      trigger: invocation.trigger,
      reasonCode: 'explicitCancellation',
      message: 'The explicit host-class refresh was cancelled.'
    });
    invocation.cancellationSource.cancel();

    const delayed = this.delayedByKey.get(key);
    if (delayed?.work.invocation === invocation) {
      delayed.disposable.dispose();
      this.delayedByKey.delete(key);
      this.settleCancelledBeforeStart(invocation);
      return;
    }

    if (this.pendingByKey.get(key)?.invocation !== invocation) {
      return;
    }

    this.pendingByKey.delete(key);
    for (let index = this.pendingOrder.length - 1; index >= 0; index -= 1) {
      if (this.pendingOrder[index] === key) {
        this.pendingOrder.splice(index, 1);
      }
    }
    this.settleCancelledBeforeStart(invocation);
  }

  private requestCancellation(
    invocation: ScheduledHostClassListInvocation,
    reasonCode: string,
    message: string
  ): void {
    if (invocation.cancellationToken.isCancellationRequested) {
      return;
    }

    this.publishTransition({
      kind: 'cancellationRequested',
      context: invocation.context,
      generation: invocation.generation,
      revision: this.states.get(documentKey(invocation.context))?.revision ?? 0,
      trigger: invocation.trigger,
      reasonCode,
      message
    });
    invocation.cancellationSource.cancel();
  }

  private settleCancelledBeforeStart(
    invocation: ScheduledHostClassListInvocation
  ): void {
    this.publishTransition({
      kind: 'cancelled',
      context: invocation.context,
      generation: invocation.generation,
      revision: this.states.get(documentKey(invocation.context))?.revision ?? 0,
      trigger: invocation.trigger,
      reasonCode: 'cancelledBeforeStart',
      message: 'The queued host-class refresh was cancelled before it started.'
    });
    invocation.completeExplicitRefresh?.({ status: 'cancelled' });
  }

  private ensurePump(): void {
    if (this.pump !== undefined || this.pendingOrder.length === 0) {
      return;
    }

    this.pump = Promise.resolve()
      .then(() => this.drainPending())
      .finally(() => {
        this.pump = undefined;
        this.ensurePump();
      });
  }

  private async drainPending(): Promise<void> {
    while (!this.shutdownRequested && this.pendingOrder.length > 0) {
      const key = this.pendingOrder.shift();
      if (key === undefined) {
        continue;
      }

      const work = this.pendingByKey.get(key);
      if (work === undefined) {
        continue;
      }
      this.pendingByKey.delete(key);

      if (work.invocation.cancellationToken.isCancellationRequested) {
        work.invocation.completeExplicitRefresh?.({ status: 'cancelled' });
        continue;
      }

      if (work.clearBeforeStart !== undefined) {
        try {
          await this.options.sendNotification(
            HostClassProjectionSnapshotMethod,
            work.clearBeforeStart
          );
        } catch {
          // A transient transport failure must not discard the replacement
          // inspection. Its result remains the desired snapshot for replay.
        }
      }

      const current = this.states.get(key);
      if (this.shutdownRequested ||
          work.invocation.cancellationToken.isCancellationRequested ||
          current === undefined ||
          !current.active ||
          current.generation !== work.invocation.generation ||
          !contextsEqual(current.context, work.invocation.context)) {
        const cancelled = this.shutdownRequested ||
          work.invocation.cancellationToken.isCancellationRequested;
        this.publishDiscarded(
          work.invocation,
          this.shutdownRequested
            ? 'shutdown'
            : cancelled ? 'cancelledBeforeInspection' : 'supersededGeneration',
          this.shutdownRequested
            ? 'The host-class inspection was dropped during extension shutdown.'
            : cancelled
              ? 'The host-class inspection was cancelled before the process started.'
              : 'The host-class inspection was no longer current before the process started.'
        );
        work.invocation.completeExplicitRefresh?.({
          status: cancelled ? 'cancelled' : 'superseded'
        });
        continue;
      }

      await this.runInvocation(key, work.invocation);
    }
  }

  private async runInvocation(
    key: string,
    invocation: ScheduledHostClassListInvocation
  ): Promise<void> {
    this.runningInvocations.set(key, invocation);
    const stateAtStart = this.states.get(key);
    this.publishTransition({
      kind: 'started',
      context: invocation.context,
      generation: invocation.generation,
      revision: stateAtStart?.revision ?? 0,
      trigger: invocation.trigger
    });
    let result: HostClassListRunResult;
    try {
      result = await this.options.runHostClassList(invocation);
      await this.manifestResolutionBarriers.get(key)?.completion;
    } catch (error) {
      await this.manifestResolutionBarriers.get(key)?.completion;
      if (this.shutdownRequested) {
        this.publishDiscarded(
          invocation,
          'shutdown',
          'The host-class inspection failure was discarded during extension shutdown.'
        );
        invocation.completeExplicitRefresh?.({ status: 'cancelled' });
        return;
      }

      const cancelled = invocation.cancellationToken.isCancellationRequested;
      await this.degradeFailedTemplateRefresh(invocation);
      this.publishDiscarded(
        invocation,
        cancelled ? 'cancelledExecutionFailure' : 'executionFailure',
        `${cancelled ? 'The cancelled' : 'The'} host-class inspection invocation failed before producing a result: ${
          error instanceof Error ? error.message : String(error)
        }`
      );
      invocation.completeExplicitRefresh?.(cancelled
        ? { status: 'cancelled' }
        : {
            status: 'failed',
            reason: 'executionFailed'
          });
      return;
    } finally {
      if (this.runningInvocations.get(key) === invocation) {
        this.runningInvocations.delete(key);
      }
    }
    if (this.shutdownRequested) {
      this.publishDiscarded(
        invocation,
        'shutdown',
        'The host-class inspection result was discarded during extension shutdown.'
      );
      invocation.completeExplicitRefresh?.({ status: 'cancelled' });
      return;
    }
    const invocationCancelled =
      invocation.cancellationToken.isCancellationRequested ||
      result.cancelled ||
      result.exitCode === 130;
    if (result.exitCode !== 0 && result.exitCode !== 1 && result.exitCode !== 130) {
      await this.degradeFailedTemplateRefresh(invocation);
      this.publishDiscarded(
        invocation,
        invocationCancelled ? 'cancelledCommandFailure' : 'commandFailure',
        invocationCancelled
          ? `The cancelled host-class inspection exited with code ${result.exitCode}.`
          : `The host-class inspection exited with code ${result.exitCode}.`
      );
      invocation.completeExplicitRefresh?.(invocationCancelled
        ? { status: 'cancelled' }
        : {
            status: 'failed',
            reason: 'commandFailed',
            exitCode: result.exitCode
          });
      return;
    }

    const parsed = parseCompletedHostClassProjectionResult(result.stdout);
    const current = this.states.get(key);
    if (current === undefined ||
        !current.active ||
        current.generation !== invocation.generation ||
        !contextsEqual(current.context, invocation.context)) {
      this.publishDiscarded(
        invocation,
        'supersededGeneration',
        'A newer host-class refresh generation superseded this result.'
      );
      invocation.completeExplicitRefresh?.({ status: 'superseded' });
      return;
    }
    if (parsed === undefined || !contextsEqual(current.context, parsed)) {
      await this.degradeFailedTemplateRefresh(invocation);
      this.publishDiscarded(
        invocation,
        invocationCancelled
          ? 'cancelledInvalidResult'
          : parsed === undefined ? 'schemaMismatch' : 'contextMismatch',
        parsed === undefined
          ? 'The host-class inspection output did not match schema 1.1.'
          : 'The host-class inspection output did not exactly match its request context.'
      );
      invocation.completeExplicitRefresh?.(invocationCancelled
        ? { status: 'cancelled' }
        : {
            status: 'failed',
            reason: 'invalidResult'
          });
      return;
    }
    if ((result.exitCode === 0) !== parsed.complete) {
      await this.degradeFailedTemplateRefresh(invocation);
      this.publishDiscarded(
        invocation,
        invocationCancelled ? 'cancelledInvalidResult' : 'completenessMismatch',
        `The host-class inspection exit code ${result.exitCode} contradicted its complete value.`
      );
      invocation.completeExplicitRefresh?.(invocationCancelled
        ? { status: 'cancelled' }
        : {
            status: 'failed',
            reason: 'invalidResult'
          });
      return;
    }

    const previousClasses = current.classes;
    current.revision += 1;
    const classes = foldProjectionClasses(
      current.classes,
      parsed.classes,
      parsed.classEnumerationComplete
    );
    const snapshot: PresentHostClassProjectionSnapshot = {
      schemaVersion: 2,
      revision: current.revision,
      project: current.context.project,
      document: current.context.document,
      sourceTemplate: current.context.sourceTemplate,
      state: 'present',
      ...(parsed.vbaProjectName === undefined
        ? {}
        : {
            vbaProjectName: parsed.vbaProjectName,
            sourceTemplateFingerprint: parsed.sourceTemplateFingerprint
          }),
      classEnumerationComplete: parsed.classEnumerationComplete,
      classes
    };
    current.hasProjection = true;
    current.vbaProjectName = parsed.vbaProjectName;
    current.sourceTemplateFingerprint = parsed.sourceTemplateFingerprint;
    current.classEnumerationComplete = parsed.classEnumerationComplete;
    current.classes = classes;
    current.associationResult = current.sourceCandidates.length === 0
      ? undefined
      : associateHostClassSources(
          current.sourceCandidates,
          classes,
          parsed.classEnumerationComplete
        );
    if (current.associationResult !== undefined) {
      this.publishSourceAssociation(
        current.context,
        current.associationResult
      );
    }
    const retainedIdentities = new Set(
      classes.map((entry) => identityKey(entry.identity))
    );
    const authoritativeDeletions = parsed.classEnumerationComplete
      ? previousClasses
        .filter((entry) => !retainedIdentities.has(identityKey(entry.identity)))
        .map((entry) => entry.identity)
      : [];
    const classesByIdentity = new Map(
      classes.map((entry) => [identityKey(entry.identity), entry] as const)
    );
    const unverifiedClasses = parsed.classes
      .filter((entry): entry is ParsedUnverifiedHostClassEntry =>
        entry.status === 'unverified'
      )
      .map((entry): HostClassUnverifiedTransitionDetail => ({
        identity: entry.identity,
        reasonCode: entry.reasonCode,
        message: entry.message,
        authorityAfter: classesByIdentity.get(identityKey(entry.identity))?.authority ===
          'lastKnownGood'
          ? 'lastKnownGood'
          : 'indeterminate'
      }));
    this.publishTransition({
      kind: 'committed',
      context: current.context,
      generation: current.generation,
      revision: current.revision,
      trigger: invocation.trigger,
      reasonCode: invocationCancelled
        ? 'cancelledPartialCommitted'
        : 'inspectionCommitted',
      message: invocationCancelled
        ? 'The cancelled inspection returned a current schema-valid partial result that committed.'
        : 'The current host-class inspection committed.',
      resolvedCount: parsed.classes.filter((entry) => entry.status === 'resolved').length,
      unverifiedCount: unverifiedClasses.length,
      lastKnownGoodCount: classes.filter(
        (entry) => entry.authority === 'lastKnownGood'
      ).length,
      indeterminateCount: classes.filter(
        (entry) => entry.authority === 'indeterminate'
      ).length,
      authoritativeDeletionCount: authoritativeDeletions.length,
      associationFailureCount: current.associationResult?.failures.length ?? 0,
      associationResult: current.associationResult,
      classEnumerationComplete: parsed.classEnumerationComplete,
      diagnostics: parsed.diagnostics,
      warnings: parsed.warnings,
      resolvedIdentities: parsed.classes
        .filter((entry): entry is ParsedResolvedHostClassEntry =>
          entry.status === 'resolved'
        )
        .map((entry) => entry.identity),
      unverifiedClasses,
      lastKnownGoodIdentities: classes
        .filter((entry) => entry.authority === 'lastKnownGood')
        .map((entry) => entry.identity),
      indeterminateIdentities: classes
        .filter((entry) => entry.authority === 'indeterminate')
        .map((entry) => entry.identity),
      authoritativeDeletions
    });
    try {
      await this.options.sendNotification(HostClassProjectionSnapshotMethod, snapshot);
    } catch {
      this.publishDiscarded(
        invocation,
        'notificationFailure',
        'The desired host-class snapshot could not be delivered to the language server.'
      );
      invocation.completeExplicitRefresh?.(invocationCancelled
        ? { status: 'cancelled' }
        : {
            status: 'failed',
            reason: 'notificationFailed'
          });
      return;
    }
    invocation.completeExplicitRefresh?.(invocationCancelled
      ? { status: 'cancelled' }
      : result.exitCode !== 0
        ? {
            status: 'failed',
            reason: 'commandFailed',
            exitCode: result.exitCode
          }
      : {
          status: 'succeeded',
          revision: current.revision,
          associationFailureCount: current.associationResult?.failures.length ?? 0
        });
  }

  private async degradeFailedTemplateRefresh(
    invocation: ScheduledHostClassListInvocation
  ): Promise<void> {
    if (invocation.trigger !== 'templateChanged') {
      return;
    }

    const state = this.states.get(documentKey(invocation.context));
    if (state === undefined ||
        !state.active ||
        state.generation !== invocation.generation ||
        !contextsEqual(state.context, invocation.context) ||
        !state.hasProjection ||
        (!state.classEnumerationComplete &&
         !state.classes.some((entry) => entry.authority === 'current'))) {
      return;
    }

    state.revision += 1;
    state.classEnumerationComplete = false;
    state.classes = state.classes.map((entry) => entry.authority === 'current'
      ? {
          identity: entry.identity,
          authority: 'lastKnownGood' as const,
          projection: entry.projection
        }
      : entry);
    state.associationResult = state.sourceCandidates.length === 0
      ? undefined
      : associateHostClassSources(
          state.sourceCandidates,
          state.classes,
          false
        );
    if (state.associationResult !== undefined) {
      this.publishSourceAssociation(state.context, state.associationResult);
    }

    const snapshot: PresentHostClassProjectionSnapshot = {
      schemaVersion: 2,
      revision: state.revision,
      project: state.context.project,
      document: state.context.document,
      sourceTemplate: state.context.sourceTemplate,
      state: 'present',
      ...(state.vbaProjectName === undefined
        ? {}
        : {
            vbaProjectName: state.vbaProjectName,
            sourceTemplateFingerprint: state.sourceTemplateFingerprint
          }),
      classEnumerationComplete: false,
      classes: state.classes
    };
    this.publishTransition({
      kind: 'committed',
      context: state.context,
      generation: state.generation,
      revision: state.revision,
      trigger: invocation.trigger,
      reasonCode: 'templateRefreshFailedLastKnownGood',
      message: 'The failed same-template refresh retained prior projection evidence as last-known-good.',
      resolvedCount: 0,
      unverifiedCount: 0,
      lastKnownGoodCount: state.classes.filter(
        (entry) => entry.authority === 'lastKnownGood'
      ).length,
      indeterminateCount: state.classes.filter(
        (entry) => entry.authority === 'indeterminate'
      ).length,
      authoritativeDeletionCount: 0,
      associationFailureCount: state.associationResult?.failures.length ?? 0,
      associationResult: state.associationResult,
      classEnumerationComplete: false,
      diagnostics: [],
      warnings: [],
      resolvedIdentities: [],
      unverifiedClasses: [],
      lastKnownGoodIdentities: state.classes
        .filter((entry) => entry.authority === 'lastKnownGood')
        .map((entry) => entry.identity),
      indeterminateIdentities: state.classes
        .filter((entry) => entry.authority === 'indeterminate')
        .map((entry) => entry.identity),
      authoritativeDeletions: []
    });
    try {
      await this.options.sendNotification(
        HostClassProjectionSnapshotMethod,
        snapshot
      );
    } catch {
      this.publishDiscarded(
        invocation,
        'notificationFailure',
        'The desired last-known-good host-class snapshot could not be delivered to the language server.'
      );
    }
  }

  private publishSourceAssociation(
    context: HostClassProjectionContext,
    result: HostClassSourceAssociationResult
  ): void {
    const state = this.states.get(documentKey(context));
    this.publishTransition({
      kind: 'sourceAssociationChanged',
      context,
      generation: state?.generation ?? 0,
      revision: state?.revision ?? 0,
      reasonCode: result.failures.length === 0
        ? 'sourceAssociationCurrent'
        : 'sourceAssociationFailure',
      message: result.failures.length === 0
        ? 'Host-class source associations are current.'
        : `${result.failures.length} host-class source association failure(s) remain.`,
      associationFailureCount: result.failures.length,
      associationResult: result
    });
    try {
      this.options.onSourceAssociationChanged?.(context, result);
    } catch {
      // Observability must not poison projection scheduling.
    }
  }

  private publishTransition(
    transition: HostClassProjectionLifecycleTransition
  ): void {
    try {
      this.options.onTransition?.(transition);
    } catch {
      // Observability must not poison projection scheduling.
    }
  }

  private publishDiscarded(
    invocation: ScheduledHostClassListInvocation,
    reasonCode: string,
    message: string
  ): void {
    this.publishTransition({
      kind: 'discarded',
      context: invocation.context,
      generation: invocation.generation,
      revision: this.states.get(documentKey(invocation.context))?.revision ?? 0,
      trigger: invocation.trigger,
      reasonCode,
      message
    });
  }
}

function parseCompletedHostClassProjectionResult(
  json: string
): ParsedHostClassProjectionResult | undefined {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return undefined;
  }

  if (!isRecord(value)) {
    return undefined;
  }

  const isCurrentSchema = value.schemaVersion === '1.1';
  const hasProjectName = Object.prototype.hasOwnProperty.call(
    value,
    'vbaProjectName'
  );
  const hasTemplateFingerprint = Object.prototype.hasOwnProperty.call(
    value,
    'sourceTemplateFingerprint'
  );
  const hasProjectAuthority = isCurrentSchema
    && hasProjectName
    && hasTemplateFingerprint;
  const allowedProperties = [
        'schemaVersion',
        'project',
        'document',
        'sourceTemplate',
        'classEnumerationComplete',
        'complete',
        'classes',
        'diagnostics',
        'warnings'
      ];
  if (isCurrentSchema) {
    allowedProperties.push('vbaProjectName', 'sourceTemplateFingerprint');
  }

  if (!hasOnlyProperties(value, allowedProperties) ||
      !isCurrentSchema ||
      hasProjectName !== hasTemplateFingerprint ||
      !isNonemptyString(value.project) ||
      !isNonemptyString(value.document) ||
      !isNonemptyString(value.sourceTemplate) ||
      (hasProjectAuthority &&
        (!isExactNonemptyString(value.vbaProjectName) ||
         [...value.vbaProjectName].length > 31 ||
         !isSha256Fingerprint(value.sourceTemplateFingerprint))) ||
      typeof value.classEnumerationComplete !== 'boolean' ||
      typeof value.complete !== 'boolean' ||
      !Array.isArray(value.classes) ||
      !isMessageArray(value.diagnostics) ||
      !isMessageArray(value.warnings)) {
    return undefined;
  }

  const classes: ParsedHostClassEntry[] = [];
  const identities = new Set<string>();
  for (const entry of value.classes) {
    const parsed = parseHostClassEntry(entry);
    if (parsed === undefined || identities.has(identityKey(parsed.identity))) {
      return undefined;
    }
    identities.add(identityKey(parsed.identity));
    classes.push(parsed);
  }

  if (value.complete &&
      (!value.classEnumerationComplete || classes.some((entry) => entry.status !== 'resolved'))) {
    return undefined;
  }

  return {
    project: value.project,
    document: value.document,
    sourceTemplate: value.sourceTemplate,
    ...(hasProjectAuthority
      ? {
          vbaProjectName: value.vbaProjectName as string,
          sourceTemplateFingerprint:
            (value.sourceTemplateFingerprint as string).toUpperCase()
        }
      : {}),
    classEnumerationComplete: value.classEnumerationComplete,
    complete: value.complete,
    classes,
    diagnostics: value.diagnostics,
    warnings: value.warnings
  };
}

function parseHostClassEntry(value: unknown): ParsedHostClassEntry | undefined {
  if (!isRecord(value) ||
      !isRecord(value.identity) ||
      !hasOnlyProperties(value.identity, ['name', 'kind']) ||
      !isExactNonemptyString(value.identity.name) ||
      (value.identity.kind !== 'form' && value.identity.kind !== 'document')) {
    return undefined;
  }

  const identity: HostClassIdentity = {
    name: value.identity.name,
    kind: value.identity.kind
  };
  if (value.status === 'unverified') {
    return hasOnlyProperties(value, ['identity', 'status', 'reasonCode', 'message']) &&
      isHostClassUnverifiedReasonCode(value.reasonCode) &&
      isNonemptyString(value.message)
      ? {
          identity,
          status: 'unverified',
          reasonCode: value.reasonCode,
          message: value.message
        }
      : undefined;
  }

  if (value.status !== 'resolved' ||
      !hasOnlyProperties(value, [
        'identity',
        'status',
        'intrinsicEventSourceName',
        'events',
        'baseTypeProvenance'
      ]) ||
      !isExactNonemptyString(value.intrinsicEventSourceName) ||
      !Array.isArray(value.events)) {
    return undefined;
  }


  const events: HostEventSignature[] = [];
  const eventNames = new Set<string>();
  for (const event of value.events) {
    const parsedEvent = parseHostEventSignature(event);
    if (parsedEvent === undefined || eventNames.has(parsedEvent.name.toLowerCase())) {
      return undefined;
    }
    eventNames.add(parsedEvent.name.toLowerCase());
    events.push(parsedEvent);
  }

  const baseTypeProvenance = Object.hasOwn(value, 'baseTypeProvenance')
    ? parseBaseTypeProvenance(value.baseTypeProvenance)
    : undefined;
  if (Object.hasOwn(value, 'baseTypeProvenance') && baseTypeProvenance === undefined) {
    return undefined;
  }

  return {
    identity,
    status: 'resolved',
    projection: {
      intrinsicEventSourceName: value.intrinsicEventSourceName,
      events,
      ...(baseTypeProvenance === undefined ? {} : { baseTypeProvenance })
    }
  };
}

function parseHostEventSignature(value: unknown): HostEventSignature | undefined {
  if (!isRecord(value) ||
      !hasOnlyProperties(value, [
        'name',
        'parameters',
        'documentation',
        'authoringAvailable',
        'existingHandlerRecognizable'
      ]) ||
      !isExactNonemptyString(value.name) ||
      !Array.isArray(value.parameters) ||
      (Object.hasOwn(value, 'documentation') && typeof value.documentation !== 'string') ||
      typeof value.authoringAvailable !== 'boolean' ||
      typeof value.existingHandlerRecognizable !== 'boolean') {
    return undefined;
  }

  const parameters: HostEventParameter[] = [];
  for (const parameter of value.parameters) {
    const parsed = parseHostEventParameter(parameter);
    if (parsed === undefined) {
      return undefined;
    }
    parameters.push(parsed);
  }

  return {
    name: value.name,
    parameters,
    ...(typeof value.documentation === 'string'
      ? { documentation: value.documentation }
      : {}),
    authoringAvailable: value.authoringAvailable,
    existingHandlerRecognizable: value.existingHandlerRecognizable
  };
}

function parseHostEventParameter(value: unknown): HostEventParameter | undefined {
  if (!isRecord(value) ||
      !hasOnlyProperties(value, [
        'name',
        'type',
        'passing',
        'arrayShape',
        'optional',
        'paramArray'
      ]) ||
      !isExactNonemptyString(value.name) ||
      (value.passing !== 'byVal' && value.passing !== 'byRef') ||
      (value.arrayShape !== 'scalar' && value.arrayShape !== 'array') ||
      typeof value.optional !== 'boolean' ||
      typeof value.paramArray !== 'boolean') {
    return undefined;
  }

  const type = parseHostEventParameterType(value.type);
  return type === undefined
    ? undefined
    : {
        name: value.name,
        type,
        passing: value.passing,
        arrayShape: value.arrayShape,
        optional: value.optional,
        paramArray: value.paramArray
      };
}

function parseHostEventParameterType(value: unknown): HostEventParameterType | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  if (value.kind === 'intrinsic') {
    return hasOnlyProperties(value, ['kind', 'name']) && isNonemptyString(value.name)
      ? { kind: 'intrinsic', name: value.name }
      : undefined;
  }

  if (value.kind === 'unresolved') {
    return hasOnlyProperties(value, ['kind', 'displayName']) &&
      isExactNonemptyString(value.displayName)
      ? { kind: 'unresolved', displayName: value.displayName }
      : undefined;
  }

  if (value.kind !== 'typeLib' ||
      !hasOnlyProperties(value, [
        'kind',
        'name',
        'libraryGuid',
        'majorVersion',
        'minorVersion',
        'lcid'
      ]) ||
      !isExactNonemptyString(value.name) ||
      !isGuid(value.libraryGuid) ||
      !isNonnegativeInteger(value.majorVersion) ||
      !isNonnegativeInteger(value.minorVersion) ||
      !isNonnegativeInteger(value.lcid)) {
    return undefined;
  }

  return {
    kind: 'typeLib',
    name: value.name,
    libraryGuid: value.libraryGuid,
    majorVersion: value.majorVersion,
    minorVersion: value.minorVersion,
    lcid: value.lcid
  };
}

function parseBaseTypeProvenance(value: unknown): HostClassBaseTypeProvenance | undefined {
  if (!isRecord(value) ||
      !hasOnlyProperties(value, [
        'name',
        'libraryGuid',
        'majorVersion',
        'minorVersion',
        'lcid'
      ]) ||
      !isExactNonemptyString(value.name) ||
      !isGuid(value.libraryGuid) ||
      !isNonnegativeInteger(value.majorVersion) ||
      !isNonnegativeInteger(value.minorVersion) ||
      !isNonnegativeInteger(value.lcid)) {
    return undefined;
  }

  return {
    name: value.name,
    libraryGuid: value.libraryGuid,
    majorVersion: value.majorVersion,
    minorVersion: value.minorVersion,
    lcid: value.lcid
  };
}

function foldProjectionClasses(
  previous: readonly HostClassProjectionSnapshotEntry[],
  observed: readonly ParsedHostClassEntry[],
  classEnumerationComplete: boolean
): readonly HostClassProjectionSnapshotEntry[] {
  const previousByIdentity = new Map(
    previous.map((entry) => [identityKey(entry.identity), entry])
  );
  const observedIdentities = new Set<string>();
  const folded = observed.map((entry): HostClassProjectionSnapshotEntry => {
    const key = identityKey(entry.identity);
    observedIdentities.add(key);
    if (entry.status === 'resolved') {
      return {
        identity: entry.identity,
        authority: 'current',
        projection: entry.projection
      };
    }

    const prior = previousByIdentity.get(key);
    return prior !== undefined && prior.authority !== 'indeterminate'
      ? {
          identity: entry.identity,
          authority: 'lastKnownGood',
          projection: prior.projection
        }
      : {
          identity: entry.identity,
          authority: 'indeterminate'
        };
  });

  if (!classEnumerationComplete) {
    for (const previousEntry of previous) {
      if (!observedIdentities.has(identityKey(previousEntry.identity))) {
        folded.push(previousEntry.authority === 'indeterminate'
          ? previousEntry
          : {
              identity: previousEntry.identity,
              authority: 'lastKnownGood',
              projection: previousEntry.projection
            });
      }
    }
  }

  return folded;
}

function contextsEqual(
  left: HostClassProjectionContext,
  right: HostClassProjectionContext
): boolean {
  return left.project === right.project &&
    left.document === right.document &&
    left.sourceTemplate === right.sourceTemplate;
}

function desiredSnapshot(
  state: DocumentLifecycleState
): HostClassProjectionSnapshot | undefined {
  if (state.revision === 0) {
    return undefined;
  }

  if (!state.hasProjection) {
    return {
      schemaVersion: 2,
      revision: state.revision,
      project: state.context.project,
      document: state.context.document,
      sourceTemplate: state.context.sourceTemplate,
      state: 'cleared'
    };
  }

  return {
    schemaVersion: 2,
    revision: state.revision,
    project: state.context.project,
    document: state.context.document,
    sourceTemplate: state.context.sourceTemplate,
    state: 'present',
    ...(state.vbaProjectName === undefined
      ? {}
      : {
          vbaProjectName: state.vbaProjectName,
          sourceTemplateFingerprint: state.sourceTemplateFingerprint
        }),
    classEnumerationComplete: state.classEnumerationComplete,
    classes: state.classes
  };
}

function documentKey(context: HostClassProjectionContext): string {
  return `${context.project.toLowerCase()}\u0000${context.document.toLowerCase()}`;
}

function identityKey(identity: HostClassIdentity): string {
  return `${identity.kind}\u0000${identity.name.toLowerCase()}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isExactNonemptyString(value: unknown): value is string {
  if (typeof value !== 'string' || value.length === 0) {
    return false;
  }

  for (const character of value) {
    if (character !== ' '
      && character !== '\t'
      && character !== '\r'
      && character !== '\n') {
      return true;
    }
  }

  return false;
}

function isSha256Fingerprint(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{64}$/iu.test(value);
}

function hasOnlyProperties(
  value: Record<string, unknown>,
  allowedProperties: readonly string[]
): boolean {
  const allowed = new Set(allowedProperties);
  return Object.keys(value).every((property) => allowed.has(property));
}

function isMessageArray(
  value: unknown
): value is readonly HostClassProjectionMessage[] {
  return Array.isArray(value) && value.every((message) => (
    isRecord(message) &&
    hasOnlyProperties(message, ['code', 'message']) &&
    isNonemptyString(message.code) &&
    isNonemptyString(message.message)
  ));
}

function isHostClassUnverifiedReasonCode(
  value: unknown
): value is HostClassUnverifiedReasonCode {
  return value === 'eventEnumerationFailure' ||
    value === 'intrinsicEventSourceNameReadFailure' ||
    value === 'signatureReadFailure' ||
    value === 'availabilityReadFailure' ||
    value === 'inspectionTimeout' ||
    value === 'inspectionAborted' ||
    value === 'cancelled' ||
    value === 'inspectionFailure';
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value);
}

function isNonnegativeInteger(value: unknown): value is number {
  return Number.isInteger(value) && Number(value) >= 0 && Number(value) <= 2147483647;
}

class HostClassCancellationSource {
  private cancellationRequested = false;
  private readonly listeners = new Set<() => void>();

  public readonly token: HostClassCancellationToken;

  public constructor() {
    const source = this;
    this.token = {
      get isCancellationRequested(): boolean {
        return source.cancellationRequested;
      },
      onCancellationRequested: (listener) => source.subscribe(listener)
    };
  }

  public cancel(): void {
    if (this.cancellationRequested) {
      return;
    }

    this.cancellationRequested = true;
    for (const listener of [...this.listeners]) {
      listener();
    }
    this.listeners.clear();
  }

  private subscribe(listener: () => void): HostClassCancellationDisposable {
    if (this.cancellationRequested) {
      listener();
      return { dispose: () => undefined };
    }

    this.listeners.add(listener);
    return {
      dispose: () => this.listeners.delete(listener)
    };
  }
}

function scheduleHostClassDelay(
  delayMilliseconds: number,
  callback: () => void
): HostClassCancellationDisposable {
  const timer = setTimeout(callback, delayMilliseconds);
  timer.unref();
  return {
    dispose: () => clearTimeout(timer)
  };
}
