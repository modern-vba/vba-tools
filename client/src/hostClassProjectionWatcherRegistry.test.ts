import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  HostClassProjectionFileWatcher,
  HostClassProjectionWatcherRegistry
} from './hostClassProjectionWatcherRegistry';
import { HostClassProjectionWorkspaceDocument } from './hostClassProjectionWorkspace';

test('HostClass selected-path watchers route exact templates and active source sets', () => {
  const created: RecordingWatcher[] = [];
  const sourceEvents: string[] = [];
  const templateEvents: string[] = [];
  const registry = new HostClassProjectionWatcherRegistry({
    createWatcher: (basePath, pattern) => {
      const watcher = new RecordingWatcher(basePath, pattern);
      created.push(watcher);
      return watcher;
    },
    sourceFileChanged: (filePath) => sourceEvents.push(filePath),
    templateFileChanged: (filePath) => templateEvents.push(filePath)
  });
  const project = path.resolve('external', 'Project');
  const first = document(project, 'Book1');
  const second = document(project, 'Book2');

  registry.synchronize([first, second]);

  assert.deepEqual(created.map((watcher) => ({
    basePath: watcher.basePath,
    pattern: watcher.pattern
  })), [
    {
      basePath: first.sourceSetPath,
      pattern: '**/*.{bas,cls,frm,frx}'
    },
    {
      basePath: second.sourceSetPath,
      pattern: '**/*.{bas,cls,frm,frx}'
    },
    {
      basePath: path.dirname(first.context.sourceTemplate),
      pattern: '*'
    }
  ]);

  const formPath = path.join(first.sourceSetPath, 'InvoiceForm.frm');
  const sidecarPath = path.join(first.sourceSetPath, 'InvoiceForm.frx');
  created[0]?.emitCreate(formPath);
  created[0]?.emitChange(sidecarPath);
  created[0]?.emitDelete(formPath);
  created[2]?.emitChange(path.join(
    path.dirname(first.context.sourceTemplate),
    'Unselected.xlsm'
  ));
  created[2]?.emitCreate(first.context.sourceTemplate);
  created[2]?.emitChange(first.context.sourceTemplate);
  created[2]?.emitDelete(first.context.sourceTemplate);

  assert.deepEqual(sourceEvents, [formPath, sidecarPath, formPath]);
  assert.deepEqual(templateEvents, [
    first.context.sourceTemplate,
    first.context.sourceTemplate,
    first.context.sourceTemplate
  ]);

  registry.synchronize([second]);
  assert.equal(created[0]?.disposed, true);
  created[2]?.emitCreate(first.context.sourceTemplate);
  assert.equal(templateEvents.length, 3);

  registry.dispose();
  assert.equal(created.every((watcher) => watcher.disposed), true);
  created[1]?.emitChange(path.join(second.sourceSetPath, 'AfterShutdown.frm'));
  created[2]?.emitChange(second.context.sourceTemplate);
  assert.equal(sourceEvents.length, 3);
  assert.equal(templateEvents.length, 3);
});

test('HostClass watcher replacement establishes new paths before disposing stale paths', () => {
  const actions: string[] = [];
  const registry = new HostClassProjectionWatcherRegistry({
    createWatcher: (basePath, pattern) => {
      actions.push(`create:${basePath}:${pattern}`);
      return new RecordingWatcher(
        basePath,
        pattern,
        () => actions.push(`dispose:${basePath}:${pattern}`)
      );
    },
    sourceFileChanged: () => undefined,
    templateFileChanged: () => undefined
  });
  const first = document(path.resolve('external', 'First'), 'Book1');
  const replacement = document(path.resolve('external', 'Replacement'), 'Book1');
  registry.synchronize([first]);
  actions.length = 0;

  registry.synchronize([replacement]);

  const firstDisposal = actions.findIndex((action) => action.startsWith('dispose:'));
  assert.equal(firstDisposal, 2);
  assert.equal(actions[0]?.startsWith(`create:${replacement.sourceSetPath}:`), true);
  assert.equal(actions[1]?.startsWith(
    `create:${path.dirname(replacement.context.sourceTemplate)}:`
  ), true);
});

class RecordingWatcher implements HostClassProjectionFileWatcher {
  public readonly creates: Array<(filePath: string) => void> = [];
  public readonly changes: Array<(filePath: string) => void> = [];
  public readonly deletes: Array<(filePath: string) => void> = [];
  public disposed = false;

  public constructor(
    public readonly basePath: string,
    public readonly pattern: string,
    private readonly onDispose: () => void = () => undefined
  ) {
  }

  public onDidCreate(listener: (filePath: string) => void): { dispose(): void } {
    return this.add(this.creates, listener);
  }

  public onDidChange(listener: (filePath: string) => void): { dispose(): void } {
    return this.add(this.changes, listener);
  }

  public onDidDelete(listener: (filePath: string) => void): { dispose(): void } {
    return this.add(this.deletes, listener);
  }

  public dispose(): void {
    this.disposed = true;
    this.onDispose();
  }

  public emitCreate(filePath: string): void {
    for (const listener of this.creates) {
      listener(filePath);
    }
  }

  public emitChange(filePath: string): void {
    for (const listener of this.changes) {
      listener(filePath);
    }
  }

  public emitDelete(filePath: string): void {
    for (const listener of this.deletes) {
      listener(filePath);
    }
  }

  private add(
    listeners: Array<(filePath: string) => void>,
    listener: (filePath: string) => void
  ): { dispose(): void } {
    listeners.push(listener);
    return {
      dispose: () => {
        const index = listeners.indexOf(listener);
        if (index >= 0) {
          listeners.splice(index, 1);
        }
      }
    };
  }
}

function document(
  project: string,
  name: string
): HostClassProjectionWorkspaceDocument {
  return {
    manifestPath: path.join(project, 'vba-project.json'),
    sourceSetPath: path.join(project, 'src', name),
    context: {
      project,
      document: name,
      sourceTemplate: path.join(project, 'templates', `${name}.xlsm`)
    }
  };
}
