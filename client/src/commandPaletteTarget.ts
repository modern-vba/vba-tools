import * as path from 'node:path';

import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';
import {
  WorkbookBackedProjectCandidate,
  findNearestProjectManifest
} from './projectDiscovery';

export type CommandPaletteTargetScope = 'project' | 'document';

export interface CommandPaletteInvocationSnapshot {
  activeFilePath?: string | undefined;
  activeEditorFilePath?: string | undefined;
  visibleEditorFilePaths: readonly string[];
  openDocumentFilePaths: readonly string[];
  workspaceRoots?: readonly string[] | undefined;
}

export interface CommandPaletteManifestSelectionDocument {
  name: string;
  sourcePath: string;
}

export interface CommandPaletteManifestSelectionProjection {
  projectName: string;
  primaryDocument: string;
  documents: readonly CommandPaletteManifestSelectionDocument[];
}

export interface CommandPalettePathIdentity {
  canonicalPath: string;
  objectIdentity?: string | undefined;
  kind?: 'file' | 'directory' | undefined;
}

export interface CommandPaletteDocumentTarget
  extends CommandPaletteManifestSelectionDocument {
  sourceRoot: string;
  sourceRootIdentity: CommandPalettePathIdentity;
}

export interface CommandPaletteProjectTarget
  extends WorkbookBackedProjectCandidate {
  projectName: string;
  primaryDocument: string;
  documents: readonly CommandPaletteDocumentTarget[];
}

export interface CommandPaletteTarget {
  project: CommandPaletteProjectTarget;
  document?: CommandPaletteDocumentTarget | undefined;
}

export type CommandPaletteProjectTargetDecision =
  | { kind: 'selected'; project: CommandPaletteProjectTarget }
  | { kind: 'choose'; candidates: readonly CommandPaletteProjectTarget[] }
  | { kind: 'failed'; code: 'nearestUnusable' | 'noUsableProject'; manifestPath?: string };

export type CommandPaletteDocumentTargetDecision =
  | { kind: 'selected'; document: CommandPaletteDocumentTarget }
  | {
    kind: 'choose';
    candidates: readonly CommandPaletteDocumentTarget[];
    initiallyFocused: CommandPaletteDocumentTarget;
  }
  | { kind: 'failed'; code: 'ambiguousActiveSource' };

export interface CommandPaletteTargetResolutionOptions {
  scope: CommandPaletteTargetScope;
  snapshot: CommandPaletteInvocationSnapshot;
  workspaceRoots: readonly string[];
  fileExists: (filePath: string) => Promise<boolean>;
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  readTextFile: (filePath: string) => Promise<string>;
  resolvePathIdentity: (filePath: string) => Promise<CommandPalettePathIdentity>;
  chooseProject: (
    candidates: readonly CommandPaletteProjectTarget[]
  ) => Promise<CommandPaletteProjectTarget | undefined>;
  chooseDocument: (
    candidates: readonly CommandPaletteDocumentTarget[],
    initiallyFocused: CommandPaletteDocumentTarget
  ) => Promise<CommandPaletteDocumentTarget | undefined>;
  showErrorMessage: (message: string) => PromiseLike<unknown> | Promise<unknown>;
}

export function parseCommandPaletteManifestSelectionProjection(
  json: string
): CommandPaletteManifestSelectionProjection | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return undefined;
  }

  if (!isRecord(parsed) ||
      parsed.schemaVersion !== 1 ||
      !isNonemptyString(parsed.projectName) ||
      !isNonemptyString(parsed.primaryDocument) ||
      !isRecord(parsed.documents)) {
    return undefined;
  }

  const documents: CommandPaletteManifestSelectionDocument[] = [];
  const documentNames = new Set<string>();
  for (const [name, document] of Object.entries(parsed.documents)) {
    if (!isNonemptyString(name) ||
        !isRecord(document) ||
        !isNonemptyString(document.sourcePath)) {
      return undefined;
    }

    const key = ordinalIgnoreCaseKey(name);
    if (documentNames.has(key)) {
      return undefined;
    }
    documentNames.add(key);
    documents.push({ name, sourcePath: document.sourcePath });
  }

  if (documents.length === 0) {
    return undefined;
  }

  const primaryDocument = documents.find(
    (document) => ordinalIgnoreCaseKey(document.name) ===
      ordinalIgnoreCaseKey(parsed.primaryDocument as string)
  );
  if (primaryDocument === undefined) {
    return undefined;
  }

  return {
    projectName: parsed.projectName,
    primaryDocument: primaryDocument.name,
    documents
  };
}

