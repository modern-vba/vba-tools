import test from 'node:test';
import assert from 'node:assert/strict';

import {
  HostClassListInvocation,
  HostClassListRunResult,
  HostClassExplicitRefreshOutcome,
  HostClassProjectionLifecycle,
  HostClassProjectionLifecycleTransition
} from './hostClassProjectionLifecycle';
import { HostClassSourceAssociationResult } from './hostClassSourceAssociation';

test('HostClass projection activation publishes revision one from the public CLI result', async () => {
  const project = String.raw`C:\work\Invoices`;
  const sourceTemplate = String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`;
  const invocations: HostClassListInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          project,
          document: 'Book1',
          sourceTemplate,
          classEnumerationComplete: true,
          complete: true,
          classes: [
            {
              identity: {
                name: 'Sheet1',
                kind: 'document'
              },
              status: 'resolved',
              intrinsicEventSourceName: 'Worksheet',
              events: []
            }
          ],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument({
    project,
    document: 'Book1',
    sourceTemplate
  });
  await lifecycle.flush();

  assert.deepEqual(invocations.map((invocation) => invocation.args), [[
    'host-class',
    'list',
    '--project',
    project,
    '--document',
    'Book1',
    '--format',
    'json'
  ]]);
  assert.deepEqual(notifications, [
    {
      method: 'vba/hostClassProjectionSnapshot',
      parameters: {
        schemaVersion: 2,
        revision: 1,
        project,
        document: 'Book1',
        sourceTemplate,
        state: 'present',
        classEnumerationComplete: true,
        classes: [
          {
            identity: {
              name: 'Sheet1',
              kind: 'document'
            },
            authority: 'current',
            projection: {
              intrinsicEventSourceName: 'Worksheet',
              events: []
            }
          }
        ]
      }
    }
  ]);
});

test('HostClass projection publishes the actual VBA project name with its template fingerprint', async () => {
  const project = String.raw`C:\work\Invoices`;
  const sourceTemplate = String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`;
  const sourceTemplateFingerprint = 'A'.repeat(64);
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        project,
        document: 'Book1',
        sourceTemplate,
        vbaProjectName: 'ActualVbaProject',
        sourceTemplateFingerprint,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument({ project, document: 'Book1', sourceTemplate });
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    method: 'vba/hostClassProjectionSnapshot',
    parameters: {
      schemaVersion: 2,
      revision: 1,
      project,
      document: 'Book1',
      sourceTemplate,
      state: 'present',
      vbaProjectName: 'ActualVbaProject',
      sourceTemplateFingerprint,
      classEnumerationComplete: true,
      classes: []
    }
  }]);
});

test('HostClass projection rejects a half project-name authority pair', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        vbaProjectName: 'ActualVbaProject',
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => undefined,
    onTransition: transition => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  assert.equal(transitions.at(-1)?.kind, 'discarded');
  assert.equal(transitions.at(-1)?.reasonCode, 'schemaMismatch');
});

