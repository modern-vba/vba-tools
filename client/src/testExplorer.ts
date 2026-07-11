import * as path from 'node:path';

import {
  ProcessRunner,
  RequiredVbaDevContract,
  resolveCompatibleVbaDev
} from './devtool';
import {
  CommandCancellationToken,
  StartVbaDevProcess,
  VbaToolsOutputChannel,
  runVbaDevCommand
} from './devtoolCommand';
import {
  WorkbookBackedProjectDocument,
  parseProjectManifest
} from './projectManifest';

export interface TestExplorerItem {
  readonly id: string;
  readonly label: string;
  readonly children: {
    add(item: TestExplorerItem): void;
  };
}

export interface TestRunRequestLike {
  readonly include?: readonly TestExplorerItem[] | undefined;
  readonly exclude?: readonly TestExplorerItem[] | undefined;
}

export interface TestRunLike {
  started(item: TestExplorerItem): void;
  passed(item: TestExplorerItem): void;
  failed(item: TestExplorerItem, message: string, location?: TestMessageLocation | undefined): void;
  errored(item: TestExplorerItem, message: string, location?: TestMessageLocation | undefined): void;
  cancelled(item: TestExplorerItem): void;
  appendOutput(output: string): void;
  end(): void;
}

export interface TestMessageLocation {
  uriPath: string;
  line: number;
  character: number;
}

export interface TestControllerAdapter {
  createTestItem(id: string, label: string, uriPath?: string | undefined): TestExplorerItem;
  replaceItems(items: readonly TestExplorerItem[]): void;
  createRunProfile(
    label: string,
    runHandler: (request: TestRunRequestLike, token: CommandCancellationToken) => Promise<void>,
    isDefault: boolean
  ): void;
  createTestRun(request: TestRunRequestLike): TestRunLike;
}

export interface WorkbookBackedTestExplorerOptions {
  controller: TestControllerAdapter;
  extensionRoot: string;
  configuredDevToolPath?: string | undefined;
  workspaceRoots: readonly string[];
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  readTextFile: (filePath: string) => Promise<string>;
  capabilitiesProcess?: ProcessRunner | undefined;
  startProcess?: StartVbaDevProcess | undefined;
  outputChannel: VbaToolsOutputChannel;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
  requiredContract?: RequiredVbaDevContract | undefined;
}

export interface WorkbookBackedTestExplorer {
  refresh(): Promise<void>;
  run(request: TestRunRequestLike, token: CommandCancellationToken): Promise<void>;
}

interface TestRunOptions {
  noBuild: boolean;
}

interface WorkbookBackedProject {
  projectRoot: string;
  manifestPath: string;
  projectName: string;
  documents: readonly WorkbookBackedProjectDocument[];
}

interface TestItemMetadata {
  kind: 'project' | 'document' | 'module' | 'procedure';
  projectRoot: string;
  documentName?: string | undefined;
  moduleName?: string | undefined;
  procedureName?: string | undefined;
}

export function createWorkbookBackedTestExplorer(
  options: WorkbookBackedTestExplorerOptions
): WorkbookBackedTestExplorer {
  const metadataById = new Map<string, TestItemMetadata>();
  const itemsById = new Map<string, TestExplorerItem>();
  const rootItems: TestExplorerItem[] = [];

  const explorer: WorkbookBackedTestExplorer = {
    refresh: async () => {
      metadataById.clear();
      itemsById.clear();
      rootItems.splice(0, rootItems.length);

      const projects = await loadWorkbookBackedProjects(options);
      for (const project of projects) {
        const projectItem = options.controller.createTestItem(
          projectItemId(project.projectRoot),
          project.projectName,
          project.manifestPath
        );
        metadataById.set(projectItem.id, {
          kind: 'project',
          projectRoot: project.projectRoot
        });
        itemsById.set(projectItem.id, projectItem);

        for (const document of project.documents) {
          const documentItem = options.controller.createTestItem(
            documentItemId(project.projectRoot, document.name),
            document.name,
            path.resolve(project.projectRoot, document.sourcePath)
          );
          projectItem.children.add(documentItem);
          metadataById.set(documentItem.id, {
            kind: 'document',
            projectRoot: project.projectRoot,
            documentName: document.name
          });
          itemsById.set(documentItem.id, documentItem);
        }

        rootItems.push(projectItem);
      }

      options.controller.replaceItems(rootItems);
    },
    run: async (request, token) => {
      await runTests(options, metadataById, itemsById, rootItems, request, token, { noBuild: false });
    }
  };

  options.controller.createRunProfile('Run Tests', explorer.run, true);
  options.controller.createRunProfile(
    'Run Tests Without Build',
    async (request, token) => {
      await runTests(options, metadataById, itemsById, rootItems, request, token, { noBuild: true });
    },
    false
  );
  return explorer;
}