export function decideCommandPaletteProjectTarget(
  nearest: {
    manifestPath: string;
    project?: CommandPaletteProjectTarget | undefined;
  } | undefined,
  workspaceTargets: readonly CommandPaletteProjectTarget[]
): CommandPaletteProjectTargetDecision {
  if (nearest !== undefined) {
    return nearest.project === undefined
      ? {
        kind: 'failed',
        code: 'nearestUnusable',
        manifestPath: nearest.manifestPath
      }
      : { kind: 'selected', project: nearest.project };
  }
  if (workspaceTargets.length === 0) {
    return { kind: 'failed', code: 'noUsableProject' };
  }
  if (workspaceTargets.length === 1) {
    return { kind: 'selected', project: workspaceTargets[0]! };
  }
  return { kind: 'choose', candidates: workspaceTargets };
}

export async function resolveCommandPaletteTarget(
  options: CommandPaletteTargetResolutionOptions
): Promise<CommandPaletteTarget | undefined> {
  const nearestManifest = options.snapshot.activeFilePath === undefined
    ? undefined
    : await findNearestProjectManifest(
      options.snapshot.activeFilePath,
      options.fileExists
    );

  let project: CommandPaletteProjectTarget | undefined;
  if (nearestManifest !== undefined) {
    const decision = decideCommandPaletteProjectTarget({
      manifestPath: nearestManifest,
      project: await loadProjectTarget(nearestManifest, options)
    }, []);
    if (decision.kind !== 'selected') {
      await options.showErrorMessage(
        `VBA Tools cannot continue because ${nearestManifest} cannot be used for Command Palette targeting.`
      );
      return undefined;
    }
    project = decision.project;
  } else {
    const manifestPaths = uniqueManifestPaths(
      await options.findProjectManifests(options.workspaceRoots)
    );
    const candidates = (
      await Promise.all(manifestPaths.map((manifestPath) =>
        loadProjectTarget(manifestPath, options)))
    ).filter((candidate): candidate is CommandPaletteProjectTarget =>
      candidate !== undefined);

    const decision = decideCommandPaletteProjectTarget(undefined, candidates);
    if (decision.kind === 'failed') {
      await options.showErrorMessage(
        'VBA Tools could not select a workbook-backed project from an on-disk vba-project.json.'
      );
      return undefined;
    }

    if (decision.kind === 'selected') {
      project = decision.project;
    } else {
      const selected = await options.chooseProject(decision.candidates);
      project = selected === undefined
        ? undefined
        : decision.candidates.find((candidate) => sameManifest(candidate, selected));
      if (project === undefined) {
        return undefined;
      }
    }
  }

  if (project === undefined) {
    return undefined;
  }

  if (options.scope === 'project') {
    return { project };
  }

  const document = await resolveDocumentTarget(project, options);
  return document === undefined ? undefined : { project, document };
}

async function loadProjectTarget(
  manifestPath: string,
  options: CommandPaletteTargetResolutionOptions
): Promise<CommandPaletteProjectTarget | undefined> {
  let projection: CommandPaletteManifestSelectionProjection | undefined;
  try {
    projection = parseCommandPaletteManifestSelectionProjection(
      await options.readTextFile(manifestPath)
    );
  } catch {
    return undefined;
  }
  if (projection === undefined) {
    return undefined;
  }

  const projectRoot = path.dirname(manifestPath);
  const documents: CommandPaletteDocumentTarget[] = [];
  try {
    for (const document of projection.documents) {
      const sourceRoot = path.resolve(projectRoot, document.sourcePath);
      documents.push({
        ...document,
        sourceRoot,
        sourceRootIdentity: normalizeIdentity(
          await options.resolvePathIdentity(sourceRoot)
        )
      });
    }
  } catch {
    return undefined;
  }

  for (let leftIndex = 0; leftIndex < documents.length; leftIndex++) {
    for (let rightIndex = leftIndex + 1; rightIndex < documents.length; rightIndex++) {
      if (rootsOverlap(
        documents[leftIndex]!.sourceRootIdentity,
        documents[rightIndex]!.sourceRootIdentity
      )) {
        return undefined;
      }
    }
  }

  return {
    projectRoot,
    manifestPath,
    projectName: projection.projectName,
    primaryDocument: projection.primaryDocument,
    documents
  };
}

