import test from 'node:test';
import assert from 'node:assert/strict';

import {
  HostClassProjectionStatusObserver,
  HostClassProjectionStatusView
} from './hostClassProjectionStatus';
import { HostClassProjectionLifecycleTransition } from './hostClassProjectionLifecycle';

test('HostClass status shows queued and running work then hides a clean commit', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({ kind: 'queued', generation: 1 }));
  observer.observe(transition({ kind: 'started', generation: 1 }));
  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    resolvedCount: 1,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    authoritativeDeletionCount: 0,
    associationFailureCount: 0
  }));

  assert.deepEqual(views.map((view) => ({
    visible: view.visible,
    text: view.text
  })), [
    { visible: true, text: '$(clock) VBA Host Events: Book1' },
    { visible: true, text: '$(sync~spin) VBA Host Events: Book1' },
    { visible: false, text: '' }
  ]);
});

test('HostClass association attention keeps source detail in Output and only the count in status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const output: string[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: (line) => output.push(line)
  });
  const sourceUri = 'file:///C:/work/Invoices/src/InvoiceForm.frm';

  observer.observe(transition({
    kind: 'sourceAssociationChanged',
    generation: 1,
    associationFailureCount: 1,
    reasonCode: 'sourceAssociationFailure',
    message: '1 host-class source association failure(s) remain.',
    associationResult: {
      associations: [],
      failures: [{
        sourceUri,
        sourceKind: 'form',
        attributeVbName: undefined,
        candidateProjectionIdentity: undefined,
        reason: 'attributeVbNameMissing',
        message: 'The source has no explicit Attribute VB_Name value.',
        guidance: 'Re-export the source or repair its explicit Attribute VB_Name metadata.'
      }]
    }
  }));

  const view = views.at(-1);
  assert.equal(view?.text, '$(warning) VBA Host Events: Book1 (1)');
  assert.match(view?.tooltip ?? '', /Source association failures: 1/u);
  assert.match(view?.tooltip ?? '', /attributeVbNameMissing: 1/u);
  assert.doesNotMatch(view?.tooltip ?? '', /InvoiceForm\.frm/u);
  assert.match(output[0] ?? '', /"attributeVbName":"<missing>"/u);
  assert.match(output[0] ?? '', /"candidateProjectionIdentity":"<none>"/u);
  assert.match(output[0] ?? '', /attributeVbNameMissing/u);
  assert.match(output[0] ?? '', /Re-export the source/u);
  assert.match(output[0] ?? '', /InvoiceForm\.frm/u);
});

test('HostClass status label reports the selected document association count', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'sourceAssociationChanged',
    generation: 1,
    associationFailureCount: 1
  }));
  observer.observe(transition({
    kind: 'sourceAssociationChanged',
    generation: 1,
    context: {
      project: String.raw`C:\work\Invoices`,
      document: 'Book2',
      sourceTemplate: String.raw`C:\work\Invoices\templates\Book2.xlsm`
    },
    associationFailureCount: 2
  }));

  const view = views.at(-1);
  assert.equal(view?.text, '$(warning) VBA Host Events: Book1 (1)');
  assert.match(view?.tooltip ?? '', /Source association failures: 1/u);
});

test('HostClass source association reevaluation preserves queued work status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 0
  }));
  observer.observe(transition({ kind: 'queued', generation: 2 }));
  observer.observe(transition({
    kind: 'sourceAssociationChanged',
    generation: 2,
    associationFailureCount: 1,
    reasonCode: 'sourceAssociationFailure'
  }));

  assert.equal(views.at(-1)?.text, '$(clock) VBA Host Events: Book1');
  assert.match(views.at(-1)?.tooltip ?? '', /Source association failures: 1/u);
});

test('HostClass source association reevaluation preserves running work status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 1
  }));
  observer.observe(transition({ kind: 'queued', generation: 2 }));
  observer.observe(transition({ kind: 'started', generation: 2 }));
  observer.observe(transition({
    kind: 'sourceAssociationChanged',
    generation: 2,
    associationFailureCount: 0,
    reasonCode: 'sourceAssociationsCurrent'
  }));

  assert.equal(views.at(-1)?.text, '$(sync~spin) VBA Host Events: Book1');
  assert.match(views.at(-1)?.tooltip ?? '', /Source association failures: 0/u);
});

