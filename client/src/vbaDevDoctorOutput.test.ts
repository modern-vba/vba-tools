import assert from 'node:assert/strict';
import test from 'node:test';

import {
  isReusableVbaDevEnvironmentDoctorReport,
  parseVbaDevDoctorReport
} from './vbaDevDoctorOutput';

test('vba-dev Doctor accepts a complete all-pass environment result for reuse', () => {
  const report = parseVbaDevDoctorReport(
    JSON.stringify({
      schemaVersion: '1.0',
      toolVersion: '0.1.0',
      scope: 'environment',
      project: null,
      status: 'pass',
      complete: true,
      checks: [
        check('platform.windows'),
        check('excel.comStartup'),
        check('excel.processOwnership'),
        check('excel.vbideProjectAccess'),
        check('excel.processCleanup')
      ]
    }),
    '1.0',
    '0.1.0',
    0
  );

  assert.equal(report.scope, 'environment');
  assert.equal(report.project, null);
  assert.equal(isReusableVbaDevEnvironmentDoctorReport(report), true);
});

test('vba-dev environment Doctor reuse also requires positive machine evidence', () => {
  const report = parseVbaDevDoctorReport(
    JSON.stringify({
      schemaVersion: '1.0',
      toolVersion: '0.1.0',
      scope: 'environment',
      project: null,
      status: 'pass',
      complete: true,
      checks: [
        check('platform.windows'),
        check('excel.comStartup'),
        check('excel.processOwnership'),
        check('excel.vbideProjectAccess'),
        check('excel.processCleanup')
      ]
    }),
    '1.0',
    '0.1.0',
    0
  );
  (report.checks[4]?.details as Record<string, unknown>).ownedProcessReleased = false;

  assert.equal(isReusableVbaDevEnvironmentDoctorReport(report), false);
});

test('vba-dev Doctor rejects unknown closed-schema context properties', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        document: 'Book1',
        status: 'pass',
        complete: true,
        checks: [
          check('platform.windows'),
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /unexpected property document/
  );
});

test('vba-dev environment Doctor rejects required checks out of stable order', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          check('excel.comStartup'),
          check('platform.windows'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /stable order/
  );
});

test('vba-dev Doctor rejects an aggregate status inconsistent with its checks', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          check('platform.windows'),
          checkWithStatus('excel.comStartup', 'warning'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /does not match/
  );
});

test('vba-dev Doctor rejects an incomplete result with exit code zero', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'unverified',
        complete: false,
        checks: [
          check('platform.windows'),
          checkWithStatus('excel.comStartup', 'unverified'),
          checkWithStatus('excel.processOwnership', 'skipped'),
          checkWithStatus('excel.vbideProjectAccess', 'skipped'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /incomplete result requires a nonzero exit code/
  );
});

test('vba-dev Doctor rejects exit code 130 without cleanup proof', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'unverified',
        complete: false,
        checks: [
          check('platform.windows'),
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          checkWithStatus('excel.processCleanup', 'unverified')
        ]
      }),
      '1.0',
      '0.1.0',
      130
    ),
    /exit code 130 requires.*cleanup/i
  );
});

test('vba-dev Doctor rejects exit code 130 when cancellation would hide a failure', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'fail',
        complete: false,
        checks: [
          check('platform.windows'),
          check('excel.comStartup'),
          check('excel.processOwnership'),
          checkWithStatus('excel.vbideProjectAccess', 'fail'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      130
    ),
    /exit code 130.*failure/i
  );
});

test('vba-dev project Doctor rejects a missing absolute project identity', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'project',
        project: null,
        status: 'pass',
        complete: true,
        checks: [check('Project manifest')]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /absolute project identity/
  );
});

test('vba-dev project Doctor rejects missing active environment evidence', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'project',
        project: 'C:\\work\\Project',
        status: 'pass',
        complete: true,
        checks: [check('project.manifest')]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /active environment evidence/i
  );
});

