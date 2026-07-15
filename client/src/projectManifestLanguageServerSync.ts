const ProjectManifestFileName = 'vba-project.json';

const DidOpenMethod = 'textDocument/didOpen';
const DidChangeMethod = 'textDocument/didChange';
const DidCloseMethod = 'textDocument/didClose';
const MaximumReconciliationAttempts = 2;

export interface ProjectManifestSyncDisposable {
  dispose(): void;
}

export interface ProjectManifestSyncUri {
  readonly scheme: string;
  readonly fsPath: string;
  toString(): string;
}

export interface ProjectManifestSyncDocument {
  readonly uri: ProjectManifestSyncUri;
  readonly languageId: string;
  readonly version: number;
  getText(): string;
}

export interface ProjectManifestSyncDocumentChange {
  readonly document: ProjectManifestSyncDocument;
}

export interface ProjectManifestLanguageServerSyncOptions {
  readonly getOpenDocuments: () => readonly ProjectManifestSyncDocument[];
  readonly onDidOpenTextDocument: (
    listener: (document: ProjectManifestSyncDocument) => void
  ) => ProjectManifestSyncDisposable;
  readonly onDidChangeTextDocument: (
    listener: (event: ProjectManifestSyncDocumentChange) => void
  ) => ProjectManifestSyncDisposable;
  readonly onDidCloseTextDocument: (
    listener: (document: ProjectManifestSyncDocument) => void
  ) => ProjectManifestSyncDisposable;
  readonly isLanguageClientRunning: () => boolean;
  readonly onDidChangeLanguageClientRunning: (
    listener: (isRunning: boolean) => void
  ) => ProjectManifestSyncDisposable;
  readonly sendNotification: (method: string, parameters: unknown) => Promise<void>;
  readonly subscriptions: ProjectManifestSyncDisposable[];
  readonly reportError?: (error: unknown) => void | Promise<void>;
}

export interface ProjectManifestLanguageServerSync {
  flush(): Promise<void>;
}

interface ProjectManifestSnapshot {
  readonly key: string;
  readonly uri: string;
  readonly languageId: string;
  readonly version: number;
  readonly text: string;
}

/**
 * Synchronizes open project-manifest buffers without registering VBA language features for JSON files.
 */
