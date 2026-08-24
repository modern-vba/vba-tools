import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import Ajv from 'ajv';

const schemaUrl = new URL('../schemas/project-manifest.schema.json', import.meta.url);
const fixtureDirectoryUrl = new URL('../fixtures/project-manifest/', import.meta.url);

test('ProjectManifest schema uses the bundled draft-07 vocabulary', async () => {
  const schema = JSON.parse(await fs.readFile(schemaUrl, 'utf8'));

  assert.equal(schema.$schema, 'http://json-schema.org/draft-07/schema#');
  assert.ok(schema.definitions);
  assert.equal(Object.hasOwn(schema, '$defs'), false);
  assert.equal(schema.properties.commandDefaults.$ref, '#/definitions/commandDefaults');
});

test('every ProjectManifest-owned schema object is closed', async () => {
  const schema = JSON.parse(await fs.readFile(schemaUrl, 'utf8'));
  const objectSchemas = collectObjectSchemas(schema);

  assert.ok(objectSchemas.length > 0);
  for (const objectSchema of objectSchemas) {
    assert.equal(objectSchema.additionalProperties, false);
  }
});

test('ProjectManifest schema validates the shared structural fixture corpus', async () => {
  const schema = JSON.parse(await fs.readFile(schemaUrl, 'utf8'));
  const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);
  const expectations = new Map([
    ['document-source-set.json', true],
    ['multi-document.json', true],
    ['primary-document.json', true],
    ['references.json', true],
    ['source-template.json', true],
    ['invalid-primary-document-not-defined.json', true],
    ['invalid-equal-source-roots.json', true],
    ['invalid-nested-source-roots.json', true],
    ['invalid-empty-reference-name.json', false],
    ['invalid-empty-common-modules-repository.json', false],
    ['invalid-empty-command-defaults.json', false],
    ['invalid-empty-excel-automation-defaults.json', false],
    ['invalid-empty-project-name.json', false],
    ['invalid-empty-primary-document.json', false],
    ['invalid-empty-test-defaults.json', false],
    ['invalid-mis-cased-document-kind.json', false],
    ['invalid-mis-cased-root-property.json', false],
    ['invalid-mis-cased-test-default-property.json', false],
    ['invalid-missing-primary-document.json', false],
    ['invalid-missing-reference-requested.json', false],
    ['invalid-missing-selection-arrays.json', false],
    ['invalid-missing-template-path.json', false],
    ['invalid-null-command-default.json', false],
    ['invalid-null-document.json', false],
    ['invalid-null-optional-property.json', false],
    ['invalid-null-reference.json', false],
    ['invalid-schema-version.json', false],
    ['invalid-standard-library-reference.json', false],
    ['invalid-test-execution-timeout.json', false],
    ['invalid-test-format.json', false],
    ['invalid-untrimmed-reference-name.json', false],
    ['invalid-unknown-excel-automation-default-property.json', false],
    ['invalid-unknown-common-module-property.json', false],
    ['invalid-unknown-command-default-property.json', false],
    ['invalid-unknown-test-default-property.json', false],
    ['invalid-workbook-open-timeout.json', false],
    ['invalid-workbook-save-timeout.json', false],
    ['invalid-unknown-document-property.json', false],
    ['invalid-unknown-root-property.json', false]
  ]);

  for (const [fixtureName, expectedValid] of expectations) {
    const fixture = JSON.parse(await fs.readFile(new URL(fixtureName, fixtureDirectoryUrl), 'utf8'));
    const actualValid = validate(fixture);
    assert.equal(
      actualValid,
      expectedValid,
      `${fixtureName}: ${new Ajv().errorsText(validate.errors)}`
    );
  }
});

function collectObjectSchemas(value) {
  if (Array.isArray(value)) {
    return value.flatMap(collectObjectSchemas);
  }

  if (value === null || typeof value !== 'object') {
    return [];
  }

  const nested = Object.values(value).flatMap(collectObjectSchemas);
  return value.type === 'object' ? [value, ...nested] : nested;
}
