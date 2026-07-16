import test from 'node:test';
import assert from 'node:assert/strict';
import type { CancellationToken } from 'vscode';

import {
  BlockSkeletonInsertionPlan,
  createLanguageClientBlockSkeletonInsertionPlanProvider,
  formatBlockSkeletonInsertionFallbackTrace,
  resolveBlockSkeletonInsertionOptions
} from './blockSkeletonInsertion';

test('fallback reasons are formatted only for verbose language-server trace', () => {
  for (const reason of [
    'declined',
    'failed',
    'cancelled',
    'timed-out',
    'stale',
    'apply-failed'
  ] as const) {
    assert.equal(
      formatBlockSkeletonInsertionFallbackTrace(reason, 'verbose'),
      `Block skeleton insertion used native Enter fallback: ${reason}.`
    );
    assert.equal(formatBlockSkeletonInsertionFallbackTrace(reason, 'messages'), undefined);
    assert.equal(formatBlockSkeletonInsertionFallbackTrace(reason, 'off'), undefined);
  }

  assert.equal(formatBlockSkeletonInsertionFallbackTrace('applied', 'verbose'), undefined);
});

test('production plan provider sends the custom request and propagates cancellation', async () => {
  const request = {
    documentUri: 'file:///C:/work/Module1.bas',
    documentVersion: 7,
    position: { line: 0, character: 16 },
    options: { insertSpaces: true, indentSize: 2, tabSize: 4 }
  };
  const token = {} as CancellationToken;
  let sentMethod: string | undefined;
  let sentRequest: unknown;
  let sentToken: CancellationToken | undefined;
  let resolveResponse: ((plan: BlockSkeletonInsertionPlan | null) => void) | undefined;
  let cancellationCount = 0;
  let disposeCount = 0;
  const response = new Promise<BlockSkeletonInsertionPlan | null>((resolve) => {
    resolveResponse = resolve;
  });
  const provider = createLanguageClientBlockSkeletonInsertionPlanProvider(
    {
      sendRequest: (method, parameters, cancellationToken) => {
        sentMethod = method;
        sentRequest = parameters;
        sentToken = cancellationToken;
        return response;
      }
    },
    () => ({
      token,
      cancel: () => {
        cancellationCount++;
      },
      dispose: () => {
        disposeCount++;
      }
    })
  );

  const pending = provider(request);
  pending.cancel();
  resolveResponse?.(null);
  await pending.response;

  assert.equal(sentMethod, 'vba/blockSkeletonInsertion');
  assert.strictEqual(sentRequest, request);
  assert.strictEqual(sentToken, token);
  assert.equal(cancellationCount, 1);
  assert.equal(disposeCount, 1);
});

test('production plan provider disposes a normally completed request without cancelling it', async () => {
  const request = {
    documentUri: 'file:///C:/work/Module1.bas',
    documentVersion: 7,
    position: { line: 0, character: 16 },
    options: { insertSpaces: true, tabSize: 4 }
  };
  let cancellationCount = 0;
  let disposeCount = 0;
  const provider = createLanguageClientBlockSkeletonInsertionPlanProvider(
    {
      sendRequest: async () => null
    },
    () => ({
      token: {} as CancellationToken,
      cancel: () => {
        cancellationCount++;
      },
      dispose: () => {
        disposeCount++;
      }
    })
  );

  const pending = provider(request);
  assert.equal(await pending.response, null);
  assert.equal(cancellationCount, 0);
  assert.equal(disposeCount, 1);
});

test('resolved editor indentation options prefer numeric indentSize and preserve tabSize fallback', () => {
  assert.deepEqual(
    resolveBlockSkeletonInsertionOptions({
      insertSpaces: true,
      tabSize: 4,
      indentSize: 2
    }),
    { insertSpaces: true, tabSize: 4, indentSize: 2 }
  );
  assert.deepEqual(
    resolveBlockSkeletonInsertionOptions({
      insertSpaces: false,
      tabSize: 8,
      indentSize: 'tabSize'
    }),
    { insertSpaces: false, tabSize: 8 }
  );
  assert.deepEqual(
    resolveBlockSkeletonInsertionOptions({
      insertSpaces: 'auto',
      tabSize: 4
    }, {
      insertSpaces: true,
      tabSize: 2
    }),
    { insertSpaces: true, tabSize: 4 }
  );
});
