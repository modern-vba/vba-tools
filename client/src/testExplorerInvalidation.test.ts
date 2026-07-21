import test from 'node:test';
import assert from 'node:assert/strict';

import {
  PathFileSystemWatcherLike,
  registerWorkbookBackedTestExplorerSourceInvalidation
} from './testExplorerInvalidation';

test('VBA editor content changes invalidate their source snapshots immediately', () => {
  const sourceWatcher = new FakePathFileSystemWatcher();
  const subscriptions: FakeDisposable[] = [];
  const invalidated: string[] = [];
  let textDocumentChanged: ((document: { uriPath: string }) => void) | undefined;
  registerWorkbookBackedTestExplorerSourceInvalidation({
    sourceWatcher,
    onDidChangeTextDocument: (listener) => {
      textDocumentChanged = listener;
      return new FakeDisposable();
    },
    subscriptions,
    explorer: {
      invalidateSourcePath: (sourcePath) => invalidated.push(sourcePath),
      invalidateFileSystemSourceChange: () => undefined
    }
  });

  textDocumentChanged?.({ uriPath: 'C:\\work\\src\\Book1\\Test_Module.bas' });
  textDocumentChanged?.({ uriPath: 'C:\\work\\src\\Book1\\Clean.bas' });

  assert.deepEqual(invalidated, [
    'C:\\work\\src\\Book1\\Test_Module.bas',
    'C:\\work\\src\\Book1\\Clean.bas'
  ]);
  assert.equal(subscriptions.length, 4);
});

test('saved VBA source watcher events invalidate their document snapshots', () => {
  const sourceWatcher = new FakePathFileSystemWatcher();
  const invalidated: string[] = [];
  const fileSystemChanges: string[] = [];
  registerWorkbookBackedTestExplorerSourceInvalidation({
    sourceWatcher,
    onDidChangeTextDocument: () => new FakeDisposable(),
    subscriptions: [],
    explorer: {
      invalidateSourcePath: (sourcePath) => invalidated.push(sourcePath),
      invalidateFileSystemSourceChange: (sourcePath) => fileSystemChanges.push(sourcePath)
    }
  });

  sourceWatcher.fireCreate('C:\\work\\src\\Book1\\Created.bas');
  sourceWatcher.fireChange('C:\\work\\src\\Book1\\Changed.cls');
  sourceWatcher.fireDelete('C:\\work\\src\\Book1\\Deleted.frm');

  assert.deepEqual(invalidated, [
    'C:\\work\\src\\Book1\\Created.bas',
    'C:\\work\\src\\Book1\\Deleted.frm'
  ]);
  assert.deepEqual(fileSystemChanges, ['C:\\work\\src\\Book1\\Changed.cls']);
});

class FakePathFileSystemWatcher implements PathFileSystemWatcherLike {
  private createListeners: Array<(sourcePath: string) => void> = [];
  private changeListeners: Array<(sourcePath: string) => void> = [];
  private deleteListeners: Array<(sourcePath: string) => void> = [];

  public onDidCreate(listener: (sourcePath: string) => void): FakeDisposable {
    this.createListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidChange(listener: (sourcePath: string) => void): FakeDisposable {
    this.changeListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidDelete(listener: (sourcePath: string) => void): FakeDisposable {
    this.deleteListeners.push(listener);
    return new FakeDisposable();
  }

  public fireCreate(sourcePath: string): void {
    this.createListeners.forEach((listener) => listener(sourcePath));
  }

  public fireChange(sourcePath: string): void {
    this.changeListeners.forEach((listener) => listener(sourcePath));
  }

  public fireDelete(sourcePath: string): void {
    this.deleteListeners.forEach((listener) => listener(sourcePath));
  }
}

class FakeDisposable {
  public dispose(): void {
  }
}
