import test from 'node:test';
import assert from 'node:assert/strict';

import {
  ReferenceQuickPick,
  ReferenceQuickPickCancellationSource,
  ReferenceQuickPickItem,
  showReferenceQuickPick
} from './referenceQuickPick';

test('ReferenceAddQuickPick shows loading immediately and returns retained names in inventory order', async () => {
  const quickPick = new TestReferenceQuickPick();
  const inventory = deferred<readonly ReferenceQuickPickItem[]>();
  const running = showReferenceQuickPick({
    title: 'VBA Tools: Add Reference — BookProject / Book2',
    createQuickPick: () => quickPick,
    createCancellationSource: () => new TestCancellationSource(),
    discover: async () => inventory.promise
  });

  assert.equal(quickPick.showCount, 1);
  assert.equal(quickPick.title, 'VBA Tools: Add Reference — BookProject / Book2');
  assert.equal(quickPick.busy, true);
  assert.equal(quickPick.enabled, false);
  assert.equal(quickPick.canSelectMany, true);
  assert.equal(quickPick.matchOnDescription, true);

  inventory.resolve([
    {
      label: 'Alpha Library',
      description: 'TypeLib 1.0',
      canonicalName: 'Alpha Library'
    },
    {
      label: 'Beta Library',
      description: 'TypeLib 2.4',
      canonicalName: 'BETA LIBRARY'
    }
  ]);
  await nextTurn();

  assert.equal(quickPick.busy, false);
  assert.equal(quickPick.enabled, true);
  quickPick.items[1]!.label = 'Rendered label changed after discovery';
  quickPick.selectedItems = [quickPick.items[1]!, quickPick.items[0]!];
  quickPick.accept();

  assert.deepEqual(await running, {
    kind: 'accepted',
    names: ['Alpha Library', 'BETA LIBRARY']
  });
  assert.equal(quickPick.hideCount, 1);
});

test('hiding ReferenceQuickPick cancels once and waits for discovery cleanup without publishing a late result', async () => {
  const quickPick = new TestReferenceQuickPick();
  const cancellation = new TestCancellationSource();
  const inventory = deferred<readonly ReferenceQuickPickItem[]>();
  const running = showReferenceQuickPick({
    title: 'VBA Tools: Add Reference — ProjectOne / Book2',
    createQuickPick: () => quickPick,
    createCancellationSource: () => cancellation,
    discover: async () => inventory.promise
  });
  let settled = false;
  void running.then(() => {
    settled = true;
  });

  quickPick.hide();
  quickPick.hide();
  await nextTurn();
  assert.equal(cancellation.cancelCount, 1);
  assert.equal(settled, false);

  inventory.resolve([{
    label: 'Late Library',
    description: 'TypeLib 1.0',
    canonicalName: 'Late Library'
  }]);

  assert.deepEqual(await running, { kind: 'cancelled' });
  assert.deepEqual(quickPick.items, []);
  assert.equal(cancellation.disposeCount, 1);
});

test('an empty ReferenceQuickPick inventory closes with a non-error result', async () => {
  const quickPick = new TestReferenceQuickPick();

  const result = await showReferenceQuickPick({
    title: 'VBA Tools: Remove Reference — ProjectOne / Book1',
    createQuickPick: () => quickPick,
    createCancellationSource: () => new TestCancellationSource(),
    discover: async () => []
  });

  assert.deepEqual(result, { kind: 'empty' });
  assert.equal(quickPick.hideCount, 1);
  assert.deepEqual(quickPick.items, []);
});

test('a failed ReferenceQuickPick discovery closes without exposing partial items', async () => {
  const quickPick = new TestReferenceQuickPick();
  const failure = new Error('inventory is untrusted');

  const result = await showReferenceQuickPick({
    title: 'VBA Tools: Add Reference — ProjectOne / Book1',
    createQuickPick: () => quickPick,
    createCancellationSource: () => new TestCancellationSource(),
    discover: async () => {
      throw failure;
    }
  });

  assert.deepEqual(result, { kind: 'failed', error: failure });
  assert.equal(quickPick.hideCount, 1);
  assert.deepEqual(quickPick.items, []);
});