async function loadWorkbookBackedProjects(
  options: WorkbookBackedTestExplorerOptions
): Promise<WorkbookBackedProject[]> {
  const manifests = await options.findProjectManifests(options.workspaceRoots);
  const projects: WorkbookBackedProject[] = [];
  for (const manifestPath of manifests) {
    const manifest = parseProjectManifest(await options.readTextFile(manifestPath));
    if (!manifest) {
      continue;
    }

    projects.push({
      projectRoot: path.dirname(manifestPath),
      manifestPath,
      projectName: manifest.projectName,
      documents: manifest.documents
    });
  }

  return projects;
}

async function runTests(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  rootItems: readonly TestExplorerItem[],
  request: TestRunRequestLike,
  token: CommandCancellationToken,
  runOptions: TestRunOptions
): Promise<void> {
  const run = options.controller.createTestRun(request);
  try {
    const items = selectedRunnableItems(request, rootItems, metadataById);
    for (const item of items) {
      await runTestItem(options, metadataById, itemsById, run, item, token, runOptions);
    }
  } finally {
    run.end();
  }
}

function selectedRunnableItems(
  request: TestRunRequestLike,
  rootItems: readonly TestExplorerItem[],
  metadataById: ReadonlyMap<string, TestItemMetadata>
): TestExplorerItem[] {
  const excluded = new Set((request.exclude ?? []).map((item) => item.id));
  return (request.include ?? rootItems)
    .filter((item) => metadataById.has(item.id))
    .filter((item) => !excluded.has(item.id));
}

async function runTestItem(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  testRun: TestRunLike,
  item: TestExplorerItem,
  token: CommandCancellationToken,
  runOptions: TestRunOptions
): Promise<void> {
  const metadata = metadataById.get(item.id);
  if (!metadata) {
    return;
  }

  testRun.started(item);
  const devtool = await resolveCompatibleVbaDev({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract
  });

  const args = [
    'test',
    '--project',
    metadata.projectRoot,
    ...(metadata.documentName
      ? ['--document', metadata.documentName]
      : []),
    ...(metadata.moduleName
      ? ['--module', metadata.moduleName]
      : []),
    ...(metadata.procedureName
      ? ['--procedure', metadata.procedureName]
      : []),
    ...(runOptions.noBuild
      ? ['--no-build']
      : []),
    '--format',
    'ndjson'
  ];

  const result = await runVbaDevCommand({
    executablePath: devtool.executablePath,
    args,
    outputChannel: options.outputChannel,
    cancellationToken: token,
    startProcess: options.startProcess
  });
  testRun.appendOutput(result.stdout);
  testRun.appendOutput(result.stderr);

  const eventState = applyNdjsonTestEvents(
    options,
    metadataById,
    itemsById,
    testRun,
    metadata,
    result.stdout);

  if (result.cancelled) {
    testRun.cancelled(item);
  } else if (result.exitCode === 0) {
    testRun.passed(item);
  } else if (eventState.hasAssertionFailure) {
    testRun.failed(item, 'One or more VBA tests failed.');
  } else {
    const errorMessage = firstNonEmptyLine(result.stderr, result.stdout) ?? 'vba-dev test failed. See the VBA Tools output for details.';
    testRun.errored(eventState.errorItem ?? item, errorMessage);
    await options.showErrorMessage('VBA Tools: Test failed. See the VBA Tools output for details.');
  }
}

function applyNdjsonTestEvents(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  testRun: TestRunLike,
  runMetadata: TestItemMetadata,
  stdout: string
): { hasAssertionFailure: boolean; errorItem?: TestExplorerItem | undefined } {
  let hasAssertionFailure = false;
  let errorItem: TestExplorerItem | undefined;
  for (const record of parseNdjsonRecords(stdout)) {
    const type = getString(record.type);
    if (type === 'testStarted') {
      const item = resolveEventItem(options, metadataById, itemsById, runMetadata, record, false);
      if (item) {
        testRun.started(item);
      }
      continue;
    }

    if (type === 'testFinished' || type === 'result') {
      const item = resolveEventItem(options, metadataById, itemsById, runMetadata, record, true);
      if (!item) {
        continue;
      }

      const outcome = (getString(record.outcome) ?? '').toLowerCase();
      if (outcome === 'passed') {
        testRun.passed(item);
      } else if (outcome === 'failed') {
        hasAssertionFailure = true;
        testRun.failed(
          item,
          getString(record.message) ?? 'VBA test failed.',
          getLocation(record));
      } else if (outcome === 'error') {
        hasAssertionFailure = true;
        testRun.failed(
          item,
          getString(record.message) ?? 'VBA test errored.',
          getLocation(record));
      }
      continue;
    }

    if (type === 'runFinished') {
      const outcome = (getString(record.outcome) ?? '').toLowerCase();
      if (outcome === 'failed' || outcome === 'error') {
        errorItem = resolveEventItem(options, metadataById, itemsById, runMetadata, record, false);
        if (errorItem) {
          testRun.errored(errorItem, getString(record.message) ?? 'vba-dev test failed.', getLocation(record));
        }
      }
    }
  }

  return { hasAssertionFailure, errorItem };
}

