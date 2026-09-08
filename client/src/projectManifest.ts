import { findOrdinalIgnoreCaseDuplicate, ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export interface ProjectManifestProjection {
  projectName: string;
  primaryDocument: string;
  documents: readonly WorkbookBackedProjectDocument[];
}

export interface WorkbookBackedProjectDocument {
  name: string;
  sourcePath: string;
  templatePath: string;
  binPath: string;
}

export function parseProjectManifestProjection(
  json: string,
  onIdentityConflict?: (message: string) => void
): ProjectManifestProjection | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return undefined;
  }

  if (!isRecord(parsed)) {
    return undefined;
  }

  if (!hasOnlyProperties(parsed, [
    'schemaVersion',
    'projectName',
    'primaryDocument',
    'documents',
    'commonModulesRepository',
    'commandDefaults'
  ])) {
    return undefined;
  }

  if ((Object.hasOwn(parsed, 'commonModulesRepository') &&
       !isNonemptyString(parsed.commonModulesRepository)) ||
      (Object.hasOwn(parsed, 'commandDefaults') &&
       !isRecord(parsed.commandDefaults))) {
    return undefined;
  }

  if (isRecord(parsed.commandDefaults) &&
      (Object.keys(parsed.commandDefaults).length === 0 ||
       !hasOnlyProperties(parsed.commandDefaults, ['test', 'excelAutomation']) ||
       (Object.hasOwn(parsed.commandDefaults, 'test') &&
        (!isRecord(parsed.commandDefaults.test) ||
         Object.keys(parsed.commandDefaults.test).length === 0 ||
         !hasOnlyProperties(parsed.commandDefaults.test, ['format', 'executionTimeoutSeconds']))) ||
       (Object.hasOwn(parsed.commandDefaults, 'excelAutomation') &&
        (!isRecord(parsed.commandDefaults.excelAutomation) ||
         Object.keys(parsed.commandDefaults.excelAutomation).length === 0 ||
         !hasOnlyProperties(parsed.commandDefaults.excelAutomation, [
           'workbookOpenTimeoutSeconds',
           'workbookSaveTimeoutSeconds'
         ]))))) {
    return undefined;
  }

  const projectName = parsed.projectName;
  const primaryDocument = parsed.primaryDocument;
  const documentsValue = parsed.documents;
  const testDefaults = isRecord(parsed.commandDefaults)
    ? parsed.commandDefaults.test
    : undefined;
  const testFormat = isRecord(testDefaults)
    ? testDefaults.format
    : undefined;
  const testExecutionTimeoutSeconds = isRecord(testDefaults)
    ? testDefaults.executionTimeoutSeconds
    : undefined;
  const excelAutomationDefaults = isRecord(parsed.commandDefaults)
    ? parsed.commandDefaults.excelAutomation
    : undefined;
  const workbookOpenTimeoutSeconds = isRecord(excelAutomationDefaults)
    ? excelAutomationDefaults.workbookOpenTimeoutSeconds
    : undefined;
  const workbookSaveTimeoutSeconds = isRecord(excelAutomationDefaults)
    ? excelAutomationDefaults.workbookSaveTimeoutSeconds
    : undefined;
  if (parsed.schemaVersion !== 1 ||
      !isNonemptyString(projectName) ||
      typeof primaryDocument !== 'string' ||
      !isRecord(documentsValue) ||
      (testFormat !== undefined && testFormat !== 'text' && testFormat !== 'ndjson') ||
      (testExecutionTimeoutSeconds !== undefined &&
       !isPositiveInteger(testExecutionTimeoutSeconds)) ||
      (workbookOpenTimeoutSeconds !== undefined &&
       !isPositiveInteger(workbookOpenTimeoutSeconds)) ||
      (workbookSaveTimeoutSeconds !== undefined &&
       !isPositiveInteger(workbookSaveTimeoutSeconds))) {
    return undefined;
  }

  const documents: WorkbookBackedProjectDocument[] = [];
  for (const [name, document] of Object.entries(documentsValue)) {
    if (name.length === 0 ||
        !isRecord(document) ||
        !hasOnlyProperties(document, [
          'kind',
          'sourcePath',
          'templatePath',
          'binPath',
          'publishPath',
          'commonModules',
          'references'
        ]) ||
        document.kind !== 'excel' ||
        !isNonemptyString(document.sourcePath) ||
        !isNonemptyString(document.templatePath) ||
        !isNonemptyString(document.binPath) ||
        !isNonemptyString(document.publishPath) ||
        !Array.isArray(document.commonModules) ||
        !document.commonModules.every(isInstalledCommonModule) ||
        !isVbaProjectReferenceSelection(document.references, name, onIdentityConflict)) {
      return undefined;
    }

    const commonModuleConflict = findOrdinalIgnoreCaseDuplicate(document.commonModules.map((module) => module.name));
    if (commonModuleConflict !== undefined) {
      onIdentityConflict?.(
        `Document '${name}' contains conflicting CommonModule names '${commonModuleConflict[0]}' and '${commonModuleConflict[1]}'.`);
      return undefined;
    }
    documents.push({
      name,
      sourcePath: document.sourcePath,
      templatePath: document.templatePath,
      binPath: document.binPath
    });
  }

  const documentConflict = findOrdinalIgnoreCaseDuplicate(documents.map((document) => document.name));
  if (documentConflict !== undefined) {
    onIdentityConflict?.(`Conflicting document names '${documentConflict[0]}' and '${documentConflict[1]}'.`);
    return undefined;
  }

  if (!documents.some((document) => ordinalIgnoreCaseKey(document.name) === ordinalIgnoreCaseKey(primaryDocument))) {
    return undefined;
  }

  return {
    projectName,
    primaryDocument,
    documents
  };
}

