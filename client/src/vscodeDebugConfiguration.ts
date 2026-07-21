import * as path from 'node:path';

import { parseProjectManifest } from './projectManifest';

export interface VbaDebugActiveEditor {
  readonly uriPath: string;
  readonly line: number;
  readonly character: number;
}

export interface VbaDebugSavableTextDocument {
  readonly uriPath: string;
  readonly isDirty: boolean;
  save(): PromiseLike<boolean>;
}

export interface VbaDebugSourceBreakpoint {
  readonly uriPath: string;
  readonly line: number;
  readonly enabled: boolean;
  readonly condition?: string | undefined;
  readonly hitCondition?: string | undefined;
  readonly logMessage?: string | undefined;
}

export interface VbaDebugConfigurationHost {
  readonly workspaceRoots: readonly string[];
  getActiveEditor(): VbaDebugActiveEditor | undefined;
  getOpenTextDocuments(): readonly VbaDebugSavableTextDocument[];
  getSourceBreakpoints(): readonly VbaDebugSourceBreakpoint[];
  findProjectManifests(workspaceRoots: readonly string[]): Promise<readonly string[]>;
  readTextFile(filePath: string): Promise<string>;
  readSourceText(filePath: string): Promise<string>;
  findExportedSourceFiles(sourceSetPath: string): Promise<readonly string[]>;
}

export type VbaDebugConfiguration = Record<string, unknown>;

export class VbaDebugSelectionError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDebugSelectionError';
  }
}

export function provideDynamicVbaDebugConfigurations(
  host: VbaDebugConfigurationHost
): readonly VbaDebugConfiguration[] {
  const activeEditor = host.getActiveEditor();
  return activeEditor && isExportedVbaSource(activeEditor.uriPath)
    ? [{
        type: 'vba',
        request: 'launch',
        name: 'VBA: Active Procedure'
      }]
    : [];
}

export function normalizeVbaDebugConfiguration(
  configuration: VbaDebugConfiguration
): VbaDebugConfiguration {
  validateLaunchSurface(configuration);
  validateOptionalSelector(configuration, 'project');
  validateOptionalSelector(configuration, 'document');
  validateExplicitProcedurePair(configuration);
  return {
    ...configuration,
    type: 'vba',
    request: 'launch',
    name: typeof configuration.name === 'string'
      ? configuration.name
      : 'VBA: Active Procedure'
  };
}

export async function resolveVbaDebugConfiguration(
  host: VbaDebugConfigurationHost,
  configuration: VbaDebugConfiguration
): Promise<VbaDebugConfiguration> {
  const normalizedConfiguration = normalizeVbaDebugConfiguration(configuration);
  const activeEditor = host.getActiveEditor();
  const explicitModule = optionalNonEmptyString(normalizedConfiguration.module);
  const explicitProcedure = optionalNonEmptyString(normalizedConfiguration.procedure);

  const hasExplicitTarget = explicitModule !== undefined && explicitProcedure !== undefined;
  if (!hasExplicitTarget && (!activeEditor || !isExportedVbaSource(activeEditor.uriPath))) {
    throw new VbaDebugSelectionError(
      'Zero-configuration VBA debugging requires an active exported .bas, .cls, or .frm source file.'
    );
  }

  const projects = await loadProjects(host);
  const explicitProject = optionalNonEmptyString(normalizedConfiguration.project);
  const explicitDocument = optionalNonEmptyString(normalizedConfiguration.document);
  const selection = resolveDocumentSelection(
    projects,
    explicitProject,
    explicitDocument,
    activeEditor,
    hasExplicitTarget);
  await saveDirtyProjectSources(host, selection.project);
  const postSaveActiveEditor = hasExplicitTarget ? undefined : host.getActiveEditor();
  if (
    !hasExplicitTarget
    && (!postSaveActiveEditor || !isExportedVbaSource(postSaveActiveEditor.uriPath))
  ) {
    throw new VbaDebugSelectionError(
      'The active exported VBA source was unavailable after save participants completed.'
    );
  }

  const postSaveSelection = resolveDocumentSelection(
    await loadProjects(host),
    selection.project.projectRoot,
    selection.document.name,
    postSaveActiveEditor,
    hasExplicitTarget);
  const sourcePaths = uniqueCanonicalPaths(
    await host.findExportedSourceFiles(postSaveSelection.sourceSetPath)
  );
  const breakpoints = captureEnabledOrdinarySourceBreakpoints(host, sourcePaths);
  const sources = [];
  for (const sourcePath of sourcePaths) {
    sources.push({
      path: sourcePath,
      text: await host.readSourceText(sourcePath)
    });
  }

  return {
    ...normalizedConfiguration,
    project: postSaveSelection.project.projectRoot,
    document: postSaveSelection.document.name,
    sourceSnapshot: {
      schemaVersion: 1,
      sources,
      ...(hasExplicitTarget
        ? {}
        : {
            activeSource: {
              path: postSaveActiveEditor!.uriPath,
              line: postSaveActiveEditor!.line,
              character: postSaveActiveEditor!.character
            }
          }),
      breakpoints
    }
  };
}