test('HostClass incomplete enumeration remains visible even with no reported classes', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: false,
    diagnostics: [{
      code: 'classEnumerationFailure',
      message: 'The host-class collection could not be enumerated.'
    }],
    warnings: [],
    resolvedCount: 0,
    unverifiedCount: 0,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    associationFailureCount: 0
  }));

  const view = views.at(-1);
  assert.equal(view?.visible, true);
  assert.match(view?.tooltip ?? '', /Class enumeration: incomplete/u);
  assert.match(view?.tooltip ?? '', /classEnumerationFailure/u);
});

test('HostClass diagnostic-only partial result remains visible for attention', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    associationFailureCount: 0,
    diagnostics: [{
      code: 'inspectionStateUntrusted',
      message: 'The terminal inspection state could not be trusted.'
    }]
  }));

  const view = views.at(-1);
  assert.equal(view?.visible, true);
  assert.match(view?.tooltip ?? '', /inspectionStateUntrusted/u);
});

test('HostClass cancellation requires attention only when no committed snapshot exists', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({ kind: 'queued', generation: 1 }));
  observer.observe(transition({
    kind: 'cancelled',
    generation: 1,
    reasonCode: 'cancelledBeforeStart',
    message: 'Cancelled.'
  }));
  assert.equal(views.at(-1)?.visible, true);

  observer.observe(transition({ kind: 'queued', generation: 2 }));
  observer.observe(transition({
    kind: 'committed',
    generation: 2,
    revision: 1,
    classEnumerationComplete: true,
    resolvedCount: 0,
    unverifiedCount: 0,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    associationFailureCount: 0
  }));
  observer.observe(transition({ kind: 'queued', generation: 3 }));
  observer.observe(transition({
    kind: 'cancelled',
    generation: 3,
    reasonCode: 'cancelledBeforeStart',
    message: 'Cancelled.'
  }));
  assert.equal(views.at(-1)?.visible, false);
});

test('HostClass cancelled execution failure preserves clean committed status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 0
  }));
  observer.observe(transition({ kind: 'queued', generation: 2 }));
  observer.observe(transition({ kind: 'started', generation: 2 }));
  observer.observe(transition({
    kind: 'cancellationRequested',
    generation: 2,
    reasonCode: 'explicitCancellation'
  }));
  observer.observe(transition({
    kind: 'discarded',
    generation: 2,
    reasonCode: 'cancelledExecutionFailure'
  }));

  assert.equal(views.at(-1)?.visible, false);
});

test('HostClass cancellation cannot erase unresolved notification attention', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 0
  }));
  observer.observe(transition({
    kind: 'discarded',
    generation: 1,
    revision: 1,
    reasonCode: 'notificationFailure'
  }));
  observer.observe(transition({ kind: 'queued', generation: 2, revision: 1 }));
  observer.observe(transition({
    kind: 'cancellationRequested',
    generation: 2,
    revision: 1,
    reasonCode: 'explicitCancellation'
  }));
  observer.observe(transition({
    kind: 'cancelled',
    generation: 2,
    revision: 1,
    reasonCode: 'cancelledBeforeStart'
  }));

  assert.equal(views.at(-1)?.visible, true);
  assert.match(views.at(-1)?.text ?? '', /^\$\(warning\) VBA Host Events/u);
});

