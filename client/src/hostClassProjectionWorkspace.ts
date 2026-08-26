import * as path from 'node:path';

import {
  HostClassCancellationDisposable,
  HostClassProjectionContext
} from './hostClassProjectionLifecycle';
import {
  HostClassSourceAssociationResult,
  HostClassSourceCandidate
} from './hostClassSourceAssociation';
import { parseProjectManifest } from './projectManifest';

export interface HostClassProjectionWorkspaceDocument {
  readonly manifestPath: string;
  readonly sourceSetPath: string;
  readonly context: HostClassProjectionContext;
}

export interface HostClassProjectionWorkspaceLifecycle {
  activateDocument(context: HostClassProjectionContext): void;
  templateChanged(context: HostClassProjectionContext): void;
  beginManifestResolution(context: HostClassProjectionContext): void;
  completeManifestResolution(context: HostClassProjectionContext): void;
  scheduleResolvedAutomaticRefresh(
    context: HostClassProjectionContext,
    trigger: 'manifestChanged' | 'templateChanged'
  ): void;
  reevaluateSourceAssociations(
    context: HostClassProjectionContext,
    sources: readonly HostClassSourceCandidate[]
  ): HostClassSourceAssociationResult | undefined;
  removeDocument(context: HostClassProjectionContext): void;
}

export interface HostClassProjectionWorkspaceOptions {
  readonly lifecycle: HostClassProjectionWorkspaceLifecycle;
  readonly findProjectManifests: () => Promise<readonly string[]>;
  readonly readManifestText: (
    canonicalManifestPath: string
  ) => Promise<string | undefined>;
  readonly collectHostClassSources: (
    document: HostClassProjectionWorkspaceDocument
  ) => Promise<readonly HostClassSourceCandidate[]>;
  readonly onActiveDocumentsChanged?: (
    documents: readonly HostClassProjectionWorkspaceDocument[]
  ) => void;
  readonly scheduleDelay?: (
    delayMilliseconds: number,
    callback: () => void
  ) => HostClassCancellationDisposable;
  readonly reportError?: (error: unknown) => void | Promise<void>;
}

export function classifyHostClassTextDocumentChange(
  uriScheme: string,
  filePath: string,
  workspaceRoots: readonly string[],
  activeDocuments: readonly HostClassProjectionWorkspaceDocument[]
): 'manifest' | 'source' | undefined {
  if (uriScheme !== 'file') {
    return undefined;
  }

  const canonicalFilePath = canonicalPath(filePath);
  if (path.basename(canonicalFilePath).toLowerCase() === 'vba-project.json') {
    const isWorkspaceManifest = workspaceRoots.some((root) =>
      pathContains(canonicalPath(root), canonicalFilePath)
    );
    const isActiveManifest = activeDocuments.some((document) =>
      pathKey(document.manifestPath) === pathKey(canonicalFilePath)
    );
    return isWorkspaceManifest || isActiveManifest ? 'manifest' : undefined;
  }

  return ['.bas', '.cls', '.frm', '.frx'].includes(
    path.extname(canonicalFilePath).toLowerCase()
  ) ? 'source' : undefined;
}

export class HostClassProjectionWorkspace {
  private readonly documents = new Map<string, HostClassProjectionWorkspaceDocument>();
  private readonly delayedManifestReconciliations = new Map<
    string,
    {
      readonly manifestPath: string;
      readonly removed: boolean;
      readonly token: object;
      readonly disposable: HostClassCancellationDisposable;
    }
  >();
  private readonly fencedManifestContexts = new Map<
    string,
    Map<string, HostClassProjectionContext>
  >();
  private operationQueue: Promise<void> = Promise.resolve();
  private shutdownRequested = false;

  public constructor(private readonly options: HostClassProjectionWorkspaceOptions) {
  }

