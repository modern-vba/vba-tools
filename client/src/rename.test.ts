import test from 'node:test';
import assert from 'node:assert/strict';
import type {
  CancellationToken,
  MessageItem,
  Position,
  TextDocument,
  WorkspaceEdit
} from 'vscode';
import type { ClientCapabilities, WorkspaceEdit as ProtocolWorkspaceEdit } from 'vscode-languageclient/node';
import {
  createVbaRenameClientCapabilitiesFeature,
  createVbaRenameMiddleware,
  type VbaRenameClient,
  type VbaRenameConfirmation,
  type VbaRenameMiddlewareOptions
} from './rename';

test('VBA Rename confirms one structured collision before converting the complete final edit', async () => {
  const uri = 'file:///C:/work/Module1.bas';
  const token = {
    isCancellationRequested: false,
    onCancellationRequested: () => ({ dispose() {} })
  } as CancellationToken;
  const finalEdit: ProtocolWorkspaceEdit = {
    documentChanges: [{
      textDocument: { uri, version: 1 },
      edits: [{
        range: { start: { line: 1, character: 6 }, end: { line: 1, character: 14 } },
        newText: 'ExistingValue'
      }]
    }]
  };
  const convertedEdit = {} as WorkspaceEdit;
  const events: string[] = [];
  const client = {
    asTextDocumentIdentifier: () => ({ uri }),
    asPosition: () => ({ line: 1, character: 7 }),
    sendRenameRequest: async () => {
      events.push('rename');
      throw {
        code: -32803,
        data: {
          kind: 'vbaRenameConfirmationRequired',
          protocolVersion: 1,
          confirmationId: 'one-operation-confirmation',
          originalName: 'OldValue',
          newName: 'ExistingValue',
          conflicts: [{
            collisionKind: 'sameScopeDeclaration',
            name: 'ExistingValue',
            uri,
            range: { start: { line: 2, character: 6 }, end: { line: 2, character: 19 } }
          }],
          concerns: ['References may become ambiguous or bind to another declaration.'],
          retainedPaths: []
        }
      };
    },
    sendConfirmationRequest: async (
      parameters: { confirmationId: string; decision: 'continue' | 'cancel' },
      requestToken?: CancellationToken
    ) => {
      assert.deepEqual(parameters, {
        confirmationId: 'one-operation-confirmation', decision: 'continue'
      });
      assert.strictEqual(requestToken, token);
      events.push('confirm');
      return finalEdit;
    },
    asWorkspaceEdit: async (edit: ProtocolWorkspaceEdit | null) => {
      assert.strictEqual(edit, finalEdit);
      events.push('convert');
      return convertedEdit;
    },
    handleFailedRenameRequest: () => {
      events.push('failed');
      return null;
    }
  };
  const options = {
    getLanguageClient: () => client,
    captureCaseOnlyFileRenames: (renames: readonly unknown[]) => {
      assert.deepEqual(renames, []);
      events.push('capture-final');
    },
    showWarningMessage: async (
      message: string,
      dialogOptions: { modal: boolean; detail?: string },
      ...items: MessageItem[]
    ) => {
      assert.deepEqual(events, ['rename']);
      assert.match(message, /OldValue/);
      assert.match(message, /ExistingValue/);
      assert.equal(dialogOptions.modal, true);
      assert.match(dialogOptions.detail ?? '', /Module1\.bas:3:7/);
      assert.match(dialogOptions.detail ?? '', /ambiguous/);
      assert.match(dialogOptions.detail ?? '', /manual/i);
      assert.ok(items.some(item => item.title === 'Cancel' && item.isCloseAffordance));
      events.push('prompt');
      return items.find(item => item.title === 'Continue once');
    }
  };

  const result = await createVbaRenameMiddleware(options)(
    {} as TextDocument,
    {} as Position,
    'ExistingValue',
    token,
    () => { throw new Error('The integrated client owns this Rename operation.'); }
  );

  assert.strictEqual(result, convertedEdit);
  assert.deepEqual(events, ['rename', 'prompt', 'confirm', 'convert', 'capture-final']);
});