async function resolveDocumentTarget(
  project: CommandPaletteProjectTarget,
  options: CommandPaletteTargetResolutionOptions
): Promise<CommandPaletteDocumentTarget | undefined> {
  let activeOwners: readonly CommandPaletteDocumentTarget[];
  try {
    activeOwners = await sourceOwners(
      options.snapshot.activeEditorFilePath,
      project.documents,
      options.resolvePathIdentity,
      true
    );
  } catch {
    await options.showErrorMessage(
      `VBA Tools cannot select a document because active source ownership cannot be resolved in ${project.manifestPath}.`
    );
    return undefined;
  }
  const visibleOwners = await sourceEvidenceOwners(
    options.snapshot.visibleEditorFilePaths,
    project.documents,
    options.resolvePathIdentity
  );
  const openOwners = await sourceEvidenceOwners(
    options.snapshot.openDocumentFilePaths,
    project.documents,
    options.resolvePathIdentity
  );
  const decision = decideCommandPaletteDocumentTarget(
    project,
    activeOwners,
    visibleOwners,
    openOwners
  );
  if (decision.kind === 'failed') {
    await options.showErrorMessage(
      `VBA Tools cannot select a document because source ownership is ambiguous in ${project.manifestPath}.`
    );
    return undefined;
  }
  if (decision.kind === 'selected') {
    return decision.document;
  }

  const selected = await options.chooseDocument(
    decision.candidates,
    decision.initiallyFocused
  );
  return selected === undefined
    ? undefined
    : project.documents.find((document) =>
      ordinalIgnoreCaseKey(document.name) === ordinalIgnoreCaseKey(selected.name));
}

export async function resolveCommandPaletteDocumentFocus(
  project: CommandPaletteProjectTarget,
  snapshot: CommandPaletteInvocationSnapshot,
  resolvePathIdentity: (filePath: string) => Promise<CommandPalettePathIdentity>
): Promise<CommandPaletteDocumentTarget> {
  const activeOwners = await sourceEvidenceOwners(
    snapshot.activeEditorFilePath === undefined
      ? []
      : [snapshot.activeEditorFilePath],
    project.documents,
    resolvePathIdentity
  );
  const visibleOwners = await sourceEvidenceOwners(
    snapshot.visibleEditorFilePaths,
    project.documents,
    resolvePathIdentity
  );
  const openOwners = await sourceEvidenceOwners(
    snapshot.openDocumentFilePaths,
    project.documents,
    resolvePathIdentity
  );
  return selectInitialCommandPaletteDocumentFocus(
    project,
    activeOwners,
    visibleOwners,
    openOwners
  );
}

export function decideCommandPaletteDocumentTarget(
  project: CommandPaletteProjectTarget,
  activeOwners: readonly CommandPaletteDocumentTarget[],
  visibleOwners: readonly CommandPaletteDocumentTarget[],
  openOwners: readonly CommandPaletteDocumentTarget[]
): CommandPaletteDocumentTargetDecision {
  if (activeOwners.length > 1) {
    return { kind: 'failed', code: 'ambiguousActiveSource' };
  }
  if (activeOwners.length === 1) {
    return { kind: 'selected', document: activeOwners[0]! };
  }
  if (project.documents.length === 1) {
    return { kind: 'selected', document: project.documents[0]! };
  }
  return {
    kind: 'choose',
    candidates: project.documents,
    initiallyFocused: selectInitialCommandPaletteDocumentFocus(
      project,
      activeOwners,
      visibleOwners,
      openOwners
    )
  };
}

export function selectInitialCommandPaletteDocumentFocus(
  project: CommandPaletteProjectTarget,
  activeOwners: readonly CommandPaletteDocumentTarget[],
  visibleOwners: readonly CommandPaletteDocumentTarget[],
  openOwners: readonly CommandPaletteDocumentTarget[]
): CommandPaletteDocumentTarget {
  return unanimousOwner(activeOwners) ??
    unanimousOwner(visibleOwners) ??
    unanimousOwner(openOwners) ??
    project.documents.find((document) =>
      ordinalIgnoreCaseKey(document.name) === ordinalIgnoreCaseKey(project.primaryDocument)
    )!;
}

