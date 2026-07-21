export interface DisposableLike {
  dispose(): void;
}

export interface PathFileSystemWatcherLike {
  onDidCreate(listener: (sourcePath: string) => void): DisposableLike;
  onDidChange(listener: (sourcePath: string) => void): DisposableLike;
  onDidDelete(listener: (sourcePath: string) => void): DisposableLike;
}

export interface ChangedTextDocument {
  uriPath: string;
}

export interface WorkbookBackedTestExplorerSourceInvalidationOptions {
  sourceWatcher: PathFileSystemWatcherLike;
  onDidChangeTextDocument(
    listener: (document: ChangedTextDocument) => void
  ): DisposableLike;
  subscriptions: DisposableLike[];
  explorer: {
    invalidateSourcePath(sourcePath: string): void;
    invalidateFileSystemSourceChange(sourcePath: string): void;
  };
}

export function registerWorkbookBackedTestExplorerSourceInvalidation(
  options: WorkbookBackedTestExplorerSourceInvalidationOptions
): void {
  const invalidate = (sourcePath: string) => {
    options.explorer.invalidateSourcePath(sourcePath);
  };

  options.subscriptions.push(
    options.sourceWatcher.onDidCreate(invalidate),
    options.sourceWatcher.onDidChange((sourcePath) => {
      options.explorer.invalidateFileSystemSourceChange(sourcePath);
    }),
    options.sourceWatcher.onDidDelete(invalidate),
    options.onDidChangeTextDocument((document) => {
      invalidate(document.uriPath);
    })
  );
}
