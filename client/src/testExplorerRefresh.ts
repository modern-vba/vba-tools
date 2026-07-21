export interface DisposableLike {
  dispose(): void;
}

export interface FileSystemWatcherLike {
  onDidCreate(listener: (manifestPath: string) => Promise<void> | void): DisposableLike;
  onDidChange(listener: (manifestPath: string) => Promise<void> | void): DisposableLike;
  onDidDelete(listener: (manifestPath: string) => Promise<void> | void): DisposableLike;
}

export interface WorkbookBackedTestExplorerRefreshOptions {
  watcher: FileSystemWatcherLike;
  subscriptions: DisposableLike[];
  explorer: {
    refreshProjectDefinition(manifestPath: string): Promise<void>;
  };
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
}

export function registerWorkbookBackedTestExplorerRefresh(
  options: WorkbookBackedTestExplorerRefreshOptions
): void {
  const refresh = async (manifestPath: string) => {
    try {
      await options.explorer.refreshProjectDefinition(manifestPath);
    } catch (error) {
      await options.showErrorMessage(`VBA Tools could not refresh Test Explorer: ${error instanceof Error ? error.message : String(error)}`);
    }
  };

  options.subscriptions.push(
    options.watcher.onDidCreate(refresh),
    options.watcher.onDidChange(refresh),
    options.watcher.onDidDelete(refresh)
  );
}
