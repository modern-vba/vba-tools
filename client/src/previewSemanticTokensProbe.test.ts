import assert from 'node:assert/strict';
import test from 'node:test';
import { createPreviewSemanticTokensProbe } from './previewSemanticTokensProbe';

test('preview observation preserves the natural full-token provider result and document revision', async () => {
  const probe = createPreviewSemanticTokensProbe();
  const document = {
    uri: { toString: () => 'file:///CommonModules/Lib_Common.bas' },
    version: 3,
    getText: () => 'Public Sub Example()\nEnd Sub\n'
  };
  const result = { data: new Uint32Array([0, 11, 7, 1, 0]) };
  const returned = await probe.observe(document, async () => result);
  assert.equal(returned, result);
  assert.equal(probe.responses.length, 1);
  assert.equal(probe.responses[0].version, 3);
  assert.equal(probe.responses[0].responseVersion, 3);
  assert.deepEqual(probe.responses[0].data, [0, 11, 7, 1, 0]);
  assert.ok(probe.responses[0].completedAt >= probe.responses[0].startedAt);
  result.data[0] = 9;
  assert.equal(probe.responses[0].data[0], 0);
});

test('cancelled full-token work cannot finish a preview measurement', async () => {
  const probe = createPreviewSemanticTokensProbe();
  const result = { data: new Uint32Array([0, 0, 4, 1, 0]) };
  await probe.observe({
    uri: { toString: () => 'file:///Cancelled.bas' }, version: 1,
    getText: () => 'Name'
  }, () => result, () => true);
  assert.equal(probe.responses.length, 0);
});
