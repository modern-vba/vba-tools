import test from 'node:test';
import assert from 'node:assert/strict';

import {
  ProjectManifestLanguageServerSyncOptions,
  ProjectManifestSyncDisposable,
  ProjectManifestSyncDocument,
  ProjectManifestSyncDocumentChange,
  registerProjectManifestLanguageServerSync
} from './projectManifestLanguageServerSync';

test('ProjectManifest sync opens only exact file manifests and keeps open change close balanced', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\VBA-PROJECT.JSON`,
    'file:///C:/work/VBA-PROJECT.JSON',
    'json',
    3,
    '{"version":3}'
  );
  const otherJson = new FakeDocument(
    'file',
    String.raw`C:\work\project.json`,
    'file:///C:/work/project.json',
    'json',
    1,
    '{}'
  );
  const untitledManifest = new FakeDocument(
    'untitled',
    'vba-project.json',
    'untitled:vba-project.json',
    'json',
    1,
    '{}'
  );
  const harness = new SyncHarness([manifest, otherJson, untitledManifest], true);
  const sync = registerProjectManifestLanguageServerSync(harness.options);

  await sync.flush();
  assert.equal(harness.subscriptions.length, 4);
  assert.deepEqual(harness.notifications, [
    {
      method: 'textDocument/didOpen',
      parameters: {
        textDocument: {
          uri: 'file:///C:/work/VBA-PROJECT.JSON',
          languageId: 'json',
          version: 3,
          text: '{"version":3}'
        }
      }
    }
  ]);

  harness.documents.fireOpen(manifest);
  manifest.version = 4;
  manifest.text = '{"version":4}';
  harness.documents.fireChange(manifest);
  harness.documents.fireClose(manifest);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didChange',
      'textDocument/didClose'
    ]
  );
  assert.deepEqual(harness.notifications[1]?.parameters, {
    textDocument: {
      uri: 'file:///C:/work/VBA-PROJECT.JSON',
      version: 4
    },
    contentChanges: [
      {
        text: '{"version":4}'
      }
    ]
  });
});

test('ProjectManifest sync reopens the latest full text after a language server restart', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\vba-project.json`,
    'file:///C:/work/vba-project.json',
    'json',
    1,
    '{"version":1}'
  );
  const harness = new SyncHarness([manifest], true);
  const sync = registerProjectManifestLanguageServerSync(harness.options);
  await sync.flush();

  harness.state.setRunning(false);
  manifest.version = 2;
  manifest.text = '{"version":2}';
  harness.documents.fireChange(manifest);
  harness.state.setRunning(true);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    ['textDocument/didOpen', 'textDocument/didOpen']
  );
  assert.deepEqual(harness.notifications[1]?.parameters, {
    textDocument: {
      uri: 'file:///C:/work/vba-project.json',
      languageId: 'json',
      version: 2,
      text: '{"version":2}'
    }
  });
});

test('ProjectManifest sync serializes open change and close notifications', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\vba-project.json`,
    'file:///C:/work/vba-project.json',
    'json',
    1,
    '{"version":1}'
  );
  const harness = new SyncHarness([], true, true);
  const sync = registerProjectManifestLanguageServerSync(harness.options);

  harness.documents.openDocuments.push(manifest);
  harness.documents.fireOpen(manifest);
  manifest.version = 2;
  manifest.text = '{"version":2}';
  harness.documents.fireChange(manifest);
  harness.documents.fireClose(manifest);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didChange',
      'textDocument/didClose'
    ]
  );
  assert.equal(harness.maximumConcurrentNotifications, 1);
});

test('ProjectManifest sync retries a failed open before acknowledging the server state', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\vba-project.json`,
    'file:///C:/work/vba-project.json',
    'json',
    1,
    '{"version":1}'
  );
  const harness = new SyncHarness(
    [manifest],
    true,
    false,
    { 'textDocument/didOpen': 1 }
  );
  const sync = registerProjectManifestLanguageServerSync(harness.options);

  await sync.flush();
  manifest.version = 2;
  manifest.text = '{"version":2}';
  harness.documents.fireChange(manifest);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didOpen',
      'textDocument/didChange'
    ]
  );
  assert.deepEqual(
    harness.successfulNotifications.map((notification) => notification.method),
    ['textDocument/didOpen', 'textDocument/didChange']
  );
  assert.equal(harness.errors.length, 1);
});

