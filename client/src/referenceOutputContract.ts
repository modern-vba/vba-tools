import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export interface ReferenceOutputWarning {
  readonly code: string;
  readonly message: string;
}

export interface ReferenceTypeLibIdentity {
  readonly guid: string;
  readonly major: number;
  readonly minor: number;
}

export interface ResolvedAvailableReference {
  readonly name: string;
  readonly status: 'resolved';
  readonly identity: ReferenceTypeLibIdentity;
}

export interface AmbiguousAvailableReference {
  readonly name: string;
  readonly status: 'ambiguous';
  readonly reasonCode: 'multipleUsableIdentities';
  readonly candidates: readonly ReferenceTypeLibIdentity[];
  readonly message: string;
}

export interface UnavailableAvailableReference {
  readonly name: string;
  readonly status: 'unavailable';
  readonly reasonCode: 'notRegistered' | 'noUsableIdentity';
  readonly candidates: readonly ReferenceTypeLibIdentity[];
  readonly message: string;
}

export type AvailableReference =
  ResolvedAvailableReference |
  AmbiguousAvailableReference |
  UnavailableAvailableReference;

export interface TrustedAvailableReferenceInventory {
  readonly project: string;
  readonly document: string;
  readonly warnings: readonly ReferenceOutputWarning[];
  readonly references: readonly AvailableReference[];
  readonly resolvedReferences: readonly ResolvedAvailableReference[];
}

export interface ReferenceSelectionEntry {
  readonly name: string;
}

export interface TrustedReferenceSelectionInventory {
  readonly project: string;
  readonly document: string;
  readonly warnings: readonly ReferenceOutputWarning[];
  readonly references: readonly ReferenceSelectionEntry[];
}

export type ReferenceMutationOperation = 'add' | 'remove';
export type AddReferenceMutationStatus =
  'added' | 'promoted' | 'alreadyPresent';

export interface AddReferenceMutationResult {
  readonly requestedName: string;
  readonly storedName: string;
  readonly status: AddReferenceMutationStatus;
}

export interface RemovedReferenceMutationResult {
  readonly requestedName: string;
  readonly storedName: string;
  readonly status: 'removed';
}

export interface AlreadyAbsentReferenceMutationResult {
  readonly requestedName: string;
  readonly storedName: null;
  readonly status: 'alreadyAbsent';
}

export type ReferenceMutationResult =
  AddReferenceMutationResult |
  RemovedReferenceMutationResult |
  AlreadyAbsentReferenceMutationResult;

export interface TrustedReferenceMutationOutput {
  readonly project: string;
  readonly document: string;
  readonly operation: ReferenceMutationOperation;
  readonly warnings: readonly ReferenceOutputWarning[];
  readonly results: readonly ReferenceMutationResult[];
}

export class ReferenceOutputContractError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'ReferenceOutputContractError';
  }
}

const canonicalGuid =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u;
const referenceNameBoundaryWhitespace =
  /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/u;
const standardLibraryNameKey = ordinalIgnoreCaseKey(
  'Visual Basic For Applications'
);
const resolutionMetadataFields = [
  'status',
  'identity',
  'reasonCode',
  'candidates',
  'message'
] as const;
const unverifiedReasonCodes = new Set([
  'excelVbeFailure',
  'probeTimeout',
  'identityReadFailure',
  'cleanupFailure',
  'probeAborted',
  'cancelled'
]);

