import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { parseProjectManifest } from './projectManifest';

test('ProjectManifest adapter reads canonical manifest fixture for Test Explorer projection', () => {
  const manifest = parseProjectManifest(readProjectManifestFixture('document-source-set.json'));

  assert.deepEqual(manifest, {
    projectName: 'BookProject',
    primaryDocument: 'Book1',
    documents: [
      {
        name: 'Book1',
        sourcePath: 'src/Book1'
      }
    ]
  });
});

test('ProjectManifest adapter rejects fixtures that violate required project identity', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-missing-primary-document.json')), undefined);
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-primary-document-not-defined.json')), undefined);
});

function readProjectManifestFixture(fileName: string): string {
  return readFileSync(path.join(process.cwd(), 'fixtures', 'project-manifest', fileName), 'utf8');
}
