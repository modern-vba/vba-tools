import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import { access, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test, { TestContext } from 'node:test';
import { runWithExtensionHostCleanup } from './testRunCleanup';

test('Extension Host failure evidence is saved before the owned profile is removed', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-failure-evidence-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profile = path.join(root, 'profile');
  const evidence = path.join(root, 'evidence');
  const failure = new Error('diagnostics=[]');
  await fs.mkdir(path.join(profile, 'logs'), { recursive: true });
  await writeFile(path.join(profile, 'logs', 'language-server.log'), 'connection closed');

  await assert.rejects(runWithExtensionHostCleanup([profile], async () => {
    throw failure;
  }, async () => {
    await fs.cp(path.join(profile, 'logs'), evidence, { recursive: true });
  }), error => error === failure);

  assert.equal(await fs.readFile(path.join(evidence, 'language-server.log'), 'utf8'),
    'connection closed');
  await assert.rejects(access(profile), { code: 'ENOENT' });
});

test('Extension Host evidence failure preserves the run failure and still cleans every root', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-evidence-failure-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profiles = [path.join(root, 'first'), path.join(root, 'second')];
  for (const profile of profiles) { await fs.mkdir(profile); }
  const primary = new Error('test failed');
  const evidence = new Error('evidence copy failed');

  await assert.rejects(runWithExtensionHostCleanup(profiles, async () => {
    throw primary;
  }, async () => { throw evidence; }), (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.cause, primary);
    assert.deepEqual(error.errors, [primary, evidence]);
    return true;
  });
  for (const profile of profiles) {
    await assert.rejects(access(profile), { code: 'ENOENT' });
  }
});

test('Extension Host cleanup tolerates a real transient Windows telemetry file lock', {
  skip: process.platform !== 'win32',
  timeout: 15_000
}, async t => {
  const { directory, release } = await createLockedProfile(t);
  let releaseTimer: NodeJS.Timeout | undefined;
  try {
    await runWithExtensionHostCleanup([directory], async () => {
      releaseTimer = setTimeout(release, 500);
    });
    await assert.rejects(access(directory), { code: 'ENOENT' });
  } finally {
    clearTimeout(releaseTimer);
    release();
  }
});

test('Extension Host cleanup fails within a bounded wait when a Windows lock persists', {
  skip: process.platform !== 'win32',
  timeout: 30_000
}, async t => {
  const { directory } = await createLockedProfile(t);
  let deadline: NodeJS.Timeout | undefined;
  try {
    // Start the budget only after PowerShell confirms the file is locked.
    await assert.rejects(Promise.race([
      runWithExtensionHostCleanup([directory], async () => {}),
      new Promise<never>((_resolve, reject) => {
        deadline = setTimeout(() => reject(new Error('Cleanup exceeded ten seconds.')), 10_000);
      })
    ]), { code: 'EBUSY' });
    await access(directory);
  } finally {
    clearTimeout(deadline);
  }
});

test('Extension Host cleanup retains the test failure when removal also fails', async t => {
  const primary = new Error('Restricted host exited 17');
  const cleanup = Object.assign(new Error('Removal failed'), { code: 'EIO' });
  t.mock.method(fs, 'rm', async () => { throw cleanup; });
  await assert.rejects(runWithExtensionHostCleanup(['profile'], async () => {
    throw primary;
  }), (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.cause, primary);
    assert.equal(error.errors[0], primary);
    assert.equal(error.errors[1].cause, cleanup);
    assert.match(error.errors[1].message, /profile/);
    return true;
  });
});

test('Extension Host cleanup attempts later directories and retains every removal failure', async t => {
  const attempted: string[] = [];
  const failures = [new Error('First removal failed'), new Error('Second removal failed')];
  t.mock.method(fs, 'rm', async (directory: string) => {
    attempted.push(directory);
    if (directory === 'first') { throw failures[0]; }
    if (directory === 'second') { throw failures[1]; }
  });
  await assert.rejects(runWithExtensionHostCleanup(['first', 'second', 'last'], async () => {}),
    (error: unknown) => {
      assert.deepEqual(attempted, ['first', 'second', 'last']);
      assert.ok(error instanceof AggregateError);
      assert.deepEqual(error.errors.map(item => item.cause), failures);
      return true;
    });
});