function captureEnabledOrdinarySourceBreakpoints(
  host: VbaDebugConfigurationHost,
  sourcePaths: readonly string[]
): readonly { readonly path: string; readonly line: number }[] {
  const exportedSourcePaths = new Map(
    sourcePaths
      .map((sourcePath) => [canonicalPath(sourcePath), sourcePath])
  );
  const breakpoints = host.getSourceBreakpoints()
    .filter((breakpoint) => breakpoint.enabled)
    .flatMap((breakpoint) => {
      const sourcePath = exportedSourcePaths.get(canonicalPath(breakpoint.uriPath));
      if (sourcePath === undefined) {
        return [];
      }

      if (breakpoint.condition !== undefined) {
        throw new VbaDebugSelectionError(
          `Conditional breakpoint at ${sourcePath}:${breakpoint.line + 1} is unsupported for VBA debug launch.`
        );
      }

      if (breakpoint.hitCondition !== undefined) {
        throw new VbaDebugSelectionError(
          `Hit-count breakpoint at ${sourcePath}:${breakpoint.line + 1} is unsupported for VBA debug launch.`
        );
      }

      if (breakpoint.logMessage !== undefined) {
        throw new VbaDebugSelectionError(
          `Logpoint at ${sourcePath}:${breakpoint.line + 1} is unsupported for VBA debug launch.`
        );
      }

      return [{ path: sourcePath, line: breakpoint.line }];
    });
  breakpoints.sort((left, right) => (
    compareOrdinal(canonicalPath(left.path), canonicalPath(right.path))
    || left.line - right.line
  ));
  for (let index = 1; index < breakpoints.length; index += 1) {
    const previous = breakpoints[index - 1];
    const current = breakpoints[index];
    if (
      canonicalPath(previous.path) === canonicalPath(current.path)
      && previous.line === current.line
    ) {
      throw new VbaDebugSelectionError(
        `Duplicate enabled VBA breakpoint at ${current.path}:${current.line + 1}.`
      );
    }
  }

  return breakpoints;
}

function validateOptionalSelector(
  configuration: VbaDebugConfiguration,
  selectorName: 'project' | 'document'
): void {
  if (
    Object.hasOwn(configuration, selectorName)
    && optionalNonEmptyString(configuration[selectorName]) === undefined
  ) {
    throw new VbaDebugSelectionError(
      `VBA debug launch selector ${selectorName} must be a non-empty string when supplied.`
    );
  }
}

function validateExplicitProcedurePair(configuration: VbaDebugConfiguration): void {
  const moduleWasSupplied = Object.hasOwn(configuration, 'module');
  const procedureWasSupplied = Object.hasOwn(configuration, 'procedure');
  if (
    moduleWasSupplied !== procedureWasSupplied
    || (
      moduleWasSupplied
      && (
        optionalNonEmptyString(configuration.module) === undefined
        || optionalNonEmptyString(configuration.procedure) === undefined
      )
    )
  ) {
    throw new VbaDebugSelectionError(
      'VBA debug launch selectors module and procedure must be supplied together as non-empty strings.'
    );
  }
}

const supportedLaunchProperties = new Set([
  'type',
  'request',
  'name',
  'project',
  'document',
  'module',
  'procedure'
]);

function validateLaunchSurface(configuration: VbaDebugConfiguration): void {
  if (configuration.type !== undefined && configuration.type !== 'vba') {
    throw new VbaDebugSelectionError('VBA debug configurations must use debug type vba.');
  }

  if (configuration.request !== undefined && configuration.request !== 'launch') {
    throw new VbaDebugSelectionError('VBA debugging supports only launch requests; attach is unsupported.');
  }

  for (const propertyName of Object.keys(configuration)) {
    if (!supportedLaunchProperties.has(propertyName) && !propertyName.startsWith('__')) {
      throw new VbaDebugSelectionError(
        `Unsupported VBA debug launch property '${propertyName}'.`
      );
    }
  }
}

interface LoadedProject {
  readonly projectRoot: string;
  readonly manifest: NonNullable<ReturnType<typeof parseProjectManifest>>;
}

interface ProjectDocumentSelection {
  readonly project: LoadedProject;
  readonly document: LoadedProject['manifest']['documents'][number];
  readonly sourceSetPath: string;
}

async function loadProjects(host: VbaDebugConfigurationHost): Promise<LoadedProject[]> {
  const projects: LoadedProject[] = [];
  for (const manifestPath of await host.findProjectManifests(host.workspaceRoots)) {
    const manifest = parseProjectManifest(await host.readTextFile(manifestPath));
    if (manifest) {
      projects.push({
        projectRoot: path.dirname(manifestPath),
        manifest
      });
    }
  }

  return projects;
}

