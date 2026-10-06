import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';
import { captureSnapshotInputEvidence } from './snapshotInputEvidence';

test('snapshot evidence records exact relative paths, byte lengths and hashes before a child starts', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-evidence-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  await mkdir(path.join(root, 'nested'));
  const first = Buffer.from('Attribute VB_Name = "First"\r\n', 'utf8');
  const second = Buffer.from([0, 1, 2, 255]);
  await writeFile(path.join(root, 'First.bas'), first);
  await writeFile(path.join(root, 'nested', 'Form.frx'), second);

  const evidence = captureSnapshotInputEvidence(root);
  assert.equal(evidence.status, 'complete');
  if (evidence.status !== 'complete') return;
  assert.deepEqual(evidence.files, [
    { path: 'First.bas', bytes: first.length, sha256: createHash('sha256').update(first).digest('hex') },
    { path: 'nested/Form.frx', bytes: second.length, sha256: createHash('sha256').update(second).digest('hex') }
  ]);
  assert.equal(evidence.totalBytes, first.length + second.length);
  assert.equal(evidence.treeSha256, createHash('sha256').update(evidence.files
    .map(file => `${file.path}\0${file.bytes}\0${file.sha256}\n`).join(''), 'utf8').digest('hex'));
});

test('snapshot evidence marks file-count and byte limits as incomplete receipts', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-limits-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  await writeFile(path.join(root, 'A.bas'), 'abc');
  await writeFile(path.join(root, 'B.bas'), 'def');
  assert.equal(captureSnapshotInputEvidence(root, { maxFiles: 1 }).status, 'unavailable');
  assert.match(JSON.stringify(captureSnapshotInputEvidence(root, { maxFiles: 1 })), /file-count-limit/);
  assert.match(JSON.stringify(captureSnapshotInputEvidence(root, { maxBytes: 2 })), /byte-limit/);
});

test('snapshot evidence reports missing input as a read failure, not a complete empty tree', async () => {
  const missing = path.join(tmpdir(), 'vba-tools-missing-snapshot-' + Math.random().toString(16).slice(2));
  const evidence = captureSnapshotInputEvidence(missing);
  assert.equal(evidence.status, 'unavailable');
  if (evidence.status !== 'unavailable') return;
  assert.equal(evidence.reason, 'read-failed');
  assert.equal(evidence.errorCode, 'ENOENT');
});