  public activate(): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    return this.enqueue(() => this.reconcileDiscoveredDocuments());
  }

  public reconcileWorkspaceFolders(
    currentWorkspaceRoots?: readonly string[]
  ): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    if (currentWorkspaceRoots !== undefined) {
      const roots = currentWorkspaceRoots.map((root) => canonicalPath(root));
      const remainsInWorkspace = (candidate: string): boolean => roots.some(
        (root) => pathContains(root, candidate)
      );
      for (const [key, delayed] of this.delayedManifestReconciliations) {
        if (!remainsInWorkspace(delayed.manifestPath)) {
          delayed.disposable.dispose();
          this.delayedManifestReconciliations.delete(key);
          this.releaseManifestResolution(key);
        }
      }
      for (const document of [...this.documents.values()].sort(compareDocuments)) {
        if (!remainsInWorkspace(document.manifestPath)) {
          this.documents.delete(documentKey(document));
          this.notifyActiveDocumentsChanged();
          this.options.lifecycle.removeDocument(document.context);
        }
      }
    }
    return this.enqueue(() => this.reconcileDiscoveredDocuments());
  }

  public manifestChanged(manifestPath: string): Promise<void> {
    return this.scheduleManifestReconciliation(manifestPath, false);
  }

  public manifestRemoved(manifestPath: string): Promise<void> {
    return this.scheduleManifestReconciliation(manifestPath, true);
  }

  public templateFileChanged(templatePath: string): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    const canonicalTemplatePath = canonicalPath(templatePath);
    const changedKey = pathKey(canonicalTemplatePath);
    const affected = [...this.documents.values()]
      .filter((document) =>
        pathKey(document.context.sourceTemplate) === changedKey
      )
      .sort(compareDocuments);
    for (const document of affected) {
      this.options.lifecycle.templateChanged(document.context);
    }
    return Promise.resolve();
  }

  public sourceFileChanged(sourcePath: string): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    return this.enqueue(async () => {
      const canonicalSourcePath = canonicalPath(sourcePath);
      const affected = [...this.documents.values()]
        .filter((document) =>
          pathContains(document.sourceSetPath, canonicalSourcePath)
        )
        .sort(compareDocuments);
      for (const document of affected) {
        await this.collectAndReevaluate(document);
      }
    });
  }

  public reevaluateAllSourceAssociations(): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    return this.enqueue(async () => {
      for (const document of [...this.documents.values()].sort(compareDocuments)) {
        await this.collectAndReevaluate(document);
      }
    });
  }

  public getActiveDocuments(): readonly HostClassProjectionWorkspaceDocument[] {
    return [...this.documents.values()].sort(compareDocuments);
  }

  public shutdown(): void {
    if (this.shutdownRequested) {
      return;
    }

    this.shutdownRequested = true;
    for (const delayed of this.delayedManifestReconciliations.values()) {
      delayed.disposable.dispose();
    }
    this.delayedManifestReconciliations.clear();
    for (const key of [...this.fencedManifestContexts.keys()]) {
      this.releaseManifestResolution(key);
    }
    this.options.onActiveDocumentsChanged?.([]);
  }

  public async flush(): Promise<void> {
    let pending = this.operationQueue;
    await pending;
    while (pending !== this.operationQueue) {
      pending = this.operationQueue;
      await pending;
    }
  }

  private enqueue(operation: () => Promise<void>): Promise<void> {
    const queued = this.operationQueue
      .then(async () => {
        if (!this.shutdownRequested) {
          await operation();
        }
      })
      .catch(async (error) => {
        await this.options.reportError?.(error);
      });
    this.operationQueue = queued;
    return queued;
  }

  private scheduleManifestReconciliation(
    manifestPath: string,
    removed: boolean
  ): Promise<void> {
    if (this.shutdownRequested) {
      return Promise.resolve();
    }
    const canonicalManifestPath = this.registeredManifestPath(manifestPath);
    const key = pathKey(canonicalManifestPath);
    const fenced = this.fencedManifestContexts.get(key) ?? new Map();
    for (const document of this.documents.values()) {
      if (pathKey(document.manifestPath) !== key) {
        continue;
      }

      this.options.lifecycle.beginManifestResolution(document.context);
      fenced.set(contextKey(document.context), document.context);
    }
    this.fencedManifestContexts.set(key, fenced);
    this.delayedManifestReconciliations.get(key)?.disposable.dispose();
    const scheduleDelay = this.options.scheduleDelay ?? scheduleWorkspaceDelay;
    const token = {};
    const disposable = scheduleDelay(1000, () => {
      if (this.delayedManifestReconciliations.get(key)?.token !== token) {
        return;
      }

      this.delayedManifestReconciliations.delete(key);
      void this.enqueue(async () => {
        try {
          if (removed) {
            await this.reconcileManifestRemoval(canonicalManifestPath);
          } else {
            await this.reconcileManifestChange(canonicalManifestPath);
          }
        } finally {
          if (!this.delayedManifestReconciliations.has(key)) {
            this.releaseManifestResolution(key);
          }
        }
      });
    });
    this.delayedManifestReconciliations.set(key, {
      manifestPath: canonicalManifestPath,
      removed,
      token,
      disposable
    });
    return Promise.resolve();
  }

  private async reconcileManifestChange(
    canonicalManifestPath: string
  ): Promise<void> {
    const resolved = await this.resolveManifestDocuments(canonicalManifestPath);
    const existing = [...this.documents.values()]
      .filter((document) =>
        pathKey(document.manifestPath) === pathKey(canonicalManifestPath)
      );
    const nextByKey = new Map(
      resolved.map((document) => [documentKey(document), document] as const)
    );

    for (const document of existing) {
      const key = documentKey(document);
      const replacement = nextByKey.get(key);
      if (replacement === undefined) {
        this.documents.delete(key);
        this.notifyActiveDocumentsChanged();
        this.options.lifecycle.removeDocument(document.context);
        continue;
      }

      nextByKey.delete(key);
      const contextChanged = !contextsEqual(document.context, replacement.context);
      const sourceSetChanged = pathKey(document.sourceSetPath) !==
        pathKey(replacement.sourceSetPath);
      this.documents.set(key, replacement);
      if (contextChanged || sourceSetChanged) {
        this.notifyActiveDocumentsChanged();
      }
      if (contextChanged) {
        this.options.lifecycle.removeDocument(document.context);
        this.options.lifecycle.scheduleResolvedAutomaticRefresh(
          replacement.context,
          'manifestChanged'
        );
      }
      await this.collectAndReevaluate(replacement);
    }

    for (const document of [...nextByKey.values()].sort(compareDocuments)) {
      this.documents.set(documentKey(document), document);
      this.notifyActiveDocumentsChanged();
      this.options.lifecycle.scheduleResolvedAutomaticRefresh(
        document.context,
        'manifestChanged'
      );
      await this.collectAndReevaluate(document);
    }
  }

  private async reconcileManifestRemoval(
    canonicalManifestPath: string
  ): Promise<void> {
    const removed = [...this.documents.values()]
      .filter((document) =>
        pathKey(document.manifestPath) === pathKey(canonicalManifestPath)
      )
      .sort(compareDocuments);
    for (const document of removed) {
      this.documents.delete(documentKey(document));
      this.notifyActiveDocumentsChanged();
      this.options.lifecycle.removeDocument(document.context);
    }
  }

  private releaseManifestResolution(manifestKey: string): void {
    const fenced = this.fencedManifestContexts.get(manifestKey);
    this.fencedManifestContexts.delete(manifestKey);
    for (const context of fenced?.values() ?? []) {
      this.options.lifecycle.completeManifestResolution(context);
    }
  }

  private async collectAndReevaluate(
    document: HostClassProjectionWorkspaceDocument
  ): Promise<void> {
    try {
      const sources = await this.options.collectHostClassSources(document);
      if (this.documents.get(documentKey(document)) !== document) {
        return;
      }
      this.options.lifecycle.reevaluateSourceAssociations(
        document.context,
        sources
      );
    } catch (error) {
      await this.options.reportError?.(error);
    }
  }

  private async reconcileDiscoveredDocuments(): Promise<void> {
    const manifestPaths = distinctCanonicalPaths(
      await this.options.findProjectManifests()
    ).sort(comparePaths);
    const resolved: HostClassProjectionWorkspaceDocument[] = [];
    for (const manifestPath of manifestPaths) {
      resolved.push(...await this.resolveManifestDocuments(manifestPath));
    }

    const nextByKey = new Map(
      resolved.map((document) => [documentKey(document), document] as const)
    );
    for (const existing of [...this.documents.values()].sort(compareDocuments)) {
      const key = documentKey(existing);
      const replacement = nextByKey.get(key);
      if (replacement === undefined) {
        this.documents.delete(key);
        this.notifyActiveDocumentsChanged();
        this.options.lifecycle.removeDocument(existing.context);
        continue;
      }

      nextByKey.delete(key);
      const contextChanged = !contextsEqual(existing.context, replacement.context);
      const sourceSetChanged = pathKey(existing.sourceSetPath) !==
        pathKey(replacement.sourceSetPath);
      this.documents.set(key, replacement);
      if (contextChanged || sourceSetChanged) {
        this.notifyActiveDocumentsChanged();
      }
      if (contextChanged) {
        this.options.lifecycle.removeDocument(existing.context);
        this.options.lifecycle.activateDocument(replacement.context);
      }
      if (contextChanged || sourceSetChanged) {
        await this.collectAndReevaluate(replacement);
      }
    }

    for (const document of [...nextByKey.values()].sort(compareDocuments)) {
      this.documents.set(documentKey(document), document);
      this.notifyActiveDocumentsChanged();
      this.options.lifecycle.activateDocument(document.context);
      await this.collectAndReevaluate(document);
    }
  }

  private registeredManifestPath(value: string): string {
    const canonical = canonicalPath(value);
    const registered = [...this.documents.values()].find(
      (document) => pathKey(document.manifestPath) === pathKey(canonical)
    );
    return registered?.manifestPath ?? canonical;
  }

  private notifyActiveDocumentsChanged(): void {
    this.options.onActiveDocumentsChanged?.(this.getActiveDocuments());
  }

  private async resolveManifestDocuments(
    manifestPath: string
  ): Promise<readonly HostClassProjectionWorkspaceDocument[]> {
    const text = await this.options.readManifestText(manifestPath);
    const manifest = text === undefined
      ? undefined
      : parseProjectManifest(text);
    if (manifest === undefined) {
      return [];
    }

    const project = path.dirname(manifestPath);
    return [...manifest.documents]
      .sort((left, right) => compareText(left.name, right.name))
      .map((document) => ({
        manifestPath,
        sourceSetPath: canonicalPath(project, document.sourcePath),
        context: {
          project,
          document: document.name,
          sourceTemplate: canonicalPath(project, document.templatePath)
        }
      }));
  }
}

