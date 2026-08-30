import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export type CommonModulesMutationOperation = 'add' | 'update';

export interface CommonModulesMutationWarning {
  readonly code: string;
  readonly message: string;
}

export interface CommonModuleInstalledChange {
  readonly kind: 'installed';
  readonly sourceSetRelativePath: string;
}

export interface CommonModuleSourceUpdatedChange {
  readonly kind: 'sourceUpdated';
  readonly sourceSetRelativePath: string;
}

export interface CommonModuleDirectRequestPromotedChange {
  readonly kind: 'directRequestPromoted';
}

export interface CommonModuleTestOnlyChangedChange {
  readonly kind: 'testOnlyChanged';
  readonly testOnly: boolean;
}

export interface CommonModuleOrphanedChangedChange {
  readonly kind: 'orphanedChanged';
  readonly orphaned: boolean;
}

export type CommonModuleMutationChange =
  CommonModuleInstalledChange |
  CommonModuleSourceUpdatedChange |
  CommonModuleDirectRequestPromotedChange |
  CommonModuleTestOnlyChangedChange |
  CommonModuleOrphanedChangedChange;

export interface CommonModulesMutationModule {
  readonly name: string;
  readonly moduleFile: string;
  readonly requested: boolean;
  readonly testOnly: boolean;
  readonly orphaned: boolean;
  readonly status: 'changed' | 'unchanged';
  readonly changes: readonly CommonModuleMutationChange[];
}

export interface CommonModulesReferenceChange {
  readonly kind: 'added';
  readonly name: string;
  readonly requested: false;
}

export interface CommonModulesMutationDocument {
  readonly document: string;
  readonly modules: readonly CommonModulesMutationModule[];
  readonly referenceChanges: readonly CommonModulesReferenceChange[];
}

export interface TrustedCommonModulesMutationOutput {
  readonly project: string;
  readonly document: string | null;
  readonly operation: CommonModulesMutationOperation;
  readonly warnings: readonly CommonModulesMutationWarning[];
  readonly documents: readonly CommonModulesMutationDocument[];
}

export class CommonModulesMutationOutputContractError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'CommonModulesMutationOutputContractError';
  }
}

const moduleNameBoundaryWhitespace =
  /^[\u0009\u0019\u0020\u1680\u180E\u2000-\u200A\u202F\u205F\u3000\r\n]|[\u0009\u0019\u0020\u1680\u180E\u2000-\u200A\u202F\u205F\u3000\r\n]$/u;
const vbaLayoutWhitespaceCodeUnit =
  /^[\u0009\u0019\u0020\u1680\u180E\u2000-\u200A\u202F\u205F\u3000]$/u;
const generalBoundaryWhitespace =
  /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/u;
const standardLibraryKey = ordinalIgnoreCaseKey('Visual Basic For Applications');
const supportedModuleExtensionKeys = new Set([
  ordinalIgnoreCaseKey('.bas'),
  ordinalIgnoreCaseKey('.cls'),
  ordinalIgnoreCaseKey('.frm')
]);
const changeRank: Readonly<Record<CommonModuleMutationChange['kind'], number>> = {
  installed: 0,
  sourceUpdated: 0,
  directRequestPromoted: 1,
  testOnlyChanged: 2,
  orphanedChanged: 3
};
const warningRank = new Map<string, number>([
  ['orphanedCommonModulesRetained', 0],
  ['cancellationDeferred', 1],
  ['commonModulesSnapshotCleanupFailed', 2],
  ['leaseMarkerCleanupFailed', 3]
]);

