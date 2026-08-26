import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

import { parseProjectManifest } from './projectManifest';
import { SnapshotSourceInventory } from './snapshotSourceInventory';

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

export interface VbaDebugCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(listener: () => void): { dispose(): void };
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
  captureSourceInventory?(
    sourceSetPath: string,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<SnapshotSourceInventory>;
}

export type VbaDebugConfiguration = Record<string, unknown>;

export class VbaDebugSelectionError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDebugSelectionError';
  }
}

export class VbaDebugCancellationError extends Error {
  public constructor() {
    super('VBA debug launch was cancelled.');
    this.name = 'VbaDebugCancellationError';
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
  configuration: VbaDebugConfiguration,
  cancellationToken?: VbaDebugCancellationToken
): Promise<VbaDebugConfiguration> {
  throwIfDebugCancellationRequested(cancellationToken);
  const normalizedConfiguration = normalizeVbaDebugConfiguration(configuration);
  const activeEditor = host.getActiveEditor();
  const explicitModule = optionalExactNonEmptyString(normalizedConfiguration.module);
  const explicitProcedure = optionalExactNonEmptyString(normalizedConfiguration.procedure);

  const hasExplicitTarget = explicitModule !== undefined && explicitProcedure !== undefined;
  if (!hasExplicitTarget && (!activeEditor || !isExportedVbaSource(activeEditor.uriPath))) {
    throw new VbaDebugSelectionError(
      'Zero-configuration VBA debugging requires an active exported .bas, .cls, or .frm source file.'
    );
  }

  const projects = await loadProjects(host);
  throwIfDebugCancellationRequested(cancellationToken);
  const explicitProject = optionalNonEmptyString(normalizedConfiguration.project);
  const explicitDocument = optionalNonEmptyString(normalizedConfiguration.document);
  const selection = resolveDocumentSelection(
    projects,
    explicitProject,
    explicitDocument,
    activeEditor,
    hasExplicitTarget);
  if (host.captureSourceInventory !== undefined) {
    const inventory = await host.captureSourceInventory(
      selection.sourceSetPath,
      cancellationToken
    );
    throwIfDebugCancellationRequested(cancellationToken);
    return createTransportedSnapshotConfiguration(
      host,
      normalizedConfiguration,
      selection,
      inventory,
      hasExplicitTarget ? undefined : activeEditor
    );
  }
  await saveDirtyProjectSources(host, selection.project, cancellationToken);
  throwIfDebugCancellationRequested(cancellationToken);
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

export async function recaptureBoundVbaDebugConfiguration(
  host: VbaDebugConfigurationHost,
  configuration: VbaDebugConfiguration,
  cancellationToken?: VbaDebugCancellationToken
): Promise<VbaDebugConfiguration> {
  throwIfDebugCancellationRequested(cancellationToken);
  const projectRoot = optionalNonEmptyString(configuration.project);
  const documentName = optionalNonEmptyString(configuration.document);
  if (projectRoot === undefined || documentName === undefined) {
    throw new VbaDebugSelectionError(
      'A bound VBA debug restart requires its original project and document.'
    );
  }
  if (host.captureSourceInventory === undefined) {
    throw new VbaDebugSelectionError(
      'Bound VBA debug restart snapshot capture is unavailable in this host.'
    );
  }

  const projects = await loadProjects(host);
  throwIfDebugCancellationRequested(cancellationToken);
  const selection = resolveDocumentSelection(
    projects,
    projectRoot,
    documentName,
    undefined,
    true
  );
  const inventory = await host.captureSourceInventory(
    selection.sourceSetPath,
    cancellationToken
  );
  throwIfDebugCancellationRequested(cancellationToken);
  return createTransportedSnapshotConfiguration(
    host,
    configuration,
    selection,
    inventory,
    boundActiveSource(configuration)
  );
}

function boundActiveSource(
  configuration: VbaDebugConfiguration
): VbaDebugActiveEditor | undefined {
  const snapshot = configuration.sourceSnapshot;
  if (typeof snapshot !== 'object' || snapshot === null) {
    return undefined;
  }
  const activeSource = (snapshot as { activeSource?: unknown }).activeSource;
  if (typeof activeSource !== 'object' || activeSource === null) {
    return undefined;
  }
  const value = activeSource as {
    sourceUri?: unknown;
    line?: unknown;
    character?: unknown;
  };
  if (
    typeof value.sourceUri !== 'string'
    || !Number.isInteger(value.line)
    || !Number.isInteger(value.character)
    || (value.line as number) < 0
    || (value.character as number) < 0
  ) {
    throw new VbaDebugSelectionError(
      'The bound VBA debug restart active source identity is invalid.'
    );
  }

  try {
    return {
      uriPath: fileURLToPath(value.sourceUri),
      line: value.line as number,
      character: value.character as number
    };
  } catch {
    throw new VbaDebugSelectionError(
      'The bound VBA debug restart active source must use a persistent file URI.'
    );
  }
}

function createTransportedSnapshotConfiguration(
  host: VbaDebugConfigurationHost,
  configuration: VbaDebugConfiguration,
  selection: ProjectDocumentSelection,
  inventory: SnapshotSourceInventory,
  activeEditor: VbaDebugActiveEditor | undefined
): VbaDebugConfiguration {
  if (!samePath(inventory.sourceSetPath, selection.sourceSetPath)) {
    throw new VbaDebugSelectionError(
      'The captured VBA source inventory does not match the selected document source set.'
    );
  }

  const sourceUrisByPath = new Map<string, string>();
  const seenRelativePaths = new Set<string>();
  const sources = inventory.entries.map((entry) => {
    const relativePath = safeTransportRelativePath(entry.relativePath);
    const relativeKey = relativePath.toLowerCase();
    if (seenRelativePaths.has(relativeKey)) {
      throw new VbaDebugSelectionError(
        `The captured VBA source inventory contains a duplicate relative path: ${relativePath}`
      );
    }
    seenRelativePaths.add(relativeKey);

    const extension = path.posix.extname(relativePath).toLowerCase();
    const contentBase64 = Buffer.from(entry.bytes).toString('base64');
    if (extension === '.frx') {
      if (entry.sourceUri !== undefined || entry.encoding !== undefined) {
        throw new VbaDebugSelectionError(
          `Binary VBA sidecar '${relativePath}' must not declare a source URI or text encoding.`
        );
      }
      return { relativePath, contentBase64 };
    }

    if (!isExportedVbaSource(relativePath)) {
      throw new VbaDebugSelectionError(
        `The captured VBA source inventory contains an unsupported file: ${relativePath}`
      );
    }
    if (entry.sourceUri === undefined || entry.encoding === undefined) {
      throw new VbaDebugSelectionError(
        `Text VBA source '${relativePath}' requires a persistent source URI and encoding.`
      );
    }

    let persistentPath: string;
    try {
      persistentPath = fileURLToPath(entry.sourceUri);
    } catch {
      throw new VbaDebugSelectionError(
        `Text VBA source '${relativePath}' requires a persistent file URI.`
      );
    }
    sourceUrisByPath.set(canonicalPath(persistentPath), entry.sourceUri);
    return {
      relativePath,
      sourceUri: entry.sourceUri,
      encoding: entry.encoding,
      contentBase64
    };
  });
  sources.sort((left, right) => compareOrdinal(
    left.relativePath,
    right.relativePath
  ));

  const activeSourceUri = activeEditor === undefined
    ? undefined
    : sourceUrisByPath.get(canonicalPath(activeEditor.uriPath));
  if (activeEditor !== undefined && activeSourceUri === undefined) {
    throw new VbaDebugSelectionError(
      `The active exported VBA source is missing from the captured inventory: ${activeEditor.uriPath}`
    );
  }

  const binPath = selection.document.binPath;
  if (binPath === undefined || path.basename(binPath).length === 0) {
    throw new VbaDebugSelectionError(
      `The selected VBA document '${selection.document.name}' requires a binPath for debugging.`
    );
  }

  return {
    ...configuration,
    project: selection.project.projectRoot,
    document: selection.document.name,
    __vbaDebugWorkbookFileName: path.basename(binPath),
    sourceSnapshot: {
      schemaVersion: 1,
      sources,
      ...(activeEditor === undefined
        ? {}
        : {
            activeSource: {
              sourceUri: activeSourceUri!,
              line: activeEditor.line,
              character: activeEditor.character
            }
          }),
      breakpoints: captureTransportedSourceBreakpoints(host, sourceUrisByPath)
    }
  };
}

function captureTransportedSourceBreakpoints(
  host: VbaDebugConfigurationHost,
  sourceUrisByPath: ReadonlyMap<string, string>
): readonly { readonly sourceUri: string; readonly line: number }[] {
  const breakpoints = host.getSourceBreakpoints()
    .filter((breakpoint) => breakpoint.enabled)
    .flatMap((breakpoint) => {
      const sourceUri = sourceUrisByPath.get(canonicalPath(breakpoint.uriPath));
      if (sourceUri === undefined) {
        return [];
      }
      if (
        breakpoint.condition !== undefined ||
        breakpoint.hitCondition !== undefined ||
        breakpoint.logMessage !== undefined
      ) {
        throw new VbaDebugSelectionError(
          `Only ordinary VBA line breakpoints are supported: ${breakpoint.uriPath}:${breakpoint.line + 1}`
        );
      }
      return [{ sourceUri, line: breakpoint.line }];
    });
  breakpoints.sort((left, right) => (
    compareOrdinal(left.sourceUri.toLowerCase(), right.sourceUri.toLowerCase()) ||
    left.line - right.line
  ));
  for (let index = 1; index < breakpoints.length; index += 1) {
    const previous = breakpoints[index - 1];
    const current = breakpoints[index];
    if (
      previous.sourceUri.toLowerCase() === current.sourceUri.toLowerCase() &&
      previous.line === current.line
    ) {
      throw new VbaDebugSelectionError(
        `Duplicate enabled VBA breakpoint at ${current.sourceUri}:${current.line + 1}.`
      );
    }
  }
  return breakpoints;
}

function safeTransportRelativePath(relativePath: string): string {
  const portablePath = relativePath.replaceAll('\\', '/');
  const segments = portablePath.split('/');
  if (
    portablePath.length === 0 ||
    portablePath.startsWith('/') ||
    /^[a-z]:/i.test(portablePath) ||
    segments.some((segment) => segment.length === 0 || segment === '.' || segment === '..')
  ) {
    throw new VbaDebugSelectionError(
      `Captured VBA source path must be a safe relative descendant: ${relativePath}`
    );
  }
  return portablePath;
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
        optionalExactNonEmptyString(configuration.module) === undefined
        || optionalExactNonEmptyString(configuration.procedure) === undefined
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
  },
  cancellationToken?: VbaDebugCancellationToken
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
    throwIfDebugCancellationRequested(cancellationToken);
    let saved: boolean;
    try {
      saved = await waitForDebugCancellation(document.save(), cancellationToken);
    } catch {
      throwIfDebugCancellationRequested(cancellationToken);
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

function throwIfDebugCancellationRequested(
  cancellationToken: VbaDebugCancellationToken | undefined
): void {
  if (cancellationToken?.isCancellationRequested) {
    throw new VbaDebugCancellationError();
  }
}

function waitForDebugCancellation<T>(
  operation: PromiseLike<T>,
  cancellationToken: VbaDebugCancellationToken | undefined
): Promise<T> {
  if (cancellationToken === undefined) {
    return Promise.resolve(operation);
  }

  throwIfDebugCancellationRequested(cancellationToken);
  return new Promise<T>((resolve, reject) => {
    let settled = false;
    let cancellationSubscription: { dispose(): void } | undefined;
    const settle = (complete: () => void) => {
      if (settled) {
        return;
      }

      settled = true;
      cancellationSubscription?.dispose();
      complete();
    };
    cancellationSubscription = cancellationToken.onCancellationRequested(() => {
      settle(() => reject(new VbaDebugCancellationError()));
    });
    if (settled) {
      cancellationSubscription.dispose();
    } else if (cancellationToken.isCancellationRequested) {
      settle(() => reject(new VbaDebugCancellationError()));
    }

    Promise.resolve(operation).then(
      (value) => settle(() => resolve(value)),
      (error: unknown) => settle(() => reject(error))
    );
  });
}

function optionalNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0
    ? value
    : undefined;
}

function optionalExactNonEmptyString(value: unknown): string | undefined {
  if (typeof value !== 'string' || value.length === 0) {
    return undefined;
  }

  for (const character of value) {
    if (character !== ' '
      && character !== '\t'
      && character !== '\r'
      && character !== '\n') {
      return value;
    }
  }

  return undefined;
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