function resolveDocumentSelection(
  projects: readonly LoadedProject[],
  explicitProject: string | undefined,
  explicitDocument: string | undefined,
  activeEditor: VbaDebugActiveEditor | undefined,
  hasExplicitTarget: boolean
): ProjectDocumentSelection {
  const useActiveSourceNarrowing = !hasExplicitTarget || (
    (explicitProject === undefined || explicitDocument === undefined)
    && activeEditor !== undefined
    && isExportedVbaSource(activeEditor.uriPath)
  );
  const matchingDocuments = projects
    .filter((project) => (
      explicitProject === undefined || samePath(project.projectRoot, explicitProject)
    ))
    .flatMap((project) => (
      project.manifest.documents
        .map((document) => ({
          project,
          document,
          sourceSetPath: path.resolve(project.projectRoot, document.sourcePath)
        }))
        .filter((candidate) => (
          (explicitDocument === undefined || sameName(candidate.document.name, explicitDocument))
          && (
            !useActiveSourceNarrowing
            || (
              activeEditor !== undefined
              && isPathWithin(activeEditor.uriPath, candidate.sourceSetPath)
            )
          )
        ))
    ));
  const matchingProjectRoots = new Set(
    matchingDocuments.map((candidate) => canonicalPath(candidate.project.projectRoot))
  );
  if (matchingProjectRoots.size > 1) {
    throw new VbaDebugSelectionError(
      'VBA debug project selection is ambiguous. Set the project launch property to one workbook-backed project root.'
    );
  }

  if (matchingDocuments.length > 1) {
    throw new VbaDebugSelectionError(
      'VBA debug document selection is ambiguous. Set the document launch property to one manifest document name.'
    );
  }

  if (matchingDocuments.length !== 1) {
    const activeSourceLabel = activeEditor?.uriPath ?? '(no active source)';
    throw new VbaDebugSelectionError(
      matchingDocuments.length === 0
        ? `The VBA debug target did not resolve to a workbook-backed project document: ${activeSourceLabel}`
        : `The VBA debug target resolves to more than one workbook-backed project document: ${activeSourceLabel}`
    );
  }

  return matchingDocuments[0];
}

async function saveDirtyProjectSources(
  host: VbaDebugConfigurationHost,
  project: {
    readonly projectRoot: string;
    readonly manifest: {
      readonly documents: readonly { readonly sourcePath: string }[];
    };
  }
): Promise<void> {
  const sourceSetPaths = project.manifest.documents.map((document) => (
    path.resolve(project.projectRoot, document.sourcePath)
  ));
  const documents = host.getOpenTextDocuments()
    .filter((document) => (
      document.isDirty
      && isExportedVbaSource(document.uriPath)
      && sourceSetPaths.some((sourceSetPath) => isPathWithin(document.uriPath, sourceSetPath))
    ))
    .sort((left, right) => canonicalPath(left.uriPath).localeCompare(canonicalPath(right.uriPath)));
  for (const document of documents) {
    let saved: boolean;
    try {
      saved = await document.save();
    } catch {
      throw new VbaDebugSelectionError(
        `Could not save exported VBA source before the debug launch: ${document.uriPath}`
      );
    }

    if (!saved) {
      throw new VbaDebugSelectionError(
        `Could not save exported VBA source before the debug launch: ${document.uriPath}`
      );
    }
  }
}

function optionalNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0
    ? value
    : undefined;
}

function isExportedVbaSource(filePath: string): boolean {
  const extension = path.extname(filePath).toLowerCase();
  return extension === '.bas' || extension === '.cls' || extension === '.frm';
}

function isPathWithin(filePath: string, directoryPath: string): boolean {
  const relativePath = path.relative(path.resolve(directoryPath), path.resolve(filePath));
  return relativePath.length > 0
    && !relativePath.startsWith(`..${path.sep}`)
    && relativePath !== '..'
    && !path.isAbsolute(relativePath);
}

function sameName(left: string, right: string): boolean {
  return left.toLowerCase() === right.toLowerCase();
}

function samePath(left: string, right: string): boolean {
  return canonicalPath(left) === canonicalPath(right);
}

function canonicalPath(filePath: string): string {
  return path.normalize(path.resolve(filePath)).toLowerCase();
}

function uniqueCanonicalPaths(filePaths: readonly string[]): string[] {
  const paths = new Map<string, string>();
  for (const filePath of filePaths) {
    const absolutePath = path.resolve(filePath);
    if (isExportedVbaSource(absolutePath)) {
      paths.set(path.normalize(absolutePath).toLowerCase(), absolutePath);
    }
  }

  return [...paths.entries()]
    .map(([, filePath]) => filePath)
    .sort(compareOrdinal);
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}
