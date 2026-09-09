import { SnapshotDiagnosticOrigin } from './toolDiagnostics';
import * as path from 'node:path';

export interface DebugSnapshotBuildReport {
  readonly schemaVersion: '1.0';
  readonly projectRoot: string;
  readonly documentName: string;
  readonly generation: number;
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  readonly origins: readonly SnapshotDiagnosticOrigin[];
}

export function parseDebugSnapshotBuildReport(value: unknown): DebugSnapshotBuildReport {
  const report = object(value);
  if (report?.schemaVersion !== '1.0'
      || typeof report.projectRoot !== 'string' || !path.win32.isAbsolute(report.projectRoot)
      || typeof report.documentName !== 'string' || report.documentName.trim().length === 0
      || !Number.isInteger(report.generation) || (report.generation as number) < 0
      || (report.generation as number) > 0x7fffffff
      || !Number.isInteger(report.exitCode)
      || typeof report.stdout !== 'string' || typeof report.stderr !== 'string'
      || !Array.isArray(report.origins)) {
    throw new Error('Invalid or unsupported VBA snapshot build report; existing Problems were retained.');
  }
  const origins = report.origins.map(value => {
    const origin = object(value);
    if (typeof origin?.snapshotUri !== 'string'
        || (origin.sourceUri != null && typeof origin.sourceUri !== 'string')) {
      throw new Error('Invalid VBA snapshot source origin; existing Problems were retained.');
    }
    return Object.freeze({ snapshotUri: origin.snapshotUri, sourceUri: origin.sourceUri as string | null | undefined });
  });
  return Object.freeze({
    schemaVersion: '1.0', projectRoot: report.projectRoot, documentName: report.documentName,
    generation: report.generation as number, exitCode: report.exitCode as number,
    stdout: report.stdout, stderr: report.stderr, origins: Object.freeze(origins)
  });
}

function object(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown> : undefined;
}
