import * as path from 'node:path';

import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export interface NewExcelProjectReceiptRequest {
  readonly projectName: string;
  readonly projectRoot: string;
}

export interface NewExcelProjectWarning {
  readonly code: string;
  readonly message: string;
  readonly [property: string]: unknown;
}

export interface NewExcelProjectCommonModule {
  readonly name: string;
  readonly moduleFile: string;
  readonly requested: boolean;
  readonly testOnly: boolean;
  readonly orphaned: false;
}

export interface NewExcelProjectReference {
  readonly name: string;
  readonly requested: boolean;
}

export interface NewExcelProjectDocument {
  readonly kind: 'excel';
  readonly sourcePath: string;
  readonly templatePath: string;
  readonly binPath: string;
  readonly publishPath: string;
  readonly commonModules: readonly NewExcelProjectCommonModule[];
  readonly references: readonly NewExcelProjectReference[];
}

export interface NewExcelProjectManifest {
  readonly schemaVersion: 1;
  readonly projectName: string;
  readonly primaryDocument: string;
  readonly documents: Readonly<Record<string, NewExcelProjectDocument>>;
  readonly commonModulesRepository?: '../common_modules_repo' | undefined;
}

export interface TrustedNewExcelProjectReceipt {
  readonly schemaVersion: '1.0';
  readonly scope: 'project';
  readonly project: string;
  readonly document: string;
  readonly operation: 'new';
  readonly template: 'excel';
  readonly complete: true;
  readonly warnings: readonly NewExcelProjectWarning[];
  readonly manifestPath: string;
  readonly manifest: NewExcelProjectManifest;
  readonly [property: string]: unknown;
}

export class NewExcelProjectReceiptError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'NewExcelProjectReceiptError';
  }
}

const repositoryAbsentWarningCode = 'commonModulesRepositoryNotFound';
const repositoryAbsentWarningMessage =
  'CommonModules repository was not found; the project was created without shared modules.';
const snapshotCleanupWarningCode = 'commonModulesSnapshotCleanupFailed';
const snapshotCleanupWarningPrefix =
  'The project was created, but its non-authoritative CommonModules snapshot workspace could not be removed: "';
const leaseCleanupWarningCode = 'leaseMarkerCleanupFailed';
const leaseCleanupWarningPrefix =
  'The project was created and its project lease was released, but the lease marker could not be removed: "';
const retainedPathWarningSuffix = '".';
const manifestReferenceBoundaryWhitespace =
  /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]$/u;