test('HostClass source-template identity change clears the old snapshot before replacement', async () => {
  const project = String.raw`C:\work\Invoices`;
  const firstTemplate = String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`;
  const replacementTemplate = String.raw`C:\work\Invoices\src\Book1\Replacement.xlsm`;
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  let invocationCount = 0;
  let markReplacementStarted: (() => void) | undefined;
  const replacementStarted = new Promise<void>((resolve) => {
    markReplacementStarted = resolve;
  });
  let completeReplacement: ((result: HostClassListRunResult) => void) | undefined;
  const replacementResult = new Promise<HostClassListRunResult>((resolve) => {
    completeReplacement = resolve;
  });
  const completedResult = (sourceTemplate: string): HostClassListRunResult => ({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      project,
      document: 'Book1',
      sourceTemplate,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      if (invocationCount === 1) {
        return completedResult(firstTemplate);
      }

      markReplacementStarted?.();
      return replacementResult;
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument({
    project,
    document: 'Book1',
    sourceTemplate: firstTemplate
  });
  await lifecycle.flush();

  lifecycle.activateDocument({
    project,
    document: 'Book1',
    sourceTemplate: replacementTemplate
  });
  await replacementStarted;

  assert.deepEqual(notifications[1], {
    method: 'vba/hostClassProjectionSnapshot',
    parameters: {
      schemaVersion: 2,
      revision: 2,
      project,
      document: 'Book1',
      sourceTemplate: replacementTemplate,
      state: 'cleared'
    }
  });

  completeReplacement?.(completedResult(replacementTemplate));
  await lifecycle.flush();
  assert.equal(notifications.length, 3);
});

test('HostClass exact context identity change clears before replacement', async () => {
  const original = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const replacement = {
    ...original,
    document: 'book1'
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...invocation.context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(original);
  await lifecycle.flush();
  lifecycle.activateDocument(replacement);
  await lifecycle.flush();

  assert.deepEqual(
    notifications.map((value) => (value as { state?: string }).state),
    ['present', 'cleared', 'present']
  );
});

test('HostClass same-template refresh retains an unverified class as last known good', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      const classes = invocationCount === 1
        ? [
            {
              identity: { name: 'Sheet1', kind: 'document' },
              status: 'resolved',
              intrinsicEventSourceName: 'Worksheet',
              events: []
            }
          ]
        : [
            {
              identity: { name: 'Sheet1', kind: 'document' },
              status: 'unverified',
              reasonCode: 'signatureReadFailure',
              message: 'The complete Event signature could not be read.'
            }
          ];
      return {
        exitCode: invocationCount === 1 ? 0 : 1,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: invocationCount === 1,
          classes,
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const refresh = lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.deepEqual(await refresh.completion, {
    status: 'failed',
    reason: 'commandFailed',
    exitCode: 1
  });

  assert.deepEqual(notifications[1], {
    method: 'vba/hostClassProjectionSnapshot',
    parameters: {
      schemaVersion: 2,
      revision: 2,
      ...context,
      state: 'present',
      classEnumerationComplete: true,
      classes: [
        {
          identity: { name: 'Sheet1', kind: 'document' },
          authority: 'lastKnownGood',
          projection: {
            intrinsicEventSourceName: 'Worksheet',
            events: []
          }
        }
      ]
    }
  });
  const committed = transitions.filter((transition) =>
    transition.kind === 'committed'
  ).at(-1);
  assert.deepEqual(committed?.unverifiedClasses, [{
    identity: { name: 'Sheet1', kind: 'document' },
    reasonCode: 'signatureReadFailure',
    message: 'The complete Event signature could not be read.',
    authorityAfter: 'lastKnownGood'
  }]);
  assert.deepEqual(committed?.lastKnownGoodIdentities, [{
    name: 'Sheet1',
    kind: 'document'
  }]);
});

test('HostClass failed same-template change degrades prior current projection to last known good', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const timers: Array<() => void> = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      if (invocationCount > 1) {
        return {
          exitCode: 2,
          stdout: '',
          stderr: 'Template is temporarily unavailable.',
          cancelled: false
        };
      }
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [{
            identity: { name: 'Sheet1', kind: 'document' },
            status: 'resolved',
            intrinsicEventSourceName: 'Worksheet',
            events: []
          }],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    scheduleDelay: (_delay, callback) => {
      timers.push(callback);
      return { dispose: () => undefined };
    }
  });
  lifecycle.activateDocument(context);
  await lifecycle.flush();

  lifecycle.templateChanged(context);
  timers[0]?.();
  await lifecycle.flush();

  assert.deepEqual(notifications[1], {
    schemaVersion: 2,
    revision: 2,
    ...context,
    state: 'present',
    classEnumerationComplete: false,
    classes: [{
      identity: { name: 'Sheet1', kind: 'document' },
      authority: 'lastKnownGood',
      projection: {
        intrinsicEventSourceName: 'Worksheet',
        events: []
      }
    }]
  });
  assert.equal(timers.length, 1);
});

test('HostClass failed same-template change degrades authoritative empty enumeration', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const timers: Array<() => void> = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return invocationCount === 1
        ? {
            exitCode: 0,
            stdout: JSON.stringify({
              schemaVersion: '1.1',
              ...context,
              classEnumerationComplete: true,
              complete: true,
              classes: [],
              diagnostics: [],
              warnings: []
            }),
            stderr: '',
            cancelled: false
          }
        : {
            exitCode: 2,
            stdout: '',
            stderr: 'Template is temporarily unavailable.',
            cancelled: false
          };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    scheduleDelay: (_delay, callback) => {
      timers.push(callback);
      return { dispose: () => undefined };
    }
  });
  lifecycle.activateDocument(context);
  await lifecycle.flush();

  lifecycle.templateChanged(context);
  timers[0]?.();
  await lifecycle.flush();

  assert.deepEqual(notifications[1], {
    schemaVersion: 2,
    revision: 2,
    ...context,
    state: 'present',
    classEnumerationComplete: false,
    classes: []
  });
});

test('HostClass enumeration completeness controls absent identity retention and deletion', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let invocationCount = 0;
  const resolvedClass = (name: string, kind: 'form' | 'document') => ({
    identity: { name, kind },
    status: 'resolved',
    intrinsicEventSourceName: kind === 'form' ? 'UserForm' : 'Worksheet',
    events: []
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      const classEnumerationComplete = invocationCount !== 2;
      return {
        exitCode: classEnumerationComplete ? 0 : 1,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete,
          complete: classEnumerationComplete,
          classes: invocationCount === 1
            ? [
                resolvedClass('Sheet1', 'document'),
                resolvedClass('InvoiceForm', 'form')
              ]
            : [resolvedClass('InvoiceForm', 'form')],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();

  const classStates = notifications.map((value) =>
    (value as {
      classes: readonly {
        identity: { name: string };
        authority: string;
      }[];
    }).classes.map((entry) => [entry.identity.name, entry.authority]));
  assert.deepEqual(classStates, [
    [['Sheet1', 'current'], ['InvoiceForm', 'current']],
    [['InvoiceForm', 'current'], ['Sheet1', 'lastKnownGood']],
    [['InvoiceForm', 'current']]
  ]);
  assert.deepEqual(
    transitions.filter((transition) => transition.kind === 'committed')
      .map((transition) => [
        transition.lastKnownGoodCount,
        transition.authoritativeDeletionCount
      ]),
    [[0, 0], [1, 0], [0, 1]]
  );
});

test('HostClass duplicate identities reject the complete invocation and preserve prior state', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  let invocationCount = 0;
  const resolvedClass = (name: string) => ({
    identity: { name, kind: 'document' },
    status: 'resolved',
    intrinsicEventSourceName: 'Worksheet',
    events: []
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      const duplicate = invocationCount > 1;
      return {
        exitCode: duplicate ? 1 : 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: !duplicate,
          complete: !duplicate,
          classes: duplicate
            ? [resolvedClass('Sheet1'), resolvedClass('sheet1')]
            : [resolvedClass('Sheet1')],
          diagnostics: duplicate
            ? [{ code: 'classEnumerationFailure', message: 'Duplicate identity.' }]
            : [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.equal(notifications.length, 1);
});

test('HostClass replacement cooperatively cancels its running document and waits for close', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let markFirstStarted: (() => void) | undefined;
  const firstStarted = new Promise<void>((resolve) => {
    markFirstStarted = resolve;
  });
  let markSecondStarted: (() => void) | undefined;
  const secondStarted = new Promise<void>((resolve) => {
    markSecondStarted = resolve;
  });
  let completeFirst: ((result: HostClassListRunResult) => void) | undefined;
  const firstResult = new Promise<HostClassListRunResult>((resolve) => {
    completeFirst = resolve;
  });
  let completeSecond: ((result: HostClassListRunResult) => void) | undefined;
  const secondResult = new Promise<HostClassListRunResult>((resolve) => {
    completeSecond = resolve;
  });
  const completedResult = (): HostClassListRunResult => ({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      if (invocations.length === 1) {
        markFirstStarted?.();
        return firstResult;
      }

      markSecondStarted?.();
      return secondResult;
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await firstStarted;
  lifecycle.refreshDocument(context);

  assert.equal(invocations[0]?.cancellationToken.isCancellationRequested, true);
  assert.equal(invocations.length, 1);
  assert.deepEqual(
    transitions.filter((transition) =>
      transition.kind === 'cancellationRequested'
    ).map((transition) => transition.reasonCode),
    ['superseded']
  );

  completeFirst?.(completedResult());
  await secondStarted;
  assert.equal(invocations.length, 2);
  assert.equal(notifications.length, 0);

  completeSecond?.(completedResult());
  await lifecycle.flush();
  assert.equal(notifications.length, 1);
});

test('HostClass scheduler moves a replaced queued document behind other waiting documents', async () => {
  const project = String.raw`C:\work\Invoices`;
  const runningContext = {
    project,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const replacedContext = {
    project,
    document: 'Book2',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book2\Book2.xlsm`
  };
  const waitingContext = {
    project,
    document: 'Book3',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book3\Book3.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  let markRunningStarted: (() => void) | undefined;
  const runningStarted = new Promise<void>((resolve) => {
    markRunningStarted = resolve;
  });
  let markWaitingStarted: (() => void) | undefined;
  const waitingStarted = new Promise<void>((resolve) => {
    markWaitingStarted = resolve;
  });
  let markNextStarted: (() => void) | undefined;
  const nextStarted = new Promise<void>((resolve) => {
    markNextStarted = resolve;
  });
  let markReplacementStarted: (() => void) | undefined;
  const replacementStarted = new Promise<void>((resolve) => {
    markReplacementStarted = resolve;
  });
  let completeRunning: ((result: HostClassListRunResult) => void) | undefined;
  const runningResult = new Promise<HostClassListRunResult>((resolve) => {
    completeRunning = resolve;
  });
  let completeWaiting: ((result: HostClassListRunResult) => void) | undefined;
  const waitingResult = new Promise<HostClassListRunResult>((resolve) => {
    completeWaiting = resolve;
  });
  let completeReplacement: ((result: HostClassListRunResult) => void) | undefined;
  const replacementResult = new Promise<HostClassListRunResult>((resolve) => {
    completeReplacement = resolve;
  });
  const completedResult = (context: typeof runningContext): HostClassListRunResult => ({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      if (invocation.context.document === 'Book1') {
        markRunningStarted?.();
        return runningResult;
      }

      if (invocation.context.document === 'Book3') {
        markWaitingStarted?.();
        markNextStarted?.();
        return waitingResult;
      }

      markReplacementStarted?.();
      markNextStarted?.();
      return replacementResult;
    },
    sendNotification: async () => undefined
  });

  lifecycle.activateDocument(runningContext);
  await runningStarted;
  lifecycle.activateDocument(replacedContext);
  lifecycle.activateDocument(waitingContext);
  lifecycle.refreshDocument(replacedContext);

  completeRunning?.(completedResult(runningContext));
  await nextStarted;
  assert.equal(invocations[1]?.context.document, 'Book3');
  await waitingStarted;
  completeWaiting?.(completedResult(waitingContext));
  await replacementStarted;

  assert.deepEqual(
    invocations.map((invocation) => [
      invocation.context.document,
      invocation.generation
    ]),
    [
      ['Book1', 1],
      ['Book3', 1],
      ['Book2', 2]
    ]
  );

  completeReplacement?.(completedResult(replacedContext));
  await lifecycle.flush();
});

