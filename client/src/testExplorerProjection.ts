import * as path from 'node:path';

import type {
  TestControllerAdapter,
  TestExplorerItem,
  TestMessageLocation,
  TestRunLike,
  TestRunRequestLike
} from './testExplorer';
import {
  VbaDevTestEvent,
  parseVbaDevTestEvents
} from './vbaDevOutputContract';

export interface TestItemMetadata {
  kind: 'project' | 'document' | 'module' | 'procedure';
  projectRoot: string;
  sourceSetPaths: readonly string[];
  documentName?: string | undefined;
  moduleName?: string | undefined;
  procedureName?: string | undefined;
}

export interface WorkbookBackedTestProject {
  projectRoot: string;
  manifestPath: string;
  projectName: string;
  documents: readonly WorkbookBackedTestDocument[];
}

export interface WorkbookBackedTestDocument {
  name: string;
  sourcePath: string;
}

export interface VbaTestRunProjectionResult {
  hasAssertionFailure: boolean;
  errorItem?: TestExplorerItem | undefined;
}

export class TestExplorerNodeIndex {
  private readonly metadataById = new Map<string, TestItemMetadata>();
  private readonly itemsById = new Map<string, TestExplorerItem>();
  private readonly rootItems: TestExplorerItem[] = [];

  public clear(): void {
    this.metadataById.clear();
    this.itemsById.clear();
    this.rootItems.splice(0, this.rootItems.length);
  }

  public get roots(): readonly TestExplorerItem[] {
    return this.rootItems;
  }

  public getMetadata(item: TestExplorerItem): TestItemMetadata | undefined {
    return this.metadataById.get(item.id);
  }

  public selectedRunnableItems(request: TestRunRequestLike): TestExplorerItem[] {
    const excluded = new Set((request.exclude ?? []).map((item) => item.id));
    return (request.include ?? this.rootItems)
      .filter((item) => this.metadataById.has(item.id))
      .filter((item) => !excluded.has(item.id));
  }

  public selectedSourceSetPaths(items: readonly TestExplorerItem[]): string[] {
    const sourceSetPaths = new Map<string, string>();
    for (const item of items) {
      for (const sourceSetPath of this.metadataById.get(item.id)?.sourceSetPaths ?? []) {
        sourceSetPaths.set(path.normalize(sourceSetPath).toLowerCase(), sourceSetPath);
      }
    }

    return [...sourceSetPaths.values()];
  }

  public addProject(controller: TestControllerAdapter, project: WorkbookBackedTestProject): void {
    const projectItem = controller.createTestItem(
      projectItemId(project.projectRoot),
      project.projectName,
      project.manifestPath
    );
    this.setItem(projectItem, {
      kind: 'project',
      projectRoot: project.projectRoot,
      sourceSetPaths: project.documents.map((document) => (
        path.resolve(project.projectRoot, document.sourcePath)
      ))
    });

    for (const document of project.documents) {
      const documentItem = controller.createTestItem(
        documentItemId(project.projectRoot, document.name),
        document.name,
        path.resolve(project.projectRoot, document.sourcePath)
      );
      projectItem.children.add(documentItem);
      this.setItem(documentItem, {
        kind: 'document',
        projectRoot: project.projectRoot,
        sourceSetPaths: [path.resolve(project.projectRoot, document.sourcePath)],
        documentName: document.name
      });
    }

    this.rootItems.push(projectItem);
  }

  public applyTestOutput(
    controller: TestControllerAdapter,
    testRun: TestRunLike,
    runMetadata: TestItemMetadata,
    stdout: string
  ): VbaTestRunProjectionResult {
    let hasAssertionFailure = false;
    let errorItem: TestExplorerItem | undefined;
    for (const event of parseVbaDevTestEvents(stdout)) {
      if (event.type === 'testStarted') {
        const item = this.resolveEventItem(controller, runMetadata, event, false);
        if (item) {
          testRun.started(item);
        }
        continue;
      }

      if (event.type === 'testFinished') {
        const item = this.resolveEventItem(controller, runMetadata, event, true);
        if (!item) {
          continue;
        }

        if (!event.location && event.module && event.procedure) {
          testRun.appendOutput(
            `Source location unavailable: ${event.module}.${event.procedure}\n`);
        }

        const outcome = (event.outcome ?? '').toLowerCase();
        if (outcome === 'passed') {
          testRun.passed(item);
        } else if (outcome === 'failed') {
          hasAssertionFailure = true;
          testRun.failed(
            item,
            event.message ?? 'VBA test failed.',
            toTestMessageLocation(event.location));
        } else if (outcome === 'error') {
          hasAssertionFailure = true;
          testRun.failed(
            item,
            event.message ?? 'VBA test errored.',
            toTestMessageLocation(event.location));
        }
        continue;
      }

      if (event.type === 'runFinished') {
        continue;
      }
    }

    return { hasAssertionFailure, errorItem };
  }