export function parseNewExcelProjectReceipt(
  stdout: string,
  request: NewExcelProjectReceiptRequest
): TrustedNewExcelProjectReceipt {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new NewExcelProjectReceiptError(
      `vba-dev new excel returned invalid JSON: ${String(error)}`
    );
  }

  if (!isRecord(parsed)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel output must be one JSON object.'
    );
  }
  if (parsed.schemaVersion !== '1.0') {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt must use schemaVersion 1.0.'
    );
  }
  requireLiteral(parsed, 'scope', 'project');
  const expectedProjectRoot = requireNormalizedAbsolutePath(
    request.projectRoot,
    'requested projectRoot'
  );
  if (
    typeof parsed.project !== 'string' ||
    !isNormalizedAbsolutePath(parsed.project) ||
    !sameWindowsPath(parsed.project, expectedProjectRoot)
  ) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt project does not match the requested projectRoot.'
    );
  }
  if (parsed.document !== request.projectName) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt document does not match the requested projectName.'
    );
  }
  requireLiteral(parsed, 'operation', 'new');
  requireLiteral(parsed, 'template', 'excel');
  requireLiteral(parsed, 'complete', true);

  const expectedManifestPath = path.win32.join(
    expectedProjectRoot,
    'vba-project.json'
  );
  if (
    typeof parsed.manifestPath !== 'string' ||
    !isNormalizedAbsolutePath(parsed.manifestPath) ||
    !sameWindowsPath(parsed.manifestPath, expectedManifestPath)
  ) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifestPath is not the requested project manifest.'
    );
  }
  const warningFacts = validateWarnings(parsed.warnings, expectedManifestPath);
  if (!isRecord(parsed.manifest)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest must be an object.'
    );
  }
  requireOnlyProperties(parsed.manifest, 'manifest', [
    'schemaVersion',
    'projectName',
    'primaryDocument',
    'documents',
    'commonModulesRepository',
    'commandDefaults'
  ]);
  if (parsed.manifest.schemaVersion !== 1) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest must use schemaVersion 1.'
    );
  }
  if (parsed.manifest.projectName !== request.projectName) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest projectName does not match the request.'
    );
  }
  if (parsed.manifest.primaryDocument !== request.projectName) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest primaryDocument does not match the request.'
    );
  }
  if (Object.hasOwn(parsed.manifest, 'commandDefaults')) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest commandDefaults must be omitted.'
    );
  }
  if (!isRecord(parsed.manifest.documents)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest documents must be an object.'
    );
  }
  const documentNames = Object.keys(parsed.manifest.documents);
  if (documentNames.length !== 1 || documentNames[0] !== request.projectName) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest must contain the sole exact requested document.'
    );
  }
  const document = parsed.manifest.documents[request.projectName];
  if (!isRecord(document)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest document must be an object.'
    );
  }
  requireOnlyProperties(document, 'manifest document', [
    'kind',
    'sourcePath',
    'templatePath',
    'binPath',
    'publishPath',
    'commonModules',
    'references'
  ]);
  if (document.kind !== 'excel') {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest document kind must be excel.'
    );
  }
  const expectedPaths: Readonly<Record<string, string>> = {
    sourcePath: `src/${request.projectName}`,
    templatePath: `src/${request.projectName}/${request.projectName}.xlsm`,
    binPath: `bin/${request.projectName}.xlsm`,
    publishPath: `publish/${request.projectName}.xlsm`
  };
  for (const [property, expected] of Object.entries(expectedPaths)) {
    if (document[property] !== expected) {
      throw new NewExcelProjectReceiptError(
        `vba-dev new excel receipt manifest document ${property} must be ${expected}.`
      );
    }
  }
  validateCommonModules(document.commonModules);
  validateReferences(document.references);
  validateRepositoryRelationship(
    parsed.manifest.commonModulesRepository,
    document.commonModules,
    warningFacts
  );

  return parsed as unknown as TrustedNewExcelProjectReceipt;
}

interface RecognizedWarningFacts {
  readonly repositoryAbsent: boolean;
  readonly snapshotCleanupFailed: boolean;
}

