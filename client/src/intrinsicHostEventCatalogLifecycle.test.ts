import test from 'node:test';
import assert from 'node:assert/strict';

import {
  IntrinsicHostEventCatalogInvocation,
  IntrinsicHostEventCatalogLifecycle
} from './intrinsicHostEventCatalogLifecycle';

const validCatalog = {
  schemaVersion: '1.0',
  sourceKind: 'userForm',
  intrinsicEventSourceName: 'UserForm',
  events: [
    {
      identity: {
        sourceName: 'UserForm',
        name: 'Initialize'
      },
      signature: {
        parameters: [],
        documentation: 'Occurs after an object is loaded.'
      },
      authoringAvailable: true,
      existingHandlerRecognizable: true
    }
  ]
} as const;

test('trusted activation acquires and publishes one environment catalog at most once', async () => {
  const invocations: IntrinsicHostEventCatalogInvocation[] = [];
  const notifications: Array<{ method: string; parameters: unknown }> = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 0,
        stdout: JSON.stringify(validCatalog),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async (method, parameters) => {
      notifications.push({ method, parameters });
    }
  });

  void lifecycle.activate();
  void lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(invocations.map((invocation) => invocation.args), [[
    'host-event',
    'list',
    '--format',
    'json'
  ]]);
  assert.deepEqual(notifications, [{
    method: 'vba/intrinsicHostEventCatalog',
    parameters: {
      schemaVersion: '1.0',
      revision: 1,
      catalog: {
        sourceKind: 'userForm',
        intrinsicEventSourceName: 'UserForm',
        events: validCatalog.events
      }
    }
  }]);
});

test('startup failure publishes one unavailable snapshot without retrying', async () => {
  let invocationCount = 0;
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => {
      invocationCount++;
      return {
        exitCode: 1,
        stdout: '',
        stderr: 'Excel unavailable',
        cancelled: false
      };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();
  lifecycle.activate();
  await lifecycle.flush();

  assert.equal(invocationCount, 1);
  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: null
  }]);
});

test('failed explicit refresh retains the healthy current catalog and revision', async () => {
  const notifications: unknown[] = [];
  let invocationCount = 0;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => {
      invocationCount++;
      return invocationCount === 1
        ? {
            exitCode: 0,
            stdout: JSON.stringify(validCatalog),
            stderr: '',
            cancelled: false
          }
        : {
            exitCode: 1,
            stdout: '',
            stderr: 'Refresh failed',
            cancelled: false
          };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });
  lifecycle.activate();
  await lifecycle.flush();

  const refresh = lifecycle.refresh();
  const outcome = await refresh.completion;
  await lifecycle.flush();

  assert.deepEqual(outcome, {
    status: 'failed',
    reason: 'commandFailed',
    exitCode: 1
  });
  assert.equal(notifications.length, 1);
  await lifecycle.replayCurrentSnapshot();
  assert.deepEqual(notifications[1], notifications[0]);
});

test('replay sends the same full snapshot and revision after language client restart', async () => {
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify(validCatalog),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });
  lifecycle.activate();
  await lifecycle.flush();

  await lifecycle.replayCurrentSnapshot();

  assert.equal(notifications.length, 2);
  assert.deepEqual(notifications[1], notifications[0]);
});

test('explicit refresh is cancellable while queued behind acquisition', async () => {
  let releaseActivation!: () => void;
  const activationGate = new Promise<void>((resolve) => {
    releaseActivation = resolve;
  });
  let invocationCount = 0;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => {
      invocationCount++;
      if (invocationCount === 1) {
        await activationGate;
      }
      return {
        exitCode: 0,
        stdout: JSON.stringify(validCatalog),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });

  lifecycle.activate();
  const refresh = lifecycle.refresh();
  refresh.cancel();

  assert.deepEqual(await Promise.race([
    refresh.completion,
    new Promise((resolve) => setTimeout(() => resolve('timed out'), 50))
  ]), { status: 'cancelled' });
  releaseActivation();
  await lifecycle.flush();
  assert.equal(invocationCount, 1);
});

