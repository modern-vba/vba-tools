import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export interface VbaDevCommandCapability {
  outputSchemaVersion: string;
}

export interface VbaDevDebugAdapterCapability {
  protocolVersion: string;
  transport: string;
  command: string;
}

export interface VbaDevCapabilities {
  toolVersion: string;
  contractVersion: string;
  commands: Record<string, VbaDevCommandCapability>;
  debugAdapter?: VbaDevDebugAdapterCapability | undefined;
}

export interface RequiredVbaDevContract {
  contractVersion: string;
  debugAdapterProtocolVersion: string;
  commandSchemaVersions: Record<string, string>;
}

export interface CommonModulesList {
  document: string;
  commonModules: readonly CommonModuleListItem[];
}

export interface CommonModuleListItem {
  name: string;
  requested: boolean;
}

export interface ReferenceList {
  document: string;
  references: readonly ReferenceListItem[];
}

export interface ReferenceListItem {
  name: string;
}

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

export interface VbaDevTestEvent {
  type: string;
  document?: string | undefined;
  module?: string | undefined;
  procedure?: string | undefined;
  outcome?: string | undefined;
  message?: string | undefined;
  location?: VbaDevTestLocation | undefined;
}

export interface VbaDevTestLocation {
  uriPath: string;
  line: number;
  character: number;
  range: VbaDevDiagnosticRange;
}

export class VbaDevOutputContractError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevOutputContractError';
  }
}

export function loadRequiredVbaDevContractFile(contractPath: string): RequiredVbaDevContract {
  let parsed: unknown;
  try {
    parsed = JSON.parse(readFileSync(contractPath, 'utf8')) as unknown;
  } catch (error) {
    throw new VbaDevOutputContractError(
      `VbaDev required contract could not be read from '${contractPath}': ${String(error)}`
    );
  }

  if (!isRequiredVbaDevContract(parsed)) {
    throw new VbaDevOutputContractError(
      `VbaDev required contract at '${contractPath}' must include contractVersion, debugAdapterProtocolVersion, and commandSchemaVersions.`
    );
  }

  return parsed;
}

export function parseVbaDevCapabilities(executablePath: string, stdout: string): VbaDevCapabilities {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' returned invalid capabilities JSON: ${String(error)}`
    );
  }

  if (!isCapabilities(parsed)) {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' returned capabilities JSON without toolVersion, contractVersion, and commands.`
    );
  }

  return parsed;
}

export function validateVbaDevCapabilities(
  executablePath: string,
  capabilities: VbaDevCapabilities,
  requiredContract: RequiredVbaDevContract
): void {
  if (capabilities.contractVersion !== requiredContract.contractVersion) {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' reports contractVersion ${capabilities.contractVersion}, but this extension requires ${requiredContract.contractVersion}.`
    );
  }

  const debugAdapter = capabilities.debugAdapter;
  if (!debugAdapter) {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' does not report the required debug adapter capability.`
    );
  }

  if (debugAdapter.protocolVersion !== requiredContract.debugAdapterProtocolVersion) {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' reports debug adapter protocolVersion ${debugAdapter.protocolVersion}, but this extension requires ${requiredContract.debugAdapterProtocolVersion}.`
    );
  }

  if (debugAdapter.transport !== 'stdio') {
    throw new VbaDevOutputContractError(
      `VbaDev at '${executablePath}' reports debug adapter transport ${debugAdapter.transport}, but this extension requires stdio.`
    );
  }

  for (const [commandName, requiredSchemaVersion] of Object.entries(requiredContract.commandSchemaVersions)) {
    const command = capabilities.commands[commandName];
    if (!command) {
      throw new VbaDevOutputContractError(
        `VbaDev at '${executablePath}' does not report required command '${commandName}'.`
      );
    }

    if (command.outputSchemaVersion !== requiredSchemaVersion) {
      throw new VbaDevOutputContractError(
        `VbaDev at '${executablePath}' reports ${commandName} outputSchemaVersion ${command.outputSchemaVersion}, but this extension requires ${requiredSchemaVersion}.`
      );
    }
  }
}

