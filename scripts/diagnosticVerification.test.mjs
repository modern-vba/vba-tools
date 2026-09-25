import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  diagnosticReleaseScripts,
  runDiagnosticVerification
} from './diagnosticVerification.mjs';

test('diagnostic verification continues independent stages after a failure and retains both outcomes', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const observed = [];
    const result = await runDiagnosticVerification({
      root,
      scripts: ['test:extension-host', 'test:devtool'],
      echo: false,
      getIdentity: async () => ({ commit: 'a'.repeat(40), dirtyPaths: [] }),
      execute: async ({ script, env, onStdout, onStderr }) => {
        observed.push({ script, runRoot: env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT });
        onStdout(Buffer.from(`${script} stdout`));
        onStderr(Buffer.from(`${script} stderr`));
        return { exitCode: script === 'test:extension-host' ? 1 : 0, signal: null, pid: 1234 };
      }
    });

    assert.equal(result.exitCode, 1);
    assert.deepEqual(observed.map((item) => item.script), ['test:extension-host', 'test:devtool']);
    assert.ok(observed.every((item) => item.runRoot === result.runRoot));
    assert.match(path.basename(result.runRoot), /^run-\d{8}T\d{9}Z-[0-9a-f]{16}$/);
    const manifest = JSON.parse(await fs.readFile(path.join(result.runRoot, 'manifest.json'), 'utf8'));
    assert.equal(manifest.kind, 'vba-tools-diagnostic-verification');
    assert.equal(manifest.releaseGate, false);
    assert.equal(manifest.identity.commit, 'a'.repeat(40));
    assert.deepEqual(manifest.stages.map((stage) => stage.status), ['failed', 'passed']);
    assert.equal(manifest.stages[0].launcherPid, 1234);
    assert.equal(await fs.readFile(path.join(result.runRoot, manifest.stages[0].stdoutFile), 'utf8'),
      'test:extension-host stdout');
    assert.equal(await fs.readFile(path.join(result.runRoot, manifest.stages[1].stderrFile), 'utf8'),
      'test:devtool stderr');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('diagnostic verification retains a spawn failure and still runs a later stage', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const observed = [];
    const result = await runDiagnosticVerification({
      root,
      scripts: ['test:extension-host', 'test:devtool'],
      echo: false,
      getIdentity: async () => ({ commit: 'b'.repeat(40), dirtyPaths: [] }),
      execute: async ({ script }) => {
        observed.push(script);
        if (script === 'test:extension-host') {
          throw new Error('controlled launch failure');
        }
        return { exitCode: 0, signal: null };
      }
    });
    assert.equal(result.exitCode, 1);
    assert.deepEqual(observed, ['test:extension-host', 'test:devtool']);
    const manifest = JSON.parse(await fs.readFile(path.join(result.runRoot, 'manifest.json'), 'utf8'));
    assert.match(manifest.stages[0].failure, /controlled launch failure/);
    assert.equal(manifest.stages[1].status, 'passed');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('diagnostic profile explicitly distinguishes omitted Excel stage from the Windows Excel profile', () => {
  assert.equal(diagnosticReleaseScripts.at(-1), 'package:verify');
  assert.ok(diagnosticReleaseScripts.includes('test:extension-host'));
  assert.ok(diagnosticReleaseScripts.includes('test:devtool'));
  assert.ok(diagnosticReleaseScripts.includes('test:packaging'));
  assert.ok(!diagnosticReleaseScripts.includes('test:windows-excel-integration'));
});

test('diagnostic verification bounds a log without converting the stage into a pass', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const result = await runDiagnosticVerification({
      root,
      scripts: ['package:verify'],
      maxLogBytes: 8,
      echo: false,
      getIdentity: async () => ({ commit: 'c'.repeat(40), dirtyPaths: [] }),
      execute: async ({ onStdout }) => {
        onStdout(Buffer.from('1234567890FAIL'));
        return { exitCode: 7, signal: null };
      }
    });
    assert.equal(result.exitCode, 1);
    const stage = result.manifest.stages[0];
    assert.equal(stage.status, 'failed');
    assert.equal(stage.stdoutTruncated, true);
    assert.equal(stage.stdoutOmittedBytes, 6);
    assert.equal(await fs.readFile(path.join(result.runRoot, stage.stdoutFile), 'utf8'),
      '12\n...[omitted 6 bytes]...\n90FAIL');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('diagnostic log retains ordered tail across multiple chunks without truncating a short stream', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const run = async (chunks) => runDiagnosticVerification({
      root,
      scripts: ['package:verify'],
      maxLogBytes: 8,
      echo: false,
      getIdentity: async () => ({ commit: 'c'.repeat(40), dirtyPaths: [] }),
      execute: async ({ onStdout }) => {
        for (const chunk of chunks) onStdout(Buffer.from(chunk));
        return { exitCode: 0, signal: null };
      }
    });
    const short = await run(['12', '345', '67']);
    assert.equal(short.manifest.stages[0].stdoutOmittedBytes, 0);
    assert.equal(await fs.readFile(path.join(short.runRoot, short.manifest.stages[0].stdoutFile), 'utf8'), '1234567');
    const long = await run(['1234', '567', '890', 'FAIL']);
    assert.equal(long.manifest.stages[0].stdoutOmittedBytes, 6);
    assert.equal(await fs.readFile(path.join(long.runRoot, long.manifest.stages[0].stdoutFile), 'utf8'),
      '12\n...[omitted 6 bytes]...\n90FAIL');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('Windows Excel diagnostic profile includes the native stage without treating it as a release gate', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const observed = [];
    const result = await runDiagnosticVerification({
      root,
      includeWindowsExcel: true,
      echo: false,
      getIdentity: async () => ({ commit: 'd'.repeat(40), dirtyPaths: [] }),
      execute: async ({ script }) => {
        observed.push(script);
        return { exitCode: 0, signal: null };
      }
    });
    assert.equal(result.exitCode, 0);
    assert.deepEqual(observed.slice(-5), [
      'build:devtool',
      'build:language-server',
      'test:devtool:windows-excel',
      'test:debug-adapter:windows-excel',
      'test:cross-product:windows-excel'
    ]);
    assert.equal(result.manifest.profile, 'windows-excel');
    assert.equal(result.manifest.releaseGate, false);
    assert.equal(result.manifest.complete, true);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('a failed native prerequisite skips only dependent stages and still runs independent work', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    const observed = [];
    const result = await runDiagnosticVerification({
      root,
      scripts: ['build:devtool', 'test:devtool:windows-excel', 'test:extension-host'],
      echo: false,
      getIdentity: async () => ({ commit: 'e'.repeat(40), dirtyPaths: [] }),
      execute: async ({ script }) => {
        observed.push(script);
        return { exitCode: script === 'build:devtool' ? 1 : 0, signal: null };
      }
    });
    assert.equal(result.exitCode, 1);
    assert.deepEqual(observed, ['build:devtool', 'test:extension-host']);
    assert.deepEqual(result.manifest.stages.map((stage) => stage.status),
      ['failed', 'skipped-prerequisite', 'passed']);
    assert.deepEqual(result.manifest.stages[1].blockedBy, ['build:devtool']);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('diagnostic verification rejects a Windows Excel profile on a non-Windows host', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    await assert.rejects(runDiagnosticVerification({
      root,
      includeWindowsExcel: true,
      hostPlatform: 'linux',
      getIdentity: async () => ({ commit: 'f'.repeat(40), dirtyPaths: [] }),
      execute: async () => ({ exitCode: 0, signal: null })
    }), /Windows Excel diagnostic profile requires Windows/);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('a log-open failure records an incomplete stage and does not suppress later independent stages', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    let opens = 0;
    const observed = [];
    const result = await runDiagnosticVerification({
      root,
      scripts: ['test:extension-host', 'test:devtool'],
      echo: false,
      getIdentity: async () => ({ commit: 'f'.repeat(40), dirtyPaths: [] }),
      openLog: async () => {
        opens += 1;
        if (opens === 1) throw new Error('controlled log-open failure');
        return { write() {}, async close() {}, truncated: () => false, omittedBytes: () => 0, error: () => null };
      },
      execute: async ({ script }) => {
        observed.push(script);
        return { exitCode: 0, signal: null };
      }
    });
    assert.equal(result.exitCode, 1);
    assert.deepEqual(observed, ['test:devtool']);
    assert.equal(result.manifest.stages[0].status, 'capture-failed');
    assert.match(result.manifest.stages[0].failure, /controlled log-open failure/);
    assert.equal(result.manifest.stages[0].stdoutFile, null);
    assert.equal(result.manifest.stages[0].stderrFile, null);
    assert.equal(result.manifest.stages[1].status, 'passed');
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('a second log-open failure names only the log file that was created', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-test-'));
  try {
    let opens = 0;
    const result = await runDiagnosticVerification({
      root,
      scripts: ['test:extension-host'],
      echo: false,
      getIdentity: async () => ({ commit: 'f'.repeat(40), dirtyPaths: [] }),
      openLog: async () => {
        if (++opens === 2) throw new Error('controlled stderr-open failure');
        return { write() {}, async close() {}, truncated: () => false, omittedBytes: () => 0, error: () => null };
      },
      execute: async () => { throw new Error('must not execute a stage without both logs'); }
    });
    assert.equal(result.manifest.stages[0].status, 'capture-failed');
    assert.notEqual(result.manifest.stages[0].stdoutFile, null);
    assert.equal(result.manifest.stages[0].stderrFile, null);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('dirty content fingerprints change when tracked or untracked bytes change', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-identity-test-'));
  try {
    await fs.writeFile(path.join(root, 'package.json'), JSON.stringify({ version: '0.1.0', packageManager: 'npm@11.19.1' }));
    await fs.writeFile(path.join(root, 'package-lock.json'), '{}');
    await fs.writeFile(path.join(root, '.gitignore'), '.tmp/\n');
    await fs.writeFile(path.join(root, 'tracked.txt'), 'before');
    execFileSync('git', ['init', '-q'], { cwd: root });
    execFileSync('git', ['config', 'core.autocrlf', 'false'], { cwd: root });
    execFileSync('git', ['add', '.'], { cwd: root });
    execFileSync('git', ['-c', 'user.name=Diagnostic Test', '-c', 'user.email=diagnostic@example.invalid',
      'commit', '-q', '-m', 'test fixture'], { cwd: root });
    const run = async () => runDiagnosticVerification({
      root,
      scripts: ['verify:architecture'],
      echo: false,
      execute: async () => ({ exitCode: 0, signal: null })
    });
    await fs.writeFile(path.join(root, 'tracked.txt'), 'first');
    await fs.writeFile(path.join(root, 'untracked.txt'), 'first');
    const first = await run();
    await fs.writeFile(path.join(root, 'tracked.txt'), 'second');
    const second = await run();
    await fs.writeFile(path.join(root, 'untracked.txt'), 'second');
    const third = await run();
    assert.equal(first.manifest.identity.dirtyContent.complete, true);
    assert.notEqual(first.manifest.identity.dirtyContent.fingerprint,
      second.manifest.identity.dirtyContent.fingerprint);
    assert.notEqual(second.manifest.identity.dirtyContent.fingerprint,
      third.manifest.identity.dirtyContent.fingerprint);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('diagnostic verification rejects a linked evidence root without writing outside the checkout', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-link-test-'));
  const outside = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-diagnostic-outside-test-'));
  try {
    await fs.symlink(outside, path.join(root, '.tmp'), 'junction');
    await assert.rejects(runDiagnosticVerification({
      root,
      scripts: ['verify:architecture'],
      getIdentity: async () => ({ commit: 'f'.repeat(40), dirtyPaths: [] }),
      execute: async () => ({ exitCode: 0, signal: null })
    }), /linked diagnostic evidence directory/);
    assert.deepEqual(await fs.readdir(outside), []);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
    await fs.rm(outside, { recursive: true, force: true });
  }
});
