import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const assembly = process.env.VBA_SOURCE_IDENTITY_REPRO_DLL
  ?? path.join(root, 'scripts/source-identity-repro/bin/Release/net10.0/SourceIdentityRepro.dll');

test('opt-in URI probe preserves input privacy and records fresh-child outcomes', async () => {
  const ignoredRoot = path.join(root, '.tmp', 'diagnostic-verification');
  await fs.mkdir(ignoredRoot, { recursive: true });
  const runRoot = await fs.mkdtemp(path.join(ignoredRoot, 'issue-415-repro-smoke-'));
  const firstUri = 'file:///C:/Sources/%E6%97%A5%E6%9C%AC/ObjectSet.cls';
  const secondUri = 'file:///C:/Sources/%E6%97%A5%E6%9C%AC/DebugInformation.cls';
  const input = path.join(runRoot, 'synthetic-pair.json');
  const report = path.join(runRoot, 'synthetic-report.json');
  await fs.writeFile(input, JSON.stringify({
    kind: 'synthetic-smoke', firstUri, secondUri,
    firstUriUtf16Length: firstUri.length, secondUriUtf16Length: secondUri.length
  }));
  const result = spawnSync('dotnet', [assembly, '--input', input, '--trials', '2',
    '--iterations', '100', '--report', report], { cwd: root, encoding: 'utf8', timeout: 30000 });
  assert.equal(result.status, 0, result.stderr);
  const evidence = JSON.parse(await fs.readFile(report, 'utf8'));
  assert.equal(evidence.complete, true);
  assert.equal(evidence.input.kind, 'synthetic-smoke');
  assert.equal(evidence.trials.length, 2);
  assert.ok(evidence.trials.every(trial => trial.status === 'passed' && trial.lastReportedCompleted === 100));
  assert.ok(evidence.trials.every(trial => trial.childSourceAssemblySha256 === evidence.environment.sourceIdentity.sha256));
  assert.notEqual(evidence.trials[0].childPid, evidence.trials[1].childPid);
  assert.equal(evidence.input.fileSha256, createHash('sha256').update(await fs.readFile(input)).digest('hex').toUpperCase());
  assert.doesNotMatch(await fs.readFile(report, 'utf8'), /file:\/\/\//);
});

test('opt-in URI probe decodes and orders a complete failure receipt', async () => {
  const ignoredRoot = path.join(root, '.tmp', 'diagnostic-verification');
  await fs.mkdir(ignoredRoot, { recursive: true });
  const runRoot = await fs.mkdtemp(path.join(ignoredRoot, 'issue-415-receipt-smoke-'));
  const right = 'file:///C:/Sources/ObjectSet.cls';
  const left = 'file:///C:/Sources/DebugInformation.cls';
  const captured = (uri) => Array.from({ length: uri.length }, (_, index) =>
    uri.charCodeAt(index).toString(16).padStart(4, '0')).join('').toUpperCase();
  const input = path.join(runRoot, 'failure-receipt.json');
  const report = path.join(runRoot, 'receipt-report.json');
  await fs.writeFile(input, JSON.stringify({
    failures: [{ exception: { uriIdentification: {
      status: 'available', comparisonSide: 'left',
      uriCodeUnitLength: left.length, uriCapturedCodeUnits: left.length,
      uriCaptureComplete: true, uriUtf16Hex: captured(left),
      comparisonOtherUriCodeUnitLength: right.length,
      comparisonOtherUriCapturedCodeUnits: right.length,
      comparisonOtherUriCaptureComplete: true,
      comparisonOtherUriUtf16Hex: captured(right)
    } } }]
  }));
  const result = spawnSync('dotnet', [assembly, '--input', input, '--trials', '1',
    '--iterations', '100', '--report', report], { cwd: root, encoding: 'utf8', timeout: 30000 });
  assert.equal(result.status, 0, result.stderr);
  const evidence = JSON.parse(await fs.readFile(report, 'utf8'));
  assert.equal(evidence.input.kind, 'source-analysis-failure-receipt');
  assert.equal(evidence.input.firstUriUtf16Sha256,
    createHash('sha256').update(Buffer.from(right, 'utf16le')).digest('hex').toUpperCase());
  assert.equal(evidence.input.secondUriUtf16Sha256,
    createHash('sha256').update(Buffer.from(left, 'utf16le')).digest('hex').toUpperCase());
  assert.equal(evidence.trials[0].status, 'passed');
  assert.doesNotMatch(await fs.readFile(report, 'utf8'), /file:\/\/\//);
});

test('opt-in URI probe does not overwrite a pre-existing report sidecar', async () => {
  const ignoredRoot = path.join(root, '.tmp', 'diagnostic-verification');
  await fs.mkdir(ignoredRoot, { recursive: true });
  const runRoot = await fs.mkdtemp(path.join(ignoredRoot, 'issue-415-sidecar-smoke-'));
  const firstUri = 'file:///C:/Sources/First.cls';
  const secondUri = 'file:///C:/Sources/Second.cls';
  const input = path.join(runRoot, 'synthetic-pair.json');
  const report = path.join(runRoot, 'synthetic-report.json');
  const sidecar = `${report}.tmp`;
  await fs.writeFile(sidecar, 'keep this existing sidecar');
  await fs.writeFile(input, JSON.stringify({
    kind: 'synthetic-smoke', firstUri, secondUri,
    firstUriUtf16Length: firstUri.length, secondUriUtf16Length: secondUri.length
  }));
  const result = spawnSync('dotnet', [assembly, '--input', input, '--trials', '1',
    '--iterations', '100', '--report', report], { cwd: root, encoding: 'utf8', timeout: 30000 });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(await fs.readFile(sidecar, 'utf8'), 'keep this existing sidecar');
  const evidence = JSON.parse(await fs.readFile(report, 'utf8'));
  assert.equal(evidence.complete, true);
});

test('opt-in URI probe retains an unhandled child failure in its local report', async () => {
  const ignoredRoot = path.join(root, '.tmp', 'diagnostic-verification');
  await fs.mkdir(ignoredRoot, { recursive: true });
  const runRoot = await fs.mkdtemp(path.join(ignoredRoot, 'issue-415-repro-failure-smoke-'));
  const uri = 'file:///C:/Sources/Same.cls';
  const input = path.join(runRoot, 'same-pair.json');
  const report = path.join(runRoot, 'same-report.json');
  await fs.writeFile(input, JSON.stringify({
    kind: 'synthetic-negative-control', firstUri: uri, secondUri: uri,
    firstUriUtf16Length: uri.length, secondUriUtf16Length: uri.length
  }));
  const result = spawnSync('dotnet', [assembly, '--input', input, '--trials', '1',
    '--iterations', '100', '--report', report], { cwd: root, encoding: 'utf8', timeout: 30000 });
  assert.equal(result.status, 1, result.stderr);
  const evidence = JSON.parse(await fs.readFile(report, 'utf8'));
  assert.equal(evidence.complete, true);
  assert.equal(evidence.trials[0].status, 'failed');
  assert.equal(evidence.trials[0].exceptionType, 'System.InvalidOperationException');
  assert.match(evidence.trials[0].stderr, /Unhandled exception/);
});
