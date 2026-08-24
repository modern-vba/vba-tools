import * as path from 'node:path';

export type VbaDevDoctorOverallStatus =
  | 'pass'
  | 'warning'
  | 'fail'
  | 'unverified';

export type VbaDevDoctorCheckStatus =
  | VbaDevDoctorOverallStatus
  | 'skipped';

export type VbaDevDoctorScope = 'environment' | 'project';

const requiredEnvironmentCheckIds = [
  'platform.windows',
  'excel.comStartup',
  'excel.processOwnership',
  'excel.vbideProjectAccess',
  'excel.processCleanup'
] as const;

const requiredEnvironmentDetailNames = [
  'isWindows',
  'dedicatedInstanceStarted',
  'ownedByInvocation',
  'projectAccessSucceeded',
  'ownedProcessReleased'
] as const;

export interface VbaDevDoctorCheck {
  readonly id: string;
  readonly status: VbaDevDoctorCheckStatus;
  readonly message: string;
  readonly durationMilliseconds: number;
  readonly details: Readonly<Record<string, unknown>>;
}

export interface VbaDevDoctorReport {
  readonly schemaVersion: string;
  readonly toolVersion: string;
  readonly scope: VbaDevDoctorScope;
  readonly project: string | null;
  readonly status: VbaDevDoctorOverallStatus;
  readonly complete: boolean;
  readonly checks: readonly VbaDevDoctorCheck[];
}

export interface VbaDevDoctorExpectedContext {
  readonly scope: VbaDevDoctorScope;
  readonly project: string | null;
}

export class VbaDevDoctorOutputError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevDoctorOutputError';
  }
}

