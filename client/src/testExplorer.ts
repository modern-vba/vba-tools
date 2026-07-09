import * as path from 'node:path';

import {
  ProcessRunner,
  RequiredVbaDevToolContract,
  resolveCompatibleVbaDevTool
} from './devtool';
import {
  CommandCancellationToken,
  StartVbaDevToolProcess,
  VbaToolsOutputChannel,
  runVbaDevToolCommand
} from './devtoolCommand';

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
  failed(item: TestExplorerItem, message: string): void;
  skipped(item: TestExplorerItem): void;
  appendOutput(output: string): void;
  end(): void;
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
  startProcess?: StartVbaDevToolProcess | undefined;
  outputChannel: VbaToolsOutputChannel;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
  requiredContract?: RequiredVbaDevToolContract | undefined;
}

export interface WorkbookBackedTestExplorer {
  refresh(): Promise<void>;
  run(request: TestRunRequestLike, token: CommandCancellationToken): Promise<void>;
}

interface WorkbookBackedProject {
  projectRoot: string;
  manifestPath: string;
  projectName: string;
  documents: readonly WorkbookBackedDocument[];
}

interface WorkbookBackedDocument {
  name: string;
  sourcePath: string;
}

interface TestItemMetadata {
  kind: 'project' | 'document';
  projectRoot: string;
  documentName?: string | undefined;
}

const RequiredTestContract: RequiredVbaDevToolContract = {
  contractVersion: '1.0',
  commandSchemaVersions: {
    test: '1.0'
  }
};

export function createWorkbookBackedTestExplorer(
  options: WorkbookBackedTestExplorerOptions
): WorkbookBackedTestExplorer {
  const metadataById = new Map<string, TestItemMetadata>();
  const rootItems: TestExplorerItem[] = [];

  const explorer: WorkbookBackedTestExplorer = {
    refresh: async () => {
      metadataById.clear();
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
        }

        rootItems.push(projectItem);
      }

      options.controller.replaceItems(rootItems);
    },
    run: async (request, token) => {
      await runTests(options, metadataById, rootItems, request, token);
    }
  };

  options.controller.createRunProfile('Run Tests', explorer.run, true);
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

function parseProjectManifest(json: string): { projectName: string; documents: WorkbookBackedDocument[] } | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return undefined;
  }

  if (!isRecord(parsed) || typeof parsed.projectName !== 'string' || !isRecord(parsed.documents)) {
    return undefined;
  }

  const documents: WorkbookBackedDocument[] = [];
  for (const [name, document] of Object.entries(parsed.documents)) {
    if (isRecord(document) && typeof document.sourcePath === 'string') {
      documents.push({ name, sourcePath: document.sourcePath });
    }
  }

  return {
    projectName: parsed.projectName,
    documents
  };
}

async function runTests(
  options: WorkbookBackedTestExplorerOptions,
  metadataById: ReadonlyMap<string, TestItemMetadata>,
  rootItems: readonly TestExplorerItem[],
  request: TestRunRequestLike,
  token: CommandCancellationToken
): Promise<void> {
  const run = options.controller.createTestRun(request);
  try {
    const items = selectedRunnableItems(request, rootItems, metadataById);
    for (const item of items) {
      await runTestItem(options, metadataById, run, item, token);
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
  metadataById: ReadonlyMap<string, TestItemMetadata>,
  testRun: TestRunLike,
  item: TestExplorerItem,
  token: CommandCancellationToken
): Promise<void> {
  const metadata = metadataById.get(item.id);
  if (!metadata) {
    return;
  }

  testRun.started(item);
  const devtool = await resolveCompatibleVbaDevTool({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract ?? RequiredTestContract
  });

  const args = [
    'test',
    '--project',
    metadata.projectRoot,
    ...(metadata.kind === 'document' && metadata.documentName
      ? ['--document', metadata.documentName]
      : []),
    '--format',
    'ndjson'
  ];

  const result = await runVbaDevToolCommand({
    executablePath: devtool.executablePath,
    args,
    outputChannel: options.outputChannel,
    cancellationToken: token,
    startProcess: options.startProcess
  });
  testRun.appendOutput(result.stdout);
  testRun.appendOutput(result.stderr);

  if (result.cancelled) {
    testRun.skipped(item);
  } else if (result.exitCode === 0) {
    testRun.passed(item);
  } else {
    testRun.failed(item, 'vba-devtool test failed. See the VBA Tools output for details.');
    await options.showErrorMessage('VBA Tools: Test failed. See the VBA Tools output for details.');
  }
}

function projectItemId(projectRoot: string): string {
  return `project:${path.normalize(projectRoot)}`;
}

function documentItemId(projectRoot: string, documentName: string): string {
  return `document:${path.normalize(projectRoot)}:${documentName}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