test('HostClass lifecycle shutdown cancels the running invocation and drops queued work', async () => {
  const project = String.raw`C:\work\Invoices`;
  const runningContext = {
    project,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const queuedContext = {
    project,
    document: 'Book2',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book2\Book2.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let markRunningStarted: (() => void) | undefined;
  const runningStarted = new Promise<void>((resolve) => {
    markRunningStarted = resolve;
  });
  let completeRunning: ((result: HostClassListRunResult) => void) | undefined;
  const runningResult = new Promise<HostClassListRunResult>((resolve) => {
    completeRunning = resolve;
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      markRunningStarted?.();
      return runningResult;
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(runningContext);
  await runningStarted;
  lifecycle.activateDocument(queuedContext);
  lifecycle.shutdown();

  assert.equal(invocations[0]?.cancellationToken.isCancellationRequested, true);
  completeRunning?.({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...runningContext,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  await lifecycle.flush();

  assert.equal(invocations.length, 1);
  assert.equal(notifications.length, 0);
  assert.deepEqual(
    transitions.filter((transition) =>
      transition.kind === 'cancellationRequested'
    ).map((transition) => [transition.context.document, transition.reasonCode]),
    [
      ['Book1', 'shutdown'],
      ['Book2', 'shutdown']
    ]
  );
});

test('HostClass lifecycle shutdown discards a late invocation rejection without degrading state', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  const timers: Array<() => void> = [];
  let invocationCount = 0;
  let markRefreshStarted: (() => void) | undefined;
  const refreshStarted = new Promise<void>((resolve) => {
    markRefreshStarted = resolve;
  });
  let rejectRefresh: ((error: Error) => void) | undefined;
  const refreshResult = new Promise<HostClassListRunResult>((_resolve, reject) => {
    rejectRefresh = reject;
  });
  const successfulResult: HostClassListRunResult = {
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  };
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      if (invocationCount === 1) {
        return successfulResult;
      }

      markRefreshStarted?.();
      return refreshResult;
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    scheduleDelay: (_delay, callback) => {
      timers.push(callback);
      return { dispose: () => undefined };
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });
  lifecycle.activateDocument(context);
  await lifecycle.flush();

  lifecycle.templateChanged(context);
  timers[0]?.();
  await refreshStarted;
  lifecycle.shutdown();
  rejectRefresh?.(new Error('The owned adapter exited during shutdown.'));
  await lifecycle.flush();

  assert.equal(notifications.length, 1);
  assert.equal(
    transitions.filter((transition) => transition.kind === 'discarded').at(-1)?.reasonCode,
    'shutdown'
  );
});

test('HostClass automatic template changes use a one-second trailing-edge debounce', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  const timers: Array<{
    delayMilliseconds: number;
    cancelled: boolean;
    callback: () => void;
  }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined,
    scheduleDelay: (delayMilliseconds, callback) => {
      const timer = { delayMilliseconds, cancelled: false, callback };
      timers.push(timer);
      return {
        dispose: () => {
          timer.cancelled = true;
        }
      };
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.templateChanged(context);
  lifecycle.templateChanged(context);

  assert.equal(invocations.length, 1);
  assert.deepEqual(
    timers.map((timer) => [timer.delayMilliseconds, timer.cancelled]),
    [
      [1000, true],
      [1000, false]
    ]
  );
  assert.deepEqual(
    transitions.filter((transition) =>
      transition.kind === 'cancellationRequested'
    ).map((transition) => [transition.generation, transition.reasonCode]),
    [[2, 'superseded']]
  );

  timers[1]?.callback();
  await lifecycle.flush();
  assert.deepEqual(
    invocations.map((invocation) => invocation.generation),
    [1, 3]
  );
});

test('HostClass automatic manifest changes use a one-second trailing-edge debounce', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const timers: Array<{
    delayMilliseconds: number;
    callback: () => void;
  }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined,
    scheduleDelay: (delayMilliseconds, callback) => {
      timers.push({ delayMilliseconds, callback });
      return { dispose: () => undefined };
    }
  });

  lifecycle.manifestChanged(context);

  assert.equal(invocations.length, 0);
  assert.equal(timers[0]?.delayMilliseconds, 1000);
  timers[0]?.callback();
  await lifecycle.flush();
  assert.equal(invocations[0]?.trigger, 'manifestChanged');
});

test('HostClass delayed replacement immediately supersedes an older queued generation', async () => {
  const project = String.raw`C:\work\Invoices`;
  const runningContext = {
    project,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const replacedContext = {
    project,
    document: 'Book2',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book2.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const timers: Array<() => void> = [];
  let completeRunning: ((result: HostClassListRunResult) => void) | undefined;
  const runningResult = new Promise<HostClassListRunResult>((resolve) => {
    completeRunning = resolve;
  });
  let completeReplacement: ((result: HostClassListRunResult) => void) | undefined;
  const replacementResult = new Promise<HostClassListRunResult>((resolve) => {
    completeReplacement = resolve;
  });
  let replacementStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    replacementStarted = resolve;
  });
  const completedResult = (context: typeof runningContext): HostClassListRunResult => ({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      if (invocation.context.document === runningContext.document) {
        return runningResult;
      }

      replacementStarted?.();
      return replacementResult;
    },
    sendNotification: async () => undefined,
    scheduleDelay: (_delayMilliseconds, callback) => {
      timers.push(callback);
      return { dispose: () => undefined };
    }
  });

  lifecycle.activateDocument(runningContext);
  const queued = lifecycle.refreshDocument(replacedContext);
  let queuedOutcome: HostClassExplicitRefreshOutcome | undefined;
  void queued.completion.then((outcome) => {
    queuedOutcome = outcome;
  });
  lifecycle.templateChanged(replacedContext);
  await Promise.resolve();

  assert.deepEqual(queuedOutcome, { status: 'superseded' });
  assert.deepEqual(invocations.map((invocation) => invocation.context.document), ['Book1']);

  completeRunning?.(completedResult(runningContext));
  await lifecycle.flush();
  timers[0]?.();
  await started;
  assert.deepEqual(invocations.map((invocation) => [
    invocation.context.document,
    invocation.generation
  ]), [
    ['Book1', 1],
    ['Book2', 2]
  ]);
  completeReplacement?.(completedResult(replacedContext));
  await lifecycle.flush();
});