export function parseVbaDevDoctorReport(
  stdout: string,
  expectedSchemaVersion: string,
  expectedToolVersion: string,
  exitCode: number,
  expectedContext?: VbaDevDoctorExpectedContext | undefined
): VbaDevDoctorReport {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor returned invalid JSON: ${String(error)}`
    );
  }

  if (!isRecord(parsed)) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor output must be one JSON object.'
    );
  }
  const allowedProperties = new Set([
    'schemaVersion',
    'toolVersion',
    'scope',
    'project',
    'status',
    'complete',
    'checks'
  ]);
  for (const property of Object.keys(parsed)) {
    if (!allowedProperties.has(property)) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor output has unexpected property ${property}.`
      );
    }
  }
  if (parsed.schemaVersion !== expectedSchemaVersion) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor reported schema ${String(parsed.schemaVersion)}, ` +
      `but ${expectedSchemaVersion} is required.`
    );
  }
  if (parsed.toolVersion !== expectedToolVersion) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor reported toolVersion ${String(parsed.toolVersion)}, ` +
      `but ${expectedToolVersion} is required.`
    );
  }
  if (parsed.scope !== 'environment' && parsed.scope !== 'project') {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor reported an invalid scope.'
    );
  }
  if (parsed.scope === 'environment' && parsed.project !== null) {
    throw new VbaDevDoctorOutputError(
      'vba-dev environment Doctor must report project null.'
    );
  }
  if (
    parsed.scope === 'project' &&
    (
      typeof parsed.project !== 'string' ||
      parsed.project.trim().length === 0 ||
      (!path.win32.isAbsolute(parsed.project) &&
       !path.posix.isAbsolute(parsed.project))
    )
  ) {
    throw new VbaDevDoctorOutputError(
      'vba-dev project Doctor must report an absolute project identity.'
    );
  }
  if (expectedContext !== undefined) {
    if (parsed.scope !== expectedContext.scope) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor scope ${parsed.scope} does not match requested scope ${expectedContext.scope}.`
      );
    }
    if (
      expectedContext.project === null
        ? parsed.project !== null
        : typeof parsed.project !== 'string' ||
          !sameProjectIdentity(parsed.project, expectedContext.project)
    ) {
      throw new VbaDevDoctorOutputError(
        'vba-dev Doctor project identity does not match the requested project.'
      );
    }
  }
  if (!isOverallStatus(parsed.status) || typeof parsed.complete !== 'boolean') {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor reported invalid status or completeness.'
    );
  }
  if (!Array.isArray(parsed.checks)) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor omitted its ordered checks.'
    );
  }

  const checks = parsed.checks.map(parseCheck);
  const checkIds = new Set<string>();
  for (const check of checks) {
    if (checkIds.has(check.id)) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor returned duplicate check ${check.id}.`
      );
    }
    checkIds.add(check.id);
  }
  if (
    parsed.scope === 'environment' &&
    (
      checks.length !== requiredEnvironmentCheckIds.length ||
      requiredEnvironmentCheckIds.some(
        (id, index) => checks[index]?.id !== id
      )
    )
  ) {
    throw new VbaDevDoctorOutputError(
      'vba-dev environment Doctor did not report exactly the required checks in stable order.'
    );
  }
  if (parsed.scope === 'project') {
    const environmentStart = checks.length - requiredEnvironmentCheckIds.length;
    if (
      environmentStart < 0 ||
      requiredEnvironmentCheckIds.some(
        (id, index) => checks[environmentStart + index]?.id !== id
      )
    ) {
      throw new VbaDevDoctorOutputError(
        'vba-dev project Doctor omitted its ordered active environment evidence.'
      );
    }
  }
  const aggregateStatus = getAggregateStatus(checks);
  if (parsed.status !== aggregateStatus) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor overall status ${parsed.status} does not match ${aggregateStatus} checks.`
    );
  }
  let earlierBlocker = false;
  for (const check of checks) {
    if (check.status === 'skipped' && !earlierBlocker) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor skipped check ${check.id} has no earlier blocker.`
      );
    }
    if (check.status === 'fail' || check.status === 'unverified') {
      earlierBlocker = true;
    }
  }
  const environmentChecks = parsed.scope === 'environment'
    ? checks
    : checks.slice(-requiredEnvironmentCheckIds.length);
  for (const [index, check] of environmentChecks.entries()) {
    const detailName = requiredEnvironmentDetailNames[index];
    const detailValue = detailName === undefined
      ? undefined
      : check.details[detailName];
    if (
      detailName === undefined ||
      !Object.prototype.hasOwnProperty.call(check.details, detailName) ||
      (detailValue !== null && typeof detailValue !== 'boolean')
    ) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor environment check ${check.id} must contain boolean-or-null detail ${String(detailName)}.`
      );
    }
    const expectedDetailValue = check.status === 'pass'
      ? true
      : check.status === 'fail'
        ? false
        : null;
    if (detailValue !== expectedDetailValue) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor environment detail ${detailName} does not match status ${check.status}.`
      );
    }
  }
  const startedOwnedExcel =
    environmentChecks[1]?.status === 'pass' ||
    environmentChecks[2]?.status === 'pass';
  if (startedOwnedExcel && environmentChecks[4]?.status === 'skipped') {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor skipped cleanup after it reported a started owned Excel process.'
    );
  }
  let earlierEnvironmentBlocker = false;
  for (const check of environmentChecks) {
    if (check.status === 'skipped' && !earlierEnvironmentBlocker) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor environment check ${check.id} has no earlier environment blocker.`
      );
    }
    if (check.status === 'fail' || check.status === 'unverified') {
      earlierEnvironmentBlocker = true;
    }
  }
  if (parsed.complete &&
      (parsed.status === 'pass' || parsed.status === 'warning') &&
      exitCode !== 0) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor status ${parsed.status} requires exit code 0.`
    );
  }
  if (!parsed.complete && exitCode === 0) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor incomplete result requires a nonzero exit code.'
    );
  }
  if (
    parsed.complete &&
    (parsed.status === 'fail' || parsed.status === 'unverified') &&
    exitCode === 0
  ) {
    throw new VbaDevDoctorOutputError(
      `vba-dev Doctor status ${parsed.status} requires a nonzero exit code.`
    );
  }
  if (
    exitCode === 130 &&
    parsed.status === 'fail'
  ) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor exit code 130 cannot hide an observed failure.'
    );
  }
  if (
    exitCode === 130 &&
    (
      parsed.complete ||
      !checks.some((check) =>
        check.id === 'excel.processCleanup' && check.status === 'pass'
      )
    )
  ) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor exit code 130 requires an incomplete result with proven cleanup.'
    );
  }

  return {
    schemaVersion: parsed.schemaVersion,
    toolVersion: parsed.toolVersion,
    scope: parsed.scope,
    project: typeof parsed.project === 'string' ? parsed.project : null,
    status: parsed.status,
    complete: parsed.complete,
    checks
  };
}

