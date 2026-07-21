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
  manifestPath?: string | undefined;
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

export type TestDiscoveryGenerationSnapshot = ReadonlyMap<string, number>;

export class TestExplorerNodeIndex {
  private readonly metadataById = new Map<string, TestItemMetadata>();
  private readonly itemsById = new Map<string, TestExplorerItem>();
  private readonly rootItems: TestExplorerItem[] = [];
  private readonly leafIdsByDocumentId = new Map<string, Set<string>>();
  private readonly generationByDocumentId = new Map<string, number>();
  private nextGeneration = 0;

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

  public captureDiscoveryGenerations(
    runMetadata: TestItemMetadata
  ): TestDiscoveryGenerationSnapshot {
    const generations = new Map<string, number>();
    for (const [itemId, metadata] of this.metadataById) {
      if (
        metadata.kind !== 'document'
        || !samePath(metadata.projectRoot, runMetadata.projectRoot)
        || (
          runMetadata.documentName !== undefined
          && metadata.documentName !== runMetadata.documentName
        )
      ) {
        continue;
      }

      const generation = this.generationByDocumentId.get(itemId);
      if (generation !== undefined) {
        generations.set(itemId, generation);
      }
    }

    return generations;
  }

  public invalidateSourcePath(sourcePath: string): void {
    const affectedDocumentIds = [...this.metadataById.entries()]
      .filter(([, metadata]) => (
        metadata.kind === 'document'
        && metadata.sourceSetPaths.some((sourceSetPath) => isPathWithin(sourcePath, sourceSetPath))
      ))
      .map(([itemId]) => itemId);
    affectedDocumentIds.forEach((itemId) => this.clearDocumentSnapshot(itemId));
  }

  public reconcileProjects(
    controller: TestControllerAdapter,
    projects: readonly WorkbookBackedTestProject[]
  ): void {
    const desiredProjectIds = new Set<string>();
    const desiredRootItems: TestExplorerItem[] = [];
    for (const project of projects) {
      const projectId = projectItemId(project.projectRoot);
      desiredProjectIds.add(projectId);
      const projectItem = this.itemsById.get(projectId)
        ?? controller.createTestItem(projectId, project.projectName, project.manifestPath);
      projectItem.label = project.projectName;
      this.setItem(projectItem, {
        kind: 'project',
        projectRoot: project.projectRoot,
        sourceSetPaths: project.documents.map((document) => (
          path.resolve(project.projectRoot, document.sourcePath)
        )),
        manifestPath: project.manifestPath
      });

      const existingDocumentIds = new Set(
        [...this.metadataById.entries()]
          .filter(([, metadata]) => (
            metadata.kind === 'document'
            && samePath(metadata.projectRoot, project.projectRoot)
          ))
          .map(([itemId]) => itemId));
      const desiredDocumentItems: TestExplorerItem[] = [];
      for (const document of project.documents) {
        const documentId = documentItemId(project.projectRoot, document.name);
        const sourceSetPath = path.resolve(project.projectRoot, document.sourcePath);
        const existingMetadata = this.metadataById.get(documentId);
        let documentItem = this.itemsById.get(documentId);
        if (
          documentItem
          && !samePath(existingMetadata?.sourceSetPaths[0], sourceSetPath)
        ) {
          this.removeDocument(documentId);
          documentItem = undefined;
        }

        documentItem ??= controller.createTestItem(
          documentId,
          document.name,
          sourceSetPath);
        documentItem.label = document.name;
        this.setItem(documentItem, {
          kind: 'document',
          projectRoot: project.projectRoot,
          sourceSetPaths: [sourceSetPath],
          documentName: document.name
        });
        if (!this.generationByDocumentId.has(documentId)) {
          this.advanceDocumentGeneration(documentId);
        }
        existingDocumentIds.delete(documentId);
        desiredDocumentItems.push(documentItem);
      }

      for (const documentId of existingDocumentIds) {
        this.removeDocument(documentId);
      }
      projectItem.children.replace(desiredDocumentItems);
      desiredRootItems.push(projectItem);
    }

    for (const [itemId, metadata] of [...this.metadataById.entries()]) {
      if (metadata.kind === 'project' && !desiredProjectIds.has(itemId)) {
        this.removeProject(itemId, metadata.projectRoot);
      }
    }

    this.rootItems.splice(0, this.rootItems.length, ...desiredRootItems);
  }

  public invalidateProjectDefinition(manifestPath: string): void {
    const project = [...this.metadataById.values()].find((metadata) => (
      metadata.kind === 'project'
      && samePath(metadata.manifestPath, manifestPath)
    ));
    if (!project) {
      return;
    }

    for (const [itemId, metadata] of this.metadataById) {
      if (metadata.kind === 'document' && samePath(metadata.projectRoot, project.projectRoot)) {
        this.clearDocumentSnapshot(itemId);
      }
    }
  }