function validateWarnings(
  value: unknown,
  expectedManifestPath: string
): RecognizedWarningFacts {
  if (!Array.isArray(value)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt warnings must be an array.'
    );
  }
  const warnings: Array<
    Record<string, unknown> & { readonly code: string; readonly message: string }
  > = [];
  for (const warning of value) {
    if (!isRecord(warning)) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt warnings entry must be an object.'
      );
    }
    if (typeof warning.code !== 'string' || warning.code.trim().length === 0) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt warnings entry must have a nonempty code.'
      );
    }
    if (
      typeof warning.message !== 'string' ||
      warning.message.trim().length === 0
    ) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt warnings entry must have a nonempty message.'
      );
    }
    warnings.push(warning as Record<string, unknown> & {
      readonly code: string;
      readonly message: string;
    });
  }

  const recognizedRanks = new Map<string, number>([
    [repositoryAbsentWarningCode, 0],
    [snapshotCleanupWarningCode, 1],
    [leaseCleanupWarningCode, 2]
  ]);
  const seen = new Set<string>();
  let lastRecognizedRank = -1;
  for (const warning of warnings) {
    const { code, message } = warning;
    const rank = recognizedRanks.get(code);
    if (rank === undefined) {
      continue;
    }
    if (seen.has(code)) {
      throw new NewExcelProjectReceiptError(
        `vba-dev new excel receipt warnings contain duplicate recognized code ${code}.`
      );
    }
    if (rank < lastRecognizedRank) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt recognized warnings are out of canonical order.'
      );
    }
    seen.add(code);
    lastRecognizedRank = rank;

    if (code === repositoryAbsentWarningCode) {
      if (message !== repositoryAbsentWarningMessage) {
        throw new NewExcelProjectReceiptError(
          'vba-dev new excel receipt repository warning has a noncanonical message.'
        );
      }
    } else if (code === snapshotCleanupWarningCode) {
      const retainedPath = parseRetainedPathWarning(
        message,
        snapshotCleanupWarningPrefix,
        'CommonModules snapshot'
      );
      if (!isNormalizedAbsolutePath(retainedPath)) {
        throw new NewExcelProjectReceiptError(
          'vba-dev new excel receipt CommonModules snapshot warning path must be normalized and absolute.'
        );
      }
    } else {
      const retainedPath = parseRetainedPathWarning(
        message,
        leaseCleanupWarningPrefix,
        'lease marker'
      );
      const expectedMarkerPath = `${expectedManifestPath}.vba-dev.lock`;
      if (
        !isNormalizedAbsolutePath(retainedPath) ||
        !sameWindowsPath(retainedPath, expectedMarkerPath)
      ) {
        throw new NewExcelProjectReceiptError(
          'vba-dev new excel receipt lease marker warning path does not match the request.'
        );
      }
    }
  }

  return {
    repositoryAbsent: seen.has(repositoryAbsentWarningCode),
    snapshotCleanupFailed: seen.has(snapshotCleanupWarningCode)
  };
}

function parseRetainedPathWarning(
  message: string,
  prefix: string,
  description: string
): string {
  if (
    !message.startsWith(prefix) ||
    !message.endsWith(retainedPathWarningSuffix)
  ) {
    throw new NewExcelProjectReceiptError(
      `vba-dev new excel receipt ${description} warning has a noncanonical message.`
    );
  }
  const retainedPath = message.slice(
    prefix.length,
    -retainedPathWarningSuffix.length
  );
  if (retainedPath.length === 0 || retainedPath.includes('"')) {
    throw new NewExcelProjectReceiptError(
      `vba-dev new excel receipt ${description} warning has an invalid retained path.`
    );
  }
  return retainedPath;
}

function validateRepositoryRelationship(
  commonModulesRepository: unknown,
  commonModules: unknown,
  warningFacts: RecognizedWarningFacts
): void {
  const repositorySelected = commonModulesRepository === '../common_modules_repo';
  if (
    commonModulesRepository !== undefined &&
    !repositorySelected
  ) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest commonModulesRepository is not canonical.'
    );
  }
  if (repositorySelected === warningFacts.repositoryAbsent) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt repository warning does not match commonModulesRepository state.'
    );
  }
  if (
    !repositorySelected &&
    (!Array.isArray(commonModules) || commonModules.length !== 0)
  ) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt absent repository must have no installed CommonModules.'
    );
  }
  if (warningFacts.snapshotCleanupFailed && !repositorySelected) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt snapshot cleanup warning requires a selected repository.'
    );
  }
}