test('VBA Rename cancellation releases the confirmation while the warning remains unanswered', { timeout: 2000 }, async () => {
  let cancelled = false;
  let cancellationListener: (() => void) | undefined;
  let completePrompt!: (item: MessageItem | undefined) => void;
  let notifyPromptShown!: () => void;
  const promptShown = new Promise<void>(resolve => { notifyPromptShown = resolve; });
  const confirmationRequests: Array<{ decision: string; token: CancellationToken | undefined }> = [];
  const token = {
    get isCancellationRequested() { return cancelled; },
    onCancellationRequested: (listener: () => void) => {
      cancellationListener = listener;
      return { dispose: () => { cancellationListener = undefined; } };
    }
  } as CancellationToken;
  const client = {
    asTextDocumentIdentifier: () => ({ uri: 'file:///C:/work/Module1.bas' }),
    asPosition: () => ({ line: 1, character: 7 }),
    sendRenameRequest: async () => {
      throw {
        code: -32803,
        data: {
          kind: 'vbaRenameConfirmationRequired', protocolVersion: 1,
          confirmationId: 'cancelled-operation', originalName: 'OldValue', newName: 'ExistingValue',
          conflicts: [{ collisionKind: 'sameScopeDeclaration', name: 'ExistingValue' }],
          concerns: ['References may become ambiguous.'], retainedPaths: []
        }
      };
    },
    sendConfirmationRequest: async (
      parameters: { confirmationId: string; decision: 'continue' | 'cancel' },
      requestToken?: CancellationToken
    ) => {
      assert.equal(parameters.confirmationId, 'cancelled-operation');
      confirmationRequests.push({ decision: parameters.decision, token: requestToken });
      return null;
    },
    asWorkspaceEdit: async () => { throw new Error('Cancelled Rename must not convert an edit.'); },
    handleFailedRenameRequest: () => null
  };
  const options = {
    getLanguageClient: () => client,
    captureCaseOnlyFileRenames: () => { throw new Error('Cancelled Rename must not track file edits.'); },
    showWarningMessage: () => {
      notifyPromptShown();
      return new Promise<MessageItem | undefined>(resolve => { completePrompt = resolve; });
    }
  };
  const result = createVbaRenameMiddleware(options)(
    {} as TextDocument, {} as Position, 'ExistingValue', token, () => null
  );

  await promptShown;
  try {
    cancelled = true;
    cancellationListener?.();
    await Promise.resolve();
    assert.deepEqual(confirmationRequests, [{ decision: 'cancel', token: undefined }]);
    assert.equal(await result, null);
  } finally {
    completePrompt(undefined);
    await result;
  }
  assert.equal(confirmationRequests.length, 1);
});

test('VBA Rename advertises the explicit confirmation protocol without replacing other experimental capabilities', () => {
  const capabilities: ClientCapabilities = { experimental: { otherFeature: { protocolVersion: 7 } } };

  createVbaRenameClientCapabilitiesFeature().fillClientCapabilities(capabilities);

  assert.deepEqual(capabilities.experimental, {
    otherFeature: { protocolVersion: 7 },
    vbaRenameConfirmation: { protocolVersion: 1 }
  });
});

test('VBA Rename cancellation finishes without waiting for the best-effort cancel response', { timeout: 2000 }, async () => {
  let completePrompt!: (item: MessageItem | undefined) => void;
  let completeCancel!: (edit: ProtocolWorkspaceEdit | null) => void;
  let notifyPromptShown!: () => void;
  const promptShown = new Promise<void>(resolve => { notifyPromptShown = resolve; });
  const harness = createRenameHarness({
    choose: () => {
      notifyPromptShown();
      return new Promise<MessageItem | undefined>(resolve => { completePrompt = resolve; });
    },
    confirm: () => new Promise<ProtocolWorkspaceEdit | null>(resolve => { completeCancel = resolve; })
  });
  const result = Promise.resolve(harness.run());
  await promptShown;
  try {
    harness.cancel();
    const pending = Symbol('The cancelled Rename still awaits the server.');
    assert.equal(await Promise.race([
      result,
      new Promise<typeof pending>(resolve => setImmediate(() => resolve(pending)))
    ]), null);
    assert.deepEqual(harness.confirmations, [{
      parameters: { confirmationId: 'confirmation-1', decision: 'cancel' }, token: undefined
    }]);
    assert.equal(harness.conversions.length, 0);
    assert.equal(harness.tracking.length, 0);
  } finally {
    completeCancel?.(null);
    completePrompt(undefined);
    await result;
  }
});

