import assert from 'node:assert/strict';
import { spawn, type SpawnOptionsWithoutStdio } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import test from 'node:test';
import { runCompanionCommand } from '../devtoolCommand';
import { IntegrationFailureDiagnostics } from './integrationFailureDiagnostics';

test('failure diagnostics retain bounded output tails from only the current phase', () => {
  const diagnostics = new IntegrationFailureDiagnostics();
  diagnostics.beginPhase('palette validation');
  diagnostics.record('events', 'previous phase event\n');
  diagnostics.beginPhase('Explorer invalid source');
  diagnostics.record('stdout', 'discarded prefix\n' + 'x'.repeat(3000));
  diagnostics.record('stdout', '\nlatest CLI output');
  diagnostics.record('events', 'errored:actual error\n');
  diagnostics.record('notifications', 'actual notification\n');

  const report = diagnostics.format();
  assert.match(report, /Explorer invalid source/);
  assert.match(report, /errored:actual error/);
  assert.match(report, /actual notification/);
  assert.match(report, /latest CLI output/);
  assert.match(report, /truncated/);
  assert.doesNotMatch(report, /previous phase event|discarded prefix/);
  assert.ok(report.length < 4000);
});

test('process diagnostics preserve streaming, stdin cancellation and the real close result', { timeout: 10000 }, async context => {
  const diagnostics = new IntegrationFailureDiagnostics();
  const script = `
    process.stdout.write('ready\\n');
    process.stdin.setEncoding('utf8');
    process.stdin.once('data', value => {
      process.stdout.write('received:' + value);
      process.stderr.write('final stderr\\n');
      process.exitCode = 17;
      process.stdin.resume();
    });
  `;
  const child = diagnostics.startProcess(process.execPath, ['-e', script, '--', '--cancellation-transport', 'stdin-v1']);
  context.after(() => child.kill());
  let stdout = '';
  let stderr = '';
  let cancellation: Promise<void> | undefined;
  try {
    const result = await new Promise<{ code: number | null; signal: string | null }>((resolve, reject) => {
      child.onStdout(value => {
        stdout += value;
        if (stdout === 'ready\n') {
          // The child cannot close until this live output reaches its consumer.
          assert.doesNotMatch(diagnostics.format(), /close: code=/);
          cancellation = child.requestCancellation!();
          void cancellation.catch(reject);
        }
      });
      child.onStderr(value => { stderr += value; });
      child.onError!(reject);
      child.onClose!((code, signal) => {
        assert.match(diagnostics.format(), /close: code=17 signal=null/);
        resolve({ code, signal });
      });
    });
    await cancellation;
    assert.deepEqual(result, { code: 17, signal: null });
    assert.equal(stdout, 'ready\nreceived:cancel\n');
    assert.equal(stderr, 'final stderr\n');
    assert.match(diagnostics.format(), /received:cancel/);
    assert.match(diagnostics.format(), /final stderr/);
  } finally { child.kill(); }
});

test('all diagnostic sections stay bounded even after repeated large output chunks', () => {
  const diagnostics = new IntegrationFailureDiagnostics();
  diagnostics.beginPhase('p'.repeat(1000));
  for (const section of ['events', 'notifications', 'process', 'stdout', 'stderr', 'testOutput', 'outputChannel'] as const) {
    for (let index = 0; index < 10; index++) diagnostics.record(section, 'x'.repeat(10000));
    diagnostics.record(section, `${section} latest`);
  }
  const report = diagnostics.format();
  assert.ok(report.length < 16 * 1024);
  assert.equal(report.match(/truncated to last 2048 characters/g)?.length, 7);
  assert.match(report, /stdout latest/);
  assert.match(report, /outputChannel latest/);
});

test('spawn failure remains a command failure with its original error in diagnostics', { timeout: 10000 }, async () => {
  const diagnostics = new IntegrationFailureDiagnostics();
  const result = await runCompanionCommand({
    executablePath: process.execPath + '.missing-' + randomUUID(), args: [],
    startProcess: diagnostics.startProcess,
    outputChannel: { append: () => undefined, appendLine: () => undefined, show: () => undefined }
  });
  assert.equal(result.exitCode, 1);
  assert.equal(result.cancelled, false);
  assert.match(result.stderr, /failed to start:.*ENOENT/);
  assert.match(diagnostics.format(), /error:.*ENOENT/);
  assert.doesNotMatch(diagnostics.format(), /close: code=0/);
});

