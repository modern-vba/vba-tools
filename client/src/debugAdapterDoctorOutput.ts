export type VbaDebugAdapterDoctorOverallStatus =
  | 'pass'
  | 'warning'
  | 'fail'
  | 'unverified';

export type VbaDebugAdapterDoctorCheckStatus =
  | VbaDebugAdapterDoctorOverallStatus
  | 'skipped';

const requiredVbaDebugAdapterDoctorCheckIds = [
  'platform.windows',
  'workspace.session',
  'excel.startup',
  'excel.processOwnership',
  'workbook.fixtureCreation',
  'workbook.open',
  'vbide.access',
  'vbe.commandContext',
  'vbe.breakpoint',
  'vbe.breakMode',
  'vbe.continue',
  'vbe.procedureCompletion',
  'vbe.breakpointCleanup',
  'excel.processClose',
  'workspace.deletion'
] as const;

export interface VbaDebugAdapterDoctorCheck {
  readonly id: string;
  readonly status: VbaDebugAdapterDoctorCheckStatus;
  readonly message: string;
  readonly durationMilliseconds: number;
  readonly remediation?: string | undefined;
  readonly details?: Readonly<Record<string, unknown>> | undefined;
}

export interface VbaDebugAdapterDoctorReport {
  readonly schemaVersion: string;
  readonly toolVersion: string;
  readonly status: VbaDebugAdapterDoctorOverallStatus;
  readonly complete: boolean;
  readonly checks: readonly VbaDebugAdapterDoctorCheck[];
}

export class VbaDebugAdapterDoctorOutputError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDebugAdapterDoctorOutputError';
  }
}

