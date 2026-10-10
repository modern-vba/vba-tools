import test from 'node:test';
import assert from 'node:assert/strict';
import { DebugWorkbookConfirmationBinding } from './debugWorkbookConfirmation';

const sessionId = '0123456789abcdef0123456789abcdef';
const requestId = 'fedcba9876543210fedcba9876543210';

test('an accepted live-workbook warning replies once to its exact debug generation', async () => {
  const prompts: string[] = [];
  const replies: { command: string; argumentsValue: Record<string, unknown> }[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 0, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: async warning => { prompts.push(warning); return true; },
    customRequest: async (command, argumentsValue) => { replies.push({ command, argumentsValue }); }
  });

  await binding.handleEvent(sessionId, {
    type: 'event', event: 'vba/workbookConfirmation', body: {
      schemaVersion: '1.0', requestId, sessionId, generationId: 0,
      workbookPath: 'c:\\project\\BOOK.xlsm', message: 'Continue debugging this workbook?'
    }
  });

  assert.equal(prompts.length, 1);
  assert.match(prompts[0], /replace.*live VBA code/i);
  assert.match(prompts[0], /not.*save.*automatically/i);
  assert.match(prompts[0], /Continue debugging this workbook\?/);
  assert.deepEqual(replies, [{
    command: 'vba/workbookConfirmationResult',
    argumentsValue: { requestId, sessionId, generationId: 0, accepted: true }
  }]);
});

test('duplicate confirmation events share one pending prompt and one reply', async () => {
  let finishPrompt: ((accepted: boolean) => void) | undefined;
  const prompt = new Promise<boolean>(resolve => { finishPrompt = resolve; });
  let prompts = 0;
  const replies: Record<string, unknown>[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 3, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: () => { prompts++; return prompt; },
    customRequest: async (_command, argumentsValue) => { replies.push(argumentsValue); }
  });
  const event = { type: 'event', event: 'vba/workbookConfirmation', body: {
    schemaVersion: '1.0', requestId, sessionId, generationId: 3,
    workbookPath: 'C:\\Project\\Book.xlsm', message: 'Continue?'
  } };

  const first = binding.handleEvent(sessionId, event);
  const duplicate = binding.handleEvent(sessionId, event);
  finishPrompt?.(true);
  await Promise.all([first, duplicate]);

  assert.equal(prompts, 1);
  assert.deepEqual(replies, [{ requestId, sessionId, generationId: 3, accepted: true }]);
});

test('disposing the debug binding while the prompt is pending sends no late consent', async () => {
  let finishPrompt: ((accepted: boolean) => void) | undefined;
  const pendingPrompt = new Promise<boolean>(resolve => { finishPrompt = resolve; });
  const replies: Record<string, unknown>[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 0, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: () => pendingPrompt,
    customRequest: async (_command, argumentsValue) => { replies.push(argumentsValue); }
  });
  const pending = binding.handleEvent(sessionId, {
    type: 'event', event: 'vba/workbookConfirmation', body: {
      schemaVersion: '1.0', requestId, sessionId, generationId: 0,
      workbookPath: 'C:\\Project\\Book.xlsm', message: 'Continue?'
    }
  });

  binding.dispose();
  finishPrompt?.(true);
  await pending;

  assert.deepEqual(replies, []);
});

