import test from 'node:test';
import assert from 'node:assert/strict';

import {
  IntrinsicHostEventCatalogRefreshHandle,
  IntrinsicHostEventCatalogRefreshOutcome
} from './intrinsicHostEventCatalogLifecycle';
import {
  IntrinsicHostEventCatalogShowOutputAction,
  runIntrinsicHostEventCatalogRefreshCommand
} from './intrinsicHostEventCatalogRefreshCommand';

test('explicit UserForm Event refresh uses cancellable environment progress without chooser', async () => {
  let cancelRefresh!: () => void;
  let resolveRefresh!: (outcome: IntrinsicHostEventCatalogRefreshOutcome) => void;
  const refresh: IntrinsicHostEventCatalogRefreshHandle = {
    completion: new Promise((resolve) => {
      resolveRefresh = resolve;
    }),
    cancel: () => cancelRefresh()
  };
  const listeners: Array<() => void> = [];
  let cancelled = false;
  cancelRefresh = () => {
    cancelled = true;
    resolveRefresh({ status: 'cancelled' });
  };
  let progressTitle = '';

  await runIntrinsicHostEventCatalogRefreshCommand({
    refreshCatalog: () => refresh,
    runWithCancellableProgress: async (title, task) => {
      progressTitle = title;
      await task({
        isCancellationRequested: false,
        onCancellationRequested: (listener) => {
          listeners.push(listener);
          queueMicrotask(listener);
          return { dispose: () => undefined };
        }
      });
    },
    showErrorMessage: async () => undefined,
    showOutput: () => undefined
  });

  assert.equal(progressTitle, 'VBA Tools: Refresh UserForm Events');
  assert.equal(cancelled, true);
});

test('failed explicit refresh offers one Output action', async () => {
  const messages: string[] = [];
  let outputShown = false;
  await runIntrinsicHostEventCatalogRefreshCommand({
    refreshCatalog: () => ({
      completion: Promise.resolve({
        status: 'failed',
        reason: 'commandFailed',
        exitCode: 1
      }),
      cancel: () => undefined
    }),
    runWithCancellableProgress: async (_title, task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    showErrorMessage: async (message) => {
      messages.push(message);
      return IntrinsicHostEventCatalogShowOutputAction;
    },
    showOutput: () => {
      outputShown = true;
    }
  });

  assert.deepEqual(messages, [
    'UserForm Events refresh failed. See VBA Tools Output for details.'
  ]);
  assert.equal(outputShown, true);
});
