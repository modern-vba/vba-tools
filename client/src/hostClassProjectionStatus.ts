import {
  HostClassProjectionContext,
  HostClassProjectionLifecycleTransition
} from './hostClassProjectionLifecycle';

export interface HostClassProjectionStatusView {
  readonly visible: boolean;
  readonly text: string;
  readonly tooltip: string;
  readonly command: 'vbaTools.hostClasses.showOutput';
}

export interface HostClassProjectionStatusObserverOptions {
  readonly updateStatus: (view: HostClassProjectionStatusView) => void;
  readonly appendOutput: (line: string) => void;
}

interface DocumentStatus {
  context: HostClassProjectionContext;
  generation: number;
  revision: number;
  lifecycleState: 'current' | 'queued' | 'running' | 'attention';
  lastKnownGoodCount: number;
  indeterminateCount: number;
  associationFailureCount: number;
  associationFailureReasonCounts: Readonly<Record<string, number>>;
  classEnumerationComplete: boolean;
  diagnostics: readonly { readonly code: string; readonly message: string }[];
  warnings: readonly { readonly code: string; readonly message: string }[];
  hasCommittedSnapshot: boolean;
  hasOperationalAttention: boolean;
  reasonCode: string;
  message: string;
}

export class HostClassProjectionStatusObserver {
  private readonly documents = new Map<string, DocumentStatus>();

  public constructor(
    private readonly options: HostClassProjectionStatusObserverOptions
  ) {
  }

  public observe(transition: HostClassProjectionLifecycleTransition): void {
    this.options.appendOutput(
      `[host-events] ${JSON.stringify(formatOutputTransition(transition))}`
    );

    const key = documentKey(transition.context);
    const previous = this.documents.get(key);
    if (previous !== undefined && transition.generation < previous.generation) {
      return;
    }

    const status = previous ?? {
      context: transition.context,
      generation: transition.generation,
      revision: transition.revision,
      lifecycleState: 'current',
      lastKnownGoodCount: 0,
      indeterminateCount: 0,
      associationFailureCount: 0,
      associationFailureReasonCounts: {},
      classEnumerationComplete: true,
      diagnostics: [],
      warnings: [],
      hasCommittedSnapshot: false,
      hasOperationalAttention: false,
      reasonCode: '',
      message: ''
    } satisfies DocumentStatus;
    if (transition.generation > status.generation) {
      status.context = transition.context;
    }
    status.generation = transition.generation;
    status.revision = transition.revision;
    status.reasonCode = transition.reasonCode ?? status.reasonCode;
    status.message = transition.message ?? status.message;

    switch (transition.kind) {
      case 'queued':
        status.lifecycleState = 'queued';
        break;
      case 'started':
      case 'cancellationRequested':
        status.lifecycleState = 'running';
        break;
      case 'committed':
        status.hasCommittedSnapshot = true;
        status.hasOperationalAttention = false;
        status.lastKnownGoodCount = transition.lastKnownGoodCount ?? 0;
        status.indeterminateCount = transition.indeterminateCount ?? 0;
        status.associationFailureCount = transition.associationFailureCount ?? 0;
        status.associationFailureReasonCounts = countAssociationFailures(transition);
        status.classEnumerationComplete = transition.classEnumerationComplete ?? true;
        status.diagnostics = transition.diagnostics ?? [];
        status.warnings = transition.warnings ?? [];
        status.lifecycleState = hasAttention(status) ? 'attention' : 'current';
        break;
      case 'sourceAssociationChanged':
        status.associationFailureCount = transition.associationFailureCount ?? 0;
        status.associationFailureReasonCounts = countAssociationFailures(transition);
        if (status.lifecycleState !== 'queued' && status.lifecycleState !== 'running') {
          status.lifecycleState = hasAttention(status) ? 'attention' : 'current';
        }
        break;
      case 'discarded':
        if (isAttentionFailure(transition.reasonCode)) {
          status.hasOperationalAttention = true;
        }
        if (transition.reasonCode !== 'replayNotificationFailure' ||
          (status.lifecycleState !== 'queued' && status.lifecycleState !== 'running')) {
          status.lifecycleState = isAttentionFailure(transition.reasonCode)
            ? 'attention'
            : hasAttention(status) ? 'attention' : 'current';
        }
        break;
      case 'cancelled':
        status.lifecycleState = hasAttention(status) ? 'attention' : 'current';
        break;
      case 'replayed':
        status.hasOperationalAttention = false;
        if (status.lifecycleState !== 'queued' && status.lifecycleState !== 'running') {
          status.lifecycleState = hasAttention(status) ? 'attention' : 'current';
        }
        break;
      case 'cleared':
        status.hasCommittedSnapshot = false;
        status.hasOperationalAttention = false;
        status.lifecycleState = 'current';
        status.lastKnownGoodCount = 0;
        status.indeterminateCount = 0;
        status.associationFailureCount = 0;
        status.associationFailureReasonCounts = {};
        status.classEnumerationComplete = true;
        status.diagnostics = [];
        status.warnings = [];
        break;
    }

    this.documents.set(key, status);
    this.options.updateStatus(this.createView());
  }