export function parseCommonModulesMutationOutput(
  stdout: string,
  expectedProjectRoot: string,
  expectedDocument: string | null,
  expectedOperation: CommonModulesMutationOperation,
  submittedModuleNames: readonly string[]
): TrustedCommonModulesMutationOutput {
  const root = parseRoot(stdout);
  requireExactKeys(root, [
    'schemaVersion',
    'scope',
    'project',
    'document',
    'operation',
    'complete',
    'warnings',
    'documents'
  ], 'CommonModules mutation output');
  if (root.schemaVersion !== '1.0') {
    fail('CommonModules mutation output must use schemaVersion 1.0.');
  }
  if (root.scope !== 'project') {
    fail('CommonModules mutation output must use project scope.');
  }
  if (typeof root.project !== 'string' ||
      !sameOrdinalIgnoreCase(root.project, expectedProjectRoot)) {
    fail('CommonModules mutation project does not match the requested projectRoot.');
  }
  if (root.document !== expectedDocument) {
    fail('CommonModules mutation document does not match the requested scope.');
  }
  if (root.operation !== expectedOperation) {
    fail('CommonModules mutation operation does not match the requested operation.');
  }
  if (root.complete !== true) {
    fail('CommonModules mutation output must be complete.');
  }

  const normalizedSubmittedModuleNames = normalizeSubmittedModuleNames(
    submittedModuleNames,
    expectedOperation
  );
  const warnings = parseWarnings(root.warnings);
  const documents = parseDocuments(root.documents, expectedOperation);
  validateDocumentScope(documents, expectedDocument, expectedOperation);
  validateOrphanWarningConsistency(warnings, documents);
  validateCancellationWarningConsistency(warnings, documents);
  if (expectedOperation === 'add') {
    validateExplicitAddRequests(documents[0]!, normalizedSubmittedModuleNames);
  }

  return {
    project: root.project,
    document: expectedDocument,
    operation: expectedOperation,
    warnings,
    documents
  };
}

function parseRoot(stdout: string): Record<string, unknown> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new CommonModulesMutationOutputContractError(
      `CommonModules mutation returned invalid JSON: ${String(error)}`
    );
  }
  return requireRecord(parsed, 'CommonModules mutation output must be one JSON object.');
}

function parseWarnings(value: unknown): readonly CommonModulesMutationWarning[] {
  const values = requireArray(value, 'CommonModules mutation warnings');
  const seenCodes = new Set<string>();
  let lastKnownRank = -1;
  return values.map((value, index) => {
    const field = `CommonModules mutation warnings[${index}]`;
    const warning = requireRecord(value, `${field} must be an object.`);
    requireExactKeys(warning, ['code', 'message'], field);
    const code = requireGeneralName(warning.code, `${field}.code`);
    if (seenCodes.has(code)) {
      fail(`${field}.code duplicates another warning code.`);
    }
    seenCodes.add(code);
    const rank = warningRank.get(code);
    if (rank !== undefined) {
      if (rank <= lastKnownRank) {
        fail('Known CommonModules warning codes are not in canonical order.');
      }
      lastKnownRank = rank;
    }
    return {
      code,
      message: requireGeneralName(warning.message, `${field}.message`)
    };
  });
}

function parseDocuments(
  value: unknown,
  operation: CommonModulesMutationOperation
): readonly CommonModulesMutationDocument[] {
  const values = requireArray(value, 'CommonModules mutation documents');
  const seenDocuments = new Set<string>();
  const documents = values.map((value, index) => {
    const field = `CommonModules mutation documents[${index}]`;
    const document = requireRecord(value, `${field} must be an object.`);
    requireExactKeys(document, ['document', 'modules', 'referenceChanges'], field);
    const documentName = requireGeneralName(document.document, `${field}.document`);
    addUniqueOrdinalIgnoreCase(seenDocuments, documentName, `${field}.document`);
    const modules = parseModules(document.modules, operation, field);
    if (operation === 'update' && modules.length === 0) {
      fail(`${field}.modules must contain every installed CommonModule target.`);
    }
    return {
      document: documentName,
      modules,
      referenceChanges: parseReferenceChanges(document.referenceChanges, field)
    };
  });

  if (operation === 'update') {
    for (let index = 1; index < documents.length; index++) {
      if (compareOrdinalIgnoreCase(documents[index - 1]!.document, documents[index]!.document) >= 0) {
        fail('CommonModules Update documents are not in canonical document-name order.');
      }
    }
  }
  return documents;
}

