import assert from 'node:assert/strict';
import { CancellationTokenSource, window } from 'vscode';

import {
  ReferenceQuickPickItem,
  showReferenceQuickPick
} from '../../referenceQuickPick';

export async function runReferenceQuickPickIntegrationTests(): Promise<void> {
  await runTest(
    'a hidden real Reference QuickPick waits for cooperative discovery cleanup and stays cancelled',
    async () => {
      const quickPick = window.createQuickPick<ReferenceQuickPickItem>();
      const cancellation = new CancellationTokenSource();
      const discoveryStarted = deferred<void>();
      const cancellationObserved = deferred<void>();
      const cleanupGate = deferred<void>();
      let cancellationEvents = 0;
      let cleanupCompleted = false;
      let hideEvents = 0;
      let mutationStarts = 0;
      const hideSubscription = quickPick.onDidHide(() => {
        hideEvents += 1;
      });
      const running = showReferenceQuickPick({
        title: 'VBA Tools: Add Reference — IntegrationProject / Book1',
        createQuickPick: () => quickPick,
        createCancellationSource: () => cancellation,
        discover: async (token) => {
          discoveryStarted.resolve();
          await new Promise<void>((resolve) => {
            const subscription = token.onCancellationRequested(() => {
              cancellationEvents += 1;
              cancellationObserved.resolve();
              void cleanupGate.promise.then(() => {
                cleanupCompleted = true;
                subscription.dispose();
                resolve();
              });
            });
          });
          return [{
            label: 'Late Library',
            description: 'TypeLib 1.0',
            canonicalName: 'Late Library'
          }];
        }
      });
      const mutationOutcome = running.then((result) => {
        if (result.kind === 'accepted') {
          mutationStarts += 1;
        }
        return result;
      });

      try {
        assert.equal(quickPick.title, 'VBA Tools: Add Reference — IntegrationProject / Book1');
        assert.equal(quickPick.busy, true);
        assert.equal(quickPick.enabled, false);
        assert.equal(quickPick.canSelectMany, true);
        assert.equal(quickPick.matchOnDescription, true);
        assert.deepEqual(quickPick.items, []);

        await withTimeout(
          discoveryStarted.promise,
          1_000,
          'Reference discovery did not start.'
        );
        let settled = false;
        void mutationOutcome.then(
          () => {
            settled = true;
          },
          () => {
            settled = true;
          }
        );

        quickPick.hide();
        quickPick.hide();
        await withTimeout(
          cancellationObserved.promise,
          1_000,
          'Hiding the Reference QuickPick did not cancel discovery.'
        );
        await delay(25);

        assert.equal(cancellationEvents, 1);
        assert.equal(hideEvents, 1);
        assert.equal(cleanupCompleted, false);
        assert.equal(settled, false);
        assert.deepEqual(quickPick.items, []);
        assert.equal(mutationStarts, 0);

        cleanupGate.resolve();
        assert.deepEqual(
          await withTimeout(
            mutationOutcome,
            1_000,
            'Reference QuickPick did not settle after discovery cleanup.'
          ),
          { kind: 'cancelled' }
        );
        await delay(25);

        assert.equal(cleanupCompleted, true);
        assert.equal(cancellationEvents, 1);
        assert.equal(hideEvents, 1);
        assert.deepEqual(quickPick.items, []);
        assert.equal(mutationStarts, 0);
      } finally {
        cleanupGate.resolve();
        quickPick.hide();
        hideSubscription.dispose();
      }
    }
  );
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
} {
  let resolve: ((value: T | PromiseLike<T>) => void) | undefined;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return {
    promise,
    resolve: (value) => resolve?.(value)
  };
}

function withTimeout<T>(
  promise: Promise<T>,
  timeoutMilliseconds: number,
  message: string
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error(message)), timeoutMilliseconds);
    void promise.then(
      (value) => {
        clearTimeout(timeout);
        resolve(value);
      },
      (error: unknown) => {
        clearTimeout(timeout);
        reject(error);
      }
    );
  });
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