export function parseCommonModulesListOutput(stdout: string): CommonModulesList {
  const parsed = parseJsonValue(stdout, 'CommonModules list returned invalid JSON');
  if (!isCommonModulesList(parsed)) {
    throw new VbaDevOutputContractError('CommonModules list JSON did not include document and commonModules.');
  }

  return parsed;
}

export function parseReferenceListOutput(stdout: string): ReferenceList {
  const parsed = parseJsonValue(stdout, 'Reference list returned invalid JSON');
  if (!isReferenceList(parsed)) {
    throw new VbaDevOutputContractError('Reference list JSON did not include document and references.');
  }

  return parsed;
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

export function parseVbaDevTestEvents(stdout: string): VbaDevTestEvent[] {
  const events: VbaDevTestEvent[] = [];
  for (const value of parseJsonRecords(stdout)) {
    const event = toTestEvent(value);
    if (event) {
      events.push(event);
    }
  }

  return events;
}

function parseJsonValue(stdout: string, message: string): unknown {
  try {
    return JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new VbaDevOutputContractError(`${message}: ${String(error)}`);
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

function toTestEvent(value: unknown): VbaDevTestEvent | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const type = getString(value.type);
  if (!type) {
    return undefined;
  }

  return {
    type,
    document: getString(value.document),
    module: getString(value.module),
    procedure: getString(value.procedure),
    outcome: getString(value.outcome),
    message: getString(value.message),
    location: toTestLocation(value.location)
  };
}

function toTestLocation(value: unknown): VbaDevTestLocation | undefined {
  if (!isRecord(value)) {
    return undefined;
  }

  const uriPath = toUriPath(getString(value.file) ?? getString(value.uriPath) ?? getString(value.uri));
  const range = toRange(value.range);
  if (uriPath && range) {
    return {
      uriPath,
      line: range.start.line,
      character: range.start.character,
      range
    };
  }

  const line = getNumber(value.line);
  const character = getNumber(value.character) ?? getNumber(value.column) ?? 0;
  if (!uriPath || line === undefined) {
    return undefined;
  }

  const position = { line, character };
  return {
    uriPath,
    line,
    character,
    range: { start: position, end: position }
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

function isCapabilities(value: unknown): value is VbaDevCapabilities {
  if (!isRecord(value)) {
    return false;
  }

  return (
    typeof value.toolVersion === 'string' &&
    typeof value.contractVersion === 'string' &&
    isCommandCapabilities(value.commands) &&
    (value.debugAdapter === undefined || isDebugAdapterCapability(value.debugAdapter))
  );
}

function isDebugAdapterCapability(value: unknown): value is VbaDevDebugAdapterCapability {
  return isRecord(value) &&
    typeof value.protocolVersion === 'string' &&
    value.protocolVersion.length > 0 &&
    typeof value.transport === 'string' &&
    value.transport.length > 0 &&
    typeof value.command === 'string' &&
    value.command.length > 0;
}

function isCommandCapabilities(value: unknown): value is Record<string, VbaDevCommandCapability> {
  if (!isRecord(value)) {
    return false;
  }

  return Object.values(value).every((command) => (
    isRecord(command) &&
    typeof command.outputSchemaVersion === 'string'
  ));
}

function isRequiredVbaDevContract(value: unknown): value is RequiredVbaDevContract {
  if (
    !isRecord(value) ||
    typeof value.contractVersion !== 'string' ||
    typeof value.debugAdapterProtocolVersion !== 'string' ||
    !isRecord(value.commandSchemaVersions)
  ) {
    return false;
  }

  return Object.values(value.commandSchemaVersions).every((schemaVersion) => typeof schemaVersion === 'string');
}

function isCommonModulesList(value: unknown): value is CommonModulesList {
  if (!isRecord(value) || typeof value.document !== 'string' || !Array.isArray(value.commonModules)) {
    return false;
  }

  return value.commonModules.every((module) => (
    isRecord(module) &&
    typeof module.name === 'string' &&
    typeof module.requested === 'boolean'
  ));
}

function isReferenceList(value: unknown): value is ReferenceList {
  if (!isRecord(value) || typeof value.document !== 'string' || !Array.isArray(value.references)) {
    return false;
  }

  return value.references.every((reference) => (
    isRecord(reference) &&
    typeof reference.name === 'string'
  ));
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