test('ProjectManifest sync retries a failed close before acknowledging the server state', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\vba-project.json`,
    'file:///C:/work/vba-project.json',
    'json',
    1,
    '{"version":1}'
  );
  const harness = new SyncHarness(
    [manifest],
    true,
    false,
    { 'textDocument/didClose': 1 }
  );
  const sync = registerProjectManifestLanguageServerSync(harness.options);
  await sync.flush();

  harness.documents.fireClose(manifest);
  await sync.flush();
  manifest.version = 2;
  manifest.text = '{"version":2}';
  harness.documents.fireOpen(manifest);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didClose',
      'textDocument/didClose',
      'textDocument/didOpen'
    ]
  );
  assert.deepEqual(
    harness.successfulNotifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didClose',
      'textDocument/didOpen'
    ]
  );
  assert.equal(harness.errors.length, 1);
});

test('ProjectManifest sync retries unresolved desired state on a later event', async () => {
  const manifest = new FakeDocument(
    'file',
    String.raw`C:\work\vba-project.json`,
    'file:///C:/work/vba-project.json',
    'json',
    1,
    '{"version":1}'
  );
  const harness = new SyncHarness(
    [manifest],
    true,
    false,
    { 'textDocument/didOpen': 2 }
  );
  const sync = registerProjectManifestLanguageServerSync(harness.options);
  await sync.flush();

  manifest.version = 2;
  manifest.text = '{"version":2}';
  harness.documents.fireChange(manifest);
  await sync.flush();

  assert.deepEqual(
    harness.notifications.map((notification) => notification.method),
    [
      'textDocument/didOpen',
      'textDocument/didOpen',
      'textDocument/didOpen'
    ]
  );
  assert.deepEqual(harness.successfulNotifications[0]?.parameters, {
    textDocument: {
      uri: 'file:///C:/work/vba-project.json',
      languageId: 'json',
      version: 2,
      text: '{"version":2}'
    }
  });
  assert.equal(harness.errors.length, 2);
});

interface RecordedNotification {
  readonly method: string;
  readonly parameters: unknown;
}

class SyncHarness {
  public readonly documents: FakeDocumentEvents;
  public readonly state: FakeRunningState;
  public readonly subscriptions: ProjectManifestSyncDisposable[] = [];
  public readonly notifications: RecordedNotification[] = [];
  public readonly successfulNotifications: RecordedNotification[] = [];
  public readonly errors: unknown[] = [];
  public maximumConcurrentNotifications = 0;
  private activeNotifications = 0;
  private readonly remainingFailures: Map<string, number>;

  public readonly options: ProjectManifestLanguageServerSyncOptions;

  public constructor(
    openDocuments: FakeDocument[],
    initiallyRunning: boolean,
    yieldDuringNotification = false,
    notificationFailures: Readonly<Record<string, number>> = {}
  ) {
    this.documents = new FakeDocumentEvents(openDocuments);
    this.state = new FakeRunningState(initiallyRunning);
    this.remainingFailures = new Map(Object.entries(notificationFailures));
    this.options = {
      getOpenDocuments: () => this.documents.openDocuments,
      onDidOpenTextDocument: (listener) => this.documents.onDidOpen(listener),
      onDidChangeTextDocument: (listener) => this.documents.onDidChange(listener),
      onDidCloseTextDocument: (listener) => this.documents.onDidClose(listener),
      isLanguageClientRunning: () => this.state.running,
      onDidChangeLanguageClientRunning: (listener) => this.state.onDidChange(listener),
      sendNotification: async (method, parameters) => {
        this.activeNotifications += 1;
        this.maximumConcurrentNotifications = Math.max(
          this.maximumConcurrentNotifications,
          this.activeNotifications
        );
        try {
          this.notifications.push({ method, parameters });
          if (yieldDuringNotification) {
            await new Promise<void>((resolve) => setImmediate(resolve));
          }
          const remainingFailures = this.remainingFailures.get(method) ?? 0;
          if (remainingFailures > 0) {
            this.remainingFailures.set(method, remainingFailures - 1);
            throw new Error(`Simulated ${method} failure.`);
          }
          this.successfulNotifications.push({ method, parameters });
        } finally {
          this.activeNotifications -= 1;
        }
      },
      subscriptions: this.subscriptions,
      reportError: (error) => {
        this.errors.push(error);
      }
    };
  }
}

class FakeDocument implements ProjectManifestSyncDocument {
  public readonly uri: {
    readonly scheme: string;
    readonly fsPath: string;
    toString(): string;
  };

  public constructor(
    public readonly scheme: string,
    public readonly fsPath: string,
    public readonly uriText: string,
    public readonly languageId: string,
    public version: number,
    public text: string
  ) {
    this.uri = {
      scheme,
      fsPath,
      toString: (): string => uriText
    };
  }

  public getText(): string {
    return this.text;
  }
}

class FakeDocumentEvents {
  public readonly openDocuments: FakeDocument[];
  private readonly openListeners: Array<(document: ProjectManifestSyncDocument) => void> = [];
  private readonly changeListeners: Array<(event: ProjectManifestSyncDocumentChange) => void> = [];
  private readonly closeListeners: Array<(document: ProjectManifestSyncDocument) => void> = [];

  public constructor(openDocuments: FakeDocument[]) {
    this.openDocuments = [...openDocuments];
  }

  public onDidOpen(listener: (document: ProjectManifestSyncDocument) => void): FakeDisposable {
    this.openListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidChange(listener: (event: ProjectManifestSyncDocumentChange) => void): FakeDisposable {
    this.changeListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidClose(listener: (document: ProjectManifestSyncDocument) => void): FakeDisposable {
    this.closeListeners.push(listener);
    return new FakeDisposable();
  }

  public fireOpen(document: FakeDocument): void {
    for (const listener of this.openListeners) {
      listener(document);
    }
  }

  public fireChange(document: FakeDocument): void {
    for (const listener of this.changeListeners) {
      listener({ document });
    }
  }

  public fireClose(document: FakeDocument): void {
    for (const listener of this.closeListeners) {
      listener(document);
    }
  }
}

class FakeRunningState {
  private readonly listeners: Array<(isRunning: boolean) => void> = [];

  public constructor(public running: boolean) {
  }

  public onDidChange(listener: (isRunning: boolean) => void): FakeDisposable {
    this.listeners.push(listener);
    return new FakeDisposable();
  }

  public setRunning(isRunning: boolean): void {
    this.running = isRunning;
    for (const listener of this.listeners) {
      listener(isRunning);
    }
  }
}

class FakeDisposable implements ProjectManifestSyncDisposable {
  public dispose(): void {
  }
}