test('Extension Host cleanup removes all owned roots after a successful run', async t => {
  const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-cleanup-success-test-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const profiles = [path.join(root, 'first'), path.join(root, 'second')];
  await runWithExtensionHostCleanup(profiles, async () => {
    for (const profile of profiles) {
      await fs.mkdir(profile);
      await writeFile(path.join(profile, 'fixture.txt'), 'owned');
    }
  });
  for (const profile of profiles) {
    await assert.rejects(access(profile), { code: 'ENOENT' });
  }
});

test('Extension Host cleanup preserves original thrown values when cleanup succeeds', async t => {
  t.mock.method(fs, 'rm', async () => {});
  for (const failure of [new Error('test failed'), undefined, null, false, 0]) {
    let observed = false;
    await assert.rejects(runWithExtensionHostCleanup(['profile'], async () => {
      throw failure;
    }, async () => { observed = true; }), error => error === failure);
    assert.equal(observed, true);
  }
});

test('Extension Host success does not invoke failure evidence capture', async t => {
  t.mock.method(fs, 'rm', async () => {});
  await runWithExtensionHostCleanup(['profile'], async () => {}, async () => {
    assert.fail('Successful runs must not capture failure logs.');
  });
});

test('Extension Host finalization retains evidence and all cleanup failures after the primary', async t => {
  const primary = new Error('test failed');
  const evidence = new Error('evidence failed');
  const cleanup = new Error('cleanup failed');
  const attempted: string[] = [];
  t.mock.method(fs, 'rm', async (directory: string) => {
    attempted.push(directory);
    throw cleanup;
  });
  await assert.rejects(runWithExtensionHostCleanup(['first', 'second'], async () => {
    throw primary;
  }, async () => { throw evidence; }), (error: unknown) => {
    assert.ok(error instanceof AggregateError);
    assert.equal(error.cause, primary);
    assert.deepEqual(error.errors.slice(0, 2), [primary, evidence]);
    assert.deepEqual(error.errors.slice(2).map(item => item.cause), [cleanup, cleanup]);
    assert.deepEqual(attempted, ['first', 'second']);
    return true;
  });
});

test('Extension Host cleanup does not retry or hide an unrelated removal error', async t => {
  const failure = Object.assign(new Error('I/O failure'), { code: 'EIO' });
  t.mock.method(fs, 'rm', async () => {}).mock.mockImplementationOnce(async () => {
    throw failure;
  });
  await assert.rejects(
    runWithExtensionHostCleanup(['profile'], async () => {}),
    error => error === failure
  );
});

async function createLockedProfile(t: TestContext): Promise<{
  directory: string;
  release: () => void;
}> {
  const directory = await mkdtemp(path.join(tmpdir(), 'vba-tools-cleanup-test-'));
  const file = path.join(directory, 'agentHostTelemetry.log');
  await writeFile(file, '');
  // Own one hidden child and one unique directory; never inspect or stop user apps.
  const child = spawn(path.join(
    process.env.SystemRoot ?? 'C:\\Windows',
    'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'
  ), ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', [
    "$ErrorActionPreference = 'Stop'",
    '$stream = [System.IO.File]::Open($env:VBA_TOOLS_TEST_LOCK_FILE, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)',
    "try { [Console]::Out.WriteLine('locked'); [Console]::Out.Flush(); [Console]::In.ReadLine() | Out-Null } finally { $stream.Dispose() }"
  ].join('; ')], {
    env: { ...process.env, VBA_TOOLS_TEST_LOCK_FILE: file },
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe']
  });
  let stderr = '';
  child.stderr.on('data', data => { stderr += String(data); });
  child.stdin.on('error', () => { /* Exit is checked by the fixture finalizer. */ });
  const closed = new Promise<number | null>(resolve => child.once('close', resolve));
  let released = false;
  const release = (): void => {
    if (!released) {
      released = true;
      child.stdin.end('release\n');
    }
  };
  t.after(async () => {
    release();
    const safetyTimer = setTimeout(() => child.kill(), 3_000);
    try {
      assert.equal(await closed, 0, stderr);
    } finally {
      clearTimeout(safetyTimer);
      await rm(directory, { recursive: true, force: true });
    }
  });
  await new Promise<void>((resolve, reject) => {
    let output = '';
    child.stdout.on('data', data => {
      output += String(data);
      if (output.includes('locked')) { resolve(); }
    });
    child.once('error', reject);
    child.once('close', code => reject(new Error(`Lock fixture exited ${code}: ${stderr}`)));
  });
  return { directory, release };
}
