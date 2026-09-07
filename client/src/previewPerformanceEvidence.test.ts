import assert from 'node:assert/strict';
import test from 'node:test';
import {
  assertVisibleSemanticName, isReferencePreparationSettled, validatePreviewTokenResponse
} from './previewPerformanceEvidence';

test('preview correctness requires every token and the accepted source revision to match fresh analysis', () => {
  const response = {
    uri: 'file:///CommonModules/Example.cls', version: 2, responseVersion: 2,
    sourceSha256: 'exact-source', startedAt: 100, completedAt: 140,
    data: [0, 4, 7, 1, 0]
  };
  const oracle = { sourceSha256: 'exact-source', data: [0, 4, 7, 1, 0] };
  validatePreviewTokenResponse(response, oracle);
  assert.throws(() => validatePreviewTokenResponse(
    { ...response, data: [0, 4, 7, 2, 0] }, oracle
  ), /full semantic-token content/u);
  assert.throws(() => validatePreviewTokenResponse(
    { ...response, responseVersion: 3 }, oracle
  ), /document revision/u);
  assert.throws(() => validatePreviewTokenResponse(
    { ...response, sourceSha256: 'stale-source' }, oracle
  ), /source content/u);
});

test('renderer proof requires a provider-classified name in the distinct semantic color', () => {
  const source = 'Public WbSrv As IWorkbookService';
  const tokens = [0, 16, 16, 4, 0];
  const ordinary = [{ text: 'IWorkbookService', color: 'rgb(187, 190, 191)' }];
  assert.throws(() => assertVisibleSemanticName(source, tokens, ordinary), /semantic color/u);
  assert.deepEqual(assertVisibleSemanticName(source, tokens, [
    { text: 'IWorkbookService', color: 'rgb(1, 255, 135)' }
  ]), { identifier: 'IWorkbookService', sourceLine: 1, color: 'rgb(1, 255, 135)' });
});

test('preview preparation waits across delayed catalog publications without awaiting validation', () => {
  const initial = [
    { fileName: '1-vba_referenceCatalogRefresh.admitted', recordedAt: 100 },
    { fileName: '1-vba_referenceCatalogRefresh.completed', recordedAt: 200 },
    { fileName: '2-workspace_diagnostic.admitted', recordedAt: 300 }
  ];
  assert.equal(isReferencePreparationSettled(initial, 300), false);
  assert.equal(isReferencePreparationSettled(initial, 2_200), true);
  const delayed = [...initial,
    { fileName: '3-vba_referenceCatalogCommit.admitted', recordedAt: 700 }];
  assert.equal(isReferencePreparationSettled(delayed, 4_000), false);
  delayed.push({ fileName: '3-vba_referenceCatalogCommit.completed', recordedAt: 3_000 });
  assert.equal(isReferencePreparationSettled(delayed, 4_000), false);
  assert.equal(isReferencePreparationSettled(delayed, 5_000), true);
});