test('activation starts one environment acquisition without blocking language startup', async () => {
  let releaseAcquisition!: () => void;
  const acquisitionGate = new Promise<void>((resolve) => {
    releaseAcquisition = resolve;
  });
  const invocations: IntrinsicHostEventCatalogInvocation[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async (invocation) => {
      invocations.push(invocation);
      await acquisitionGate;
      return {
        exitCode: 0,
        stdout: JSON.stringify(validCatalog),
        stderr: '',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });
  let languageClientStarted = false;

  lifecycle.activate();
  await Promise.resolve().then(() => {
    languageClientStarted = true;
  });

  assert.equal(languageClientStarted, true);
  assert.equal(invocations.length, 1);
  assert.deepEqual(invocations[0]?.args, [
    'host-event', 'list', '--format', 'json'
  ]);
  assert.deepEqual(Object.keys(invocations[0] ?? {}).sort(), [
    'args', 'cancellationToken', 'trigger'
  ]);
  assert.equal(invocations[0]?.args.some((argument) =>
    ['--project', '--document', '--source-template'].includes(argument)
  ), false);
  releaseAcquisition();
  await lifecycle.flush();
});

test('startup failure never starts an automatic fallback', async () => {
  const invocations: IntrinsicHostEventCatalogInvocation[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async (invocation) => {
      invocations.push(invocation);
      return {
        exitCode: 1,
        stdout: '',
        stderr: 'Excel unavailable',
        cancelled: false
      };
    },
    sendNotification: async () => undefined
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.equal(invocations.length, 1);
  assert.deepEqual(invocations[0]?.args, [
    'host-event', 'list', '--format', 'json'
  ]);
});

test('unavailable clear and later catalog replacement use positive monotonic revisions', async () => {
  const notifications: Array<{
    schemaVersion: string;
    revision: number;
    catalog: unknown;
  }> = [];
  let invocationCount = 0;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => {
      invocationCount++;
      return invocationCount === 1
        ? {
            exitCode: 1,
            stdout: '',
            stderr: 'Unavailable',
            cancelled: false
          }
        : {
            exitCode: 0,
            stdout: JSON.stringify(validCatalog),
            stderr: '',
            cancelled: false
          };
    },
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters as typeof notifications[number]);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();
  assert.deepEqual(await lifecycle.refresh().completion, {
    status: 'succeeded',
    revision: 2
  });

  assert.deepEqual(notifications.map((snapshot) => ({
    revision: snapshot.revision,
    catalog: snapshot.catalog === null ? null : 'present'
  })), [
    { revision: 1, catalog: null },
    { revision: 2, catalog: 'present' }
  ]);
});

test('a new extension activation starts with no persisted catalog or revision', async () => {
  const revisions: number[] = [];
  const createLifecycle = () => new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify(validCatalog),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      revisions.push((parameters as { revision: number }).revision);
    }
  });

  const firstActivation = createLifecycle();
  firstActivation.activate();
  await firstActivation.flush();
  const secondActivation = createLifecycle();
  secondActivation.activate();
  await secondActivation.flush();

  assert.deepEqual(revisions, [1, 1]);
});

test('catalog parsing preserves structured parameter and base-type provenance metadata', async () => {
  const catalog = {
    schemaVersion: '1.0',
    sourceKind: 'userForm',
    intrinsicEventSourceName: 'UserForm',
    events: [{
      identity: { sourceName: 'UserForm', name: 'QueryClose' },
      signature: {
        parameters: [
          {
            name: 'Cancel',
            type: { kind: 'intrinsic', name: 'Integer' },
            passing: 'byRef',
            arrayShape: 'scalar',
            optional: false,
            paramArray: false
          },
          {
            name: 'CloseMode',
            type: {
              kind: 'typeLib',
              name: 'VbQueryClose',
              libraryGuid: '000204ef-0000-0000-c000-000000000046',
              majorVersion: 4,
              minorVersion: 2,
              lcid: 0
            },
            passing: 'byVal',
            arrayShape: 'scalar',
            optional: false,
            paramArray: false
          }
        ]
      },
      authoringAvailable: true,
      existingHandlerRecognizable: true
    }],
    baseTypeProvenance: {
      name: '_UserForm',
      libraryGuid: '000204ef-0000-0000-c000-000000000046',
      majorVersion: 4,
      minorVersion: 2,
      lcid: 0
    }
  };
  let published: unknown;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify(catalog),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      published = parameters;
    }
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(published, {
    schemaVersion: '1.0',
    revision: 1,
    catalog: {
      sourceKind: 'userForm',
      intrinsicEventSourceName: 'UserForm',
      events: catalog.events,
      baseTypeProvenance: catalog.baseTypeProvenance
    }
  });
});

test('schema-invalid success is unavailable rather than an authoritative empty catalog', async () => {
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        ...validCatalog,
        intrinsicEventSourceName: 'Worksheet'
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: null
  }]);
});

test('notification-failed explicit refresh retains the previously current catalog', async () => {
  const notifications: Array<{ revision: number; catalog: unknown }> = [];
  let invocationCount = 0;
  let notificationCount = 0;
  const replacementCatalog = {
    ...validCatalog,
    events: [{
      ...validCatalog.events[0],
      identity: { sourceName: 'UserForm', name: 'QueryClose' }
    }]
  };
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify(invocationCount++ === 0
        ? validCatalog
        : replacementCatalog),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notificationCount++;
      if (notificationCount === 2) {
        throw new Error('language client stopped');
      }
      notifications.push(parameters as typeof notifications[number]);
    }
  });
  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(await lifecycle.refresh().completion, {
    status: 'failed',
    reason: 'notificationFailed'
  });
  await lifecycle.replayCurrentSnapshot();

  assert.deepEqual(notifications.map((snapshot) => ({
    revision: snapshot.revision,
    eventName: (snapshot.catalog as typeof validCatalog).events[0]?.identity.name
  })), [
    { revision: 1, eventName: 'Initialize' },
    { revision: 1, eventName: 'Initialize' }
  ]);
});

