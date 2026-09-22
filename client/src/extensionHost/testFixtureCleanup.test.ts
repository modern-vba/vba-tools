import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { access, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test, { TestContext } from 'node:test';
import { runWithExtensionHostFixtureCleanup } from './testFixtureCleanup';

test('snapshot fixture cleanup waits for a transient Windows directory handle after releasing its surface', {
  skip: process.platform !== 'win32', timeout: 15_000
}, async t => {
  const { directory, release } = await createLockedSourceDirectory(t);
  let surfaceReleased = false;
  let releaseTimer: NodeJS.Timeout | undefined;
  try {
    await runWithExtensionHostFixtureCleanup(directory, async () => {}, async () => {
      surfaceReleased = true;
      releaseTimer = setTimeout(release, 500);
    });
    assert.equal(surfaceReleased, true);
    await assert.rejects(access(directory), { code: 'ENOENT' });
  } finally {
    clearTimeout(releaseTimer);
    release();
  }
});

test('snapshot fixture cleanup bounds a persistent directory lock and preserves its body failure', {
  skip: process.platform !== 'win32', timeout: 30_000
}, async t => {
  const { directory } = await createLockedSourceDirectory(t);
  const primary = new Error('snapshot functional assertion failed');
  let deadline: NodeJS.Timeout | undefined;
  try {
    await assert.rejects(Promise.race([
      runWithExtensionHostFixtureCleanup(directory, async () => { throw primary; }, async () => {}),
      new Promise<never>((_resolve, reject) => {
        deadline = setTimeout(() => reject(new Error('Cleanup exceeded ten seconds.')), 10_000);
      })
    ]), (error: unknown) => {
      assert.ok(error instanceof AggregateError);
      assert.equal(error.cause, primary);
      assert.equal(error.errors[0], primary);
      assert.equal(error.errors[1].cause.code, 'EBUSY');
      assert.equal(error.errors[1].cause.syscall, 'rmdir');
      assert.equal(error.errors[1].cause.path, path.join(directory, 'src', '日本語'));
      return true;
    });
    await access(directory);
  } finally {
    clearTimeout(deadline);
  }
});

test('snapshot fixture cleanup preserves both failures and retains files when surface release is unproved', async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-release-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const primary = new Error('snapshot assertion failed');
  const releaseFailure = new Error('owned debug session teardown failed');
  await assert.rejects(runWithExtensionHostFixtureCleanup(directory,
    async () => { throw primary; }, async () => { throw releaseFailure; }), (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.cause, primary);
    assert.equal(error.errors.length, 2);
    assert.equal(error.errors[0], primary);
    assert.equal(error.errors[1], releaseFailure);
    return true;
  });
  await access(directory);
});

test('snapshot fixture cleanup keeps the exact release error and never removes an unreleased surface', async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-release-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const releaseFailure = new Error('fixture ownership is unproved');
  await assert.rejects(runWithExtensionHostFixtureCleanup(directory,
    async () => {}, async () => { throw releaseFailure; }), error => error === releaseFailure);
  await access(directory);
});

test('snapshot fixture cleanup removes files only after its asynchronous surface release settles', {
  timeout: 10_000
}, async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-release-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  let enterRelease!: () => void;
  let finishRelease!: () => void;
  const entered = new Promise<void>(resolve => { enterRelease = resolve; });
  const released = new Promise<void>(resolve => { finishRelease = resolve; });
  const completed = runWithExtensionHostFixtureCleanup(directory, async () => {}, async () => {
    enterRelease();
    await released;
  });
  let deadline: NodeJS.Timeout | undefined;
  try {
    await Promise.race([entered, new Promise<never>((_resolve, reject) => {
      deadline = setTimeout(() => reject(new Error('Surface release did not start.')), 5_000);
    })]);
    await access(directory);
  } finally {
    clearTimeout(deadline);
    finishRelease();
    await completed;
  }
  await assert.rejects(access(directory), { code: 'ENOENT' });
});

test('snapshot fixture cleanup retains exact thrown values after successful release and removal', async t => {
  for (const failure of [new Error('body failed'), undefined, null, false, 0]) {
    const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-release-test-'));
    t.after(() => rm(directory, { recursive: true, force: true }));
    await assert.rejects(runWithExtensionHostFixtureCleanup(directory,
      async () => { throw failure; }, async () => {}), error => error === failure);
    await assert.rejects(access(directory), { code: 'ENOENT' });
  }
});

async function createLockedSourceDirectory(t: TestContext): Promise<{
  directory: string;
  release: () => void;
}> {
  const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-snapshot-cleanup-test-'));
  const source = path.join(directory, 'src', '日本語');
  await mkdir(source, { recursive: true });
  const nativeLock = [
    'using System;',
    'using System.ComponentModel;',
    'using System.Runtime.InteropServices;',
    'using Microsoft.Win32.SafeHandles;',
    'public static class SnapshotDirectoryLock {',
    '[DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]',
    'private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);',
    'public static SafeFileHandle Open(string directory) {',
    'var handle = CreateFileW(directory, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);',
    'if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());',
    'return handle; } }'
  ].join('\n');
  const child = spawn(path.join(process.env.SystemRoot ?? 'C:\\Windows',
    'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
  ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', [
    "$ErrorActionPreference = 'Stop'",
    'Add-Type -TypeDefinition $env:VBA_TOOLS_TEST_DIRECTORY_LOCK_TYPE',
    '$handle = [SnapshotDirectoryLock]::Open($env:VBA_TOOLS_TEST_LOCK_DIRECTORY)',
    "try { [Console]::Out.WriteLine('locked'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null } finally { $handle.Dispose() }"
  ].join('; ')], {
    env: { ...process.env, VBA_TOOLS_TEST_DIRECTORY_LOCK_TYPE: nativeLock,
      VBA_TOOLS_TEST_LOCK_DIRECTORY: source },
    windowsHide: true, stdio: ['pipe', 'pipe', 'pipe']
  });
  let stderr = '';
  child.stderr.on('data', data => { stderr += String(data); });
  child.stdin.on('error', () => {});
  const closed = new Promise<number | null>(resolve => child.once('close', resolve));
  let released = false;
  const release = (): void => {
    if (!released) { released = true; child.stdin.end('release\n'); }
  };
  t.after(async () => {
    release();
    const watchdog = setTimeout(() => child.kill(), 3_000);
    try {
      assert.equal(await closed, 0, stderr);
    } finally {
      clearTimeout(watchdog);
      assert.equal(path.dirname(directory), tmpdir());
      assert.ok(path.basename(directory).startsWith('vba-tools-snapshot-cleanup-test-'));
      await rm(directory, { recursive: true, force: true });
    }
  });
  await new Promise<void>((resolve, reject) => {
    let stdout = '';
    child.stdout.on('data', data => {
      stdout += String(data);
      if (stdout.includes('locked')) resolve();
    });
    child.once('error', reject);
    child.once('close', code => reject(new Error(`Directory lock child exited ${code}: ${stderr}`)));
  });
  return { directory, release };
}
