import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import { PassThrough } from 'node:stream';
import { setImmediate } from 'node:timers/promises';
import test, { TestContext } from 'node:test';
import { runRestrictedModeExtensionHostTests } from './restrictedModeExtensionHost';

test('Restricted Mode runner waits for stdio closure after a successful process exit', async t => {
  const { child, result } = await startControlledHost(t);
  let settled = false;
  void result.then(() => { settled = true; }, () => { settled = true; });
  child.emit('exit', 0, null);
  await setImmediate();
  assert.equal(settled, false, 'process exit must not start profile cleanup before stdio closes');
  child.emit('close', 0, null);
  await result;
});

test('Restricted Mode runner retains a spawn error but waits for stream closure', async t => {
  const { child, result } = await startControlledHost(t);
  const failure = Object.assign(new Error('Code could not start'), { code: 'ENOENT' });
  let settled = false;
  void result.then(() => { settled = true; }, () => { settled = true; });
  child.emit('error', failure);
  await setImmediate();
  assert.equal(settled, false);
  child.emit('close', -1, null);
  await assert.rejects(result, error => error === failure);
});

for (const outcome of [
  { code: 17, signal: null, message: /failed with exit code 17/ },
  { code: null, signal: 'SIGTERM', message: /ended with signal SIGTERM/ }
]) {
  test(`Restricted Mode runner preserves failed termination: ${outcome.code ?? outcome.signal}`, async t => {
    const { child, result } = await startControlledHost(t);
    const rejected = assert.rejects(result, outcome.message);
    child.emit('exit', outcome.code, outcome.signal);
    child.emit('close', outcome.code, outcome.signal);
    await rejected;
  });
}

test('Restricted Mode runner reports a real executable launch failure', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-host-spawn-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  await assert.rejects(runRestrictedModeExtensionHostTests({
    vscodeExecutablePath: path.join(root, 'missing-code.exe'),
    extensionDevelopmentPath: root,
    extensionTestsPath: root,
    userDataPath: path.join(root, 'profile'),
    workspacePath: root,
    extensionTestsEnvironment: {}
  }), { code: 'ENOENT' });
});

async function startControlledHost(t: TestContext): Promise<{
  child: childProcess.ChildProcess;
  result: Promise<void>;
}> {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-host-lifecycle-test-'));
  const child = Object.assign(new childProcess.ChildProcess(), {
    stdout: new PassThrough(),
    stderr: new PassThrough()
  });
  let markSpawned!: () => void;
  const spawned = new Promise<void>(resolve => { markSpawned = resolve; });
  t.mock.method(childProcess, 'spawn', () => {
    markSpawned();
    return child;
  });
  t.after(async () => {
    child.emit('close', 0, null);
    child.stdout.destroy();
    child.stderr.destroy();
    await rm(root, { recursive: true, force: true });
  });
  const result = runRestrictedModeExtensionHostTests({
    vscodeExecutablePath: 'controlled-code',
    extensionDevelopmentPath: root,
    extensionTestsPath: root,
    userDataPath: path.join(root, 'profile'),
    workspacePath: root,
    extensionTestsEnvironment: {}
  });
  await Promise.race([spawned, result]);
  return { child, result };
}
