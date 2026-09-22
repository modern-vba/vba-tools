import assert from 'node:assert/strict';
import test from 'node:test';
import type { CancellationToken, Position, TextDocument } from 'vscode';
import {
  createVbaRenameMiddleware,
  useVbaRenameFailureObserverForTest,
  type VbaRenameClient
} from '../rename';
import { withRenameFailureDiagnostics } from './renameFailureDiagnostics';

test('Rename failure diagnostics retain allowlisted provider evidence before command error conversion', async () => {
  const reports: string[] = [];
  const commandFailure = new Error('Rename provider failed.');
  const providerFailure = {
    code: -32803,
    message: 'private source content',
    data: {
      reason: 'sourceCollision', condition: 'fileExists', path: 'C:\\fixture\\Target.bas',
      source: 'private source content', edit: { documentChanges: ['private source content'] }
    }
  };

  await assert.rejects(withRenameFailureDiagnostics('Case-only file rename', async () => {
    await failRename(providerFailure);
    throw commandFailure;
  }, text => reports.push(text)), error => error === commandFailure);

  assert.equal(reports.length, 1);
  assert.match(reports[0], /\[Rename failure diagnostics\]/);
  assert.match(reports[0], /Case-only file rename/);
  assert.match(reports[0], /phase="rename" code=-32803/);
  assert.match(reports[0], /reason="sourceCollision" condition="fileExists"/);
  assert.ok(reports[0].includes(`path=${JSON.stringify(providerFailure.data.path)}`));
  assert.doesNotMatch(reports[0], /private source content|documentChanges/);
});

test('Rename failure diagnostics bound the last four observations and every escaped field', async () => {
  const reports: string[] = [];
  const commandFailure = new Error('Rename provider failed.');
  const longValue = '\r\n\u2028\u2029\0'.repeat(2000);
  await assert.rejects(withRenameFailureDiagnostics(longValue, async () => {
    for (let index = 0; index < 10; index++) {
      await failRename({
        code: -32803,
        data: { reason: `reason-${index}:${longValue}`, condition: longValue, path: longValue }
      });
    }
    throw commandFailure;
  }, text => reports.push(text)), error => error === commandFailure);

  const report = reports[0];
  assert.equal(report.split('\n').filter(line => line.startsWith('phase=')).length, 4);
  assert.match(report, /reason-6:/);
  assert.match(report, /reason-9:/);
  assert.doesNotMatch(report, /reason-[0-5]:/);
  assert.doesNotMatch(report, /[\r\u2028\u2029\0]/);
  assert.equal(report.split('\n').length, 7);
  for (const quotedValue of report.match(/"(?:[^"\\]|\\.)*"/g) ?? []) {
    assert.ok(quotedValue.length <= 512);
    assert.equal(typeof JSON.parse(quotedValue), 'string');
  }
  assert.ok(report.length < 12000);
});

test('Rename diagnostic reporter errors preserve the command failure and restore the previous observer', async () => {
  const commandFailure = new Error('Rename provider failed.');
  const outsideFailure = { code: -32803, data: { reason: 'outsideScenario' } };
  const outsideObservations: unknown[] = [];
  const previous = useVbaRenameFailureObserverForTest(value => outsideObservations.push(value.error));
  try {
    await assert.rejects(withRenameFailureDiagnostics('Reporter failure', async () => {
      await failRename({ code: -32803, data: { reason: 'insideScenario' } });
      throw commandFailure;
    }, () => { throw new Error('Reporter failed.'); }), error => error === commandFailure);

    await failRename(outsideFailure);
    assert.deepEqual(outsideObservations, [outsideFailure]);
  } finally {
    previous.dispose();
  }
});

test('Rename diagnostics stay silent for success and expected handled failures and release their observer', async () => {
  const reports: string[] = [];
  const outsideObservations: unknown[] = [];
  const outsideFailure = { code: -32803, data: { reason: 'outsideScenario' } };
  const previous = useVbaRenameFailureObserverForTest(value => outsideObservations.push(value.error));
  try {
    await withRenameFailureDiagnostics('Successful scenario', async () => {}, text => reports.push(text));
    await withRenameFailureDiagnostics('Expected invalid identifier', async () => {
      await failRename({ code: -32803, data: { reason: 'expectedRejection' } });
    }, text => reports.push(text));
    await failRename(outsideFailure);

    assert.deepEqual(reports, []);
    assert.deepEqual(outsideObservations, [outsideFailure]);
  } finally {
    previous.dispose();
  }
});

test('Rename diagnostics isolate consecutive scenarios and ignore errors outside their lifetime', async () => {
  const reports: string[] = [];
  const commandFailure = new Error('Rename provider failed.');
  for (const reason of ['firstScenario', 'secondScenario']) {
    await assert.rejects(withRenameFailureDiagnostics(reason, async () => {
      await failRename({ code: -32803, data: { reason } });
      throw commandFailure;
    }, text => reports.push(text)), error => error === commandFailure);
    await failRename({ code: -32803, data: { reason: 'outsideScenario' } });
  }

  assert.equal(reports.length, 2);
  assert.match(reports[0], /firstScenario/);
  assert.match(reports[1], /secondScenario/);
  assert.doesNotMatch(reports[0], /secondScenario|outsideScenario/);
  assert.doesNotMatch(reports[1], /firstScenario|outsideScenario/);
});

test('Rename diagnostics safely omit missing and nonstring structured data without serializing it', async () => {
  const reports: string[] = [];
  const commandFailure = new Error('Rename provider failed.');
  const malformedFailures = [
    null,
    'private source content',
    { code: '-32803', data: ['private source content'] },
    {
      code: Number.NaN,
      data: {
        reason: 123, condition: { source: 'private source content' }, path: false,
        toJSON: () => { throw new Error('Do not serialize raw data.'); }
      }
    }
  ];
  await assert.rejects(withRenameFailureDiagnostics('Malformed provider errors', async () => {
    for (const failure of malformedFailures) await failRename(failure);
    throw commandFailure;
  }, text => reports.push(text)), error => error === commandFailure);

  assert.equal(reports.length, 1);
  assert.equal(reports[0].split('\n').filter(line => line === 'phase="rename"').length, 4);
  assert.doesNotMatch(reports[0], /code=|reason=|condition=|path=|private source content/);
});

async function failRename(error: unknown): Promise<void> {
  const client: VbaRenameClient = {
    asTextDocumentIdentifier: () => ({ uri: 'file:///C:/fixture/Module1.bas' }),
    asPosition: () => ({ line: 0, character: 0 }),
    sendRenameRequest: async () => { throw error; },
    asWorkspaceEdit: async () => undefined,
    handleFailedRenameRequest: () => null
  };
  const middleware = createVbaRenameMiddleware({
    getLanguageClient: () => client,
    captureCaseOnlyFileRenames: () => undefined
  });
  const token: CancellationToken = {
    isCancellationRequested: false,
    onCancellationRequested: () => ({ dispose() {} })
  };
  await middleware({} as TextDocument, {} as Position, 'Target', token, () => null);
}
