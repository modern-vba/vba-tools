import * as path from 'node:path';

import {
  ProcessRunner,
  RequiredVbaDevContract
} from './devtool';
import {
  CommandCancellationToken,
  StartVbaDevProcess,
  VbaToolsOutputChannel
} from './devtoolCommand';
import {
  runVbaDevProjectCommandInvocation
} from './devtoolRuntime';
import { parseProjectManifest } from './projectManifest';
import {
  TestExplorerNodeIndex,
  WorkbookBackedTestProject,
  createTestSelectorArgs
} from './testExplorerProjection';

export interface TestExplorerItem {
  readonly id: string;
  readonly label: string;
  readonly range?: TestSourceRange | undefined;
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
  range: TestSourceRange;
}

export interface TestSourcePosition {
  line: number;
  character: number;
}

export interface TestSourceRange {
  start: TestSourcePosition;
  end: TestSourcePosition;
}

export interface TestControllerAdapter {
  createTestItem(
    id: string,
    label: string,
    uriPath?: string | undefined,
    range?: TestSourceRange | undefined
  ): TestExplorerItem;
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

export function createWorkbookBackedTestExplorer(
  options: WorkbookBackedTestExplorerOptions
): WorkbookBackedTestExplorer {
  const nodeIndex = new TestExplorerNodeIndex();

  const explorer: WorkbookBackedTestExplorer = {
    refresh: async () => {
      nodeIndex.clear();

      const projects = await loadWorkbookBackedProjects(options);
      for (const project of projects) {
        nodeIndex.addProject(options.controller, project);
      }

      options.controller.replaceItems(nodeIndex.roots);
    },
    run: async (request, token) => {
      await runTests(options, nodeIndex, request, token, { noBuild: false });
    }
  };

  options.controller.createRunProfile('Run Tests', explorer.run, true);
  options.controller.createRunProfile(
    'Run Tests Without Build',
    async (request, token) => {
      await runTests(options, nodeIndex, request, token, { noBuild: true });
    },
    false
  );
  return explorer;
}

async function loadWorkbookBackedProjects(
  options: WorkbookBackedTestExplorerOptions
): Promise<WorkbookBackedTestProject[]> {
  const manifests = await options.findProjectManifests(options.workspaceRoots);
  const projects: WorkbookBackedTestProject[] = [];
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
  nodeIndex: TestExplorerNodeIndex,
  request: TestRunRequestLike,
  token: CommandCancellationToken,
  runOptions: TestRunOptions
): Promise<void> {
  const run = options.controller.createTestRun(request);
  try {
    const items = nodeIndex.selectedRunnableItems(request);
    for (const item of items) {
      await runTestItem(options, nodeIndex, run, item, token, runOptions);
    }
  } finally {
    run.end();
  }
}

async function runTestItem(
  options: WorkbookBackedTestExplorerOptions,
  nodeIndex: TestExplorerNodeIndex,
  testRun: TestRunLike,
  item: TestExplorerItem,
  token: CommandCancellationToken,
  runOptions: TestRunOptions
): Promise<void> {
  const metadata = nodeIndex.getMetadata(item);
  if (!metadata) {
    return;
  }

  testRun.started(item);
  const result = await runVbaDevProjectCommandInvocation({
    ...options,
    cancellationToken: token
  }, {
    projectRoot: metadata.projectRoot,
    argsBeforeProject: ['test'],
    argsAfterProject: createTestSelectorArgs(metadata, runOptions.noBuild)
  });
  testRun.appendOutput(result.stdout);
  testRun.appendOutput(result.stderr);

  const eventState = nodeIndex.applyTestOutput(
    options.controller,
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

function firstNonEmptyLine(...values: readonly string[]): string | undefined {
  for (const value of values) {
    const line = value.split(/\r?\n/).find((candidate) => candidate.trim().length > 0);
    if (line) {
      return line.trim();
    }
  }

  return undefined;
}