function parseModules(
  value: unknown,
  operation: CommonModulesMutationOperation,
  documentField: string
): readonly CommonModulesMutationModule[] {
  const values = requireArray(value, `${documentField}.modules`);
  const seenNames = new Set<string>();
  const seenModuleFiles = new Set<string>();
  return values.map((value, index) => {
    const field = `${documentField}.modules[${index}]`;
    const module = requireRecord(value, `${field} must be an object.`);
    requireExactKeys(module, [
      'name',
      'moduleFile',
      'requested',
      'testOnly',
      'orphaned',
      'status',
      'changes'
    ], field);
    const name = requireModuleName(module.name, `${field}.name`);
    const moduleFile = requireModuleFile(module.moduleFile, `${field}.moduleFile`);
    if (!sameOrdinalIgnoreCase(name, moduleFile.slice(0, moduleFile.lastIndexOf('.')))) {
      fail(`${field}.name does not match the moduleFile basename.`);
    }
    addUniqueOrdinalIgnoreCase(seenNames, name, `${field}.name`);
    addUniqueOrdinalIgnoreCase(seenModuleFiles, moduleFile, `${field}.moduleFile`);
    const requested = requireBoolean(module.requested, `${field}.requested`);
    const testOnly = requireBoolean(module.testOnly, `${field}.testOnly`);
    const orphaned = requireBoolean(module.orphaned, `${field}.orphaned`);
    if (module.status !== 'changed' && module.status !== 'unchanged') {
      fail(`${field}.status must be changed or unchanged.`);
    }
    const changes = parseChanges(
      module.changes,
      operation,
      { name, moduleFile, requested, testOnly, orphaned },
      field
    );
    if ((module.status === 'unchanged') !== (changes.length === 0)) {
      fail(`${field}.status must be unchanged exactly when changes is empty.`);
    }
    return {
      name,
      moduleFile,
      requested,
      testOnly,
      orphaned,
      status: module.status,
      changes
    };
  });
}

function parseChanges(
  value: unknown,
  operation: CommonModulesMutationOperation,
  module: {
    readonly name: string;
    readonly moduleFile: string;
    readonly requested: boolean;
    readonly testOnly: boolean;
    readonly orphaned: boolean;
  },
  moduleField: string
): readonly CommonModuleMutationChange[] {
  const values = requireArray(value, `${moduleField}.changes`);
  const changes: CommonModuleMutationChange[] = [];
  const seenKinds = new Set<string>();
  let lastRank = -1;
  for (let index = 0; index < values.length; index++) {
    const field = `${moduleField}.changes[${index}]`;
    const change = requireRecord(values[index], `${field} must be an object.`);
    if (typeof change.kind !== 'string' ||
        !Object.prototype.hasOwnProperty.call(changeRank, change.kind)) {
      fail(`${field}.kind is not a supported CommonModules change kind.`);
    }
    const kind = change.kind as CommonModuleMutationChange['kind'];
    if (seenKinds.has(kind)) {
      fail(`${field}.kind duplicates another module change.`);
    }
    seenKinds.add(kind);
    const rank = changeRank[kind];
    if (rank < lastRank) {
      fail(`${moduleField}.changes are not in canonical order.`);
    }
    lastRank = rank;

    if (operation === 'add' &&
        kind !== 'installed' &&
        kind !== 'directRequestPromoted') {
      fail(`${field}.kind is not valid for CommonModules Add.`);
    }
    if (operation === 'update' && kind === 'installed' && module.requested) {
      fail(`${field} cannot install a directly requested module during Update.`);
    }

    switch (kind) {
      case 'installed':
      case 'sourceUpdated': {
        requireExactKeys(change, ['kind', 'sourceSetRelativePath'], field);
        const sourceSetRelativePath = requireSourceSetRelativePath(
          change.sourceSetRelativePath,
          module.moduleFile,
          `${field}.sourceSetRelativePath`
        );
        changes.push({ kind, sourceSetRelativePath });
        break;
      }
      case 'directRequestPromoted':
        requireExactKeys(change, ['kind'], field);
        if (!module.requested || operation !== 'add') {
          fail(`${field} is inconsistent with final direct-request state or operation.`);
        }
        changes.push({ kind });
        break;
      case 'testOnlyChanged': {
        requireExactKeys(change, ['kind', 'testOnly'], field);
        const testOnly = requireBoolean(change.testOnly, `${field}.testOnly`);
        if (testOnly !== module.testOnly) {
          fail(`${field}.testOnly does not match the final module state.`);
        }
        changes.push({ kind, testOnly });
        break;
      }
      case 'orphanedChanged': {
        requireExactKeys(change, ['kind', 'orphaned'], field);
        const orphaned = requireBoolean(change.orphaned, `${field}.orphaned`);
        if (orphaned !== module.orphaned) {
          fail(`${field}.orphaned does not match the final module state.`);
        }
        changes.push({ kind, orphaned });
        break;
      }
    }
  }

  if (seenKinds.has('installed')) {
    if (changes.length !== 1 || module.orphaned) {
      fail(`${moduleField} installation must be the sole change and finish non-orphaned.`);
    }
  }
  if (module.orphaned &&
      (seenKinds.has('sourceUpdated') || seenKinds.has('testOnlyChanged'))) {
    fail(`${moduleField} cannot remain orphaned after a repository-backed update.`);
  }
  return changes;
}