export function parseAvailableReferenceInventoryOutput(
  stdout: string,
  expectedProjectRoot: string,
  expectedDocument: string
): TrustedAvailableReferenceInventory {
  const root = parseRoot(stdout);
  requireInventoryEnvelope(
    root,
    expectedProjectRoot,
    expectedDocument,
    'available'
  );
  rejectPresentFields(root, ['operation', 'results', 'diagnostics'], 'available inventory');
  const warnings = parseMessages(root.warnings, 'available inventory warnings');
  const values = requireArray(root.references, 'available inventory references');
  const seenNames = new Set<string>();
  const references: AvailableReference[] = [];

  for (let index = 0; index < values.length; index++) {
    const field = `available inventory references[${index}]`;
    const entry = requireRecord(values[index], `${field} must be an object.`);
    const name = requireReferenceName(entry.name, `${field}.name`);
    rejectStandardLibraryName(name, `${field}.name`);
    addUniqueName(seenNames, name, `${field}.name`);

    switch (entry.status) {
      case 'resolved':
        rejectPresentFields(
          entry,
          ['reasonCode', 'candidates', 'message'],
          `${field} resolved entry`
        );
        references.push({
          name,
          status: 'resolved',
          identity: parseIdentity(entry.identity, `${field}.identity`)
        });
        break;

      case 'ambiguous': {
        rejectPresentFields(entry, ['identity'], `${field} ambiguous entry`);
        if (entry.reasonCode !== 'multipleUsableIdentities') {
          fail(`${field}.reasonCode must be multipleUsableIdentities.`);
        }
        const candidates = parseCandidates(entry.candidates, `${field}.candidates`);
        if (candidates.length < 2) {
          fail(`${field}.candidates must contain at least two identities.`);
        }
        references.push({
          name,
          status: 'ambiguous',
          reasonCode: 'multipleUsableIdentities',
          candidates,
          message: requireNonBlankString(entry.message, `${field}.message`)
        });
        break;
      }

      case 'unavailable': {
        rejectPresentFields(entry, ['identity'], `${field} unavailable entry`);
        if (entry.reasonCode !== 'notRegistered' &&
            entry.reasonCode !== 'noUsableIdentity') {
          fail(`${field}.reasonCode is not a supported unavailable reason.`);
        }
        const candidates = parseCandidates(entry.candidates, `${field}.candidates`);
        if (entry.reasonCode === 'notRegistered' && candidates.length !== 0) {
          fail(`${field}.candidates must be empty when no registration matched.`);
        }
        references.push({
          name,
          status: 'unavailable',
          reasonCode: entry.reasonCode,
          candidates,
          message: requireNonBlankString(entry.message, `${field}.message`)
        });
        break;
      }

      case 'unverified':
        validateUnverifiedEntry(entry, field);
        fail('Available inventory contains an unverified reference.');

      default:
        fail(`${field}.status is not a supported available-reference status.`);
    }
  }

  return {
    project: expectedProjectRoot,
    document: expectedDocument,
    warnings,
    references,
    resolvedReferences: references.filter(
      (reference): reference is ResolvedAvailableReference =>
        reference.status === 'resolved'
    )
  };
}

export function parseReferenceSelectionInventoryOutput(
  stdout: string,
  expectedProjectRoot: string,
  expectedDocument: string
): TrustedReferenceSelectionInventory {
  const root = parseRoot(stdout);
  requireInventoryEnvelope(
    root,
    expectedProjectRoot,
    expectedDocument,
    'selection'
  );
  rejectPresentFields(root, ['operation', 'results', 'diagnostics'], 'reference selection');
  const warnings = parseMessages(root.warnings, 'reference selection warnings');
  const values = requireArray(root.references, 'reference selection references');
  const seenNames = new Set<string>();
  const references = values.map((value, index) => {
    const field = `reference selection references[${index}]`;
    const entry = requireRecord(value, `${field} must be an object.`);
    rejectPresentFields(entry, resolutionMetadataFields, field);
    const name = requireReferenceName(entry.name, `${field}.name`);
    rejectStandardLibraryName(name, `${field}.name`);
    addUniqueName(seenNames, name, `${field}.name`);
    return { name };
  });

  return {
    project: expectedProjectRoot,
    document: expectedDocument,
    warnings,
    references
  };
}