test('startup catalog waits in memory for replay when the language client is not running yet', async () => {
  const notifications: unknown[] = [];
  const transitions: string[] = [];
  let languageClientRunning = false;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify(validCatalog),
      stderr: '',
      cancelled: false
    }),
    isNotificationTargetAvailable: () => languageClientRunning,
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    },
    onTransition: (transition) => transitions.push(transition.kind)
  });
  lifecycle.activate();
  await lifecycle.flush();
  assert.deepEqual(notifications, []);
  assert.equal(transitions.includes('notificationFailed'), false);
  languageClientRunning = true;

  await lifecycle.replayCurrentSnapshot();

  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: {
      sourceKind: 'userForm',
      intrinsicEventSourceName: 'UserForm',
      events: validCatalog.events
    }
  }]);
});

test('a stale unavailable replay cannot replace a newer healthy refresh commit', async () => {
  let invocationCount = 0;
  let languageClientRunning = false;
  let markReplayStarted!: () => void;
  const replayStarted = new Promise<void>((resolve) => {
    markReplayStarted = resolve;
  });
  let releaseReplay!: () => void;
  const replayGate = new Promise<void>((resolve) => {
    releaseReplay = resolve;
  });
  let firstRevisionReplay = true;
  const notifications: Array<{
    revision: number;
    catalog: typeof validCatalog | null;
  }> = [];
  const replacementCatalog = {
    ...validCatalog,
    events: [{
      ...validCatalog.events[0],
      identity: { sourceName: 'UserForm', name: 'QueryClose' }
    }]
  } as const;
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => {
      invocationCount += 1;
      if (invocationCount === 2) {
        return {
          exitCode: 0,
          stdout: JSON.stringify(replacementCatalog),
          stderr: '',
          cancelled: false
        };
      }
      return {
        exitCode: 1,
        stdout: '',
        stderr: 'Excel unavailable',
        cancelled: false
      };
    },
    isNotificationTargetAvailable: () => languageClientRunning,
    sendNotification: async (_method, parameters) => {
      const snapshot = parameters as typeof notifications[number];
      notifications.push(snapshot);
      if (snapshot.revision === 1 && firstRevisionReplay) {
        firstRevisionReplay = false;
        markReplayStarted();
        await replayGate;
      }
    }
  });
  lifecycle.activate();
  await lifecycle.flush();
  languageClientRunning = true;

  const staleReplay = lifecycle.replayCurrentSnapshot();
  await replayStarted;
  assert.deepEqual(await lifecycle.refresh().completion, {
    status: 'succeeded',
    revision: 2
  });
  releaseReplay();
  await staleReplay;
  assert.deepEqual(await lifecycle.refresh().completion, {
    status: 'failed',
    reason: 'commandFailed',
    exitCode: 1
  });
  await lifecycle.replayCurrentSnapshot();

  assert.deepEqual(notifications.map((snapshot) => ({
    revision: snapshot.revision,
    eventName: snapshot.catalog?.events[0]?.identity.name ?? null
  })), [
    { revision: 1, eventName: null },
    { revision: 2, eventName: 'QueryClose' },
    { revision: 2, eventName: 'QueryClose' }
  ]);
});

test('an empty Event array cannot become a healthy environment catalog', async () => {
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({ ...validCatalog, events: [] }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: null
  }]);
});

test('duplicate Event identities use .NET OrdinalIgnoreCase rather than locale folding', async () => {
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        ...validCatalog,
        events: [
          {
            ...validCatalog.events[0],
            identity: { sourceName: 'UserForm', name: '\u00b5' }
          },
          {
            ...validCatalog.events[0],
            identity: { sourceName: 'UserForm', name: '\u039c' }
          }
        ]
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: null
  }]);
});

test('document or source-template fields invalidate the environment catalog', async () => {
  const notifications: unknown[] = [];
  const lifecycle = new IntrinsicHostEventCatalogLifecycle({
    runHostEventList: async () => ({
      exitCode: 0,
      stdout: JSON.stringify({
        ...validCatalog,
        project: String.raw`C:\work\Invoices`,
        document: 'Book1',
        sourceTemplate: String.raw`C:\work\Invoices\Book1.xlsm`
      }),
      stderr: '',
      cancelled: false
    }),
    sendNotification: async (_method, parameters) => {
      notifications.push(parameters);
    }
  });

  lifecycle.activate();
  await lifecycle.flush();

  assert.deepEqual(notifications, [{
    schemaVersion: '1.0',
    revision: 1,
    catalog: null
  }]);
});