export function isReusableVbaDevEnvironmentDoctorReport(
  report: VbaDevDoctorReport
): boolean {
  if (
    report.scope !== 'environment' ||
    report.project !== null ||
    !report.complete ||
    report.status !== 'pass' ||
    report.checks.length !== requiredEnvironmentCheckIds.length
  ) {
    return false;
  }

  return requiredEnvironmentCheckIds.every((id, index) =>
    report.checks[index]?.id === id &&
    report.checks[index]?.status === 'pass' &&
    report.checks[index]?.details[requiredEnvironmentDetailNames[index]!] === true
  );
}

export function renderVbaDevDoctorReport(
  report: VbaDevDoctorReport
): readonly string[] {
  const lines = [
    `Overall: ${report.status.toUpperCase()} (${report.complete ? 'complete' : 'incomplete'})`
  ];
  for (const check of report.checks) {
    lines.push(
      `[${check.status.toUpperCase()}] ${check.id}: ${check.message} ` +
      `(${check.durationMilliseconds} ms)`
    );
    lines.push(`  Details: ${JSON.stringify(check.details)}`);
  }
  return lines;
}

function parseCheck(value: unknown): VbaDevDoctorCheck {
  if (!isRecord(value)) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor returned an invalid check.'
    );
  }
  const allowedProperties = new Set([
    'id',
    'status',
    'message',
    'durationMilliseconds',
    'details'
  ]);
  for (const property of Object.keys(value)) {
    if (!allowedProperties.has(property)) {
      throw new VbaDevDoctorOutputError(
        `vba-dev Doctor returned unexpected check property ${property}.`
      );
    }
  }
  if (
    typeof value.id !== 'string' ||
    value.id.trim().length === 0 ||
    !isCheckStatus(value.status) ||
    typeof value.message !== 'string' ||
    value.message.trim().length === 0 ||
    typeof value.durationMilliseconds !== 'number' ||
    !Number.isSafeInteger(value.durationMilliseconds) ||
    value.durationMilliseconds < 0
  ) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor returned an invalid check.'
    );
  }
  if (!isRecord(value.details)) {
    throw new VbaDevDoctorOutputError(
      'vba-dev Doctor check must contain machine-readable details.'
    );
  }

  return {
    id: value.id,
    status: value.status,
    message: value.message,
    durationMilliseconds: value.durationMilliseconds,
    details: value.details
  };
}

function isOverallStatus(value: unknown): value is VbaDevDoctorOverallStatus {
  return value === 'pass' ||
    value === 'warning' ||
    value === 'fail' ||
    value === 'unverified';
}

function isCheckStatus(value: unknown): value is VbaDevDoctorCheckStatus {
  return isOverallStatus(value) || value === 'skipped';
}

function getAggregateStatus(
  checks: readonly VbaDevDoctorCheck[]
): VbaDevDoctorOverallStatus {
  if (checks.some((check) => check.status === 'fail')) {
    return 'fail';
  }
  if (checks.some((check) =>
    check.status === 'unverified' || check.status === 'skipped'
  )) {
    return 'unverified';
  }
  return checks.some((check) => check.status === 'warning')
    ? 'warning'
    : 'pass';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function sameProjectIdentity(left: string, right: string): boolean {
  if (path.win32.isAbsolute(left) && path.win32.isAbsolute(right)) {
    return path.win32.normalize(left).toLocaleLowerCase('en-US') ===
      path.win32.normalize(right).toLocaleLowerCase('en-US');
  }
  if (path.posix.isAbsolute(left) && path.posix.isAbsolute(right)) {
    return path.posix.normalize(left) === path.posix.normalize(right);
  }
  return false;
}
