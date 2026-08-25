import test from 'node:test';
import assert from 'node:assert/strict';

import {
  runHostClassProjectionRefreshCommand
} from './hostClassProjectionRefreshCommand';
import {
  HostClassExplicitRefreshHandle,
  HostClassProjectionContext
} from './hostClassProjectionLifecycle';

test('HostClass explicit refresh selects one document and keeps clean success silent', async () => {
  const context: HostClassProjectionContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const refreshed: HostClassProjectionContext[] = [];
  const warnings: string[] = [];
  const errors: string[] = [];
  let progressTitle = '';

  await runHostClassProjectionRefreshCommand({
    getActiveDocuments: () => [{ context }],
    chooseDocument: async (documents) => documents[0],
    refreshDocument: (selected) => {
      refreshed.push(selected);
      return completedRefresh({
        status: 'succeeded',
        revision: 1,
        associationFailureCount: 0
      });
    },
    runWithCancellableProgress: async (title, task) => {
      progressTitle = title;
      await task({
        isCancellationRequested: false,
        onCancellationRequested: () => ({ dispose: () => undefined })
      });
    },
    showWarningMessage: async (message) => {
      warnings.push(message);
      return undefined;
    },
    showErrorMessage: async (message) => {
      errors.push(message);
      return undefined;
    },
    showOutput: () => undefined
  });

  assert.deepEqual(refreshed, [context]);
  assert.equal(progressTitle, 'VBA Tools: Refresh Host Events — Book1');
  assert.deepEqual(warnings, []);
  assert.deepEqual(errors, []);
});

test('HostClass explicit refresh does not reactivate a selection removed while the chooser is open', async () => {
  const context: HostClassProjectionContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  let activeDocumentReads = 0;
  const refreshed: HostClassProjectionContext[] = [];
  const messages: string[] = [];

  await runHostClassProjectionRefreshCommand({
    getActiveDocuments: () => {
      activeDocumentReads += 1;
      return activeDocumentReads === 1 ? [{ context }] : [];
    },
    chooseDocument: async (documents) => documents[0],
    refreshDocument: (selected) => {
      refreshed.push(selected);
      return completedRefresh({
        status: 'succeeded',
        revision: 1,
        associationFailureCount: 0
      });
    },
    runWithCancellableProgress: async (_title, task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    showWarningMessage: async (message) => {
      messages.push(message);
      return undefined;
    },
    showErrorMessage: async (message) => {
      messages.push(message);
      return undefined;
    },
    showOutput: () => undefined
  });

  assert.equal(activeDocumentReads, 2);
  assert.deepEqual(refreshed, []);
  assert.deepEqual(messages, []);
});

test('HostClass explicit refresh warns once and opens Output only when requested', async () => {
  const context: HostClassProjectionContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const warnings: Array<{ message: string; action: string }> = [];
  let outputShows = 0;

  await runHostClassProjectionRefreshCommand({
    getActiveDocuments: () => [{ context }],
    chooseDocument: async (documents) => documents[0],
    refreshDocument: () => completedRefresh({
      status: 'succeeded',
      revision: 1,
      associationFailureCount: 2
    }),
    runWithCancellableProgress: async (_title, task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    showWarningMessage: async (message, action) => {
      warnings.push({ message, action });
      return action;
    },
    showErrorMessage: async () => undefined,
    showOutput: () => {
      outputShows += 1;
    }
  });

  assert.deepEqual(warnings, [{
    message: 'Host Events refreshed, but 2 source module(s) could not be associated.',
    action: 'Show Output'
  }]);
  assert.equal(outputShows, 1);
});

test('HostClass explicit refresh reports one failure action without revealing Output automatically', async () => {
  const context: HostClassProjectionContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  const errors: Array<{ message: string; action: string }> = [];
  let outputShows = 0;

  await runHostClassProjectionRefreshCommand({
    getActiveDocuments: () => [{ context }],
    chooseDocument: async (documents) => documents[0],
    refreshDocument: () => completedRefresh({
      status: 'failed',
      reason: 'commandFailed',
      exitCode: 2
    }),
    runWithCancellableProgress: async (_title, task) => task({
      isCancellationRequested: false,
      onCancellationRequested: () => ({ dispose: () => undefined })
    }),
    showWarningMessage: async () => undefined,
    showErrorMessage: async (message, action) => {
      errors.push({ message, action });
      return undefined;
    },
    showOutput: () => {
      outputShows += 1;
    }
  });

  assert.deepEqual(errors, [{
    message: 'Host Events refresh failed. See VBA Tools Output for details.',
    action: 'Show Output'
  }]);
  assert.equal(outputShows, 0);
});

test('HostClass explicit progress cancellation cancels only its refresh and stays silent', async () => {
  const context: HostClassProjectionContext = {
    project: String.raw`C:\work\Invoices`,
    document: 'Book1',
    sourceTemplate: String.raw`C:\work\Invoices\templates\Book1.xlsm`
  };
  let cancellationListener: (() => void) | undefined;
  let cancelCalls = 0;
  let complete: ((outcome: { status: 'cancelled' }) => void) | undefined;
  const completion = new Promise<{ status: 'cancelled' }>((resolve) => {
    complete = resolve;
  });
  const messages: string[] = [];

  await runHostClassProjectionRefreshCommand({
    getActiveDocuments: () => [{ context }],
    chooseDocument: async (documents) => documents[0],
    refreshDocument: () => ({
      completion,
      cancel: () => {
        cancelCalls += 1;
        complete?.({ status: 'cancelled' });
      }
    }),
    runWithCancellableProgress: async (_title, task) => {
      const pending = task({
        isCancellationRequested: false,
        onCancellationRequested: (listener) => {
          cancellationListener = listener;
          return { dispose: () => undefined };
        }
      });
      await Promise.resolve();
      cancellationListener?.();
      await pending;
    },
    showWarningMessage: async (message) => {
      messages.push(message);
      return undefined;
    },
    showErrorMessage: async (message) => {
      messages.push(message);
      return undefined;
    },
    showOutput: () => undefined
  });

  assert.equal(cancelCalls, 1);
  assert.deepEqual(messages, []);
});

function completedRefresh(
  outcome: Awaited<HostClassExplicitRefreshHandle['completion']>
): HostClassExplicitRefreshHandle {
  return {
    completion: Promise.resolve(outcome),
    cancel: () => undefined
  };
}
