import assert from 'node:assert/strict';
import type { PreviewTokenResponse } from './previewSemanticTokensProbe';

export function assertVisibleSemanticName(
  source: string,
  data: readonly number[],
  spans: readonly { readonly text: string; readonly color: string }[]
): { readonly identifier: string; readonly sourceLine: number; readonly color: string } {
  const lines = source.split(/\r?\n/u);
  let line = 0;
  let character = 0;
  for (let offset = 0; offset < data.length; offset += 5) {
    line += data[offset];
    character = data[offset] === 0 ? character + data[offset + 1] : data[offset + 1];
    const identifier = lines[line]?.slice(character, character + data[offset + 2]);
    if (identifier !== undefined && /^[A-Za-z_][A-Za-z_0-9]*$/u.test(identifier)
        && spans.some(span => span.text === identifier && span.color === 'rgb(1, 255, 135)')) {
      return { identifier, sourceLine: line + 1, color: 'rgb(1, 255, 135)' };
    }
  }
  throw new Error('No provider-classified visible identifier acquired the distinct semantic color.');
}

export function isReferencePreparationSettled(
  evidence: readonly { readonly fileName: string; readonly recordedAt: number }[],
  now: number
): boolean {
  const references = evidence.filter(record => record.fileName.includes('-vba_referenceCatalog'));
  const admissions = references.filter(record => record.fileName.endsWith('.admitted'));
  return admissions.length > 0
    && references.every(record => now - record.recordedAt >= 2_000)
    && admissions.every(admission => references.some(record =>
      record.fileName === admission.fileName.replace(/\.admitted$/u, '.completed')));
}

export function validatePreviewTokenResponse(
  response: PreviewTokenResponse,
  oracle: { readonly sourceSha256: string; readonly data: readonly number[] }
): void {
  assert.equal(response.responseVersion, response.version,
    'The provider response must retain the accepted document revision.');
  assert.equal(response.sourceSha256, oracle.sourceSha256,
    'The provider response must use exactly the oracle source content.');
  assert.ok(response.data.length > 0 && response.data.length % 5 === 0,
    'The provider must return nonempty well-formed full semantic tokens.');
  assert.deepEqual(response.data, oracle.data,
    'The full semantic-token content must exactly match fresh analysis.');
}