export function parseReferenceMutationOutput(
  stdout: string,
  expectedProjectRoot: string,
  expectedDocument: string,
  expectedOperation: ReferenceMutationOperation,
  submittedNames: readonly string[]
): TrustedReferenceMutationOutput {
  const root = parseRoot(stdout);
  requireBaseEnvelope(root, expectedProjectRoot, expectedDocument);
  rejectPresentFields(root, ['mode', 'references', 'diagnostics'], 'reference mutation');
  if (root.operation !== expectedOperation) {
    fail('Reference mutation operation does not match the requested operation.');
  }
  if (root.complete !== true) {
    fail('Reference mutation output must be complete.');
  }
  const warnings = parseMessages(root.warnings, 'reference mutation warnings');
  validateSubmittedNames(submittedNames);
  const values = requireArray(root.results, 'reference mutation results');
  if (values.length !== submittedNames.length) {
    fail('Reference mutation results must contain one entry per submitted name.');
  }

  const results = values.map((value, index): ReferenceMutationResult => {
    const field = `reference mutation results[${index}]`;
    const entry = requireRecord(value, `${field} must be an object.`);
    const requestedName = requireReferenceName(
      entry.requestedName,
      `${field}.requestedName`
    );
    if (requestedName !== submittedNames[index]) {
      fail(`${field}.requestedName does not exactly match the submitted name.`);
    }

    if (expectedOperation === 'add') {
      if (entry.status !== 'added' &&
          entry.status !== 'promoted' &&
          entry.status !== 'alreadyPresent') {
        fail(`${field}.status is not valid for reference add.`);
      }
      return {
        requestedName,
        storedName: requireMatchingStoredName(
          entry.storedName,
          requestedName,
          `${field}.storedName`
        ),
        status: entry.status
      };
    }

    if (entry.status === 'removed') {
      return {
        requestedName,
        storedName: requireMatchingStoredName(
          entry.storedName,
          requestedName,
          `${field}.storedName`
        ),
        status: 'removed'
      };
    }
    if (entry.status === 'alreadyAbsent') {
      if (!Object.prototype.hasOwnProperty.call(entry, 'storedName') ||
          entry.storedName !== null) {
        fail(`${field}.storedName must be null for alreadyAbsent.`);
      }
      return {
        requestedName,
        storedName: null,
        status: 'alreadyAbsent'
      };
    }
    fail(`${field}.status is not valid for reference remove.`);
  });

  return {
    project: expectedProjectRoot,
    document: expectedDocument,
    operation: expectedOperation,
    warnings,
    results
  };
}

function parseRoot(stdout: string): Record<string, unknown> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new ReferenceOutputContractError(
      `Reference command returned invalid JSON: ${String(error)}`
    );
  }
  return requireRecord(parsed, 'Reference command output must be one JSON object.');
}

function requireInventoryEnvelope(
  root: Record<string, unknown>,
  expectedProjectRoot: string,
  expectedDocument: string,
  expectedMode: 'available' | 'selection'
): void {
  requireBaseEnvelope(root, expectedProjectRoot, expectedDocument);
  if (root.mode !== expectedMode) {
    fail(`Reference inventory must use mode ${expectedMode}.`);
  }
  if (root.complete !== true) {
    fail('Reference inventory must be complete.');
  }
}

function requireBaseEnvelope(
  root: Record<string, unknown>,
  expectedProjectRoot: string,
  expectedDocument: string
): void {
  if (root.schemaVersion !== '1.0') {
    fail('Reference output must use schemaVersion 1.0.');
  }
  if (root.scope !== 'project') {
    fail('Reference output must use project scope.');
  }
  if (typeof root.project !== 'string' ||
      !sameOrdinalIgnoreCase(root.project, expectedProjectRoot)) {
    fail('Reference output project does not match the requested projectRoot.');
  }
  if (root.document !== expectedDocument) {
    fail('Reference output document does not match the requested document.');
  }
}

function parseMessages(value: unknown, field: string): readonly ReferenceOutputWarning[] {
  const values = requireArray(value, field);
  return values.map((message, index) => {
    const entry = requireRecord(message, `${field}[${index}] must be an object.`);
    return {
      code: requireNonBlankString(entry.code, `${field}[${index}].code`),
      message: requireNonBlankString(entry.message, `${field}[${index}].message`)
    };
  });
}

function validateUnverifiedEntry(
  entry: Record<string, unknown>,
  field: string
): void {
  rejectPresentFields(entry, ['identity'], `${field} unverified entry`);
  if (typeof entry.reasonCode !== 'string' ||
      !unverifiedReasonCodes.has(entry.reasonCode)) {
    fail(`${field}.reasonCode is not a supported unverified reason.`);
  }
  parseCandidates(entry.candidates, `${field}.candidates`);
  requireNonBlankString(entry.message, `${field}.message`);
}