test('opt-in native Test build capture gives only its selected vba-dev child a local full dump path',
  { timeout: 10000 }, async context => {
    const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-native-test-dump-'));
    context.after(() => rm(root, { recursive: true, force: true }));
    const inheritedDumpName = process.env.DOTNET_DbgMiniDumpName;
    const launches: { executablePath: string; args: string[]; options: SpawnOptionsWithoutStdio }[] = [];
    const start = (executablePath: string, args: string[], options: SpawnOptionsWithoutStdio) => {
      launches.push({ executablePath, args, options });
      return spawn(process.execPath, ['-e', 'process.stdout.write("ok\\n")'], options);
    };
    const diagnostics = new IntegrationFailureDiagnostics({ dumpRoot: root, target: 'corrected' }, start);
    diagnostics.beginPhase('Explorer corrected unsaved source');
    const snapshot = path.join(root, 'snapshot-1');
    await mkdir(snapshot);
    await writeFile(path.join(snapshot, 'Caller.bas'), 'Attribute VB_Name = "Caller"\n');
    const args = ['test', '--source-snapshot', snapshot, '--format', 'ndjson'];
    const child = diagnostics.startProcess(path.join(root, 'vba-dev.exe'), args);
    await new Promise<void>((resolve, reject) => {
      child.onError?.(reject);
      child.onClose?.((code) => code === 0 ? resolve() : reject(new Error(`Child exited ${code}`)));
    });

    assert.equal(launches.length, 1);
    assert.deepEqual(launches[0].args, args);
    const environment = launches[0].options.env;
    assert.equal(environment?.DOTNET_DbgEnableMiniDump, '1');
    assert.equal(environment?.DOTNET_DbgMiniDumpType, '4');
    assert.equal(path.dirname(environment?.DOTNET_DbgMiniDumpName ?? ''), root);
    assert.match(path.basename(environment?.DOTNET_DbgMiniDumpName ?? ''),
      /^vba-dev-[0-9a-f-]+-%p\.dmp$/);
    assert.equal(process.env.DOTNET_DbgMiniDumpName, inheritedDumpName);
    assert.match(diagnostics.format(), /Explorer corrected unsaved source/);
    assert.match(diagnostics.format(), /--source-snapshot/);
    assert.match(diagnostics.format(), /close: code=0 signal=null/);
    const metadata = (await readdir(root)).filter(name => name.endsWith('.jsonl'));
    assert.equal(metadata.length, 1);
    const records = (await readFile(path.join(root, metadata[0]), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
    assert.deepEqual(records[0].args, args);
    assert.equal(records[0].sourceSnapshot, snapshot);
    assert.equal(records[0].snapshotEvidence.status, 'complete');
    assert.deepEqual(records[0].snapshotEvidence.files.map((file: { path: string }) => file.path), ['Caller.bas']);
    assert.equal(typeof records[0].pid, 'number');
    assert.equal(records[1].exitCode, 0);
  });

test('native Test build dump opt-in never arms another phase, executable, or second child',
  { timeout: 10000 }, async context => {
    const root = await mkdtemp(path.join(tmpdir(), 'vba-tools-native-test-scope-'));
    context.after(() => rm(root, { recursive: true, force: true }));
    const launches: SpawnOptionsWithoutStdio[] = [];
    const start = (_executablePath: string, _args: string[], options: SpawnOptionsWithoutStdio) => {
      launches.push(options);
      return spawn(process.execPath, ['-e', 'process.exit(0)'], options);
    };
    const diagnostics = new IntegrationFailureDiagnostics({ dumpRoot: root, target: 'invalid' }, start);
    const args = ['test', '--source-snapshot', path.join(root, 'snapshot')];
    const run = async (executablePath: string): Promise<void> => {
      const child = diagnostics.startProcess(executablePath, args);
      await new Promise<void>((resolve, reject) => {
        child.onError?.(reject);
        child.onClose?.(code => code === 0 ? resolve() : reject(new Error(`Child exited ${code}`)));
      });
    };
    diagnostics.beginPhase('Explorer corrected unsaved source');
    await run(path.join(root, 'vba-dev.exe'));
    diagnostics.beginPhase('Explorer invalid unsaved source');
    await run(path.join(root, 'not-vba-dev.exe'));
    await run(path.join(root, 'vba-dev.exe'));
    await run(path.join(root, 'vba-dev.exe'));
    assert.deepEqual(launches.map(options => options.env?.DOTNET_DbgEnableMiniDump),
      [undefined, undefined, '1', undefined]);
    assert.equal((await readdir(root)).filter(name => name.endsWith('.jsonl')).length, 1);
  });