  private createView(): HostClassProjectionStatusView {
    const statuses = [...this.documents.values()];
    const selected = statuses.find((status) => status.lifecycleState === 'running')
      ?? statuses.find((status) => status.lifecycleState === 'queued')
      ?? statuses.find((status) => status.lifecycleState === 'attention');
    if (selected === undefined) {
      return {
        visible: false,
        text: '',
        tooltip: '',
        command: 'vbaTools.hostClasses.showOutput'
      };
    }

    const label = `VBA Host Events: ${selected.context.document}`;
    const text = selected.lifecycleState === 'running'
      ? `$(sync~spin) ${label}`
      : selected.lifecycleState === 'queued'
        ? `$(clock) ${label}`
        : `$(warning) ${label}${selected.associationFailureCount === 0
          ? ''
          : ` (${selected.associationFailureCount})`}`;
    return {
      visible: true,
      text,
      tooltip: [
        `Project: ${selected.context.project}`,
        `Document: ${selected.context.document}`,
        `State: ${selected.lifecycleState}`,
        `Last-known-good: ${selected.lastKnownGoodCount}`,
        `Class enumeration: ${selected.classEnumerationComplete ? 'complete' : 'incomplete'}`,
        `Reason: ${selected.reasonCode || '<none>'}`,
        `Message: ${selected.message || '<none>'}`,
        ...selected.diagnostics.map((diagnostic) =>
          `Diagnostic ${diagnostic.code}: ${diagnostic.message}`
        ),
        ...selected.warnings.map((warning) =>
          `Warning ${warning.code}: ${warning.message}`
        ),
        `Source association failures: ${selected.associationFailureCount}`,
        ...Object.entries(selected.associationFailureReasonCounts)
          .sort(([left], [right]) => left.localeCompare(right))
          .map(([reason, count]) => `  ${reason}: ${count}`)
      ].join('\n'),
      command: 'vbaTools.hostClasses.showOutput'
    };
  }
}

function hasAttention(status: DocumentStatus): boolean {
  return status.hasOperationalAttention ||
    !status.hasCommittedSnapshot ||
    !status.classEnumerationComplete ||
    status.diagnostics.length > 0 ||
    status.warnings.length > 0 ||
    status.lastKnownGoodCount > 0 ||
    status.indeterminateCount > 0 ||
    status.associationFailureCount > 0;
}

function isAttentionFailure(reasonCode: string | undefined): boolean {
  return reasonCode !== 'supersededGeneration' &&
    reasonCode !== 'shutdown' &&
    reasonCode !== 'cancelledExecutionFailure' &&
    reasonCode !== 'cancelledCommandFailure' &&
    reasonCode !== 'cancelledInvalidResult';
}

function documentKey(context: HostClassProjectionContext): string {
  return `${context.project.toLowerCase()}\u0000${context.document.toLowerCase()}`;
}

function countAssociationFailures(
  transition: HostClassProjectionLifecycleTransition
): Readonly<Record<string, number>> {
  const counts: Record<string, number> = {};
  for (const failure of transition.associationResult?.failures ?? []) {
    counts[failure.reason] = (counts[failure.reason] ?? 0) + 1;
  }
  return counts;
}

function formatOutputTransition(
  transition: HostClassProjectionLifecycleTransition
): unknown {
  const associationResult = transition.associationResult;
  return {
    kind: transition.kind,
    generation: transition.generation,
    revision: transition.revision,
    context: transition.context,
    trigger: transition.trigger ?? '<none>',
    reasonCode: transition.reasonCode ?? '<none>',
    message: transition.message ?? '<none>',
    classEnumerationComplete: transition.classEnumerationComplete ?? '<none>',
    resolvedCount: transition.resolvedCount ?? 0,
    unverifiedCount: transition.unverifiedCount ?? 0,
    lastKnownGoodCount: transition.lastKnownGoodCount ?? 0,
    indeterminateCount: transition.indeterminateCount ?? 0,
    authoritativeDeletionCount: transition.authoritativeDeletionCount ?? 0,
    associationFailureCount: transition.associationFailureCount ?? 0,
    resolvedIdentities: transition.resolvedIdentities ?? [],
    unverifiedClasses: transition.unverifiedClasses ?? [],
    lastKnownGoodIdentities: transition.lastKnownGoodIdentities ?? [],
    indeterminateIdentities: transition.indeterminateIdentities ?? [],
    authoritativeDeletions: transition.authoritativeDeletions ?? [],
    diagnostics: transition.diagnostics ?? [],
    warnings: transition.warnings ?? [],
    associationResult: {
      associations: associationResult?.associations ?? [],
      failures: (associationResult?.failures ?? []).map((failure) => ({
        ...failure,
        attributeVbName: failure.attributeVbName ?? '<missing>',
        candidateProjectionIdentity:
          failure.candidateProjectionIdentity ?? '<none>'
      }))
    }
  };
}
