import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { copyFile, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const run = promisify(execFile);
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const privateRoot = path.join(repositoryRoot, '.local', 'performance');
const measurementScript = path.join(repositoryRoot, 'scripts', 'measureWorkbookBuildPerformance.ps1');

// This public-script regression substitutes only the external Build executable.
// Its timings are mock-only and establish no Excel or performance evidence.
async function withMockBuild(operation, { mutateOtherSource = false } = {}) {
  await mkdir(privateRoot, { recursive: true });
  const fixtureRoot = await mkdtemp(path.join(privateRoot, 'issue450-script-'));
  const project = path.join(fixtureRoot, 'project');
  const executable = path.join(fixtureRoot, 'frozen-executable', 'node.exe');
  const evidence = path.join(fixtureRoot, 'evidence');
  const sourceRoot = path.join(project, 'custom-authoring');
  const sourceWorkbook = path.join(sourceRoot, 'Selected.xlsm');
  const sourceFile = path.join(sourceRoot, 'Local.bas');
  await mkdir(path.dirname(executable), { recursive: true });
  await mkdir(sourceRoot, { recursive: true });
  await copyFile(process.execPath, executable);
  await writeFile(sourceWorkbook, 'mock-source-workbook');
  await writeFile(sourceFile, 'mock-authoring-source');
  await writeFile(path.join(project, 'vba-project.json'), JSON.stringify({
    schemaVersion: 1, projectName: 'MockOnly', primaryDocument: 'Selected',
    documents: { Selected: { kind: 'excel', sourcePath: 'custom-authoring',
      templatePath: 'custom-authoring/Selected.xlsm', publishPath: 'release/Selected.xlsm',
      commonModules: [], references: [] } }
  }));
  await writeFile(path.join(project, 'build'), `
const fs = require('node:fs');
const path = require('node:path');
const root = process.argv[process.argv.indexOf('--project') + 1];
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'vba-project.json'), 'utf8'));
const document = manifest.documents[process.argv[process.argv.indexOf('--document') + 1]];
fs.appendFileSync(path.resolve(root, document.templatePath), '\\nmock-build-save');
${mutateOtherSource ? "fs.appendFileSync(path.resolve(root, document.sourcePath, 'Local.bas'), '\\nforbidden-source-mutation');" : ''}
console.log('Mock-only SourceBuild; no Excel or timing-quality proof.');
`);
  try {
    await operation({ project, executable, evidence, sourceWorkbook, sourceFile });
  } finally {
    const relative = path.relative(privateRoot, fixtureRoot);
    assert.ok(relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative));
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

async function measure(fixture, variant) {
  return run('pwsh', ['-NoProfile', '-File', measurementScript,
    '-ExecutablePath', fixture.executable, '-ProjectPath', fixture.project,
    '-DocumentName', 'Selected', '-EvidenceDirectory', fixture.evidence,
    '-Variant', variant, '-Repetitions', '3'], { windowsHide: true });
}

test('bin-free SourceBuild measurement permits only its exact selected source workbook to change',
  { skip: process.platform !== 'win32' }, async () => {
    await withMockBuild(async fixture => {
      const sourceBefore = await readFile(fixture.sourceFile);
      await measure(fixture, 'mock-only');
      const report = JSON.parse(await readFile(path.join(fixture.evidence, 'mock-only', 'report.json'), 'utf8'));
      assert.equal(report.outputPath, fixture.sourceWorkbook);
      assert.equal(report.completed, true);
      assert.equal(report.trials.length, 4);
      assert.ok(report.trials.every(trial => trial.succeeded && trial.inputUnchanged));
      assert.ok(report.trials.every(trial => trial.outputBefore.sha256 !== trial.outputAfter.sha256));
      assert.deepEqual(await readFile(fixture.sourceFile), sourceBefore);
    });
  });

test('SourceBuild measurement rejects mutation of another authoring input',
  { skip: process.platform !== 'win32' }, async () => {
    await withMockBuild(async fixture => {
      const sourceBefore = await readFile(fixture.sourceFile);
      await assert.rejects(measure(fixture, 'mock-input-mutation'), error => {
        assert.match(error.stderr, /Input\/executable\/catalog identity or Excel process inventory changed/);
        return true;
      });
      const report = JSON.parse(await readFile(path.join(fixture.evidence, 'mock-input-mutation', 'report.json'), 'utf8'));
      assert.equal(report.completed, false);
      assert.equal(report.trials.length, 1);
      assert.equal(report.trials[0].inputUnchanged, false);
      assert.notEqual(report.trials[0].outputBefore.sha256, report.trials[0].outputAfter.sha256);
      assert.notDeepEqual(await readFile(fixture.sourceFile), sourceBefore);
    }, { mutateOtherSource: true });
  });