  private setItem(item: TestExplorerItem, metadata: TestItemMetadata): void {
    this.itemsById.set(item.id, item);
    this.metadataById.set(item.id, metadata);
  }

  private resolveEventItem(
    controller: TestControllerAdapter,
    runMetadata: TestItemMetadata,
    event: VbaDevTestEvent,
    createMissing: boolean
  ): TestExplorerItem | undefined {
    const documentName = event.document ?? runMetadata.documentName;
    if (!documentName) {
      return this.itemsById.get(projectItemId(runMetadata.projectRoot));
    }

    const documentItem = this.itemsById.get(documentItemId(runMetadata.projectRoot, documentName));
    if (!documentItem) {
      return undefined;
    }

    const moduleName = event.module;
    if (!moduleName) {
      return documentItem;
    }

    const moduleItem = createMissing
      ? this.ensureModuleItem(controller, runMetadata.projectRoot, documentName, moduleName, event)
      : this.itemsById.get(moduleItemId(runMetadata.projectRoot, documentName, moduleName));
    if (!moduleItem) {
      return documentItem;
    }

    const procedureName = event.procedure;
    if (!procedureName) {
      return moduleItem;
    }

    return createMissing
      ? this.ensureProcedureItem(
        controller,
        runMetadata.projectRoot,
        documentName,
        moduleName,
        procedureName,
        moduleItem,
        event)
      : this.itemsById.get(procedureItemId(runMetadata.projectRoot, documentName, moduleName, procedureName));
  }

  private ensureModuleItem(
    controller: TestControllerAdapter,
    projectRoot: string,
    documentName: string,
    moduleName: string,
    event: VbaDevTestEvent
  ): TestExplorerItem | undefined {
    const id = moduleItemId(projectRoot, documentName, moduleName);
    const existing = this.itemsById.get(id);
    if (existing) {
      return existing;
    }

    const documentItem = this.itemsById.get(documentItemId(projectRoot, documentName));
    if (!documentItem) {
      return undefined;
    }

    const moduleItem = controller.createTestItem(id, moduleName);
    documentItem.children.add(moduleItem);
    this.setItem(moduleItem, {
      kind: 'module',
      projectRoot,
      sourceSetPaths: this.metadataById.get(documentItem.id)?.sourceSetPaths ?? [],
      documentName,
      moduleName
    });
    return moduleItem;
  }

  private ensureProcedureItem(
    controller: TestControllerAdapter,
    projectRoot: string,
    documentName: string,
    moduleName: string,
    procedureName: string,
    moduleItem: TestExplorerItem,
    event: VbaDevTestEvent
  ): TestExplorerItem {
    const id = procedureItemId(projectRoot, documentName, moduleName, procedureName);
    const existing = this.itemsById.get(id);
    if (existing) {
      return existing;
    }

    const location = toTestMessageLocation(event.location);
    const procedureItem = controller.createTestItem(
      id,
      procedureName,
      location?.uriPath,
      event.location?.range);
    moduleItem.children.add(procedureItem);
    this.setItem(procedureItem, {
      kind: 'procedure',
      projectRoot,
      sourceSetPaths: this.metadataById.get(moduleItem.id)?.sourceSetPaths ?? [],
      documentName,
      moduleName,
      procedureName
    });
    return procedureItem;
  }
}

export function createTestSelectorArgs(metadata: TestItemMetadata, noBuild: boolean): readonly string[] {
  return [
    ...(metadata.documentName
      ? ['--document', metadata.documentName]
      : []),
    ...(metadata.moduleName
      ? ['--module', metadata.moduleName]
      : []),
    ...(metadata.procedureName
      ? ['--procedure', metadata.procedureName]
      : []),
    ...(noBuild
      ? ['--no-build']
      : []),
    '--format',
    'ndjson'
  ];
}

function toTestMessageLocation(location: VbaDevTestEvent['location']): TestMessageLocation | undefined {
  return location
    ? {
      uriPath: location.uriPath,
      range: location.range
    }
    : undefined;
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
