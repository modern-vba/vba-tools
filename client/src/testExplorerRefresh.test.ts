import test from 'node:test';
import assert from 'node:assert/strict';

import {
  registerWorkbookBackedTestExplorerRefresh,
  FileSystemWatcherLike
} from './testExplorerRefresh';

test('Test Explorer refreshes only the affected project definition for each manifest event', async () => {
  const watcher = new FakeFileSystemWatcher();
  const subscriptions: FakeDisposable[] = [];
  const refreshed: string[] = [];

  registerWorkbookBackedTestExplorerRefresh({
    watcher,
    subscriptions,
    explorer: {
      refreshProjectDefinition: async (manifestPath) => {
        refreshed.push(manifestPath);
      }
    },
    showErrorMessage: async () => undefined
  });

  assert.equal(subscriptions.length, 3);

  await watcher.fireCreate('C:\\work\\Created\\vba-project.json');
  await watcher.fireChange('C:\\work\\Changed\\vba-project.json');
  await watcher.fireDelete('C:\\work\\Deleted\\vba-project.json');

  assert.deepEqual(refreshed, [
    'C:\\work\\Created\\vba-project.json',
    'C:\\work\\Changed\\vba-project.json',
    'C:\\work\\Deleted\\vba-project.json'
  ]);
});

test('Test Explorer refresh errors are surfaced without rejecting the file watcher event', async () => {
  const watcher = new FakeFileSystemWatcher();
  const errors: string[] = [];

  registerWorkbookBackedTestExplorerRefresh({
    watcher,
    subscriptions: [],
    explorer: {
      refreshProjectDefinition: async () => {
        throw new Error('manifest could not be read');
      }
    },
    showErrorMessage: async (message) => {
      errors.push(message);
    }
  });

  await watcher.fireCreate('C:\\work\\Project\\vba-project.json');

  assert.deepEqual(errors, ['VBA Tools could not refresh Test Explorer: manifest could not be read']);
});

class FakeFileSystemWatcher implements FileSystemWatcherLike {
  private createListeners: Array<(manifestPath: string) => Promise<void> | void> = [];
  private changeListeners: Array<(manifestPath: string) => Promise<void> | void> = [];
  private deleteListeners: Array<(manifestPath: string) => Promise<void> | void> = [];

  public onDidCreate(listener: (manifestPath: string) => Promise<void> | void): FakeDisposable {
    this.createListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidChange(listener: (manifestPath: string) => Promise<void> | void): FakeDisposable {
    this.changeListeners.push(listener);
    return new FakeDisposable();
  }

  public onDidDelete(listener: (manifestPath: string) => Promise<void> | void): FakeDisposable {
    this.deleteListeners.push(listener);
    return new FakeDisposable();
  }

  public async fireCreate(manifestPath: string): Promise<void> {
    await Promise.all(this.createListeners.map((listener) => listener(manifestPath)));
  }

  public async fireChange(manifestPath: string): Promise<void> {
    await Promise.all(this.changeListeners.map((listener) => listener(manifestPath)));
  }

  public async fireDelete(manifestPath: string): Promise<void> {
    await Promise.all(this.deleteListeners.map((listener) => listener(manifestPath)));
  }
}

class FakeDisposable {
  public dispose(): void {
  }
}