test('vba-dev project Doctor does not let a project failure justify skipped environment checks', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'project',
        project: 'C:\\work\\Project',
        status: 'fail',
        complete: true,
        checks: [
          { ...check('project.manifest'), status: 'fail' },
          checkWithStatus('platform.windows', 'skipped'),
          checkWithStatus('excel.comStartup', 'skipped'),
          checkWithStatus('excel.processOwnership', 'skipped'),
          checkWithStatus('excel.vbideProjectAccess', 'skipped'),
          checkWithStatus('excel.processCleanup', 'skipped')
        ]
      }),
      '1.0',
      '0.1.0',
      1
    ),
    /environment check platform\.windows has no earlier environment blocker/
  );
});

test('vba-dev Doctor rejects unknown closed-schema check properties', () => {
  const platformCheck = check('platform.windows');
  platformCheck.extra = true;
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          platformCheck,
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /unexpected check property extra/
  );
});

test('vba-dev Doctor rejects adapter-only remediation in its closed check schema', () => {
  const platformCheck = check('platform.windows');
  platformCheck.remediation = 'Use an adapter-owned remediation field.';
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          platformCheck,
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /unexpected check property remediation/
  );
});

test('vba-dev Doctor rejects a check without machine-readable details', () => {
  const startup = check('excel.comStartup');
  delete startup.details;
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          check('platform.windows'),
          startup,
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /machine-readable details/
  );
});

test('vba-dev environment Doctor rejects a skipped check without an earlier blocker', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'unverified',
        complete: true,
        checks: [
          check('platform.windows'),
          checkWithStatus('excel.comStartup', 'skipped'),
          checkWithStatus('excel.processOwnership', 'skipped'),
          checkWithStatus('excel.vbideProjectAccess', 'skipped'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      1
    ),
    /no earlier blocker/
  );
});

test('vba-dev environment Doctor rejects skipped cleanup after owned Excel startup', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'fail',
        complete: true,
        checks: [
          check('platform.windows'),
          check('excel.comStartup'),
          check('excel.processOwnership'),
          checkWithStatus('excel.vbideProjectAccess', 'fail'),
          checkWithStatus('excel.processCleanup', 'skipped')
        ]
      }),
      '1.0',
      '0.1.0',
      1
    ),
    /cleanup.*started owned Excel/i
  );
});

test('vba-dev environment Doctor rejects cleanup evidence without its machine detail', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          check('platform.windows'),
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          { ...check('excel.processCleanup'), details: {} }
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /ownedProcessReleased/
  );
});

test('vba-dev environment Doctor rejects machine evidence that contradicts status', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'environment',
        project: null,
        status: 'pass',
        complete: true,
        checks: [
          { ...check('platform.windows'), details: { isWindows: false } },
          check('excel.comStartup'),
          check('excel.processOwnership'),
          check('excel.vbideProjectAccess'),
          check('excel.processCleanup')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /isWindows.*status pass/
  );
});

test('vba-dev project Doctor rejects duplicate check identities', () => {
  assert.throws(
    () => parseVbaDevDoctorReport(
      JSON.stringify({
        schemaVersion: '1.0',
        toolVersion: '0.1.0',
        scope: 'project',
        project: 'C:\\work\\Project',
        status: 'pass',
        complete: true,
        checks: [
          check('project.manifest'),
          check('project.manifest')
        ]
      }),
      '1.0',
      '0.1.0',
      0
    ),
    /duplicate check project\.manifest/
  );
});

function check(id: string): Record<string, unknown> {
  const detailNames: Record<string, string> = {
    'platform.windows': 'isWindows',
    'excel.comStartup': 'dedicatedInstanceStarted',
    'excel.processOwnership': 'ownedByInvocation',
    'excel.vbideProjectAccess': 'projectAccessSucceeded',
    'excel.processCleanup': 'ownedProcessReleased'
  };
  const detailName = detailNames[id];
  return {
    id,
    status: 'pass',
    message: `${id} passed.`,
    durationMilliseconds: 0,
    details: detailName === undefined ? {} : { [detailName]: true }
  };
}

function checkWithStatus(
  id: string,
  status: 'warning' | 'fail' | 'unverified' | 'skipped'
): Record<string, unknown> {
  const result = check(id);
  const details = result.details as Record<string, unknown>;
  const detailName = Object.keys(details)[0];
  if (detailName !== undefined) {
    details[detailName] = status === 'fail' ? false : null;
  }
  return { ...result, status, details };
}