function canonicalPath(base: string, value?: string): string {
  return path.normalize(value === undefined
    ? path.resolve(base)
    : path.resolve(base, value));
}

function distinctCanonicalPaths(values: readonly string[]): string[] {
  const paths = new Map<string, string>();
  for (const value of values) {
    const canonical = canonicalPath(value);
    const key = pathKey(canonical);
    if (!paths.has(key)) {
      paths.set(key, canonical);
    }
  }
  return [...paths.values()];
}

function documentKey(document: HostClassProjectionWorkspaceDocument): string {
  return `${pathKey(document.manifestPath)}\u0000${document.context.document.toLowerCase()}`;
}

function contextKey(context: HostClassProjectionContext): string {
  return `${context.project.toLowerCase()}\u0000${context.document.toLowerCase()}`;
}

function contextsEqual(
  left: HostClassProjectionContext,
  right: HostClassProjectionContext
): boolean {
  return left.project === right.project &&
    left.document === right.document &&
    left.sourceTemplate === right.sourceTemplate;
}

function pathKey(value: string): string {
  return value.toLowerCase();
}

function pathContains(parent: string, candidate: string): boolean {
  const parentKey = pathKey(parent);
  const candidateKey = pathKey(candidate);
  return candidateKey === parentKey ||
    candidateKey.startsWith(`${parentKey}${path.sep}`);
}

function compareDocuments(
  left: HostClassProjectionWorkspaceDocument,
  right: HostClassProjectionWorkspaceDocument
): number {
  return comparePaths(left.manifestPath, right.manifestPath) ||
    compareText(left.context.document, right.context.document);
}

function comparePaths(left: string, right: string): number {
  return compareText(pathKey(left), pathKey(right)) || compareText(left, right);
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function scheduleWorkspaceDelay(
  delayMilliseconds: number,
  callback: () => void
): HostClassCancellationDisposable {
  const handle = setTimeout(callback, delayMilliseconds);
  return {
    dispose: () => clearTimeout(handle)
  };
}
