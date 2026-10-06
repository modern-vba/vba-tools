import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';
import { runWithForegroundAssist } from './foregroundAssist';

test('test foreground assist remains available through a debug launch and exits afterward', async () => {
  const result = await runWithForegroundAssist({
    executable: process.execPath,
    arguments: ['-e', [
      'process.stdout.write("READY\\n");',
      'process.stdin.resume();',
      'process.stdin.on("end", () => process.exit(0));'
    ].join('')]
  }, async () => 'debug launch completed');

  assert.equal(result, 'debug launch completed');
});

test('test foreground assist preserves the original debug launch failure', async () => {
  const launchFailure = new Error('owned VBE refused the native command');
  await assert.rejects(runWithForegroundAssist({
    executable: process.execPath,
    arguments: ['-e', [
      'process.stdout.write("READY\\n");',
      'process.stdin.resume();',
      'process.stdin.on("end", () => process.exit(0));'
    ].join('')]
  }, async () => { throw launchFailure; }), error => error === launchFailure);
});

test('test foreground assist cannot exit early and silently pass a debug launch', async () => {
  await assert.rejects(runWithForegroundAssist({
    executable: process.execPath,
    arguments: ['-e', 'process.stdout.write("READY\\n");process.exit(0);']
  }, async () => {
    await new Promise(resolve => setTimeout(resolve, 100));
    return true;
  }), /before debug launch completed/);
});

test('test foreground assist never turns an undefined rejection into success', async () => {
  let rejected = false;
  try {
    await runWithForegroundAssist({
      executable: process.execPath,
      arguments: ['-e', [
        'process.stdout.write("READY\\n");',
        'process.stdin.resume();',
        'process.stdin.on("end", () => process.exit(0));'
      ].join('')]
    }, async () => Promise.reject(undefined));
  } catch (error) {
    rejected = true;
    assert.equal(error, undefined);
  }
  assert.equal(rejected, true);
});

test('test foreground assist does not run the debug launch when its executable is missing', async () => {
  let launched = false;
  await assert.rejects(runWithForegroundAssist({
    executable: path.join(tmpdir(), `missing-vba-foreground-assist-${randomUUID()}.exe`),
    arguments: []
  }, async () => {
    launched = true;
    return true;
  }), error => error instanceof Error &&
    error.message.includes('did not become ready') &&
    error.message.includes('missing-vba-foreground-assist-') &&
    error.message.includes('ENOENT') &&
    error.message.includes('exit code'));
  assert.equal(launched, false);
});