export function parseVbaDebugAdapterDoctorReport(
  stdout: string,
  expectedSchemaVersion: string,
  expectedToolVersion: string,
  exitCode: number,
  allowIncomplete = false
): VbaDebugAdapterDoctorReport {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor returned invalid JSON: ${String(error)}`
    );
  }

  if (!isRecord(parsed)) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor output must be one JSON object.'
    );
  }
  if (parsed.schemaVersion !== expectedSchemaVersion) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor reported schema ${String(parsed.schemaVersion)}, ` +
      `but ${expectedSchemaVersion} is required.`
    );
  }
  for (const contextProperty of ['scope', 'project', 'document'] as const) {
    if (Object.hasOwn(parsed, contextProperty)) {
      throw new VbaDebugAdapterDoctorOutputError(
        `vba-debug-adapter Doctor output must not contain ${contextProperty}.`
      );
    }
  }
  if (!isNonemptyString(parsed.toolVersion)) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor omitted toolVersion.'
    );
  }
  if (parsed.toolVersion !== expectedToolVersion) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor reported toolVersion ${parsed.toolVersion}, ` +
      `but ${expectedToolVersion} is required.`
    );
  }
  if (!isOverallStatus(parsed.status)) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor reported an invalid overall status.'
    );
  }
  if (typeof parsed.complete !== 'boolean') {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor omitted its completeness classification.'
    );
  }
  if (!parsed.complete && !allowIncomplete) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor returned an incomplete diagnostic.'
    );
  }
  if (!Array.isArray(parsed.checks)) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor omitted its ordered checks.'
    );
  }

  const checks = parsed.checks.map((value, index) => parseCheck(value, index));
  const checkIds = new Set<string>();
  for (const check of checks) {
    if (checkIds.has(check.id)) {
      throw new VbaDebugAdapterDoctorOutputError(
        `vba-debug-adapter Doctor returned duplicate check ${check.id}.`
      );
    }
    checkIds.add(check.id);
  }
  for (const requiredId of requiredVbaDebugAdapterDoctorCheckIds) {
    if (!checks.some((check) => check.id === requiredId)) {
      throw new VbaDebugAdapterDoctorOutputError(
        `vba-debug-adapter Doctor is missing required check ${requiredId}.`
      );
    }
  }
  const requiredIds = new Set<string>(requiredVbaDebugAdapterDoctorCheckIds);
  const reportedRequiredIds = checks
    .map((check) => check.id)
    .filter((id) => requiredIds.has(id));
  if (
    reportedRequiredIds.some(
      (id, index) => id !== requiredVbaDebugAdapterDoctorCheckIds[index]
    )
  ) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor did not report required checks in their stable order.'
    );
  }
  if (!parsed.complete && !checks.some((check) => check.status === 'unverified')) {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor incomplete diagnostic must include an unverified check.'
    );
  }
  const platformCheck = checks.find((check) => check.id === 'platform.windows');
  if (platformCheck?.status !== 'pass' && platformCheck?.status !== 'fail') {
    throw new VbaDebugAdapterDoctorOutputError(
      'vba-debug-adapter Doctor check platform.windows must report pass or fail.'
    );
  }
  if (platformCheck.status === 'fail') {
    for (const requiredId of requiredVbaDebugAdapterDoctorCheckIds.slice(1)) {
      const requiredCheck = checks.find((check) => check.id === requiredId);
      if (requiredCheck?.status !== 'skipped') {
        throw new VbaDebugAdapterDoctorOutputError(
          `vba-debug-adapter Doctor required check ${requiredId} ` +
          'must be skipped after platform.windows failed.'
        );
      }
    }
  }
  if (platformCheck?.status === 'pass') {
    for (const cleanupId of [
      'vbe.breakpointCleanup',
      'excel.processClose',
      'workspace.deletion'
    ] as const) {
      const cleanupCheck = checks.find((check) => check.id === cleanupId);
      if (cleanupCheck?.status === 'skipped') {
        throw new VbaDebugAdapterDoctorOutputError(
          `vba-debug-adapter Doctor cleanup check ${cleanupId} ` +
          'cannot be skipped after platform.windows passed.'
        );
      }
    }
  }
  let earlierBlocker = false;
  for (const check of checks) {
    if (check.status === 'skipped' && !earlierBlocker) {
      throw new VbaDebugAdapterDoctorOutputError(
        `vba-debug-adapter Doctor skipped check ${check.id} has no earlier blocker.`
      );
    }
    if (check.status === 'fail' || check.status === 'unverified') {
      earlierBlocker = true;
    }
  }
  const aggregateStatus = getAggregateStatus(checks);
  if (parsed.status !== aggregateStatus) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor overall status ${parsed.status} ` +
      `does not match ${aggregateStatus} checks.`
    );
  }
  let readinessBlocked = false;
  for (const readinessId of requiredVbaDebugAdapterDoctorCheckIds.slice(1, -3)) {
    const readinessCheck = checks.find((check) => check.id === readinessId);
    if (readinessBlocked && readinessCheck?.status !== 'skipped') {
      throw new VbaDebugAdapterDoctorOutputError(
        `vba-debug-adapter Doctor readiness check ${readinessId} ` +
        'must be skipped after an earlier blocker.'
      );
    }
    if (
      readinessCheck?.status === 'fail' ||
      readinessCheck?.status === 'unverified' ||
      readinessCheck?.status === 'skipped'
    ) {
      readinessBlocked = true;
    }
  }
  if (
    parsed.complete &&
    (parsed.status === 'pass' || parsed.status === 'warning') &&
    exitCode !== 0
  ) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor overall status ${parsed.status} ` +
      `requires exit code 0, received ${exitCode}.`
    );
  }
  if (
    (!parsed.complete || parsed.status === 'fail' || parsed.status === 'unverified') &&
    exitCode === 0
  ) {
    throw new VbaDebugAdapterDoctorOutputError(
      `vba-debug-adapter Doctor ${parsed.complete
        ? `overall status ${parsed.status}`
        : 'incomplete result'} requires a nonzero exit code.`
    );
  }
  return {
    schemaVersion: parsed.schemaVersion,
    toolVersion: parsed.toolVersion,
    status: parsed.status,
    complete: parsed.complete,
    checks
  };
}

export function renderVbaDebugAdapterDoctorReport(
  report: VbaDebugAdapterDoctorReport
): readonly string[] {
  const lines = [
    `Overall: ${report.status.toUpperCase()} (${report.complete ? 'complete' : 'incomplete'})`
  ];
  for (const check of report.checks) {
    lines.push(
      `[${check.status.toUpperCase()}] ${check.id}: ${check.message} ` +
      `(${check.durationMilliseconds} ms)`
    );
    if (check.remediation !== undefined) {
      lines.push(`  Remediation: ${check.remediation}`);
    }
    if (check.details !== undefined) {
      lines.push(`  Details: ${JSON.stringify(check.details)}`);
    }
  }
  return lines;
}

function parseCheck(value: unknown, index: number): VbaDebugAdapterDoctorCheck {
  if (!isRecord(value)) {
    throw invalidCheck(index, 'must be an object');
  }
  if (!isNonemptyString(value.id)) {
    throw invalidCheck(index, 'must have a nonempty id');
  }
  if (!isCheckStatus(value.status)) {
    throw invalidCheck(index, 'has an invalid status');
  }
  if (!isNonemptyString(value.message)) {
    throw invalidCheck(index, 'must have a nonempty message');
  }
  if (
    typeof value.durationMilliseconds !== 'number' ||
    !Number.isSafeInteger(value.durationMilliseconds) ||
    value.durationMilliseconds < 0
  ) {
    throw invalidCheck(index, 'must have a nonnegative safe-integer durationMilliseconds');
  }
  if (value.remediation !== undefined && typeof value.remediation !== 'string') {
    throw invalidCheck(index, 'has invalid remediation');
  }
  if (value.details !== undefined && !isRecord(value.details)) {
    throw invalidCheck(index, 'has invalid details');
  }

  return {
    id: value.id,
    status: value.status,
    message: value.message,
    durationMilliseconds: value.durationMilliseconds,
    ...(value.remediation === undefined ? {} : { remediation: value.remediation }),
    ...(value.details === undefined ? {} : { details: value.details })
  };
}

function invalidCheck(index: number, detail: string): VbaDebugAdapterDoctorOutputError {
  return new VbaDebugAdapterDoctorOutputError(
    `vba-debug-adapter Doctor check ${index + 1} ${detail}.`
  );
}

function isOverallStatus(value: unknown): value is VbaDebugAdapterDoctorOverallStatus {
  return value === 'pass' ||
    value === 'warning' ||
    value === 'fail' ||
    value === 'unverified';
}

function getAggregateStatus(
  checks: readonly VbaDebugAdapterDoctorCheck[]
): VbaDebugAdapterDoctorOverallStatus {
  for (const status of ['fail', 'unverified', 'warning'] as const) {
    if (checks.some((check) => check.status === status)) {
      return status;
    }
  }
  return 'pass';
}

function isCheckStatus(value: unknown): value is VbaDebugAdapterDoctorCheckStatus {
  return isOverallStatus(value) || value === 'skipped';
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