test('HostClass source reassociation clears repaired metadata without inspection or generation advance', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [
            {
              identity: { name: 'InvoiceForm', kind: 'form' },
              status: 'resolved',
              intrinsicEventSourceName: 'UserForm',
              events: []
            }
          ],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const failed = lifecycle.reevaluateSourceAssociations(context, [
    {
      sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
      kind: 'form',
      moduleIdentity: { state: 'missing' }
    }
  ]);
  const repaired = lifecycle.reevaluateSourceAssociations(context, [
    {
      sourceUri: 'file:///C:/work/Invoices/src/InvoiceForm.frm',
      kind: 'form',
      moduleIdentity: { state: 'authoritative', name: 'InvoiceForm' }
    }
  ]);

  assert.equal(failed?.failures.length, 1);
  assert.equal(repaired?.failures.length, 0);
  assert.equal(invocations.length, 1);
  assert.equal(notifications.length, 1);

  lifecycle.refreshDocument(context);
  await lifecycle.flush();
  assert.deepEqual(
    invocations.map((invocation) => invocation.generation),
    [1, 2]
  );
});

test('HostClass source candidates observed during initial inspection associate when projection commits', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const associationResults: HostClassSourceAssociationResult[] = [];
  let markStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    markStarted = resolve;
  });
  let complete: ((result: HostClassListRunResult) => void) | undefined;
  const result = new Promise<HostClassListRunResult>((resolve) => {
    complete = resolve;
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      markStarted?.();
      return result;
    },
    sendNotification: async () => undefined,
    onSourceAssociationChanged: (_context, associationResult) => {
      associationResults.push(associationResult);
    }
  });

  lifecycle.activateDocument(context);
  await started;
  const moduleIdentity = {
    state: 'authoritative' as const,
    name: 'InvoiceForm'
  };
  const beforeProjection = lifecycle.reevaluateSourceAssociations(context, [
    {
      sourceUri: 'file:///C:/work/Invoices/src/Book1/InvoiceForm.frm',
      kind: 'form',
      moduleIdentity
    }
  ]);
  moduleIdentity.name = 'MutatedAfterObservation';
  complete?.({
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [
        {
          identity: { name: 'InvoiceForm', kind: 'form' },
          status: 'resolved',
          intrinsicEventSourceName: 'UserForm',
          events: []
        }
      ],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  });
  await lifecycle.flush();

  assert.equal(beforeProjection, undefined);
  assert.deepEqual(invocations.map((invocation) => invocation.generation), [1]);
  assert.equal(associationResults.length, 1);
  assert.equal(associationResults[0]?.failures.length, 0);
  assert.equal(
    associationResults[0]?.associations[0]?.projectionIdentity.name,
    'InvoiceForm'
  );
});