test('VBA Rename cancellation during final edit conversion returns no edit or file tracking', async () => {
  const token = {
    isCancellationRequested: false,
    onCancellationRequested: () => ({ dispose() {} })
  } as CancellationToken & { isCancellationRequested: boolean };
  let captureCount = 0;
  const client = {
    asTextDocumentIdentifier: () => ({ uri: 'file:///C:/work/OldModule.bas' }),
    asPosition: () => ({ line: 0, character: 22 }),
    sendRenameRequest: async (): Promise<ProtocolWorkspaceEdit> => ({
      documentChanges: [{
        kind: 'rename', oldUri: 'file:///C:/work/OldModule.bas', newUri: 'file:///C:/work/OLDMODULE.bas'
      }]
    }),
    asWorkspaceEdit: async () => {
      token.isCancellationRequested = true;
      return {} as WorkspaceEdit;
    },
    handleFailedRenameRequest: () => null
  };
  const result = await createVbaRenameMiddleware({
    getLanguageClient: () => client,
    captureCaseOnlyFileRenames: () => { captureCount += 1; }
  })({} as TextDocument, {} as Position, 'OLDMODULE', token, () => null);

  assert.equal(result, null);
  assert.equal(captureCount, 0);
});

test('VBA Rename Cancel and dismissal discard each challenge and later operations require fresh consent', async () => {
  let choice: string | undefined = 'Cancel';
  const harness = createRenameHarness({ choose: items => items.find(item => item.title === choice) });

  assert.equal(await harness.run(), null);
  choice = undefined;
  assert.equal(await harness.run(), null);
  choice = 'Continue once';
  assert.strictEqual(await harness.run(), harness.convertedEdit);
  assert.strictEqual(await harness.run(), harness.convertedEdit);

  assert.equal(harness.prompts.length, 4);
  assert.equal(harness.renameRequests, 4);
  assert.deepEqual(harness.confirmations.map(request => request.parameters), [
    { confirmationId: 'confirmation-1', decision: 'cancel' },
    { confirmationId: 'confirmation-2', decision: 'cancel' },
    { confirmationId: 'confirmation-3', decision: 'continue' },
    { confirmationId: 'confirmation-4', decision: 'continue' }
  ]);
  assert.ok(harness.confirmations.slice(0, 2).every(request => request.token === undefined));
  assert.equal(harness.conversions.length, 2);
  assert.equal(harness.tracking.length, 2);
});

test('VBA Rename releases a disposed warning and ignores failure of the best-effort release', async () => {
  const promptFailure = new Error('The warning host was disposed.');
  const harness = createRenameHarness({
    choose: () => { throw promptFailure; },
    confirm: async () => { throw new Error('The server has already discarded this confirmation.'); }
  });

  assert.equal(await harness.run(), null);
  assert.deepEqual(harness.confirmations.map(request => request.parameters.decision), ['cancel']);
  assert.deepEqual(harness.failures, [promptFailure]);
  assert.equal(harness.conversions.length, 0);
  assert.equal(harness.tracking.length, 0);
});

test('VBA Rename retains strict failure handling without explicit integration or a recognized complete challenge', async () => {
  for (const options of [
    { warningHost: false },
    { confirmationClient: false },
    { challenge: { protocolVersion: 2 } },
    { challenge: { confirmationId: '' } },
    { challenge: { newName: 'AnotherValue' } },
    { challenge: { concerns: 'not structured' } },
    { challenge: { conflicts: [{ collisionKind: 'declaration', name: 'ExistingValue', range: {} }] } },
    { error: { code: -32803, data: { reason: 'sameScopeCollision' } } },
    { error: new Error('Rename failed with sameScopeCollision in a message.') }
  ]) {
    const harness = createRenameHarness(options);
    assert.equal(await harness.run(), null);
    assert.equal(harness.failures.length, 1);
    assert.equal(harness.prompts.length, 0);
    assert.equal(harness.confirmations.length, 0);
    assert.equal(harness.conversions.length, 0);
    assert.equal(harness.tracking.length, 0);
  }
});

test('VBA Rename cancellation after a confirmation response discards the complete edit', async () => {
  const harness = createRenameHarness({
    choose: items => items.find(item => item.title === 'Continue once'),
    confirm: async () => {
      harness.token.isCancellationRequested = true;
      return {};
    }
  });

  assert.equal(await harness.run(), null);
  assert.equal(harness.confirmations.length, 1);
  assert.equal(harness.conversions.length, 0);
  assert.equal(harness.tracking.length, 0);
});