function parseReferenceChanges(
  value: unknown,
  documentField: string
): readonly CommonModulesReferenceChange[] {
  const values = requireArray(value, `${documentField}.referenceChanges`);
  const seenNames = new Set<string>();
  return values.map((value, index) => {
    const field = `${documentField}.referenceChanges[${index}]`;
    const change = requireRecord(value, `${field} must be an object.`);
    requireExactKeys(change, ['kind', 'name', 'requested'], field);
    if (change.kind !== 'added' || change.requested !== false) {
      fail(`${field} must be an added reference with requested false.`);
    }
    const name = requireGeneralName(change.name, `${field}.name`);
    if (ordinalIgnoreCaseKey(name) === standardLibraryKey) {
      fail(`${field}.name must not identify Visual Basic For Applications.`);
    }
    addUniqueOrdinalIgnoreCase(seenNames, name, `${field}.name`);
    return { kind: 'added', name, requested: false };
  });
}

function validateDocumentScope(
  documents: readonly CommonModulesMutationDocument[],
  expectedDocument: string | null,
  operation: CommonModulesMutationOperation
): void {
  if (operation === 'add') {
    if (expectedDocument === null ||
        documents.length !== 1 ||
        documents[0]!.document !== expectedDocument) {
      fail('CommonModules Add must return exactly the selected document.');
    }
    return;
  }
  if (expectedDocument !== null) {
    fail('CommonModules Update must be project-scoped.');
  }
}

function validateOrphanWarningConsistency(
  warnings: readonly CommonModulesMutationWarning[],
  documents: readonly CommonModulesMutationDocument[]
): void {
  const hasOrphanedModule = documents.some((document) =>
    document.modules.some((module) => module.orphaned)
  );
  const hasOrphanWarning = warnings.some((warning) =>
    warning.code === 'orphanedCommonModulesRetained'
  );
  if (hasOrphanedModule !== hasOrphanWarning) {
    fail('CommonModules orphan-retention warning does not match the final module state.');
  }
}

function validateCancellationWarningConsistency(
  warnings: readonly CommonModulesMutationWarning[],
  documents: readonly CommonModulesMutationDocument[]
): void {
  const hasDeferredCancellation = warnings.some((warning) =>
    warning.code === 'cancellationDeferred'
  );
  const hasSourceChange = documents.some((document) =>
    document.modules.some((module) => module.changes.some((change) =>
      change.kind === 'installed' || change.kind === 'sourceUpdated'
    ))
  );
  if (hasDeferredCancellation && !hasSourceChange) {
    fail('CommonModules cancellation cannot be deferred without a source change.');
  }
}

function normalizeSubmittedModuleNames(
  submittedNames: readonly string[],
  operation: CommonModulesMutationOperation
): readonly string[] {
  if (operation === 'update') {
    if (submittedNames.length !== 0) {
      fail('CommonModules Update must not include submitted module names.');
    }
    return [];
  }
  const normalizedNames: string[] = [];
  for (let index = 0; index < submittedNames.length; index++) {
    const submittedName = submittedNames[index];
    if (typeof submittedName !== 'string') {
      fail(`submittedModuleNames[${index}] must be a string.`);
    }
    const name = trimVbaLayoutWhitespace(submittedName);
    if (name.length > 0) {
      normalizedNames.push(requireModuleName(name, `submittedModuleNames[${index}]`));
    }
  }
  if (normalizedNames.length === 0) {
    fail('CommonModules Add must include at least one submitted module name.');
  }
  return normalizedNames;
}

function validateExplicitAddRequests(
  document: CommonModulesMutationDocument,
  submittedNames: readonly string[]
): void {
  for (let index = 0; index < submittedNames.length; index++) {
    const submittedName = submittedNames[index]!;
    const matches = document.modules.filter((module) =>
      matchesSubmittedModuleName(module, submittedName)
    );
    if (matches.length !== 1 || !matches[0]!.requested) {
      fail(`submittedModuleNames[${index}] is not represented by one final requested module.`);
    }
  }
  for (const module of document.modules) {
    const establishesDirectIntent = module.changes.some((change) =>
      change.kind === 'directRequestPromoted' ||
      (change.kind === 'installed' && module.requested)
    );
    if (establishesDirectIntent && !submittedNames.some((submittedName) =>
      matchesSubmittedModuleName(module, submittedName)
    )) {
      fail(`CommonModules Add established direct intent for non-submitted module '${module.name}'.`);
    }
  }
}