test('HostClass desired snapshot commits before notification transport acknowledgement', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  let markFirstNotificationStarted: (() => void) | undefined;
  const firstNotificationStarted = new Promise<void>((resolve) => {
    markFirstNotificationStarted = resolve;
  });
  let releaseFirstNotification: (() => void) | undefined;
  const firstNotificationRelease = new Promise<void>((resolve) => {
    releaseFirstNotification = resolve;
  });
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: invocationCount === 1 ? 0 : 1,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: invocationCount === 1,
          classes: invocationCount === 1
            ? [
                {
                  identity: { name: 'Sheet1', kind: 'document' },
                  status: 'resolved',
                  intrinsicEventSourceName: 'Worksheet',
                  events: []
                }
              ]
            : [
                {
                  identity: { name: 'Sheet1', kind: 'document' },
                  status: 'unverified',
                  reasonCode: 'signatureReadFailure',
                  message: 'Signature unavailable.'
                }
              ],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
      if (notifications.length === 1) {
        markFirstNotificationStarted?.();
        await firstNotificationRelease;
      }
    }
  });

  lifecycle.activateDocument(context);
  await firstNotificationStarted;
  lifecycle.refreshDocument(context);
  releaseFirstNotification?.();
  await lifecycle.flush();

  const second = notifications[1]?.parameters as {
    classes?: Array<{ authority?: string }>;
  };
  assert.equal(second.classes?.[0]?.authority, 'lastKnownGood');
});

test('HostClass clear notification failure does not discard replacement inspection', async () => {
  const project = String.raw`C:\work\Invoices`;
  const original = {
    project,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const replacement = {
    ...original,
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Replacement.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const delivered: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...invocation.context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      const snapshot = parameters as { state?: string; sourceTemplate?: string };
      if (snapshot.state === 'cleared') {
        throw new Error('Simulated clear transport failure.');
      }
      delivered.push(parameters);
    }
  });

  lifecycle.activateDocument(original);
  await lifecycle.flush();
  lifecycle.activateDocument(replacement);
  await lifecycle.flush();

  assert.deepEqual(
    invocations.map((invocation) => invocation.context.sourceTemplate),
    [original.sourceTemplate, replacement.sourceTemplate]
  );
  assert.equal(delivered.length, 2);
});

test('HostClass restart replay republishes desired snapshots without another inspection', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  await lifecycle.replayDesiredSnapshots();

  assert.equal(invocations.length, 1);
  assert.deepEqual(notifications, [notifications[0], notifications[0]]);
  assert.deepEqual(
    transitions.map((transition) => transition.kind).slice(-1),
    ['replayed']
  );
});

test('HostClass completed replay cannot overwrite a newer running refresh generation', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  let resolveReplayTransport: (() => void) | undefined;
  let observeReplayTransport: (() => void) | undefined;
  const replayTransportStarted = new Promise<void>((resolve) => {
    observeReplayTransport = resolve;
  });
  let resolveRefresh: ((result: HostClassListRunResult) => void) | undefined;
  let observeRefresh: (() => void) | undefined;
  const refreshStarted = new Promise<void>((resolve) => {
    observeRefresh = resolve;
  });
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let invocationCount = 0;
  let notificationCount = 0;
  const successfulResult: HostClassListRunResult = {
    exitCode: 0,
    stdout: JSON.stringify({
      schemaVersion: '1.1',
      ...context,
      classEnumerationComplete: true,
      complete: true,
      classes: [],
      diagnostics: [],
      warnings: []
    }),
    stderr: '',
    cancelled: false
  };
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      if (invocationCount === 1) {
        return successfulResult;
      }
      observeRefresh?.();
      return new Promise<HostClassListRunResult>((resolve) => {
        resolveRefresh = resolve;
      });
    },
    sendNotification: async () => {
      notificationCount += 1;
      if (notificationCount === 2) {
        observeReplayTransport?.();
        await new Promise<void>((resolve) => {
          resolveReplayTransport = resolve;
        });
      }
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const replay = lifecycle.replayDesiredSnapshots();
  await replayTransportStarted;
  const refresh = lifecycle.refreshDocument(context);
  await refreshStarted;

  resolveReplayTransport?.();
  await replay;
  const terminalKindAfterReplay = transitions.at(-1)?.kind;

  resolveRefresh?.(successfulResult);
  await refresh.completion;
  await lifecycle.flush();

  assert.equal(terminalKindAfterReplay, 'started');
});

test('HostClass failed replay cannot restore attention after its document is removed', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  let releaseReplayTransport: (() => void) | undefined;
  let observeReplayTransport: (() => void) | undefined;
  const replayTransportStarted = new Promise<void>((resolve) => {
    observeReplayTransport = resolve;
  });
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let notificationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => {
      notificationCount += 1;
      if (notificationCount === 2) {
        observeReplayTransport?.();
        await new Promise<void>((resolve) => {
          releaseReplayTransport = resolve;
        });
        throw new Error('Simulated stale replay transport failure.');
      }
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const replay = lifecycle.replayDesiredSnapshots();
  await replayTransportStarted;
  lifecycle.removeDocument(context);

  releaseReplayTransport?.();
  await replay;

  assert.equal(transitions.at(-1)?.kind, 'cleared');
});