test('foreign or malformed events neither prompt nor consume the valid request', async () => {
  let prompts = 0;
  const replies: Record<string, unknown>[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 2, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: async () => { prompts++; return true; },
    customRequest: async (_command, argumentsValue) => { replies.push(argumentsValue); }
  });
  const validBody = {
    schemaVersion: '1.0', requestId, sessionId, generationId: 2,
    workbookPath: 'C:\\Project\\Book.xlsm', message: 'Continue?'
  };
  const eventWith = (body: unknown, event = 'vba/workbookConfirmation') =>
    ({ type: 'event', event, body });
  const foreignSessionId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const rejected: { origin: string; event: unknown }[] = [
    { origin: foreignSessionId, event: eventWith(validBody) },
    { origin: sessionId, event: eventWith({ ...validBody, sessionId: foreignSessionId }) },
    { origin: sessionId, event: eventWith({ ...validBody, generationId: 1 }) },
    { origin: sessionId, event: eventWith({ ...validBody, workbookPath: 'C:\\Project\\Other.xlsm' }) },
    { origin: sessionId, event: eventWith({ ...validBody, workbookPath: 'C:\\Project\\..\\Book.xlsm' }) },
    { origin: sessionId, event: eventWith({ ...validBody, requestId: requestId.toUpperCase() }) },
    { origin: sessionId, event: eventWith({ ...validBody, schemaVersion: '2.0' }) },
    { origin: sessionId, event: eventWith({ ...validBody, message: '  ' }) },
    { origin: sessionId, event: eventWith(validBody, 'vba/otherEvent') },
    { origin: sessionId, event: eventWith(null) }
  ];

  for (const candidate of rejected) {
    await binding.handleEvent(candidate.origin, candidate.event);
  }
  assert.equal(prompts, 0);
  assert.deepEqual(replies, []);

  await binding.handleEvent(sessionId, eventWith(validBody));
  assert.equal(prompts, 1);
  assert.deepEqual(replies, [{ requestId, sessionId, generationId: 2, accepted: true }]);
});

test('dismissed confirmation returns an explicit refusal rather than authorization', async () => {
  const replies: Record<string, unknown>[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 1, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: async () => undefined as unknown as boolean,
    customRequest: async (_command, argumentsValue) => { replies.push(argumentsValue); }
  });

  await binding.handleEvent(sessionId, {
    type: 'event', event: 'vba/workbookConfirmation', body: {
      schemaVersion: '1.0', requestId, sessionId, generationId: 1,
      workbookPath: 'C:\\Project\\Book.xlsm', message: 'Continue?'
    }
  });

  assert.deepEqual(replies, [{ requestId, sessionId, generationId: 1, accepted: false }]);
});

test('a failed confirmation UI replies with refusal rather than leaving an approval path', async () => {
  const replies: Record<string, unknown>[] = [];
  const binding = new DebugWorkbookConfirmationBinding({
    sessionId, generationId: 1, workbookPath: 'C:\\Project\\Book.xlsm',
    confirmReplacement: async () => { throw new Error('UI unavailable'); },
    customRequest: async (_command, argumentsValue) => { replies.push(argumentsValue); }
  });

  await binding.handleEvent(sessionId, {
    type: 'event', event: 'vba/workbookConfirmation', body: {
      schemaVersion: '1.0', requestId, sessionId, generationId: 1,
      workbookPath: 'C:\\Project\\Book.xlsm', message: 'Continue?'
    }
  });

  assert.deepEqual(replies, [{ requestId, sessionId, generationId: 1, accepted: false }]);
});

test('a debug binding refuses an unbounded or fractional generation', () => {
  for (const generationId of [-1, 0.5, 2147483648]) {
    assert.throws(() => new DebugWorkbookConfirmationBinding({
      sessionId, generationId, workbookPath: 'C:\\Project\\Book.xlsm',
      confirmReplacement: async () => true,
      customRequest: async () => {}
    }), RangeError);
  }
});

test('a debug binding requires an exact session ID and canonical Windows workbook path', () => {
  const makeBinding = (boundSessionId: string, workbookPath: string) =>
    new DebugWorkbookConfirmationBinding({
      sessionId: boundSessionId, generationId: 0, workbookPath,
      confirmReplacement: async () => true,
      customRequest: async () => {}
    });

  assert.throws(() => makeBinding('wrong', 'C:\\Project\\Book.xlsm'), TypeError);
  assert.throws(() => makeBinding(sessionId.toUpperCase(), 'C:\\Project\\Book.xlsm'), TypeError);
  assert.throws(() => makeBinding(sessionId, 'Book.xlsm'), TypeError);
  assert.throws(() => makeBinding(sessionId, 'C:\\Project\\..\\Book.xlsm'), TypeError);
});