test('VBA Rename reports changed confirmation evidence without retrying the operation', async () => {
  const staleEvidence = { code: -32801, message: 'The participating source or destination changed.' };
  const harness = createRenameHarness({
    choose: items => items.find(item => item.title === 'Continue once'),
    confirm: async () => { throw staleEvidence; }
  });

  assert.equal(await harness.run(), null);
  assert.equal(harness.renameRequests, 1);
  assert.equal(harness.prompts.length, 1);
  assert.deepEqual(harness.failures, [staleEvidence]);
  assert.equal(harness.conversions.length, 0);
  assert.equal(harness.tracking.length, 0);
});

test('VBA Rename warns about the resulting module name and both retained UserForm paths', async () => {
  const retainedPaths = ['C:\\work\\OldForm.frm', 'C:\\work\\OldForm.frx'];
  const harness = createRenameHarness({ challenge: { conflicts: [], retainedPaths } });

  assert.equal(await harness.run(), null);
  assert.equal(harness.prompts.length, 1);
  assert.match(harness.prompts[0].detail, /module name will be 'ExistingValue'/);
  for (const retainedPath of retainedPaths) {
    assert.ok(harness.prompts[0].detail.includes(retainedPath));
  }
  assert.match(harness.prompts[0].detail, /retained/);
  assert.equal(harness.conversions.length, 0);
});

function createRenameHarness(options: {
  choose?: (items: readonly MessageItem[]) => MessageItem | undefined | PromiseLike<MessageItem | undefined>;
  challenge?: Record<string, unknown>;
  error?: unknown;
  warningHost?: boolean;
  confirmationClient?: boolean;
  confirm?: (parameters: VbaRenameConfirmation) => Promise<ProtocolWorkspaceEdit | null>;
} = {}) {
  const cancellationListeners = new Set<() => void>();
  const token = {
    isCancellationRequested: false,
    onCancellationRequested: (listener: () => void) => {
      cancellationListeners.add(listener);
      return { dispose: () => { cancellationListeners.delete(listener); } };
    }
  } as CancellationToken & { isCancellationRequested: boolean };
  const confirmations: Array<{ parameters: VbaRenameConfirmation; token: CancellationToken | undefined }> = [];
  const prompts: Array<{ message: string; detail: string }> = [];
  const failures: unknown[] = [];
  const conversions: Array<ProtocolWorkspaceEdit | null> = [];
  const tracking: Array<readonly unknown[]> = [];
  const convertedEdit = {} as WorkspaceEdit;
  let renameRequests = 0;
  const client: VbaRenameClient = {
    asTextDocumentIdentifier: () => ({ uri: 'file:///C:/work/Module1.bas' }),
    asPosition: () => ({ line: 1, character: 7 }),
    sendRenameRequest: async () => {
      renameRequests += 1;
      throw options.error ?? {
        code: -32803,
        data: {
          kind: 'vbaRenameConfirmationRequired', protocolVersion: 1,
          confirmationId: `confirmation-${renameRequests}`,
          originalName: 'OldValue', newName: 'ExistingValue',
          conflicts: [{ collisionKind: 'sameScopeDeclaration', name: 'ExistingValue' }],
          concerns: ['References may become ambiguous.'], retainedPaths: [],
          ...options.challenge
        }
      };
    },
    asWorkspaceEdit: async edit => {
      conversions.push(edit);
      return convertedEdit;
    },
    handleFailedRenameRequest: error => {
      failures.push(error);
      return null;
    }
  };
  if (options.confirmationClient !== false) {
    client.sendConfirmationRequest = async (parameters, requestToken) => {
      confirmations.push({ parameters, token: requestToken });
      return options.confirm === undefined
        ? parameters.decision === 'continue' ? {} : null
        : options.confirm(parameters);
    };
  }
  const middlewareOptions: VbaRenameMiddlewareOptions = {
    getLanguageClient: () => client,
    captureCaseOnlyFileRenames: renames => { tracking.push(renames); },
    ...(options.warningHost === false ? {} : {
      showWarningMessage: async (message: string, dialogOptions: { detail: string }, ...items: MessageItem[]) => {
        prompts.push({ message, detail: dialogOptions.detail });
        return options.choose?.(items);
      }
    })
  };
  const middleware = createVbaRenameMiddleware(middlewareOptions);
  return {
    token, confirmations, prompts, failures, conversions, tracking, convertedEdit,
    cancel: () => {
      token.isCancellationRequested = true;
      for (const listener of cancellationListeners) {
        listener();
      }
    },
    get renameRequests() { return renameRequests; },
    run: () => middleware({} as TextDocument, {} as Position, 'ExistingValue', token, () => null)
  };
}