test('HostClass replay continues with every active document after one notification fails', async () => {
  const contexts = ['Book1', 'Book2'].map((document) => ({
    project: String.raw`C:\work\Invoices`,
    document,
    sourceTemplate: String.raw`C:\work\Invoices\templates\${document}.xlsm`
  }));
  const replayAttempts: string[] = [];
  let replaying = false;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...invocation.context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      if (!replaying) {
        return;
      }
      const document = (parameters as { document: string }).document;
      replayAttempts.push(document);
      if (document === 'Book1') {
        throw new Error('First replay transport failure');
      }
    }
  });
  for (const context of contexts) {
    lifecycle.activateDocument(context);
  }
  await lifecycle.flush();
  replaying = true;

  await assert.doesNotReject(() => lifecycle.replayDesiredSnapshots());

  assert.deepEqual(replayAttempts, ['Book1', 'Book2']);
});

test('HostClass explicit refresh handle resolves its committed generation', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => undefined
  });

  const refresh = lifecycle.refreshDocument(context);
  const outcome = await refresh.completion;

  assert.deepEqual(outcome, {
    status: 'succeeded',
    revision: 1,
    associationFailureCount: 0
  });
});

test('HostClass explicit refresh cancellation drops only its own queued generation', async () => {
  const first = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const second = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book2',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book2\Book2.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...invocation.context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });

  const refresh = lifecycle.refreshDocument(first);
  lifecycle.activateDocument(second);
  refresh.cancel();
  const outcome = await refresh.completion;
  await lifecycle.flush();

  assert.deepEqual(outcome, { status: 'cancelled' });
  assert.deepEqual(
    invocations.map((invocation) => invocation.context.document),
    ['Book2']
  );
});

test('HostClass queued explicit cancellation settles without waiting for another document', async () => {
  const runningContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Running',
    sourceTemplate: String.raw`C:\work\Invoices\src\Running\Running.xlsm`
  };
  const queuedContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Queued',
    sourceTemplate: String.raw`C:\work\Invoices\src\Queued\Queued.xlsm`
  };
  let releaseRunning: (() => void) | undefined;
  let runningInvocation: HostClassListInvocation | undefined;
  const runningReleased = new Promise<void>((resolve) => {
    releaseRunning = resolve;
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      runningInvocation = invocation;
      await runningReleased;
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...invocation.context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });
  lifecycle.activateDocument(runningContext);
  await Promise.resolve();
  const queued = lifecycle.refreshDocument(queuedContext);
  let outcome: HostClassExplicitRefreshOutcome | undefined;
  void queued.completion.then((value) => {
    outcome = value;
  });

  queued.cancel();
  await Promise.resolve();

  assert.deepEqual(outcome, { status: 'cancelled' });
  assert.equal(runningInvocation?.cancellationToken.isCancellationRequested, false);
  releaseRunning?.();
  await lifecycle.flush();
});

test('HostClass current cancelled refresh commits schema-valid terminal partial output', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  let markStarted: (() => void) | undefined;
  const started = new Promise<void>((resolve) => {
    markStarted = resolve;
  });
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      markStarted?.();
      if (!invocation.cancellationToken.isCancellationRequested) {
        await new Promise<void>((resolve) => {
          invocation.cancellationToken.onCancellationRequested(resolve);
        });
      }
      return {
        exitCode: 130,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: false,
          complete: false,
          classes: [
            {
              identity: { name: 'Sheet1', kind: 'document' },
              status: 'resolved',
              intrinsicEventSourceName: 'Worksheet',
              events: []
            }
          ],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: true
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  const refresh = lifecycle.refreshDocument(context);
  await started;
  refresh.cancel();
  const outcome = await refresh.completion;
  await lifecycle.flush();

  assert.deepEqual(outcome, { status: 'cancelled' });
  assert.equal((notifications[0] as { state?: string } | undefined)?.state, 'present');
});

test('HostClass explicit cancellation discards exit-zero output marked incomplete', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      if (!invocation.cancellationToken.isCancellationRequested) {
        await new Promise<void>((resolve) => {
          invocation.cancellationToken.onCancellationRequested(resolve);
        });
      }
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: false,
          complete: false,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  const refresh = lifecycle.refreshDocument(context);
  await Promise.resolve();
  refresh.cancel();
  const outcome = await refresh.completion;

  assert.deepEqual(outcome, { status: 'cancelled' });
  assert.equal(notifications.length, 0);
});

test('HostClass explicit cancellation remains cancelled when the adapter exits unexpectedly', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      if (!invocation.cancellationToken.isCancellationRequested) {
        await new Promise<void>((resolve) => {
          invocation.cancellationToken.onCancellationRequested(resolve);
        });
      }
      return {
        exitCode: 2,
        stdout: '',
        stderr: 'Process cleanup interrupted the command.',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });

  const refresh = lifecycle.refreshDocument(context);
  await Promise.resolve();
  refresh.cancel();

  assert.deepEqual(await refresh.completion, { status: 'cancelled' });
});

test('HostClass explicit cancellation remains cancelled when the adapter rejects', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      if (!invocation.cancellationToken.isCancellationRequested) {
        await new Promise<void>((resolve) => {
          invocation.cancellationToken.onCancellationRequested(resolve);
        });
      }
      throw new Error('The cancelled process transport closed.');
    },
    sendNotification: async () => undefined
  });

  const refresh = lifecycle.refreshDocument(context);
  await Promise.resolve();
  refresh.cancel();

  assert.deepEqual(await refresh.completion, { status: 'cancelled' });
});

test('HostClass lifecycle reports structured queue start and commit transitions', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const transitions: Array<{
    kind: string;
    generation: number;
    revision: number;
  }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => undefined,
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  assert.deepEqual(
    transitions.map(({ kind, generation, revision }) => ({
      kind,
      generation,
      revision
    })),
    [
      { kind: 'queued', generation: 1, revision: 0 },
      { kind: 'started', generation: 1, revision: 0 },
      { kind: 'committed', generation: 1, revision: 1 }
    ]
  );
});

test('HostClass lifecycle rejects the superseded CLI output schema', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const transitions: Array<{ kind: string; reasonCode?: string }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.0',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => undefined,
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const last = transitions.at(-1);
  assert.deepEqual(last === undefined
    ? undefined
    : { kind: last.kind, reasonCode: last.reasonCode }, {
    kind: 'discarded',
    reasonCode: 'schemaMismatch'
  });
});