  public applyTestOutput(
    controller: TestControllerAdapter,
    testRun: TestRunLike,
    runMetadata: TestItemMetadata,
    stdout: string,
    discoveryGenerations: TestDiscoveryGenerationSnapshot,
    processCompleted: boolean
  ): VbaTestRunProjectionResult {
    let hasAssertionFailure = false;
    let errorItem: TestExplorerItem | undefined;
    const events = parseVbaDevTestEvents(stdout);
    const completedDocumentIds = new Set(
      events
        .filter((event) => event.type === 'runFinished')
        .flatMap((event) => {
          const documentId = this.eventDocumentId(runMetadata, event);
          return documentId ? [documentId] : [];
        }));
    for (const event of events) {
      if (event.type === 'testStarted') {
        if (!this.canProjectEvent(
          runMetadata,
          event,
          discoveryGenerations,
          completedDocumentIds,
          processCompleted)) {
          continue;
        }

        const item = this.resolveEventItem(controller, runMetadata, event, false);
        if (item) {
          testRun.started(item);
        }
        continue;
      }

      if (event.type === 'testFinished') {
        const outcome = (event.outcome ?? '').toLowerCase();
        const completedEvent = this.isCompletedEvent(
          runMetadata,
          event,
          completedDocumentIds,
          processCompleted);
        if (completedEvent && (outcome === 'failed' || outcome === 'error')) {
          hasAssertionFailure = true;
        }

        if (
          !completedEvent
          || !this.isCurrentEventGeneration(runMetadata, event, discoveryGenerations)
        ) {
          continue;
        }

        const item = this.resolveEventItem(controller, runMetadata, event, true);
        if (!item) {
          continue;
        }

        if (!event.location && event.module && event.procedure) {
          testRun.appendOutput(
            `Source location unavailable: ${event.module}.${event.procedure}\n`);
        }

        if (outcome === 'passed') {
          testRun.passed(item);
        } else if (outcome === 'failed') {
          testRun.failed(
            item,
            event.message ?? 'VBA test failed.',
            toTestMessageLocation(event.location));
        } else if (outcome === 'error') {
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

  private eventDocumentId(
    runMetadata: TestItemMetadata,
    event: VbaDevTestEvent
  ): string | undefined {
    const documentName = event.document ?? runMetadata.documentName;
    return documentName
      ? documentItemId(runMetadata.projectRoot, documentName)
      : undefined;
  }

  private canProjectEvent(
    runMetadata: TestItemMetadata,
    event: VbaDevTestEvent,
    discoveryGenerations: TestDiscoveryGenerationSnapshot,
    completedDocumentIds: ReadonlySet<string>,
    processCompleted: boolean
  ): boolean {
    return this.isCompletedEvent(
      runMetadata,
      event,
      completedDocumentIds,
      processCompleted)
      && this.isCurrentEventGeneration(runMetadata, event, discoveryGenerations);
  }

  private isCompletedEvent(
    runMetadata: TestItemMetadata,
    event: VbaDevTestEvent,
    completedDocumentIds: ReadonlySet<string>,
    processCompleted: boolean
  ): boolean {
    const documentId = this.eventDocumentId(runMetadata, event);
    return processCompleted
      && documentId !== undefined
      && completedDocumentIds.has(documentId);
  }

  private isCurrentEventGeneration(
    runMetadata: TestItemMetadata,
    event: VbaDevTestEvent,
    discoveryGenerations: TestDiscoveryGenerationSnapshot
  ): boolean {
    const documentId = this.eventDocumentId(runMetadata, event);
    return documentId !== undefined
      && discoveryGenerations.get(documentId) === this.generationByDocumentId.get(documentId);
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
    this.trackDocumentLeaf(documentItem.id, moduleItem.id);
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
    this.trackDocumentLeaf(
      documentItemId(projectRoot, documentName),
      procedureItem.id);
    return procedureItem;
  }

  private trackDocumentLeaf(documentId: string, leafId: string): void {
    const leafIds = this.leafIdsByDocumentId.get(documentId) ?? new Set<string>();
    leafIds.add(leafId);
    this.leafIdsByDocumentId.set(documentId, leafIds);
  }

  private clearDocumentSnapshot(documentId: string): void {
    const documentItem = this.itemsById.get(documentId);
    if (!documentItem) {
      return;
    }

    this.advanceDocumentGeneration(documentId);
    documentItem.children.replace([]);
    for (const leafId of this.leafIdsByDocumentId.get(documentId) ?? []) {
      this.metadataById.delete(leafId);
      this.itemsById.delete(leafId);
    }
    this.leafIdsByDocumentId.delete(documentId);
  }

  private removeDocument(documentId: string): void {
    this.clearDocumentSnapshot(documentId);
    this.metadataById.delete(documentId);
    this.itemsById.delete(documentId);
    this.generationByDocumentId.delete(documentId);
  }

  private advanceDocumentGeneration(documentId: string): void {
    this.nextGeneration += 1;
    this.generationByDocumentId.set(documentId, this.nextGeneration);
  }

  private removeProject(projectId: string, projectRoot: string): void {
    const documentIds = [...this.metadataById.entries()]
      .filter(([, metadata]) => (
        metadata.kind === 'document'
        && samePath(metadata.projectRoot, projectRoot)
      ))
      .map(([itemId]) => itemId);
    documentIds.forEach((documentId) => this.removeDocument(documentId));
    this.metadataById.delete(projectId);
    this.itemsById.delete(projectId);
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

function isPathWithin(filePath: string, directoryPath: string): boolean {
  const relativePath = path.relative(path.resolve(directoryPath), path.resolve(filePath));
  return relativePath.length > 0
    && !relativePath.startsWith(`..${path.sep}`)
    && relativePath !== '..'
    && !path.isAbsolute(relativePath);
}

function samePath(left: string | undefined, right: string | undefined): boolean {
  return left !== undefined
    && right !== undefined
    && path.normalize(left).toLowerCase() === path.normalize(right).toLowerCase();
}
