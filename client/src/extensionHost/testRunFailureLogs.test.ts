import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import { access, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';
import { runWithExtensionHostCleanup } from './testRunCleanup';
import { resolveExtensionHostFailureLogRoot, saveExtensionHostFailureLogs } from './testRunFailureLogs';

test('Extension Host failure logs join a validated diagnostic verification run', () => {
  const extensionRoot = path.resolve('extension');
  const runRoot = path.resolve('evidence', 'run-20260925T101530123Z-0123456789abcdef');
  assert.equal(resolveExtensionHostFailureLogRoot(extensionRoot, {
    VBA_TOOLS_DIAGNOSTIC_RUN_ROOT: runRoot
  }), path.join(runRoot, 'extension-host'));
  assert.equal(resolveExtensionHostFailureLogRoot(extensionRoot, {
    VBA_TOOLS_DIAGNOSTIC_RUN_ROOT: path.resolve('evidence')
  }), path.join(extensionRoot, '.tmp', 'extension-host-failures'));
  if (process.platform === 'win32') {
    assert.equal(resolveExtensionHostFailureLogRoot(extensionRoot, {
      VBA_TOOLS_DIAGNOSTIC_RUN_ROOT: '//invalid.example/share/run-20260925T101530123Z-0123456789abcdef'
    }), path.join(extensionRoot, '.tmp', 'extension-host-failures'));
  }
});

test('Extension Host failure capture retains only isolated profile logs after cleanup', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-failure-logs-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profile = path.join(root, 'profile');
  const evidenceRoot = path.join(root, 'evidence');
  await mkdir(path.join(profile, 'logs', 'session', 'exthost'), { recursive: true });
  await mkdir(path.join(profile, 'User'));
  await writeFile(path.join(profile, 'logs', 'session', 'exthost', 'server.log'), 'server exited');
  await writeFile(path.join(profile, 'User', 'settings.json'), 'excluded');
  const primary = new Error('diagnostics=[]');
  let evidence: string | undefined;

  await assert.rejects(runWithExtensionHostCleanup([profile], async () => {
    throw primary;
  }, async () => {
    evidence = await saveExtensionHostFailureLogs([{ name: 'primary', userDataPath: profile }],
      evidenceRoot);
  }), error => error === primary);

  assert.ok(evidence);
  assert.equal(await readFile(path.join(evidence, 'primary', 'session', 'exthost', 'server.log'),
    'utf8'), 'server exited');
  assert.deepEqual(await readdir(path.join(evidence, 'primary')), ['session']);
  await assert.rejects(access(profile), { code: 'ENOENT' });
});

test('Extension Host failure capture tolerates profiles whose host never started', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-unstarted-logs-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profile = path.join(root, 'profile');
  await mkdir(path.join(profile, 'logs'), { recursive: true });
  await writeFile(path.join(profile, 'logs', 'server.log'), 'available');
  const evidence = await saveExtensionHostFailureLogs([
    { name: 'not-started', userDataPath: path.join(root, 'not-started') },
    { name: 'primary', userDataPath: profile }
  ], path.join(root, 'evidence'));
  assert.equal(await readFile(path.join(evidence, 'primary', 'server.log'), 'utf8'), 'available');
  await assert.rejects(access(path.join(evidence, 'not-started')), { code: 'ENOENT' });
});

test('Extension Host failure capture never follows or copies links out of its logs', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-linked-logs-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profile = path.join(root, 'profile');
  const outside = path.join(root, 'outside');
  await mkdir(path.join(profile, 'logs'), { recursive: true });
  await mkdir(outside);
  await writeFile(path.join(outside, 'private.txt'), 'excluded');
  await symlink(outside, path.join(profile, 'logs', 'linked'), 'junction');
  const evidence = await saveExtensionHostFailureLogs([{ name: 'primary', userDataPath: profile }],
    path.join(root, 'evidence'));
  assert.deepEqual(await readdir(path.join(evidence, 'primary')), []);
  assert.equal(await readFile(path.join(outside, 'private.txt'), 'utf8'), 'excluded');
});

test('Extension Host failure capture refuses a linked evidence ancestor', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-linked-evidence-test-'));
  const outside = await mkdtemp(path.join(tmpdir(), 'vba-tools-linked-evidence-outside-'));
  t.after(async () => {
    await rm(root, { recursive: true, force: true });
    await rm(outside, { recursive: true, force: true });
  });
  const profile = path.join(root, 'profile');
  await mkdir(path.join(profile, 'logs'), { recursive: true });
  await writeFile(path.join(profile, 'logs', 'server.log'), 'primary failure');
  const linked = path.join(root, 'linked');
  await symlink(outside, linked, 'junction');

  await assert.rejects(saveExtensionHostFailureLogs(
    [{ name: 'primary', userDataPath: profile }], path.join(linked, 'evidence')),
  /linked diagnostic evidence directory/);
  assert.deepEqual(await readdir(outside), []);
});

test('Extension Host failure capture keeps later logs when one profile cannot be copied', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-partial-logs-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profiles = ['first', 'second'].map(name => ({ name, userDataPath: path.join(root, name) }));
  for (const profile of profiles) {
    await mkdir(path.join(profile.userDataPath, 'logs'), { recursive: true });
    await writeFile(path.join(profile.userDataPath, 'logs', 'server.log'), profile.name);
  }
  const failure = Object.assign(new Error('copy failed'), { code: 'EIO' });
  const originalCopy = fs.cp;
  t.mock.method(fs, 'cp', originalCopy).mock.mockImplementationOnce(async () => { throw failure; });
  const evidenceRoot = path.join(root, 'evidence');
  await assert.rejects(saveExtensionHostFailureLogs(profiles, evidenceRoot), (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.errors[0].cause, failure);
    assert.match(error.errors[0].message, /first/);
    assert.ok(error.message.includes(evidenceRoot));
    return true;
  });
  const entries = await readdir(evidenceRoot);
  assert.equal(entries.length, 1);
  assert.equal(await readFile(path.join(evidenceRoot, entries[0], 'second', 'server.log'), 'utf8'),
    'second');
});
