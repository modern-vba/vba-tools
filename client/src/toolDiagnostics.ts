import { fileURLToPath } from 'node:url';

export type VbaDevDiagnosticSeverity = 'error' | 'warning' | 'information' | 'hint';

export interface VbaDevDiagnosticRange {
  start: VbaDevDiagnosticPosition;
  end: VbaDevDiagnosticPosition;
}

export interface VbaDevDiagnosticPosition {
  line: number;
  character: number;
}

export interface VbaDevDiagnostic {
  owner: string;
  severity: VbaDevDiagnosticSeverity;
  uriPath: string;
  range: VbaDevDiagnosticRange;
  message: string;
  code: string;
}

export interface VbaDevDiagnosticCollection {
  set(uriPath: string, diagnostics: readonly VbaDevDiagnostic[]): void;
  delete(uriPath: string): void;
}

export interface VbaDevDiagnosticReporterLike {
  refresh(scopeKey: string, output: string): readonly VbaDevDiagnostic[];
}

export function projectDiagnosticScope(projectRoot: string): string {
  return `project:${projectRoot}`;
}

export function combineVbaDevDiagnosticOutput(stdout: string, stderr: string): string {
  if (stdout.length > 0 && stderr.length > 0) {
    return `${stdout}\n${stderr}`;
  }

  return stdout.length > 0 ? stdout : stderr;
}

export function parseVbaDevDiagnostics(output: string): VbaDevDiagnostic[] {
  const diagnostics: VbaDevDiagnostic[] = [];
  for (const value of parseJsonRecords(output)) {
    if (isRecord(value) && Array.isArray(value.diagnostics)) {
      for (const diagnostic of value.diagnostics) {
        const mapped = toDiagnostic(diagnostic);
        if (mapped) {
          diagnostics.push(mapped);
        }
      }
      continue;
    }

    const mapped = toDiagnostic(value);
    if (mapped) {
      diagnostics.push(mapped);
    }
  }

  return diagnostics;
}

export class VbaDevDiagnosticReporter implements VbaDevDiagnosticReporterLike {
  private readonly uriPathsByScope = new Map<string, Set<string>>();

  public constructor(private readonly collection: VbaDevDiagnosticCollection) {
  }

  public refresh(scopeKey: string, output: string): VbaDevDiagnostic[] {
    const previous = this.uriPathsByScope.get(scopeKey);
    if (previous) {
      for (const uriPath of previous) {
        this.collection.delete(uriPath);
      }
    }

    const diagnostics = parseVbaDevDiagnostics(output);
    const diagnosticsByUri = groupByUriPath(diagnostics);
    const next = new Set<string>();
    for (const [uriPath, items] of diagnosticsByUri) {
      this.collection.set(uriPath, items);
      next.add(uriPath);
    }

    this.uriPathsByScope.set(scopeKey, next);
    return diagnostics;
  }
}

function parseJsonRecords(output: string): unknown[] {
  const trimmed = output.trim();
  if (trimmed.length === 0) {
    return [];
  }

  const whole = tryParseJson(trimmed);
  if (whole !== undefined) {
    return [whole];
  }

  const records: unknown[] = [];
  for (const line of output.split(/\r?\n/)) {
    const parsed = tryParseJson(line.trim());
    if (parsed !== undefined) {
      records.push(parsed);
    }
  }

  return records;
}

function toDiagnostic(value: unknown): VbaDevDiagnostic | undefined {
  if (!isRecord(value) || value.type !== 'diagnostic') {
    return undefined;
  }

  const owner = getString(value.owner) ?? getString(value.source);
  const severity = toSeverity(value.severity);
  const uriPath = toUriPath(getString(value.uri) ?? getString(value.file));
  const range = toRange(value.range);
  const message = getString(value.message);
  const code = getString(value.code);
  if (!owner || !severity || !uriPath || !range || !message || !code) {
    return undefined;
  }

  return {
    owner,
    severity,
    uriPath,
    range,
    message,
    code
  };
}

function toRange(value: unknown): VbaDevDiagnosticRange | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const start = toPosition(value.start);
  const end = toPosition(value.end);
  if (!start || !end) {
    return undefined;
  }

  return { start, end };
}

function toPosition(value: unknown): VbaDevDiagnosticPosition | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const line = getNumber(value.line);
  const character = getNumber(value.character);
  if (line === undefined || character === undefined) {
    return undefined;
  }

  return { line, character };
}

function toSeverity(value: unknown): VbaDevDiagnosticSeverity | undefined {
  if (typeof value !== 'string') {
    return undefined;
  }

  const normalized = value.toLowerCase();
  if (normalized === 'error' || normalized === 'warning' || normalized === 'information' || normalized === 'hint') {
    return normalized;
  }

  return undefined;
}

function toUriPath(value: string | undefined): string | undefined {
  if (!value) {
    return undefined;
  }

  if (value.startsWith('file:')) {
    try {
      return fileURLToPath(value);
    } catch {
      return undefined;
    }
  }

  return value;
}

function groupByUriPath(diagnostics: readonly VbaDevDiagnostic[]): Map<string, VbaDevDiagnostic[]> {
  const result = new Map<string, VbaDevDiagnostic[]>();
  for (const diagnostic of diagnostics) {
    const group = result.get(diagnostic.uriPath) ?? [];
    group.push(diagnostic);
    result.set(diagnostic.uriPath, group);
  }

  return result;
}

function tryParseJson(value: string): unknown | undefined {
  if (value.length === 0) {
    return undefined;
  }

  try {
    return JSON.parse(value) as unknown;
  } catch {
    return undefined;
  }
}

function getString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function getNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
