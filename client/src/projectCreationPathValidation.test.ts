import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

import {
  validateExcelWorkbookPath,
  validateProjectName
} from './projectCreationPathValidation';

interface ProjectCreationPathValidationFixtureSet {
  readonly schemaVersion: string;
  readonly projectNameCases: readonly ProjectCreationPathValidationFixtureCase[];
  readonly excelWorkbookPathCases: readonly ProjectCreationPathValidationFixtureCase[];
}

interface ProjectCreationPathValidationFixtureCase {
  readonly id: string;
  readonly value?: string;
  readonly utf16CodeUnits?: readonly number[];
  readonly repeatCodeUnit?: number;
  readonly repeatCount?: number;
  readonly suffix?: string;
  readonly expectedReason: string | null;
  readonly expectedUtf16CodeUnitLength?: number;
}

test('TypeScript project-name validation matches the shared version 1.0 vectors', () => {
  const fixtureSet = readFixtureSet();

  assert.equal(fixtureSet.schemaVersion, '1.0');
  for (const testCase of fixtureSet.projectNameCases) {
    const result = validateProjectName(materialize(testCase));

    assert.equal(result.isValid, testCase.expectedReason === null, testCase.id);
    assert.equal(result.reason, testCase.expectedReason, testCase.id);
  }
});

test('TypeScript Excel path validation matches the shared version 1.0 vectors', () => {
  const fixtureSet = readFixtureSet();

  assert.equal(fixtureSet.schemaVersion, '1.0');
  for (const testCase of fixtureSet.excelWorkbookPathCases) {
    const candidate = materialize(testCase);
    const result = validateExcelWorkbookPath(candidate);

    assert.equal(candidate.length, testCase.expectedUtf16CodeUnitLength, testCase.id);
    assert.equal(result.isValid, testCase.expectedReason === null, testCase.id);
    assert.equal(result.reason, testCase.expectedReason, testCase.id);
  }
});

function readFixtureSet(): ProjectCreationPathValidationFixtureSet {
  return JSON.parse(readFileSync(path.join(
    process.cwd(),
    'fixtures',
    'project-creation-path-validation',
    'v1',
    'fixture-set.json'), 'utf8')) as ProjectCreationPathValidationFixtureSet;
}

function materialize(testCase: ProjectCreationPathValidationFixtureCase): string {
  if (testCase.utf16CodeUnits !== undefined) {
    return String.fromCharCode(...testCase.utf16CodeUnits);
  }

  return (testCase.value ?? '')
    + String.fromCharCode(testCase.repeatCodeUnit ?? 0).repeat(testCase.repeatCount ?? 0)
    + (testCase.suffix ?? '');
}