function parseCandidates(
  value: unknown,
  field: string
): readonly ReferenceTypeLibIdentity[] {
  const values = requireArray(value, field);
  const identities = values.map((identity, index) =>
    parseIdentity(identity, `${field}[${index}]`));
  for (let index = 1; index < identities.length; index++) {
    if (compareIdentity(identities[index - 1]!, identities[index]!) >= 0) {
      fail(`${field} must contain distinct identities in canonical order.`);
    }
  }
  return identities;
}

function compareIdentity(
  left: ReferenceTypeLibIdentity,
  right: ReferenceTypeLibIdentity
): number {
  const guidOrder = left.guid < right.guid ? -1 : left.guid > right.guid ? 1 : 0;
  return guidOrder !== 0
    ? guidOrder
    : left.major !== right.major
      ? left.major - right.major
      : left.minor - right.minor;
}

function parseIdentity(value: unknown, field: string): ReferenceTypeLibIdentity {
  const identity = requireRecord(value, `${field} must be an object.`);
  const guid = requireNonBlankString(identity.guid, `${field}.guid`);
  if (!canonicalGuid.test(guid)) {
    fail(`${field}.guid must be a canonical lowercase GUID.`);
  }
  return {
    guid,
    major: requireUShort(identity.major, `${field}.major`),
    minor: requireUShort(identity.minor, `${field}.minor`)
  };
}

function validateSubmittedNames(submittedNames: readonly string[]): void {
  if (submittedNames.length === 0) {
    fail('Reference mutation must have at least one submitted name.');
  }
  const seenNames = new Set<string>();
  for (let index = 0; index < submittedNames.length; index++) {
    const name = requireReferenceName(
      submittedNames[index],
      `submittedNames[${index}]`
    );
    rejectStandardLibraryName(name, `submittedNames[${index}]`);
    addUniqueName(seenNames, name, `submittedNames[${index}]`);
  }
}

function requireMatchingStoredName(
  value: unknown,
  requestedName: string,
  field: string
): string {
  const storedName = requireReferenceName(value, field);
  rejectStandardLibraryName(storedName, field);
  if (!sameOrdinalIgnoreCase(storedName, requestedName)) {
    fail(`${field} does not identify the submitted reference name.`);
  }
  return storedName;
}

function addUniqueName(seen: Set<string>, name: string, field: string): void {
  const key = ordinalIgnoreCaseKey(name);
  if (seen.has(key)) {
    fail(`${field} duplicates another reference name under OrdinalIgnoreCase.`);
  }
  seen.add(key);
}

function rejectStandardLibraryName(name: string, field: string): void {
  if (ordinalIgnoreCaseKey(name) === standardLibraryNameKey) {
    fail(`${field} must not select Visual Basic For Applications.`);
  }
}

function sameOrdinalIgnoreCase(left: string, right: string): boolean {
  return ordinalIgnoreCaseKey(left) === ordinalIgnoreCaseKey(right);
}

function rejectPresentFields(
  value: Record<string, unknown>,
  fields: readonly string[],
  subject: string
): void {
  for (const field of fields) {
    if (Object.prototype.hasOwnProperty.call(value, field)) {
      fail(`${subject} must not include ${field}.`);
    }
  }
}

function requireArray(value: unknown, field: string): readonly unknown[] {
  if (!Array.isArray(value)) {
    fail(`${field} must be an array.`);
  }
  return value;
}

function requireUShort(value: unknown, field: string): number {
  if (typeof value !== 'number' ||
      !Number.isInteger(value) ||
      value < 0 ||
      value > 65_535) {
    fail(`${field} must be an integer from 0 through 65535.`);
  }
  return value;
}

function requireRecord(value: unknown, message: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    fail(message);
  }
  return value as Record<string, unknown>;
}

function requireReferenceName(value: unknown, field: string): string {
  const name = requireNonBlankString(value, field);
  if (referenceNameBoundaryWhitespace.test(name)) {
    fail(`${field} must already be trimmed.`);
  }
  return name;
}

function requireNonBlankString(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    fail(`${field} must be a nonblank string.`);
  }
  return value;
}

function fail(message: string): never {
  throw new ReferenceOutputContractError(message);
}