export function registerProjectManifestLanguageServerSync(
  options: ProjectManifestLanguageServerSyncOptions
): ProjectManifestLanguageServerSync {
  let isRunning = options.isLanguageClientRunning();
  let connectionGeneration = 0;
  let notificationQueue = Promise.resolve();
  const desiredManifests = new Map<string, ProjectManifestSnapshot>();
  const acknowledgedManifests = new Map<string, ProjectManifestSnapshot>();

  const reportError = async (error: unknown): Promise<void> => {
    try {
      await options.reportError?.(error);
    } catch {
      // Reporting must not poison later synchronization notifications.
    }
  };

  const reconcileOnce = async (
    generation: number,
    key: string,
    desired: ProjectManifestSnapshot | undefined
  ): Promise<void> => {
    const acknowledged = acknowledgedManifests.get(key);
    if (desired === undefined) {
      if (acknowledged === undefined) {
        return;
      }

      await options.sendNotification(DidCloseMethod, {
        textDocument: {
          uri: acknowledged.uri
        }
      });
      if (isRunning && generation === connectionGeneration) {
        acknowledgedManifests.delete(key);
      }
      return;
    }

    if (acknowledged === undefined) {
      await options.sendNotification(DidOpenMethod, {
        textDocument: {
          uri: desired.uri,
          languageId: desired.languageId,
          version: desired.version,
          text: desired.text
        }
      });
    } else if (!manifestSnapshotsEqual(acknowledged, desired)) {
      await options.sendNotification(DidChangeMethod, {
        textDocument: {
          uri: desired.uri,
          version: desired.version
        },
        contentChanges: [
          {
            text: desired.text
          }
        ]
      });
    } else {
      return;
    }

    if (isRunning && generation === connectionGeneration) {
      acknowledgedManifests.set(key, desired);
    }
  };

  const reconcileWithRetry = async (
    generation: number,
    key: string,
    desired: ProjectManifestSnapshot | undefined
  ): Promise<void> => {
    for (let attempt = 0; attempt < MaximumReconciliationAttempts; attempt += 1) {
      if (!isRunning || generation !== connectionGeneration) {
        return;
      }

      try {
        await reconcileOnce(generation, key, desired);
        return;
      } catch (error) {
        await reportError(error);
      }
    }
  };

  const enqueueReconciliation = (key: string): void => {
    const generation = connectionGeneration;
    const desired = desiredManifests.get(key);
    notificationQueue = notificationQueue
      .then(() => reconcileWithRetry(generation, key, desired))
      .catch(reportError);
  };

  const openDocument = (document: ProjectManifestSyncDocument): void => {
    const snapshot = getProjectManifestSnapshot(document);
    if (snapshot === undefined) {
      return;
    }

    desiredManifests.set(snapshot.key, snapshot);
    if (isRunning) {
      enqueueReconciliation(snapshot.key);
    }
  };

  const changeDocument = (event: ProjectManifestSyncDocumentChange): void => {
    const snapshot = getProjectManifestSnapshot(event.document);
    if (snapshot === undefined) {
      return;
    }

    desiredManifests.set(snapshot.key, snapshot);
    if (isRunning) {
      enqueueReconciliation(snapshot.key);
    }
  };

  const closeDocument = (document: ProjectManifestSyncDocument): void => {
    const key = getProjectManifestKey(document);
    if (key === undefined) {
      return;
    }

    desiredManifests.delete(key);
    if (isRunning) {
      enqueueReconciliation(key);
    }
  };

  const synchronizeOpenDocuments = (): void => {
    desiredManifests.clear();
    for (const document of options.getOpenDocuments()) {
      const snapshot = getProjectManifestSnapshot(document);
      if (snapshot !== undefined) {
        desiredManifests.set(snapshot.key, snapshot);
      }
    }

    for (const key of desiredManifests.keys()) {
      enqueueReconciliation(key);
    }
  };

  const changeRunningState = (nextIsRunning: boolean): void => {
    if (nextIsRunning === isRunning) {
      return;
    }

    isRunning = nextIsRunning;
    connectionGeneration += 1;
    acknowledgedManifests.clear();
    if (isRunning) {
      synchronizeOpenDocuments();
    }
  };

  options.subscriptions.push(
    options.onDidOpenTextDocument(openDocument),
    options.onDidChangeTextDocument(changeDocument),
    options.onDidCloseTextDocument(closeDocument),
    options.onDidChangeLanguageClientRunning(changeRunningState)
  );

  if (isRunning) {
    synchronizeOpenDocuments();
  }

  return {
    async flush(): Promise<void> {
      let pending = notificationQueue;
      await pending;
      while (pending !== notificationQueue) {
        pending = notificationQueue;
        await pending;
      }
    }
  };
}

function getProjectManifestSnapshot(
  document: ProjectManifestSyncDocument
): ProjectManifestSnapshot | undefined {
  const key = getProjectManifestKey(document);
  if (key === undefined) {
    return undefined;
  }

  return {
    key,
    uri: document.uri.toString(),
    languageId: document.languageId,
    version: document.version,
    text: document.getText()
  };
}

function manifestSnapshotsEqual(
  left: ProjectManifestSnapshot,
  right: ProjectManifestSnapshot
): boolean {
  return left.uri === right.uri
    && left.languageId === right.languageId
    && left.version === right.version
    && left.text === right.text;
}

function getProjectManifestKey(
  document: ProjectManifestSyncDocument
): string | undefined {
  if (document.uri.scheme.toLowerCase() !== 'file') {
    return undefined;
  }

  const normalizedPath = document.uri.fsPath.replace(/\\/g, '/');
  const separatorIndex = normalizedPath.lastIndexOf('/');
  const fileName = normalizedPath.slice(separatorIndex + 1);
  if (fileName.toLowerCase() !== ProjectManifestFileName) {
    return undefined;
  }

  return normalizedPath.toLowerCase();
}