test('HostClass successful replay clears resolved notification transport attention', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    associationFailureCount: 0
  }));
  observer.observe(transition({
    kind: 'discarded',
    generation: 1,
    revision: 1,
    reasonCode: 'notificationFailure'
  }));
  observer.observe(transition({
    kind: 'replayed',
    generation: 1,
    revision: 1,
    reasonCode: 'desiredSnapshotReplayed'
  }));

  assert.deepEqual(
    views.slice(-2).map((view) => view.visible),
    [true, false]
  );

  observer.observe(transition({
    kind: 'committed',
    generation: 2,
    revision: 2,
    classEnumerationComplete: true,
    lastKnownGoodCount: 1,
    indeterminateCount: 0,
    associationFailureCount: 0
  }));
  observer.observe(transition({
    kind: 'discarded',
    generation: 2,
    revision: 2,
    reasonCode: 'replayNotificationFailure'
  }));
  observer.observe(transition({
    kind: 'replayed',
    generation: 2,
    revision: 2,
    reasonCode: 'desiredSnapshotReplayed'
  }));

  assert.equal(views.at(-1)?.visible, true);
  assert.match(views.at(-1)?.tooltip ?? '', /Last-known-good: 1/u);
});

test('HostClass replay completion preserves a running refresh status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 0
  }));
  observer.observe(transition({ kind: 'queued', generation: 2, revision: 1 }));
  observer.observe(transition({ kind: 'started', generation: 2, revision: 1 }));
  observer.observe(transition({
    kind: 'replayed',
    generation: 2,
    revision: 1,
    reasonCode: 'desiredSnapshotReplayed'
  }));

  assert.match(views.at(-1)?.text ?? '', /^\$\(sync~spin\) VBA Host Events/u);
});

test('HostClass replay failure preserves a running refresh status', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    associationFailureCount: 0
  }));
  observer.observe(transition({ kind: 'queued', generation: 2, revision: 1 }));
  observer.observe(transition({ kind: 'started', generation: 2, revision: 1 }));
  observer.observe(transition({
    kind: 'discarded',
    generation: 2,
    revision: 1,
    reasonCode: 'replayNotificationFailure'
  }));

  assert.match(views.at(-1)?.text ?? '', /^\$\(sync~spin\) VBA Host Events/u);
});

test('HostClass cleanup warning remains visible after an otherwise clean commit', () => {
  const views: HostClassProjectionStatusView[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: (view) => views.push(view),
    appendOutput: () => undefined
  });

  observer.observe(transition({
    kind: 'committed',
    generation: 1,
    revision: 1,
    classEnumerationComplete: true,
    diagnostics: [],
    warnings: [{
      code: 'inspectionWorkspaceRetained',
      message: 'The inspection workspace was retained for cleanup troubleshooting.'
    }],
    resolvedCount: 0,
    unverifiedCount: 0,
    lastKnownGoodCount: 0,
    indeterminateCount: 0,
    associationFailureCount: 0
  }));

  const view = views.at(-1);
  assert.equal(view?.visible, true);
  assert.match(view?.tooltip ?? '', /inspectionWorkspaceRetained/u);
});

test('HostClass Output uses stable explicit fields for every lifecycle transition', () => {
  const output: string[] = [];
  const observer = new HostClassProjectionStatusObserver({
    updateStatus: () => undefined,
    appendOutput: (line) => output.push(line)
  });

  observer.observe(transition({
    kind: 'queued',
    generation: 4,
    revision: 2,
    trigger: 'activation'
  }));

  assert.match(output[0] ?? '', /"trigger":"activation"/u);
  assert.match(output[0] ?? '', /"reasonCode":"<none>"/u);
  assert.match(output[0] ?? '', /"resolvedCount":0/u);
  assert.match(output[0] ?? '', /"unverifiedClasses":\[\]/u);
  assert.match(output[0] ?? '', /"authoritativeDeletions":\[\]/u);
  assert.match(output[0] ?? '', /"diagnostics":\[\]/u);
  assert.match(output[0] ?? '', /"warnings":\[\]/u);
});

function transition(
  values: Partial<HostClassProjectionLifecycleTransition> &
    Pick<HostClassProjectionLifecycleTransition, 'kind' | 'generation'>
): HostClassProjectionLifecycleTransition {
  return {
    context: {
      project: String.raw`C:\work\Invoices`,
      document: 'Book1',
      sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
    },
    revision: 0,
    ...values
  } as HostClassProjectionLifecycleTransition;
}