test('HostClass exit zero with incomplete output preserves the prior snapshot', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: invocationCount === 1,
          complete: invocationCount === 1,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const refresh = lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.deepEqual(await refresh.completion, {
    status: 'failed',
    reason: 'invalidResult'
  });
  assert.equal(notifications.length, 1);
});

test('HostClass exit one with complete output preserves the prior snapshot', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: invocationCount === 1 ? 0 : 1,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  const refresh = lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.deepEqual(await refresh.completion, {
    status: 'failed',
    reason: 'invalidResult'
  });
  assert.equal(notifications.length, 1);
  assert.equal(transitions.at(-1)?.reasonCode, 'completenessMismatch');
});

test('HostClass exit 130 with complete output remains cancelled without committing', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 130,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: true
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  const refresh = lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.deepEqual(await refresh.completion, { status: 'cancelled' });
  assert.equal(notifications.length, 0);
  assert.equal(transitions.at(-1)?.reasonCode, 'cancelledInvalidResult');
});

test('HostClass schema-valid mismatched CLI context preserves the prior snapshot', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          sourceTemplate: invocationCount === 1
            ? context.sourceTemplate
            : String.raw`C:\work\Invoices\src\Book1\Other.xlsm`,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.equal(notifications.length, 1);
  assert.equal(transitions.at(-1)?.reasonCode, 'contextMismatch');
});

test('HostClass lifecycle reports complete source-association failure detail', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const transitions: Array<{
    kind: string;
    associationFailureCount?: number;
    associationResult?: HostClassSourceAssociationResult;
  }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async () => undefined,
    onTransition: (transition) => {
      transitions.push(transition);
    }
  });
  lifecycle.activateDocument(context);
  await lifecycle.flush();

  lifecycle.reevaluateSourceAssociations(context, [{
    sourceUri: 'file:///C:/work/Invoices/src/Book1/InvoiceForm.frm',
    kind: 'form',
    moduleIdentity: { state: 'missing' }
  }]);

  const last = transitions.at(-1);
  assert.equal(last?.kind, 'sourceAssociationChanged');
  assert.equal(last?.associationFailureCount, 1);
  assert.equal(last?.associationResult?.failures[0]?.reason, 'attributeVbNameMissing');
});

test('HostClass lifecycle preserves exact code-page class and intrinsic source identities', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [{
          identity: { name: '\u00A0', kind: 'document' },
          status: 'resolved',
          intrinsicEventSourceName: '\u00A0',
          events: [],
          baseTypeProvenance: {
            name: '\u00A0',
            libraryGuid: '00020813-0000-0000-C000-000000000046',
            majorVersion: 1,
            minorVersion: 9,
            lcid: 0
          }
        }],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const snapshot = notifications[0] as {
    classes: readonly {
      identity: { name: string };
      projection: {
        intrinsicEventSourceName: string;
        baseTypeProvenance: { name: string };
      };
    }[];
  };
  assert.equal(snapshot.classes[0]?.identity.name, '\u00A0');
  assert.equal(snapshot.classes[0]?.projection.intrinsicEventSourceName, '\u00A0');
  assert.equal(snapshot.classes[0]?.projection.baseTypeProvenance.name, '\u00A0');
});

test('HostClass lifecycle preserves an exact code-page Event name', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [{
          identity: { name: 'Sheet1', kind: 'document' },
          status: 'resolved',
          intrinsicEventSourceName: 'Worksheet',
          events: [{
            name: '\u00A0',
            parameters: [],
            authoringAvailable: true,
            existingHandlerRecognizable: true
          }]
        }],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const snapshot = notifications[0] as {
    classes: readonly { projection: { events: readonly { name: string }[] } }[];
  };
  assert.equal(snapshot.classes[0]?.projection.events[0]?.name, '\u00A0');
});

test('HostClass lifecycle preserves an exact code-page Event parameter name', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [{
          identity: { name: 'Sheet1', kind: 'document' },
          status: 'resolved',
          intrinsicEventSourceName: 'Worksheet',
          events: [{
            name: 'Changed',
            parameters: [{
              name: '\u00A0',
              type: { kind: 'intrinsic', name: 'String' },
              passing: 'byVal',
              arrayShape: 'scalar',
              optional: false,
              paramArray: false
            }],
            authoringAvailable: true,
            existingHandlerRecognizable: true
          }]
        }],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const snapshot = notifications[0] as {
    classes: readonly {
      projection: { events: readonly { parameters: readonly { name: string }[] }[] };
    }[];
  };
  assert.equal(snapshot.classes[0]?.projection.events[0]?.parameters[0]?.name, '\u00A0');
});

test('HostClass lifecycle preserves an exact code-page TypeLib parameter type name', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [{
          identity: { name: 'Sheet1', kind: 'document' },
          status: 'resolved',
          intrinsicEventSourceName: 'Worksheet',
          events: [{
            name: 'Changed',
            parameters: [{
              name: 'value',
              type: {
                kind: 'typeLib',
                name: '\u00A0',
                libraryGuid: '00020813-0000-0000-C000-000000000046',
                majorVersion: 1,
                minorVersion: 9,
                lcid: 0
              },
              passing: 'byVal',
              arrayShape: 'scalar',
              optional: false,
              paramArray: false
            }],
            authoringAvailable: true,
            existingHandlerRecognizable: true
          }]
        }],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const snapshot = notifications[0] as {
    classes: readonly {
      projection: {
        events: readonly {
          parameters: readonly { type: { kind: string; name: string } }[];
        }[];
      };
    }[];
  };
  assert.deepEqual(snapshot.classes[0]?.projection.events[0]?.parameters[0]?.type, {
    kind: 'typeLib',
    name: '\u00A0',
    libraryGuid: '00020813-0000-0000-C000-000000000046',
    majorVersion: 1,
    minorVersion: 9,
    lcid: 0
  });
});

