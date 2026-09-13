import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';
import { admitVbaDevCapabilities } from './capabilityAdmission';
import { loadRequiredVbaDevContractFile } from './vbaDevOutputContract';

const root = path.resolve(__dirname, '..', '..');
const extensionRequirements = loadRequiredVbaDevContractFile(path.join(root, 'vba-dev-contract.json'));
const corpus = JSON.parse(readFileSync(path.join(root, 'fixtures', 'capability-admission', 'cases.json'), 'utf8')) as {
  schemaVersion: number;
  cases: Array<{ id: string; rawJson: string; expectedByProfile: { extension?: string } }>;
};
assert.equal(corpus.schemaVersion, 1);
for (const fixture of corpus.cases) {
  if (fixture.expectedByProfile.extension === undefined) continue;
  test('extension capability conformance: ' + fixture.id, () => {
    const admitted = admitVbaDevCapabilities(fixture.rawJson, extensionRequirements);
    assert.equal(admitted.accepted ? 'Accepted' : admitted.rejection.code, fixture.expectedByProfile.extension);
  });
}

test('capability admission matches prototype-like names as own offered capabilities', () => {
  const requirements = {
    contractVersion: '1.0',
    commandSchemaVersions: JSON.parse('{"__proto__":"1.0","constructor":"1.0"}') as Record<string, string>,
    featureVersions: JSON.parse('{"__proto__":"1.0","constructor":"1.0"}') as Record<string, string>
  };
  const raw = '{"toolVersion":"t","contractVersion":"1.0","commands":{"__proto__":{"outputSchemaVersion":"1.0"},"constructor":{"outputSchemaVersion":"1.0"}},"featureVersions":{"__proto__":"1.0","constructor":"1.0"}}';
  const admitted = admitVbaDevCapabilities(raw, requirements);
  assert.equal(admitted.accepted, true);
  if (admitted.accepted) {
    assert.equal(Object.hasOwn(admitted.facts.commands, '__proto__'), true);
    assert.equal(admitted.facts.commands.__proto__.outputSchemaVersion, '1.0');
    assert.equal(admitted.facts.featureVersions?.constructor, '1.0');
    assert.equal(Object.getPrototypeOf(admitted.facts.commands), Object.prototype);
    assert.equal(Object.isFrozen(admitted.facts), true);
    assert.equal(Object.isFrozen(admitted.facts.commands.__proto__), true);
  }
  const absent = admitVbaDevCapabilities('{"toolVersion":"t","contractVersion":"1.0","commands":{},"featureVersions":{}}', requirements);
  assert.equal(absent.accepted, false);
  if (!absent.accepted) assert.equal(absent.rejection.code, 'MissingCapability');
});

test('capability admission classifies undecodable consumed strings without accepting their values', () => {
  for (const raw of [
    '{"toolVersion":"\\uD800","contractVersion":"1.0","commands":{}}',
    '{"toolVersion":"t","contractVersion":"\\uD800","commands":{}}',
    '{"toolVersion":"t","contractVersion":"1.0","commands":{"extra":{"outputSchemaVersion":"\\uD800"}}}',
    '{"toolVersion":"t","contractVersion":"1.0","commands":{},"featureVersions":{"extra":"\\uD800"}}'
  ]) {
    const admitted = admitVbaDevCapabilities(raw, { contractVersion: '1.0', commandSchemaVersions: {} });
    assert.equal(admitted.accepted, false, raw);
    if (!admitted.accepted) assert.equal(admitted.rejection.code, 'InvalidConsumedValue', raw);
  }
});

test('capability admission rejects raw incomplete UTF-16 without replacing it', () => {
  const raw = '{"toolVersion":"","contractVersion":"1.0","commands":{},"unknown":"' + String.fromCharCode(0xd800) + '"}';
  const admitted = admitVbaDevCapabilities(raw, { contractVersion: '1.0', commandSchemaVersions: {} });
  assert.equal(admitted.accepted, false);
  if (!admitted.accepted) assert.equal(admitted.rejection.code, 'InvalidJson');
});

test('capability admission rejects an undecodable escaped property name as invalid JSON', () => {
  const admitted = admitVbaDevCapabilities('{"toolVersion":"","contractVersion":"1.0","commands":{},"\\uD800":1}',
    { contractVersion: '1.0', commandSchemaVersions: {} });
  assert.equal(admitted.accepted, false);
  if (!admitted.accepted) assert.equal(admitted.rejection.code, 'InvalidJson');
});

test('capability admission preserves the extension minimum response', () => {
  const admitted = admitVbaDevCapabilities('{"toolVersion":"","contractVersion":"1.0","commands":{}}',
    { contractVersion: '1.0', commandSchemaVersions: {} });
  assert.equal(admitted.accepted, true);
  if (admitted.accepted) {
    assert.equal(admitted.facts.toolVersion, '');
    assert.deepEqual(admitted.facts.commands, {});
  }
});
