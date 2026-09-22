import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
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