test('HostClass lifecycle preserves an exact unresolved parameter type display name', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        schemaVersion: '1.1',
        ...context,
        classEnumerationComplete: true,
        complete: true,
        classes: [{
          identity: { name: 'Sheet1', kind: 'document' },
          status: 'resolved',
          intrinsicEventSourceName: 'Worksheet',
          events: [{
            name: 'Changed',
            parameters: [{
              name: 'value',
              type: { kind: 'unresolved', displayName: '\u00A0' },
              passing: 'byVal',
              arrayShape: 'scalar',
              optional: false,
              paramArray: false
            }],
            authoringAvailable: false,
            existingHandlerRecognizable: false
          }]
        }],
        diagnostics: [],
        warnings: []
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();

  const snapshot = notifications[0] as {
    classes: readonly {
      projection: {
        events: readonly {
          parameters: readonly { type: { kind: string; displayName: string } }[];
        }[];
      };
    }[];
  };
  assert.deepEqual(snapshot.classes[0]?.projection.events[0]?.parameters[0]?.type, {
    kind: 'unresolved',
    displayName: '\u00A0'
  });
});

test('HostClass invalid nested Event schema preserves the complete prior snapshot', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [
            {
              identity: { name: 'Sheet1', kind: 'document' },
              status: 'resolved',
              intrinsicEventSourceName: 'Worksheet',
              events: invocationCount === 1
                ? []
                : [
                    {
                      name: 'Change',
                      parameters: [],
                      authoringAvailable: true
                    }
                  ]
            }
          ],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.equal(notifications.length, 1);
});

test('HostClass notification-incompatible nested values preserve the complete prior snapshot', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const notifications: unknown[] = [];
  let invocationCount = 0;
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      invocationCount += 1;
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [{
            identity: { name: 'Sheet1', kind: 'document' },
            status: 'resolved',
            intrinsicEventSourceName: 'Worksheet',
            events: invocationCount === 2
              ? [{
                  name: '   ',
                  parameters: [],
                  authoringAvailable: true,
                  existingHandlerRecognizable: true
                }]
              : [],
            ...(invocationCount === 3
              ? {
                  baseTypeProvenance: {
                    name: 'Worksheet',
                    libraryGuid: '00020813-0000-0000-C000-000000000046',
                    majorVersion: 2147483648,
                    minorVersion: 9,
                    lcid: 0
                  }
                }
              : {})
          }],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();
  lifecycle.refreshDocument(context);
  await lifecycle.flush();

  assert.equal(notifications.length, 1);
});

test('HostClass document removal publishes a cleared revision without another inspection', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\src\Book1\Book1.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  lifecycle.activateDocument(context);
  await lifecycle.flush();
  lifecycle.removeDocument(context);
  await lifecycle.flush();
  await lifecycle.replayDesiredSnapshots();

  assert.equal(invocations.length, 1);
  assert.deepEqual(notifications[1], {
    method: 'vba/hostClassProjectionSnapshot',
    parameters: {
      schemaVersion: 2,
      revision: 2,
      ...context,
      state: 'cleared'
    }
  });
  assert.equal(notifications.length, 2);
});

test('HostClass document removal reports cancellation of its running inspection', async () => {
  const context = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const transitions: HostClassProjectionLifecycleTransition[] = [];
  let started: (() => void) | undefined;
  const invocationStarted = new Promise<void>((resolve) => {
    started = resolve;
  });
  let complete: ((result: HostClassListRunResult) => void) | undefined;
  const result = new Promise<HostClassListRunResult>((resolve) => {
    complete = resolve;
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async () => {
      started?.();
      return result;
    },
    sendNotification: async () => undefined,
    onTransition: (transition) => transitions.push(transition)
  });
  lifecycle.activateDocument(context);
  await invocationStarted;

  lifecycle.removeDocument(context);

  assert.deepEqual(
    transitions.filter((transition) =>
      transition.kind === 'cancellationRequested'
    ).map((transition) => transition.reasonCode),
    ['documentRemoved']
  );
  complete?.({
    exitCode: 130,
    stdout: '',
    stderr: '',
    cancelled: true
  });
  await lifecycle.flush();
});

test('HostClass removal preserves a pending identity-change clear before replacement inspection', async () => {
  const original = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const replacement = {
    ...original,
    sourceTemplate: String.raw`C:\work\Invoices\templates\Replacement.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  const notifications: unknown[] = [];
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...invocation.context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    scheduleDelay: () => ({ dispose: () => undefined })
  });
  lifecycle.activateDocument(original);
  await lifecycle.flush();

  lifecycle.templateChanged(replacement);
  lifecycle.removeDocument(replacement);
  await lifecycle.flush();

  assert.deepEqual(
    notifications.map((value) => (value as { state?: string }).state),
    ['present', 'cleared']
  );
  assert.deepEqual(
    invocations.map((invocation) => invocation.context.sourceTemplate),
    [original.sourceTemplate]
  );
});

test('HostClass removal during identity clear prevents replacement inspection from starting', async () => {
  const original = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const replacement = {
    ...original,
    sourceTemplate: String.raw`C:\work\Invoices\templates\Replacement.xlsm`
  };
  const invocations: HostClassListInvocation[] = [];
  let markClearStarted: (() => void) | undefined;
  const clearStarted = new Promise<void>((resolve) => {
    markClearStarted = resolve;
  });
  let releaseClear: (() => void) | undefined;
  const clearReleased = new Promise<void>((resolve) => {
    releaseClear = resolve;
  });
  const lifecycle = new HostClassProjectionLifecycle({
    runHostClassList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify({
          schemaVersion: '1.1',
          ...invocation.context,
          classEnumerationComplete: true,
          complete: true,
          classes: [],
          diagnostics: [],
          warnings: []
        }),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      if ((parameters as { state?: string }).state === 'cleared') {
        markClearStarted?.();
        await clearReleased;
      }
    }
  });
  lifecycle.activateDocument(original);
  await lifecycle.flush();

  lifecycle.activateDocument(replacement);
  await clearStarted;
  lifecycle.removeDocument(replacement);
  releaseClear?.();
  await lifecycle.flush();

  assert.deepEqual(
    invocations.map((invocation) => invocation.context.sourceTemplate),
    [original.sourceTemplate]
  );
});