function matchesSubmittedModuleName(
  module: CommonModulesMutationModule,
  submittedName: string
): boolean {
  return sameOrdinalIgnoreCase(module.name, submittedName) ||
    sameOrdinalIgnoreCase(module.moduleFile, submittedName);
}

function trimVbaLayoutWhitespace(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && vbaLayoutWhitespaceCodeUnit.test(value[start]!)) {
    start++;
  }
  while (end > start && vbaLayoutWhitespaceCodeUnit.test(value[end - 1]!)) {
    end--;
  }
  return value.slice(start, end);
}

function requireSourceSetRelativePath(
  value: unknown,
  moduleFile: string,
  field: string
): string {
  const path = requireGeneralName(value, field);
  if (path.includes('\\') || path.startsWith('/') || /^[a-z]:/iu.test(path)) {
    fail(`${field} must be a normalized DocumentSourceSet-relative path.`);
  }
  const segments = path.split('/');
  if (segments.some((segment) => segment.length === 0 || segment === '.' || segment === '..') ||
      !sameOrdinalIgnoreCase(segments[segments.length - 1]!, moduleFile)) {
    fail(`${field} must end in the moduleFile without traversal.`);
  }
  return path;
}

function requireModuleName(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.length === 0 || moduleNameBoundaryWhitespace.test(value)) {
    fail(`${field} must be a nonempty already-trimmed CommonModuleName.`);
  }
  return value;
}

function requireModuleFile(value: unknown, field: string): string {
  if (typeof value !== 'string' ||
      value.length === 0 ||
      value.includes('/') ||
      value.includes('\\') ||
      moduleNameBoundaryWhitespace.test(value)) {
    fail(`${field} must be a flat .bas, .cls, or .frm source identity.`);
  }
  const extensionIndex = value.lastIndexOf('.');
  const name = extensionIndex < 0 ? '' : value.slice(0, extensionIndex);
  const extension = extensionIndex < 0 ? '' : value.slice(extensionIndex);
  if (name.length === 0 ||
      !supportedModuleExtensionKeys.has(ordinalIgnoreCaseKey(extension))) {
    fail(`${field} must be a flat .bas, .cls, or .frm source identity.`);
  }
  return value;
}

function requireGeneralName(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.length === 0 || generalBoundaryWhitespace.test(value)) {
    fail(`${field} must be a nonempty already-trimmed string.`);
  }
  return value;
}

function requireBoolean(value: unknown, field: string): boolean {
  if (typeof value !== 'boolean') {
    fail(`${field} must be a boolean.`);
  }
  return value;
}

function requireArray(value: unknown, field: string): readonly unknown[] {
  if (!Array.isArray(value)) {
    fail(`${field} must be an array.`);
  }
  return value;
}

function requireRecord(value: unknown, message: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    fail(message);
  }
  return value as Record<string, unknown>;
}

function requireExactKeys(
  value: Record<string, unknown>,
  expected: readonly string[],
  field: string
): void {
  const actual = Object.keys(value).sort();
  const canonical = [...expected].sort();
  if (actual.length !== canonical.length ||
      actual.some((key, index) => key !== canonical[index])) {
    fail(`${field} contains missing or unsupported properties.`);
  }
}

function addUniqueOrdinalIgnoreCase(
  seen: Set<string>,
  value: string,
  field: string
): void {
  const key = ordinalIgnoreCaseKey(value);
  if (seen.has(key)) {
    fail(`${field} duplicates another value under OrdinalIgnoreCase.`);
  }
  seen.add(key);
}

function compareOrdinalIgnoreCase(left: string, right: string): number {
  const leftKey = ordinalIgnoreCaseKey(left);
  const rightKey = ordinalIgnoreCaseKey(right);
  if (leftKey !== rightKey) {
    return leftKey < rightKey ? -1 : 1;
  }
  return left === right ? 0 : left < right ? -1 : 1;
}

function sameOrdinalIgnoreCase(left: string, right: string): boolean {
  return ordinalIgnoreCaseKey(left) === ordinalIgnoreCaseKey(right);
}

function fail(message: string): never {
  throw new CommonModulesMutationOutputContractError(message);
}
