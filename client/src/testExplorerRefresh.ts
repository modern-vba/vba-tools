export interface DisposableLike {
  dispose(): void;
}

export interface FileSystemWatcherLike {
  onDidCreate(listener: () => Promise<void> | void): DisposableLike;
  onDidChange(listener: () => Promise<void> | void): DisposableLike;
  onDidDelete(listener: () => Promise<void> | void): DisposableLike;
}

export interface WorkbookBackedTestExplorerRefreshOptions {
  watcher: FileSystemWatcherLike;
  subscriptions: DisposableLike[];
  explorer: {
    refresh(): Promise<void>;
  };
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
}

export function registerWorkbookBackedTestExplorerRefresh(
  options: WorkbookBackedTestExplorerRefreshOptions
): void {
  const refresh = async () => {
    try {
      await options.explorer.refresh();
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
