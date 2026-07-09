import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { CommandCancellationToken } from './devtoolCommand';
import {
  TestControllerAdapter,
  TestExplorerItem,
  TestRunRequestLike,
  createWorkbookBackedTestExplorer
} from './testExplorer';

test('Test Explorer creates project and document nodes from project manifests', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'project.json'), manifestJson('BookProject', ['Book1', 'SecondBook'])]
    ])
  });

  await explorer.refresh();

  assert.deepEqual(controller.items.map((item) => item.label), ['BookProject']);
  assert.deepEqual(controller.items[0].children.items.map((item) => item.label), ['Book1', 'SecondBook']);
});

test('Test Explorer excludes standalone VBA files outside project manifests', async () => {
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map()
  });

  await explorer.refresh();

  assert.deepEqual(controller.items, []);
});

test('Running a project node invokes vba-devtool test ndjson with explicit project root', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    output,
    manifests: new Map([
      [path.join(projectRoot, 'project.json'), manifestJson('BookProject', ['Book1'])]
    ])
  });
  await explorer.refresh();

  await explorer.run({ include: [controller.items[0]] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--format', 'ndjson']
  ]);
  assert.deepEqual(controller.runs[0].events, [
    `started:${controller.items[0].id}`,
    `passed:${controller.items[0].id}`,
    'end'
  ]);
  assert.match(output.join(''), /"type":"summary"/);
});

test('Running a document node invokes vba-devtool test ndjson with explicit project and document', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'project.json'), manifestJson('BookProject', ['Book1'])]
    ])
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--format', 'ndjson']
  ]);
});

test('Test Explorer creates only the default run profile', () => {
  const controller = new FakeTestController();
  createExplorer(controller, { manifests: new Map() });

  assert.deepEqual(controller.runProfiles, [
    { label: 'Run Tests', isDefault: true }
  ]);
});

test('Cancelled VS Code test runs terminate the spawned CLI process', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  let killed = false;
  let cancelListener: (() => void) | undefined;
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    startProcess: () => ({
      onStdout: () => undefined,
      onStderr: () => undefined,
      onExit: (listener) => setTimeout(() => listener(null, 'SIGTERM'), 10),
      kill: () => {
        killed = true;
      }
    })
  });
  await explorer.refresh();

  const runPromise = explorer.run(
    { include: [controller.items[0]] },
    {
      isCancellationRequested: false,
      onCancellationRequested: (listener) => {
        cancelListener = listener;
        return { dispose: () => undefined };
      }
    }
  );
  await new Promise((resolve) => setTimeout(resolve, 0));
  cancelListener?.();
  await runPromise;

  assert.equal(killed, true);
  assert.deepEqual(controller.runs[0].events, [
    `started:${controller.items[0].id}`,
    `skipped:${controller.items[0].id}`,
    'end'
  ]);
});

function createExplorer(
  controller: FakeTestController,
  options: {
    manifests: Map<string, string>;
    calls?: Array<{ file: string; args: readonly string[] }>;
    output?: string[];
    startProcess?: TestControllerStartProcess;
  }
) {
  const calls = options.calls ?? [];
  const output = options.output ?? [];
  return createWorkbookBackedTestExplorer({
    controller,
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-devtool.exe'),
    workspaceRoots: [path.join('C:', 'work')],
    findProjectManifests: async () => [...options.manifests.keys()],
    readTextFile: async (filePath) => {
      const manifest = options.manifests.get(filePath);
      if (manifest === undefined) {
        throw new Error(`Missing fixture manifest: ${filePath}`);
      }

      return manifest;
    },
    capabilitiesProcess: async (file, args) => {
      calls.push({ file, args });
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: '1.0',
          commands: {
            test: { outputSchemaVersion: '1.0' }
          }
        }),
        stderr: ''
      };
    },
    startProcess: options.startProcess ?? ((file, args) => {
      calls.push({ file, args });
      return {
        onStdout: (listener) => listener('{"type":"summary","document":"Book1","total":1,"passed":1,"failed":0,"errors":0}\n'),
        onStderr: (listener) => listener(''),
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    }),
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    showErrorMessage: async () => undefined
  });
}

function manifestJson(projectName: string, documentNames: readonly string[]): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: documentNames[0],
    documents: Object.fromEntries(documentNames.map((documentName) => [
      documentName,
      {
        kind: 'excel',
        sourcePath: `src/${documentName}`,
        templatePath: `src/${documentName}/${documentName}.xlsm`,
        binPath: `bin/${documentName}/${documentName}.xlsm`,
        publishPath: `publish/${documentName}/${documentName}.xlsm`,
        commonModules: [],
        references: []
      }
    ]))
  });
}

type TestControllerStartProcess = Parameters<typeof createWorkbookBackedTestExplorer>[0]['startProcess'];

class FakeTestController implements TestControllerAdapter {
  public readonly items: FakeTestItem[] = [];
  public readonly runProfiles: Array<{ label: string; isDefault: boolean }> = [];
  public readonly runs: FakeTestRun[] = [];

  public createTestItem(id: string, label: string, uriPath?: string | undefined): TestExplorerItem {
    return new FakeTestItem(id, label, uriPath);
  }

  public replaceItems(items: readonly TestExplorerItem[]): void {
    this.items.splice(0, this.items.length, ...(items as FakeTestItem[]));
  }

  public createRunProfile(
    label: string,
    runHandler: (request: TestRunRequestLike, token: CommandCancellationToken) => Promise<void>,
    isDefault: boolean
  ): void {
    void runHandler;
    this.runProfiles.push({ label, isDefault });
  }

  public createTestRun(): FakeTestRun {
    const run = new FakeTestRun();
    this.runs.push(run);
    return run;
  }
}

class FakeTestItem implements TestExplorerItem {
  public readonly children = new FakeTestItemCollection();

  public constructor(
    public readonly id: string,
    public readonly label: string,
    public readonly uriPath?: string | undefined
  ) {
  }
}

class FakeTestItemCollection {
  public readonly items: FakeTestItem[] = [];

  public add(item: TestExplorerItem): void {
    this.items.push(item as FakeTestItem);
  }
}

class FakeTestRun {
  public readonly events: string[] = [];

  public started(item: TestExplorerItem): void {
    this.events.push(`started:${item.id}`);
  }

  public passed(item: TestExplorerItem): void {
    this.events.push(`passed:${item.id}`);
  }

  public failed(item: TestExplorerItem): void {
    this.events.push(`failed:${item.id}`);
  }

  public skipped(item: TestExplorerItem): void {
    this.events.push(`skipped:${item.id}`);
  }

  public appendOutput(): void {
  }

  public end(): void {
    this.events.push('end');
  }
}
