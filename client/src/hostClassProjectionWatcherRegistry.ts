import * as path from 'node:path';

import { HostClassProjectionWorkspaceDocument } from './hostClassProjectionWorkspace';

export interface HostClassProjectionWatcherDisposable {
  dispose(): void;
}

export interface HostClassProjectionFileWatcher extends HostClassProjectionWatcherDisposable {
  onDidCreate(
    listener: (filePath: string) => void
  ): HostClassProjectionWatcherDisposable;
  onDidChange(
    listener: (filePath: string) => void
  ): HostClassProjectionWatcherDisposable;
  onDidDelete(
    listener: (filePath: string) => void
  ): HostClassProjectionWatcherDisposable;
}

export interface HostClassProjectionWatcherRegistryOptions {
  readonly createWatcher: (
    basePath: string,
    pattern: string
  ) => HostClassProjectionFileWatcher;
  readonly sourceFileChanged: (filePath: string) => void;
  readonly templateFileChanged: (filePath: string) => void;
}

interface WatchRegistration extends HostClassProjectionWatcherDisposable {
  readonly watcher: HostClassProjectionFileWatcher;
}

interface TemplateDirectoryWatchRegistration extends WatchRegistration {
  targets: ReadonlySet<string>;
}

export class HostClassProjectionWatcherRegistry
implements HostClassProjectionWatcherDisposable {
  private readonly sourceSets = new Map<string, WatchRegistration>();
  private readonly templateDirectories = new Map<
    string,
    TemplateDirectoryWatchRegistration
  >();

  public constructor(
    private readonly options: HostClassProjectionWatcherRegistryOptions
  ) {
  }

  public synchronize(
    documents: readonly HostClassProjectionWorkspaceDocument[]
  ): void {
    const ordered = [...documents].sort((left, right) =>
      comparePaths(left.sourceSetPath, right.sourceSetPath) ||
      comparePaths(left.context.sourceTemplate, right.context.sourceTemplate)
    );
    const desiredSourceSets = new Map<string, string>();
    const desiredTemplateDirectories = new Map<string, {
      readonly directoryPath: string;
      readonly targets: Set<string>;
    }>();
    for (const document of ordered) {
      const sourceSetPath = canonicalPath(document.sourceSetPath);
      desiredSourceSets.set(pathKey(sourceSetPath), sourceSetPath);

      const templatePath = canonicalPath(document.context.sourceTemplate);
      const directoryPath = path.dirname(templatePath);
      const directoryKey = pathKey(directoryPath);
      const desired = desiredTemplateDirectories.get(directoryKey) ?? {
        directoryPath,
        targets: new Set<string>()
      };
      desired.targets.add(pathKey(templatePath));
      desiredTemplateDirectories.set(directoryKey, desired);
    }

    for (const [key, sourceSetPath] of desiredSourceSets) {
      if (this.sourceSets.has(key)) {
        continue;
      }
      this.sourceSets.set(key, this.createSourceSetWatch(sourceSetPath));
    }
    for (const [key, desired] of desiredTemplateDirectories) {
      if (!this.templateDirectories.has(key)) {
        this.templateDirectories.set(
          key,
          this.createTemplateDirectoryWatch(desired.directoryPath, desired.targets)
        );
      }
    }

    for (const [key, registration] of this.sourceSets) {
      if (!desiredSourceSets.has(key)) {
        registration.dispose();
        this.sourceSets.delete(key);
      }
    }
    for (const [key, registration] of this.templateDirectories) {
      const desired = desiredTemplateDirectories.get(key);
      if (desired === undefined) {
        registration.dispose();
        this.templateDirectories.delete(key);
      } else {
        registration.targets = desired.targets;
      }
    }
  }

  public dispose(): void {
    for (const registration of this.sourceSets.values()) {
      registration.dispose();
    }
    for (const registration of this.templateDirectories.values()) {
      registration.dispose();
    }
    this.sourceSets.clear();
    this.templateDirectories.clear();
  }

  private createSourceSetWatch(sourceSetPath: string): WatchRegistration {
    return createRegistration(
      this.options.createWatcher(sourceSetPath, '**/*.{bas,cls,frm,frx}'),
      (filePath) => {
        const canonical = canonicalPath(filePath);
        if (pathContains(sourceSetPath, canonical)) {
          this.options.sourceFileChanged(canonical);
        }
      }
    );
  }

  private createTemplateDirectoryWatch(
    directoryPath: string,
    targets: ReadonlySet<string>
  ): TemplateDirectoryWatchRegistration {
    const registration = createRegistration(
      this.options.createWatcher(directoryPath, '*'),
      (filePath) => {
        const canonical = canonicalPath(filePath);
        if (registration.targets.has(pathKey(canonical))) {
          this.options.templateFileChanged(canonical);
        }
      }
    ) as TemplateDirectoryWatchRegistration;
    registration.targets = targets;
    return registration;
  }
}

function createRegistration(
  watcher: HostClassProjectionFileWatcher,
  listener: (filePath: string) => void
): WatchRegistration {
  const subscriptions = [
    watcher.onDidCreate(listener),
    watcher.onDidChange(listener),
    watcher.onDidDelete(listener)
  ];
  return {
    watcher,
    dispose: () => {
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      watcher.dispose();
    }
  };
}

function canonicalPath(value: string): string {
  return path.normalize(path.resolve(value));
}

function pathKey(value: string): string {
  return canonicalPath(value).toLowerCase();
}

function pathContains(parent: string, candidate: string): boolean {
  const parentKey = pathKey(parent);
  const candidateKey = pathKey(candidate);
  return candidateKey === parentKey ||
    candidateKey.startsWith(`${parentKey}${path.sep}`);
}

function comparePaths(left: string, right: string): number {
  const leftKey = pathKey(left);
  const rightKey = pathKey(right);
  return leftKey.localeCompare(rightKey) || left.localeCompare(right);
}