test('accept winning over the resulting hide settles ReferenceQuickPick once without cancellation', async () => {
  const quickPick = new TestReferenceQuickPick();
  const cancellation = new TestCancellationSource();
  const running = showReferenceQuickPick({
    title: 'VBA Tools: Remove Reference — ProjectOne / Book1',
    createQuickPick: () => quickPick,
    createCancellationSource: () => cancellation,
    discover: async () => [{
      label: 'Broken Library',
      canonicalName: 'Broken Library'
    }]
  });
  await nextTurn();
  quickPick.selectedItems = [quickPick.items[0]!];

  quickPick.accept();
  quickPick.hide();

  assert.deepEqual(await running, {
    kind: 'accepted',
    names: ['Broken Library']
  });
  assert.equal(cancellation.cancelCount, 0);
  assert.equal(quickPick.disposeCount, 1);
});

test('a discovery rejection after ReferenceQuickPick is hidden remains silent', async () => {
  const quickPick = new TestReferenceQuickPick();
  const inventory = deferred<readonly ReferenceQuickPickItem[]>();
  const running = showReferenceQuickPick({
    title: 'VBA Tools: Add Reference — ProjectOne / Book1',
    createQuickPick: () => quickPick,
    createCancellationSource: () => new TestCancellationSource(),
    discover: async () => inventory.promise
  });

  quickPick.hide();
  inventory.reject(new Error('late discovery failure'));

  assert.deepEqual(await running, { kind: 'cancelled' });
  assert.deepEqual(quickPick.items, []);
});

class TestReferenceQuickPick implements ReferenceQuickPick {
  public title: string | undefined;
  public busy = false;
  public enabled = true;
  public canSelectMany = false;
  public matchOnDescription = false;
  public items: readonly ReferenceQuickPickItem[] = [];
  public selectedItems: readonly ReferenceQuickPickItem[] = [];
  public showCount = 0;
  public hideCount = 0;
  public disposeCount = 0;
  private readonly acceptListeners: Array<() => void> = [];
  private readonly hideListeners: Array<() => void> = [];

  public onDidAccept(listener: () => void) {
    this.acceptListeners.push(listener);
    return { dispose: () => undefined };
  }

  public onDidHide(listener: () => void) {
    this.hideListeners.push(listener);
    return { dispose: () => undefined };
  }

  public show(): void {
    this.showCount += 1;
  }

  public hide(): void {
    this.hideCount += 1;
    for (const listener of this.hideListeners) {
      listener();
    }
  }

  public dispose(): void {
    this.disposeCount += 1;
  }

  public accept(): void {
    for (const listener of this.acceptListeners) {
      listener();
    }
  }
}

class TestCancellationSource implements ReferenceQuickPickCancellationSource {
  public cancelCount = 0;
  public disposeCount = 0;
  public readonly token: ReferenceQuickPickCancellationSource['token'] = {
    get isCancellationRequested() {
      return false;
    },
    onCancellationRequested: () => ({ dispose: () => undefined })
  };

  public cancel(): void {
    this.cancelCount += 1;
  }

  public dispose(): void {
    this.disposeCount += 1;
  }
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T): void;
  reject(error: unknown): void;
} {
  let resolve: ((value: T) => void) | undefined;
  let reject: ((error: unknown) => void) | undefined;
  const promise = new Promise<T>((accept, fail) => {
    resolve = accept;
    reject = fail;
  });
  return {
    promise,
    resolve: (value) => resolve?.(value),
    reject: (error) => reject?.(error)
  };
}

async function nextTurn(): Promise<void> {
  await new Promise<void>((resolve) => setImmediate(resolve));
}