function unanimousOwner(
  owners: readonly CommandPaletteDocumentTarget[]
): CommandPaletteDocumentTarget | undefined {
  if (owners.length === 0) {
    return undefined;
  }
  const firstKey = ordinalIgnoreCaseKey(owners[0]!.name);
  return owners.every((owner) => ordinalIgnoreCaseKey(owner.name) === firstKey)
    ? owners[0]
    : undefined;
}

async function sourceEvidenceOwners(
  filePaths: readonly string[],
  documents: readonly CommandPaletteDocumentTarget[],
  resolvePathIdentity: (filePath: string) => Promise<CommandPalettePathIdentity>
): Promise<readonly CommandPaletteDocumentTarget[]> {
  const eligibleOwners: CommandPaletteDocumentTarget[] = [];
  for (const filePath of filePaths) {
    const owners = await sourceOwners(filePath, documents, resolvePathIdentity);
    if (owners.length === 1) {
      eligibleOwners.push(owners[0]!);
    } else if (owners.length > 1) {
      eligibleOwners.push(...owners);
    }
  }
  return eligibleOwners;
}

async function sourceOwners(
  filePath: string | undefined,
  documents: readonly CommandPaletteDocumentTarget[],
  resolvePathIdentity: (filePath: string) => Promise<CommandPalettePathIdentity>,
  failOnUnresolvable = false
): Promise<readonly CommandPaletteDocumentTarget[]> {
  if (filePath === undefined || !isExportedVbaSource(filePath)) {
    return [];
  }

  let identity: CommandPalettePathIdentity;
  try {
    identity = normalizeIdentity(await resolvePathIdentity(filePath));
  } catch {
    if (failOnUnresolvable) {
      throw new Error(`Source identity cannot be resolved: ${filePath}`);
    }
    return [];
  }

  return documents.filter((document) =>
    sameOrDescendant(identity, document.sourceRootIdentity));
}

function isExportedVbaSource(filePath: string): boolean {
  const extension = path.extname(filePath).toLowerCase();
  return extension === '.bas' || extension === '.cls' || extension === '.frm';
}

function rootsOverlap(
  left: CommandPalettePathIdentity,
  right: CommandPalettePathIdentity
): boolean {
  return sameOrDescendant(left, right) || sameOrDescendant(right, left);
}

function sameOrDescendant(
  candidate: CommandPalettePathIdentity,
  directory: CommandPalettePathIdentity
): boolean {
  if (sameIdentity(candidate, directory)) {
    return true;
  }

  const candidateKey = ordinalIgnoreCaseKey(trimEndingSeparator(candidate.canonicalPath));
  const directoryPath = trimEndingSeparator(directory.canonicalPath);
  const directoryPrefix = directoryPath.endsWith(path.sep)
    ? directoryPath
    : `${directoryPath}${path.sep}`;
  return candidateKey.startsWith(ordinalIgnoreCaseKey(directoryPrefix));
}

function sameIdentity(
  left: CommandPalettePathIdentity,
  right: CommandPalettePathIdentity
): boolean {
  return ordinalIgnoreCaseKey(trimEndingSeparator(left.canonicalPath)) ===
      ordinalIgnoreCaseKey(trimEndingSeparator(right.canonicalPath)) ||
    left.objectIdentity !== undefined &&
      right.objectIdentity !== undefined &&
      left.objectIdentity === right.objectIdentity;
}

function normalizeIdentity(identity: CommandPalettePathIdentity): CommandPalettePathIdentity {
  return {
    canonicalPath: path.normalize(identity.canonicalPath),
    objectIdentity: identity.objectIdentity,
    kind: identity.kind
  };
}

function trimEndingSeparator(value: string): string {
  const normalized = path.normalize(value);
  const root = path.parse(normalized).root;
  if (normalized === root) {
    return normalized;
  }
  return normalized.replace(/[\\/]+$/u, '');
}

function uniqueManifestPaths(manifestPaths: readonly string[]): readonly string[] {
  const unique = new Map<string, string>();
  for (const manifestPath of manifestPaths) {
    const normalized = path.normalize(manifestPath);
    const key = ordinalIgnoreCaseKey(normalized);
    if (!unique.has(key)) {
      unique.set(key, normalized);
    }
  }
  return [...unique.values()];
}

function sameManifest(
  left: WorkbookBackedProjectCandidate,
  right: WorkbookBackedProjectCandidate
): boolean {
  return ordinalIgnoreCaseKey(path.normalize(left.manifestPath)) ===
    ordinalIgnoreCaseKey(path.normalize(right.manifestPath));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNonemptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}