function resolveEventItem(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  runMetadata: TestItemMetadata,
  record: Record<string, unknown>,
  createMissing: boolean
): TestExplorerItem | undefined {
  const documentName = getString(record.document) ?? runMetadata.documentName;
  if (!documentName) {
    return itemsById.get(projectItemId(runMetadata.projectRoot));
  }

  const documentItem = itemsById.get(documentItemId(runMetadata.projectRoot, documentName));
  if (!documentItem) {
    return undefined;
  }

  const moduleName = getString(record.module) ?? getString(record.category);
  if (!moduleName) {
    return documentItem;
  }

  const moduleItem = createMissing
    ? ensureModuleItem(options, metadataById, itemsById, runMetadata.projectRoot, documentName, moduleName, record)
    : itemsById.get(moduleItemId(runMetadata.projectRoot, documentName, moduleName));
  if (!moduleItem) {
    return documentItem;
  }

  const procedureName = getString(record.procedure) ?? getString(record.testName);
  if (!procedureName) {
    return moduleItem;
  }

  return createMissing
    ? ensureProcedureItem(options, metadataById, itemsById, runMetadata.projectRoot, documentName, moduleName, procedureName, moduleItem, record)
    : itemsById.get(procedureItemId(runMetadata.projectRoot, documentName, moduleName, procedureName));
}

function ensureModuleItem(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  projectRoot: string,
  documentName: string,
  moduleName: string,
  record: Record<string, unknown>
): TestExplorerItem | undefined {
  const id = moduleItemId(projectRoot, documentName, moduleName);
  const existing = itemsById.get(id);
  if (existing) {
    return existing;
  }

  const documentItem = itemsById.get(documentItemId(projectRoot, documentName));
  if (!documentItem) {
    return undefined;
  }

  const location = getLocation(record);
  const moduleItem = options.controller.createTestItem(id, moduleName, location?.uriPath);
  documentItem.children.add(moduleItem);
  itemsById.set(id, moduleItem);
  metadataById.set(id, {
    kind: 'module',
    projectRoot,
    documentName,
    moduleName
  });
  return moduleItem;
}

function ensureProcedureItem(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: Map<string, TestItemMetadata>,
  itemsById: Map<string, TestExplorerItem>,
  projectRoot: string,
  documentName: string,
  moduleName: string,
  procedureName: string,
  moduleItem: TestExplorerItem,
  record: Record<string, unknown>
): TestExplorerItem {
  const id = procedureItemId(projectRoot, documentName, moduleName, procedureName);
  const existing = itemsById.get(id);
  if (existing) {
    return existing;
  }

  const location = getLocation(record);
  const procedureItem = options.controller.createTestItem(id, procedureName, location?.uriPath);
  moduleItem.children.add(procedureItem);
  itemsById.set(id, procedureItem);
  metadataById.set(id, {
    kind: 'procedure',
    projectRoot,
    documentName,
    moduleName,
    procedureName
  });
  return procedureItem;
}

function parseNdjsonRecords(stdout: string): Record<string, unknown>[] {
  const records: Record<string, unknown>[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    if (line.trim().length === 0) {
      continue;
    }

    try {
      const parsed = JSON.parse(line) as unknown;
      if (isRecord(parsed)) {
        records.push(parsed);
      }
    } catch {
      continue;
    }
  }

  return records;
}

function getLocation(record: Record<string, unknown>): TestMessageLocation | undefined {
  const location = record.location;
  if (!isRecord(location)) {
    return undefined;
  }

  const uriPath = getString(location.file) ?? getString(location.uriPath) ?? getString(location.uri);
  const line = getNumber(location.line);
  const character = getNumber(location.character) ?? getNumber(location.column) ?? 0;
  if (!uriPath || line === undefined) {
    return undefined;
  }

  return { uriPath, line, character };
}

function getString(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function getNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function firstNonEmptyLine(...values: readonly string[]): string | undefined {
  for (const value of values) {
    const line = value.split(/\r?\n/).find((candidate) => candidate.trim().length > 0);
    if (line) {
      return line.trim();
    }
  }

  return undefined;
}

function projectItemId(projectRoot: string): string {
  return `project:${path.normalize(projectRoot)}`;
}

function documentItemId(projectRoot: string, documentName: string): string {
  return `document:${path.normalize(projectRoot)}:${documentName}`;
}

function moduleItemId(projectRoot: string, documentName: string, moduleName: string): string {
  return `module:${path.normalize(projectRoot)}:${documentName}:${moduleName}`;
}

function procedureItemId(projectRoot: string, documentName: string, moduleName: string, procedureName: string): string {
  return `procedure:${path.normalize(projectRoot)}:${documentName}:${moduleName}:${procedureName}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
