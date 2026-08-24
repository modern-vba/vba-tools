import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import { parseProjectManifest } from './projectManifest';

test('ProjectManifest adapter reads canonical manifest fixture for Test Explorer projection', () => {
  const manifest = parseProjectManifest(readProjectManifestFixture('document-source-set.json'));

  assert.deepEqual(manifest, {
    projectName: 'DocumentSourceSetProject',
    primaryDocument: 'Book1',
    documents: [
      {
        name: 'Book1',
        sourcePath: 'src/Book1',
        binPath: 'bin/Book1.xlsm'
      }
    ]
  });
});

test('ProjectManifest adapter accepts direct-intent state on reference selections', () => {
  const manifest = parseProjectManifest(
    readProjectManifestFixture('references.json'));

  assert.equal(manifest?.projectName, 'ReferencesProject');
});

test('ProjectManifest adapter rejects fixtures that violate required project identity', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-missing-primary-document.json')), undefined);
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-primary-document-not-defined.json')), undefined);
});

test('ProjectManifest adapter rejects an unsupported schema version', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-schema-version.json')), undefined);
});

test('ProjectManifest adapter rejects an empty project name', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-project-name.json')), undefined);
});

test('ProjectManifest adapter rejects an empty primary-document name', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-primary-document.json')), undefined);
});

test('ProjectManifest adapter rejects an unknown root property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-root-property.json')), undefined);
});

test('ProjectManifest adapter rejects an unknown document property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-document-property.json')), undefined);
});

test('ProjectManifest adapter rejects a mis-cased document kind', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-mis-cased-document-kind.json')), undefined);
});

test('ProjectManifest adapter rejects an unsupported test output format', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-test-format.json')), undefined);
});

test('ProjectManifest adapter rejects a non-positive test execution timeout', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-test-execution-timeout.json')), undefined);
});

test('ProjectManifest adapter rejects a non-positive workbook-open timeout', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-workbook-open-timeout.json')), undefined);
});

test('ProjectManifest adapter rejects a non-positive workbook-save timeout', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-workbook-save-timeout.json')), undefined);
});

test('ProjectManifest adapter rejects an incomplete document selection', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-missing-selection-arrays.json')), undefined);
});

test('ProjectManifest adapter rejects a document missing a required path', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-missing-template-path.json')), undefined);
});

test('ProjectManifest adapter rejects explicit null optional state', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-null-optional-property.json')), undefined);
});

test('ProjectManifest adapter rejects an empty CommonModules repository path', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-common-modules-repository.json')), undefined);
});

test('ProjectManifest adapter rejects explicit null nested command state', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-null-command-default.json')), undefined);
});

test('ProjectManifest adapter rejects an empty command-default override container', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-command-defaults.json')), undefined);
});

test('ProjectManifest adapter rejects an empty test-default override container', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-test-defaults.json')), undefined);
});

test('ProjectManifest adapter rejects an empty Excel automation-default override container', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-excel-automation-defaults.json')), undefined);
});

test('ProjectManifest adapter rejects a null document definition', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-null-document.json')), undefined);
});

test('ProjectManifest adapter rejects a null reference entry', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-null-reference.json')), undefined);
});

test('ProjectManifest adapter rejects an invalid reference entry', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-empty-reference-name.json')), undefined);
});

test('ProjectManifest adapter rejects a reference missing direct-intent state', () => {
  assert.equal(
    parseProjectManifest(readProjectManifestFixture('invalid-missing-reference-requested.json')),
    undefined);
});

test('ProjectManifest adapter rejects the always-active standard library selection', () => {
  assert.equal(
    parseProjectManifest(readProjectManifestFixture('invalid-standard-library-reference.json')),
    undefined);
});

test('ProjectManifest adapter rejects a reference name with leading or trailing whitespace', () => {
  assert.equal(
    parseProjectManifest(readProjectManifestFixture('invalid-untrimmed-reference-name.json')),
    undefined);
});

test('ProjectManifest adapter rejects case-insensitive duplicate reference names', () => {
  assert.equal(
    parseProjectManifest(readProjectManifestFixture('invalid-duplicate-reference-name.json')),
    undefined);
});

test('ProjectManifest adapter rejects an invalid CommonModules entry', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-common-module-property.json')), undefined);
});

test('ProjectManifest adapter rejects an unknown command-default property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-command-default-property.json')), undefined);
});

test('ProjectManifest adapter rejects an unknown test-default property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-test-default-property.json')), undefined);
});

test('ProjectManifest adapter rejects a mis-cased nested property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-mis-cased-test-default-property.json')), undefined);
});

test('ProjectManifest adapter rejects an unknown Excel automation-default property', () => {
  assert.equal(parseProjectManifest(readProjectManifestFixture('invalid-unknown-excel-automation-default-property.json')), undefined);
});

test('ProjectManifest adapter accepts Excel automation defaults in the shared manifest contract', () => {
  const manifest = parseProjectManifest(readProjectManifestFixture('primary-document.json'));

  assert.equal(manifest?.projectName, 'PrimaryDocumentProject');
  assert.equal(manifest?.primaryDocument, 'Book1');
});

function readProjectManifestFixture(fileName: string): string {
  return readFileSync(path.join(process.cwd(), 'fixtures', 'project-manifest', fileName), 'utf8');
}
