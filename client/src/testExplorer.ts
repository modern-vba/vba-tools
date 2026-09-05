import * as path from 'node:path';
import { SnapshotProviderOptions, resolveSnapshotProviders, snapshotActiveWindowsCodePage } from './snapshotProviders';

import {
  CompanionExecutableResolver,
  ProcessRunner,
  RequiredVbaDevContract,
  isReportedVbaDevResolutionFailure,
  noCompatibleVbaDevMessage
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
  TestItemMetadata,
  TestExplorerNodeIndex,
  WorkbookBackedTestProject,
  createTestSelectorArgs
} from './testExplorerProjection';

export interface TestExplorerItem {
  readonly id: string;
  label: string;
  readonly range?: TestSourceRange | undefined;
  readonly children: {
    add(item: TestExplorerItem): void;
    replace(items: readonly TestExplorerItem[]): void;
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

export interface WorkbookBackedTestExplorerOptions extends SnapshotProviderOptions {
  controller: TestControllerAdapter;
  extensionRoot: string;
  configuredDevToolPath?: string | undefined;
  vbaDevResolver?: CompanionExecutableResolver | undefined;
  workspaceRoots: readonly string[];
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  readTextFile: (filePath: string) => Promise<string>;
  openTextDocuments: () => readonly OpenTextDocumentState[];
  captureSourceSnapshot: CaptureSourceSnapshot;
  capabilitiesProcess?: ProcessRunner | undefined;
  startProcess?: StartVbaDevProcess | undefined;
  requireTrustedWorkspace?: (() => Promise<boolean>) | undefined;
  outputChannel: VbaToolsOutputChannel;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
  requiredContract?: RequiredVbaDevContract | undefined;
}

export interface OpenTextDocumentState {
  readonly uriPath: string;
  readonly isDirty: boolean;
}

export interface CallerOwnedSourceSnapshot {
  readonly directoryPath: string;
  cleanup(): Promise<{ readonly retainedPath?: string | undefined }>;
}

export type CaptureSourceSnapshot = (
  sourceSetPath: string,
  cancellationToken: CommandCancellationToken,
  activeWindowsCodePage?: number
) => Promise<CallerOwnedSourceSnapshot>;

export interface WorkbookBackedTestExplorer {
  refresh(): Promise<void>;
  invalidateSourcePath(sourcePath: string): void;
  invalidateFileSystemSourceChange(sourcePath: string): void;
  refreshProjectDefinition(manifestPath: string): Promise<void>;
  run(request: TestRunRequestLike, token: CommandCancellationToken): Promise<void>;
}

interface TestRunOptions {
  noBuild: boolean;
}

export function createWorkbookBackedTestExplorer(
  options: WorkbookBackedTestExplorerOptions
): WorkbookBackedTestExplorer {
  const nodeIndex = new TestExplorerNodeIndex();
  let latestRefreshRequest = 0;

  const explorer: WorkbookBackedTestExplorer = {
    refresh: async () => {
      const refreshRequest = ++latestRefreshRequest;
      let projects: WorkbookBackedTestProject[];
      try {
        projects = await loadWorkbookBackedProjects(options);
      } catch (error) {
        if (refreshRequest === latestRefreshRequest) {
          throw error;
        }

        return;
      }

      if (refreshRequest !== latestRefreshRequest) {
        return;
      }

      nodeIndex.reconcileProjects(options.controller, projects);
      options.controller.replaceItems(nodeIndex.roots);
    },
    invalidateSourcePath: (sourcePath) => {
      if (isExportedVbaSource(sourcePath)) {
        nodeIndex.invalidateSourcePath(sourcePath);
      }
    },
    invalidateFileSystemSourceChange: (sourcePath) => {
      if (options.openTextDocuments().some((document) => samePath(document.uriPath, sourcePath))) {
        return;
      }

      explorer.invalidateSourcePath(sourcePath);
    },
    refreshProjectDefinition: async (manifestPath) => {
      nodeIndex.invalidateProjectDefinition(manifestPath);
      await explorer.refresh();
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
      primaryDocument: manifest.primaryDocument,
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
  if (options.requireTrustedWorkspace !== undefined
      && !await options.requireTrustedWorkspace()) {
    return;
  }

  const run = options.controller.createTestRun(request);
  try {
    const items = nodeIndex.selectedRunnableItems(request);
    let invocationOptions = options;
    try {
      if (runOptions.noBuild) {
        await options.vbaDevResolver?.resolve();
      } else {
        const providers = await resolveSnapshotProviders({ ...options, cancellationToken: token });
        const activeCodePage = snapshotActiveWindowsCodePage(providers);
        invocationOptions = {
          ...options,
          vbaDevResolver: { resolve: async () => providers.vbaDev },
          captureSourceSnapshot: (sourceSetPath, token) => options.captureSourceSnapshot(sourceSetPath, token, activeCodePage)
        };
      }
    } catch (error) {
      if (runOptions.noBuild && !isReportedVbaDevResolutionFailure(error)) {
        throw error;
      }
      const errorItem = items[0];
      if (errorItem !== undefined) {
        if (token.isCancellationRequested) {
          run.cancelled(errorItem);
        } else {
          run.errored(errorItem, isReportedVbaDevResolutionFailure(error)
            ? noCompatibleVbaDevMessage : error instanceof Error ? error.message : String(error));
        }
      }
      return;
    }
    const selections = items.flatMap((item) => {
      const metadata = nodeIndex.getMetadata(item);
      return metadata ? [{ item, metadata }] : [];
    });
    for (const selection of selections) {
      await runTestItem(
        invocationOptions,
        nodeIndex,
        run,
        selection.item,
        selection.metadata,
        token,
        runOptions);
    }
  } finally {
    run.end();
  }
}

function hasDirtyExportedSourceInScope(
  options: WorkbookBackedTestExplorerOptions,
  metadata: TestItemMetadata
): boolean {
  return options.openTextDocuments().some((document) => (
    document.isDirty
    && isExportedVbaSource(document.uriPath)
    && metadata.sourceSetPaths.some((sourceSetPath) => (
      isPathWithin(document.uriPath, sourceSetPath)
    ))
  ));
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

function samePath(left: string, right: string): boolean {
  return path.normalize(left).toLowerCase() === path.normalize(right).toLowerCase();
}

async function runTestItem(
  options: WorkbookBackedTestExplorerOptions,
  nodeIndex: TestExplorerNodeIndex,
  testRun: TestRunLike,
  item: TestExplorerItem,
  metadata: TestItemMetadata,
  token: CommandCancellationToken,
  runOptions: TestRunOptions
): Promise<void> {
  if (token.isCancellationRequested) {
    testRun.cancelled(item);
    return;
  }

  const omitSourceLocations = runOptions.noBuild
    && hasDirtyExportedSourceInScope(options, metadata);
  if (omitSourceLocations) {
    nodeIndex.resetDiscoveryForRun(metadata);
  }
  const discoveryGenerations = nodeIndex.captureDiscoveryGenerations(metadata);

  let sourceSnapshot: CallerOwnedSourceSnapshot | undefined;
  try {
    if (!runOptions.noBuild) {
      const sourceSetPath = metadata.sourceSetPaths[0];
      if (sourceSetPath === undefined) {
        testRun.errored(item, 'VBA Tools could not resolve the source set for this test run.');
        return;
      }

      try {
        sourceSnapshot = await options.captureSourceSnapshot(sourceSetPath, token);
      } catch (error) {
        if (token.isCancellationRequested) {
          testRun.cancelled(item);
          return;
        }
        const message = error instanceof Error ? error.message : String(error);
        testRun.errored(item, `Could not capture the VBA source snapshot: ${message}`);
        return;
      }
    }

    if (token.isCancellationRequested) {
      testRun.cancelled(item);
      return;
    }

    testRun.started(item);
    const result = await runVbaDevProjectCommandInvocation({
      ...options,
      cancellationToken: token
    }, {
      projectRoot: metadata.projectRoot,
      argsBeforeProject: ['test'],
      argsAfterProject: createTestSelectorArgs(
        metadata,
        runOptions.noBuild,
        sourceSnapshot?.directoryPath)
    });
    if (result === undefined) {
      testRun.errored(item, noCompatibleVbaDevMessage);
      return;
    }
    testRun.appendOutput(result.stdout);
    testRun.appendOutput(result.stderr);

    const eventState = nodeIndex.applyTestOutput(
      options.controller,
      testRun,
      metadata,
      result.stdout,
      discoveryGenerations,
      !result.cancelled && result.exitCode !== null,
      { omitSourceLocations });

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
  } finally {
    if (sourceSnapshot !== undefined) {
      try {
        const cleanupResult = await sourceSnapshot.cleanup();
        if (cleanupResult.retainedPath !== undefined) {
          testRun.appendOutput(
            `Source snapshot cleanup warning: retained caller-owned directory ${cleanupResult.retainedPath}\n`);
        }
      } catch {
        testRun.appendOutput(
          `Source snapshot cleanup warning: retained caller-owned directory ${sourceSnapshot.directoryPath}\n`);
      }
    }
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
