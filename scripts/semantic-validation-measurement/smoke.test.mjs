import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import test from 'node:test';

const measurementAssembly = process.env.VBA_SEMANTIC_MEASUREMENT_ASSEMBLY;
assert.ok(measurementAssembly, 'Set VBA_SEMANTIC_MEASUREMENT_ASSEMBLY to the built harness DLL.');

test('generated semantic workload reports four complete, independently checked trials', () => {
  assert.ok(existsSync(measurementAssembly), 'The runnable measurement harness must exist.');
  const result = spawnSync('dotnet', [measurementAssembly,
    '--variant', 'smoke', '--calls-per-document', '2', '--lookup-repetitions', '2'],
  { encoding: 'utf8', timeout: 60_000 });
  assert.equal(result.status, 0, `${result.error ?? ''}\n${result.stdout}\n${result.stderr}`);
  const report = JSON.parse(result.stdout);
  assert.equal(report.schemaVersion, '1.0');
  assert.equal(report.variant, 'smoke');
  assert.equal(report.corpus.documentCount, 8);
  assert.equal(report.trials.length, 4);
  assert.equal(report.trials[0].excludedWarmup, true);
  assert.deepEqual(report.trials.slice(1).map(trial => trial.excludedWarmup), [false, false, false]);
  assert.ok(report.trials.every(trial => trial.success));
  assert.ok(report.trials.every(trial => trial.diagnosticCount === 4));
  assert.ok(report.trials.every(trial => trial.resolutionCount === 64));
  assert.equal(new Set(report.trials.map(trial => trial.diagnosticsSha256)).size, 1);
  assert.equal(new Set(report.trials.map(trial => trial.resolutionsSha256)).size, 1);
  assert.match(report.corpus.sourcesSha256, /^[A-F0-9]{64}$/);
  assert.match(report.corpus.catalogSha256, /^[A-F0-9]{64}$/);
  assert.ok(report.assemblies.every(assembly => /^[A-F0-9]{64}$/.test(assembly.sha256)));
  assert.ok(report.medians.analysisMilliseconds >= 0);
  assert.ok(report.medians.repeatedResolutionMilliseconds >= 0);
});

test('concentrating calls preserves total workload, documents, catalogs and checked outcomes', () => {
  const reports = [];
  for (const layoutArguments of [[], ['--layout', 'concentrated']]) {
    const result = spawnSync('dotnet', [measurementAssembly,
      '--variant', 'layout-smoke', '--calls-per-document', '2', '--lookup-repetitions', '2',
      ...layoutArguments], { encoding: 'utf8', timeout: 60_000 });
    assert.equal(result.status, 0, `${result.error ?? ''}\n${result.stdout}\n${result.stderr}`);
    reports.push(JSON.parse(result.stdout));
  }
  const [split, concentrated] = reports;
  assert.equal(split.corpus.layout, 'split');
  assert.equal(concentrated.corpus.layout, 'concentrated');
  assert.deepEqual(split.corpus.callGroupsByCaller, [2, 2, 2, 2]);
  assert.deepEqual(concentrated.corpus.callGroupsByCaller, [8, 0, 0, 0]);
  assert.equal(concentrated.corpus.documentCount, 8);
  assert.equal(concentrated.corpus.sourceCharacters, split.corpus.sourceCharacters);
  assert.equal(concentrated.corpus.sourceLines, split.corpus.sourceLines);
  assert.equal(concentrated.corpus.catalogSha256, split.corpus.catalogSha256);
  assert.equal(concentrated.corpus.referenceSelectionSha256, split.corpus.referenceSelectionSha256);
  assert.equal(concentrated.corpus.queriesPerRepetition, split.corpus.queriesPerRepetition);
  assert.notEqual(concentrated.corpus.sourcesSha256, split.corpus.sourcesSha256);
  assert.equal(concentrated.trials.length, 4);
  assert.ok(concentrated.trials.every(trial => trial.success && trial.diagnosticCount === 4 && trial.resolutionCount === 64));
});