export const parseProjectManifest = parseProjectManifestProjection;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isPositiveInteger(value: unknown): value is number {
  return Number.isInteger(value) && Number(value) > 0;
}

function hasOnlyProperties(
  value: Record<string, unknown>,
  allowedProperties: readonly string[]
): boolean {
  const allowed = new Set(allowedProperties);
  return Object.keys(value).every((property) => allowed.has(property));
}

function isVbaProjectReference(value: unknown): boolean {
  return isRecord(value) &&
    hasOnlyProperties(value, ['name', 'requested']) &&
    typeof value.name === 'string' &&
    value.name.trim().length > 0 &&
    value.name === value.name.trim() &&
    ordinalIgnoreCaseKey(value.name) !== ordinalIgnoreCaseKey('Visual Basic For Applications') &&
    typeof value.requested === 'boolean';
}

function isVbaProjectReferenceSelection(
  value: unknown,
  documentName: string,
  onIdentityConflict?: (message: string) => void
): boolean {
  if (!Array.isArray(value) || !value.every(isVbaProjectReference)) {
    return false;
  }

  const conflict = findOrdinalIgnoreCaseDuplicate(value.map((reference) => reference.name));
  if (conflict !== undefined) {
    onIdentityConflict?.(
      `Document '${documentName}' contains conflicting reference names '${conflict[0]}' and '${conflict[1]}'.`);
    return false;
  }

  return true;
}

function isInstalledCommonModule(value: unknown): boolean {
  if (!isRecord(value) ||
      !hasOnlyProperties(value, ['name', 'moduleFile', 'requested', 'testOnly', 'orphaned']) ||
      typeof value.name !== 'string' ||
      value.name.length === 0 ||
      typeof value.moduleFile !== 'string' ||
      typeof value.requested !== 'boolean' ||
      typeof value.testOnly !== 'boolean' ||
      typeof value.orphaned !== 'boolean') {
    return false;
  }

  const match = /^([^/\\]+)\.(bas|cls|frm)$/iu.exec(value.moduleFile);
  return match !== null && match[1] !== undefined &&
    ordinalIgnoreCaseKey(match[1]) === ordinalIgnoreCaseKey(value.name);
}