function validateCommonModules(value: unknown): void {
  if (!Array.isArray(value)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest CommonModules selection must be an array.'
    );
  }

  const names = new Set<string>();
  const moduleFiles = new Set<string>();
  for (const entry of value) {
    if (!isRecord(entry)) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules entry must be an object.'
      );
    }
    requireOnlyProperties(entry, 'manifest CommonModules entry', [
      'name',
      'moduleFile',
      'requested',
      'testOnly',
      'orphaned'
    ]);
    if (typeof entry.name !== 'string' || entry.name.length === 0) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules entry must have a nonempty name.'
      );
    }
    // The producer owns MS-VBAL lexical and other manifest-domain validation.
    // This consumer checks only receipt-local facts it can reproduce exactly.
    if (typeof entry.moduleFile !== 'string') {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules entry must have a moduleFile.'
      );
    }
    const moduleFileMatch = /^([^/\\]+)\.([^./\\]+)$/u.exec(entry.moduleFile);
    if (
      moduleFileMatch === null ||
      !['bas', 'cls', 'frm'].some((extension) =>
        sameOrdinalIgnoreCase(moduleFileMatch[2] ?? '', extension))
    ) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules moduleFile must be one flat VBA source file.'
      );
    }
    if (!sameOrdinalIgnoreCase(entry.name, moduleFileMatch[1] ?? '')) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules name must match moduleFile.'
      );
    }
    if (
      typeof entry.requested !== 'boolean' ||
      typeof entry.testOnly !== 'boolean' ||
      entry.orphaned !== false
    ) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules entry has invalid final state.'
      );
    }

    const normalizedName = ordinalIgnoreCaseKey(entry.name);
    const normalizedModuleFile = ordinalIgnoreCaseKey(entry.moduleFile);
    if (names.has(normalizedName) || moduleFiles.has(normalizedModuleFile)) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest CommonModules selection contains a duplicate entry.'
      );
    }
    names.add(normalizedName);
    moduleFiles.add(normalizedModuleFile);
  }
}

function validateReferences(value: unknown): void {
  if (!Array.isArray(value)) {
    throw new NewExcelProjectReceiptError(
      'vba-dev new excel receipt manifest references selection must be an array.'
    );
  }

  const names = new Set<string>();
  for (const entry of value) {
    if (!isRecord(entry)) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest references entry must be an object.'
      );
    }
    requireOnlyProperties(entry, 'manifest references entry', [
      'name',
      'requested'
    ]);
    if (
      typeof entry.name !== 'string' ||
      entry.name.length === 0 ||
      manifestReferenceBoundaryWhitespace.test(entry.name)
    ) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest references entry must have a nonempty trimmed name.'
      );
    }
    if (sameOrdinalIgnoreCase(entry.name, 'Visual Basic For Applications')) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest references cannot select the always-active standard library.'
      );
    }
    if (typeof entry.requested !== 'boolean') {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest references entry must have requested state.'
      );
    }

    const normalizedName = ordinalIgnoreCaseKey(entry.name);
    if (names.has(normalizedName)) {
      throw new NewExcelProjectReceiptError(
        'vba-dev new excel receipt manifest references selection contains a duplicate name.'
      );
    }
    names.add(normalizedName);
  }
}

function requireOnlyProperties(
  owner: Record<string, unknown>,
  description: string,
  allowedProperties: readonly string[]
): void {
  const allowed = new Set(allowedProperties);
  for (const property of Object.keys(owner)) {
    if (!allowed.has(property)) {
      throw new NewExcelProjectReceiptError(
        `vba-dev new excel receipt ${description} has unexpected property ${property}.`
      );
    }
  }
}

function requireLiteral(
  owner: Record<string, unknown>,
  property: string,
  expected: string | boolean
): void {
  if (owner[property] !== expected) {
    throw new NewExcelProjectReceiptError(
      `vba-dev new excel receipt ${property} must be ${String(expected)}.`
    );
  }
}

function requireNormalizedAbsolutePath(value: string, description: string): string {
  if (!isNormalizedAbsolutePath(value)) {
    throw new NewExcelProjectReceiptError(
      `${description} must be a normalized absolute Windows path.`
    );
  }
  return value;
}

function isNormalizedAbsolutePath(value: string): boolean {
  return path.win32.isAbsolute(value) && path.win32.normalize(value) === value;
}

function sameWindowsPath(left: string, right: string): boolean {
  return sameOrdinalIgnoreCase(left, right);
}

function sameOrdinalIgnoreCase(left: string, right: string): boolean {
  return ordinalIgnoreCaseKey(left) === ordinalIgnoreCaseKey(right);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
