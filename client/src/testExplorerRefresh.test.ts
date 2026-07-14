import test from 'node:test';
import assert from 'node:assert/strict';

import {
  registerWorkbookBackedTestExplorerRefresh,
  FileSystemWatcherLike
} from './testExplorerRefresh';

test('Test Explorer refreshes when a project manifest is created changed or deleted', async () => {
  const watcher = new FakeFileSystemWatcher();
  const subscriptions: FakeDisposable[] = [];
  let refreshCount = 0;

  registerWorkbookBackedTestExplorerRefresh({
    watcher,
    subscriptions,
    explorer: {
      refresh: async () => {
        refreshCount += 1;
      }
    },
    showErrorMessage: async () => undefined
  });

  assert.equal(subscriptions.length, 3);

  await watcher.fireCreate();
  await watcher.fireChange();
  await watcher.fireDelete();

  assert.equal(refreshCount, 3);
});

test('Test Explorer refresh errors are surfaced without rejecting the file watcher event', async () => {
  const watcher = new FakeFileSystemWatcher();
  const errors: string[] = [];

  registerWorkbookBackedTestExplorerRefresh({
    watcher,
    subscriptions: [],
    explorer: {
      refresh: async () => {
        throw new Error('manifest could not be read');
      }
    },
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  await watcher.fireCreate();

  assert.deepEqual(errors, ['VBA Tools could not refresh Test Explorer: manifest could not be read']);
});

class FakeFileSystemWatcher implements FileSystemWatcherLike {
  private createListeners: Array<() => Promise<void> | void> = [];
  private changeListeners: Array<() => Promise<void> | void> = [];
  private deleteListeners: Array<() => Promise<void> | void> = [];

  public onDidCreate(listener: () => Promise<void> | void): FakeDisposable {
    this.createListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidChange(listener: () => Promise<void> | void): FakeDisposable {
    this.changeListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidDelete(listener: () => Promise<void> | void): FakeDisposable {
    this.deleteListeners.push(listener);
    return new FakeDisposable();
  }

  public async fireCreate(): Promise<void> {
    await Promise.all(this.createListeners.map((listener) => listener()));
  }

  public async fireChange(): Promise<void> {
    await Promise.all(this.changeListeners.map((listener) => listener()));
  }

  public async fireDelete(): Promise<void> {
    await Promise.all(this.deleteListeners.map((listener) => listener()));
  }
}

class FakeDisposable {
  public dispose(): void {
  }
}
