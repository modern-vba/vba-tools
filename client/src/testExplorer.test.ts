import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

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
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1', 'SecondBook'])]
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

test('Running a project node invokes vba-dev test ndjson with explicit project root', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const output: string[] = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    output,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
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
  assert.match(output.join(''), /"type":"runFinished"/);
});

test('Running a document node invokes vba-dev test ndjson with explicit project and document', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
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

test('a document run saves its dirty exported VBA source before starting vba-dev', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  const order: string[] = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    openTextDocuments: () => [
      {
        uriPath: sourcePath,
        isDirty: true,
        save: async () => {
          order.push(`save:${sourcePath}`);
          return true;
        }
      }
    ],
    startProcess: () => {
      order.push('process');
      return completedProcess();
    }
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  assert.deepEqual(order, [`save:${sourcePath}`, 'process']);
});

test('a failed required source save records a Test Run error without starting vba-dev', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  let processStarted = false;
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    openTextDocuments: () => [
      {
        uriPath: sourcePath,
        isDirty: true,
        save: async () => false
      }
    ],
    startProcess: () => {
      processStarted = true;
      return completedProcess();
    }
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  assert.equal(processStarted, false);
  assert.deepEqual(controller.runs[0].events, [
    `errored:${documentItem.id}:Could not save exported VBA source before the test run: ${sourcePath}`,
    'end'
  ]);
});

test('a throwing required source save records a Test Run error without starting vba-dev', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  let processStarted = false;
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    openTextDocuments: () => [
      {
        uriPath: sourcePath,
        isDirty: true,
        save: async () => {
          throw new Error('synthetic save failure');
        }
      }
    ],
    startProcess: () => {
      processStarted = true;
      return completedProcess();
    }
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  assert.equal(processStarted, false);
  assert.deepEqual(controller.runs[0].events, [
    `errored:${documentItem.id}:Could not save exported VBA source before the test run: ${sourcePath}`,
    'end'
  ]);
});

test('cancellation during a required source save records a Test Run error without starting vba-dev', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  let cancelled = false;
  let processStarted = false;
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    openTextDocuments: () => [
      {
        uriPath: sourcePath,
        isDirty: true,
        save: async () => {
          cancelled = true;
          return true;
        }
      }
    ],
    startProcess: () => {
      processStarted = true;
      return completedProcess();
    }
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];
  const token: CommandCancellationToken = {
    get isCancellationRequested() {
      return cancelled;
    },
    onCancellationRequested: () => ({ dispose: () => undefined })
  };

  await explorer.run({ include: [documentItem] }, token);

  assert.equal(processStarted, false);
  assert.deepEqual(controller.runs[0].events, [
    `errored:${documentItem.id}:Test run cancelled while saving exported VBA source: ${sourcePath}`,
    'end'
  ]);
});

test('a project run saves only dirty exported VBA sources in its document source sets', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const otherProjectRoot = path.join('C:', 'work', 'OtherProject');
  const book1Module = path.join(projectRoot, 'src', 'Book1', 'Test_One.bas');
  const book2Class = path.join(projectRoot, 'src', 'Book2', 'Nested', 'Test_Two.cls');
  const book2Form = path.join(projectRoot, 'src', 'Book2', 'Test_Form.frm');
  const saved: string[] = [];
  const document = (uriPath: string, isDirty = true) => ({
    uriPath,
    isDirty,
    save: async () => {
      saved.push(uriPath);
      return true;
    }
  });
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1', 'Book2'])]
    ]),
    openTextDocuments: () => [
      document(book1Module),
      document(book2Class),
      document(book2Form),
      document(path.join(projectRoot, 'src', 'Book1', 'Test_One.frx')),
      document(path.join(projectRoot, 'src', 'Book1', 'notes.txt')),
      document(path.join(projectRoot, 'src', 'Book1', 'Clean.bas'), false),
      document(path.join(otherProjectRoot, 'src', 'Book1', 'Outside.bas'))
    ]
  });
  await explorer.refresh();

  await explorer.run({ include: [controller.items[0]] }, uncancelledToken());

  assert.deepEqual(saved, [book1Module, book2Class, book2Form]);
});

test('document module and procedure selections save only the owning document source set', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const book1Source = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  const book2Source = path.join(projectRoot, 'src', 'Book2', 'Test_Other.bas');
  const saved: string[] = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1', 'Book2'])]
    ]),
    openTextDocuments: () => [book1Source, book2Source].map((uriPath) => ({
      uriPath,
      isDirty: true,
      save: async () => {
        saved.push(uriPath);
        return true;
      }
    })),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());
  assert.deepEqual(saved, [book1Source]);
  const moduleItem = documentItem.children.items[0];
  const procedureItem = moduleItem.children.items[0];

  saved.splice(0, saved.length);
  await explorer.run({ include: [moduleItem] }, uncancelledToken());
  assert.deepEqual(saved, [book1Source]);

  saved.splice(0, saved.length);
  await explorer.run({ include: [procedureItem] }, uncancelledToken());
  assert.deepEqual(saved, [book1Source]);
});

test('Test Explorer creates default and no-build run profiles', () => {
  const controller = new FakeTestController();
  createExplorer(controller, { manifests: new Map() });

  assert.deepEqual(controller.runProfiles.map(({ label, isDefault }) => ({ label, isDefault })), [
    { label: 'Run Tests', isDefault: true },
    { label: 'Run Tests Without Build', isDefault: false }
  ]);
});

test('No-build Test Explorer profile invokes vba-dev test without building', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ])
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await controller.runProfiles[1].runHandler({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--no-build', '--format', 'ndjson']
  ]);
});

test('testFinished events discover module and TestProcedure nodes and mark passed outcomes', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const moduleItem = documentItem.children.items[0];
  const procedureItem = moduleItem.children.items[0];
  assert.equal(moduleItem.label, 'Test_Module');
  assert.equal(procedureItem.label, 'Test_Passes');
  assert.ok(controller.runs[0].events.includes(`passed:${procedureItem.id}`));
});

test('an unavailable source location preserves the procedure result and writes a non-failing warning', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Missing_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  const moduleItem = documentItem.children.items[0];
  const procedureItem = moduleItem.children.items[0];
  assert.equal(moduleItem.label, 'Missing_Module');
  assert.equal(procedureItem.label, 'Test_Passes');
  assert.equal(procedureItem.uriPath, undefined);
  assert.equal(procedureItem.range, undefined);
  assert.ok(controller.runs[0].events.includes(`passed:${procedureItem.id}`));
  assert.deepEqual(controller.runs[0].outputs, [
    'Source location unavailable: Missing_Module.Test_Passes\n'
  ]);
  assert.equal(controller.runs[0].events.some((event) => event.startsWith('errored:')), false);
});

test('each unavailable location warns without replacing passed or failed procedure outcomes', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const errorMessages: string[] = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson(
      {
        type: 'testFinished',
        document: 'Book1',
        module: 'Test_Module',
        procedure: 'Test_Passes',
        outcome: 'passed',
        message: ''
      },
      {
        type: 'testFinished',
        document: 'Book1',
        module: 'Test_Module',
        procedure: 'Test_Fails',
        outcome: 'failed',
        message: 'Expected 1 but was 2'
      }
    ),
    exitCode: 1,
    errorMessages
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  const moduleItem = documentItem.children.items[0];
  const passedItem = moduleItem.children.items[0];
  const failedItem = moduleItem.children.items[1];
  assert.equal(moduleItem.uriPath, undefined);
  assert.equal(moduleItem.range, undefined);
  assert.equal(passedItem.uriPath, undefined);
  assert.equal(failedItem.uriPath, undefined);
  assert.ok(controller.runs[0].events.includes(`passed:${passedItem.id}`));
  assert.ok(controller.runs[0].events.includes(
    `failed:${failedItem.id}:Expected 1 but was 2`));
  assert.deepEqual(controller.runs[0].outputs, [
    'Source location unavailable: Test_Module.Test_Passes\n',
    'Source location unavailable: Test_Module.Test_Fails\n'
  ]);
  assert.equal(controller.runs[0].events.some((event) => event.startsWith('errored:')), false);
  assert.deepEqual(errorMessages, []);
});

test('canonical source ranges are projected only onto TestProcedure nodes', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: '',
      location: {
        uri: pathToFileURL(sourcePath).href,
        range: {
          start: { line: 3, character: 11 },
          end: { line: 3, character: 22 }
        }
      }
    })
  });
  await explorer.refresh();
  const projectItem = controller.items[0];
  const documentItem = projectItem.children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const moduleItem = documentItem.children.items[0];
  const procedureItem = moduleItem.children.items[0];
  assert.equal(procedureItem.uriPath, sourcePath);
  assert.deepEqual(procedureItem.range, {
    start: { line: 3, character: 11 },
    end: { line: 3, character: 22 }
  });
  assert.equal(projectItem.range, undefined);
  assert.equal(documentItem.range, undefined);
  assert.equal(moduleItem.range, undefined);
});

test('canonical locations from every supported source encoding project onto procedures', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const cases = [
    {
      module: 'テストモジュール',
      procedure: 'Test_Run',
      sourcePath: path.join(projectRoot, 'src', 'Book1', 'Cp932.bas'),
      range: { start: { line: 2, character: 11 }, end: { line: 2, character: 19 } }
    },
    {
      module: 'test_module',
      procedure: 'scenario_multi',
      sourcePath: path.join(projectRoot, 'src', 'Book1', 'nested', 'Test_Module.bas'),
      range: { start: { line: 1, character: 11 }, end: { line: 1, character: 25 } }
    },
    {
      module: 'preferred_module',
      procedure: 'TEST_UTF16',
      sourcePath: path.join(projectRoot, 'src', 'Book1', 'Utf16.bas'),
      range: { start: { line: 2, character: 11 }, end: { line: 2, character: 21 } }
    }
  ];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson(...cases.map((item) => ({
      type: 'testFinished',
      document: 'Book1',
      module: item.module,
      procedure: item.procedure,
      outcome: 'passed',
      message: '',
      location: {
        uri: pathToFileURL(item.sourcePath).href,
        range: item.range
      }
    })))
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, uncancelledToken());

  assert.equal(documentItem.children.items.length, cases.length);
  cases.forEach((expected, index) => {
    const moduleItem = documentItem.children.items[index];
    const procedureItem = moduleItem.children.items[0];
    assert.equal(moduleItem.uriPath, undefined);
    assert.equal(moduleItem.range, undefined);
    assert.equal(procedureItem.uriPath, expected.sourcePath);
    assert.deepEqual(procedureItem.range, expected.range);
    assert.ok(controller.runs[0].events.includes(`passed:${procedureItem.id}`));
  });
});

test('legacy result records are ignored by Test Explorer event projection', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'result',
      document: 'Book1',
      category: 'Test_Module',
      testName: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(documentItem.children.items, []);
  assert.deepEqual(controller.runs[0].events, [
    `started:${documentItem.id}`,
    `passed:${documentItem.id}`,
    'end'
  ]);
});

test('Running a discovered module node invokes vba-dev test with module selector', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];
  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });
  const moduleItem = documentItem.children.items[0];
  calls.splice(0, calls.length);
  controller.runs.splice(0, controller.runs.length);

  await explorer.run({ include: [moduleItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--module', 'Test_Module', '--format', 'ndjson']
  ]);
});

test('Running a discovered procedure node invokes vba-dev test with module and procedure selectors', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];
  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });
  const moduleItem = documentItem.children.items[0];
  const procedureItem = moduleItem.children.items[0];
  calls.splice(0, calls.length);
  controller.runs.splice(0, controller.runs.length);

  await explorer.run({ include: [procedureItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--module', 'Test_Module', '--procedure', 'Test_Passes', '--format', 'ndjson']
  ]);
});

test('No-build Test Explorer profile preserves module and procedure selectors', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Passes',
      outcome: 'passed',
      message: ''
    })
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];
  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });
  const procedureItem = documentItem.children.items[0].children.items[0];
  calls.splice(0, calls.length);
  controller.runs.splice(0, controller.runs.length);

  await controller.runProfiles[1].runHandler({ include: [procedureItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--module', 'Test_Module', '--procedure', 'Test_Passes', '--no-build', '--format', 'ndjson']
  ]);
});

test('testStarted events update known TestProcedure running state', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  let runIndex = 0;
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    startProcess: () => {
      const stdout = runIndex++ === 0
        ? ndjson({
          type: 'testFinished',
          document: 'Book1',
          module: 'Test_Module',
          procedure: 'Test_Passes',
          outcome: 'passed',
          message: ''
        })
        : ndjson(
          {
            type: 'testStarted',
            document: 'Book1',
            module: 'Test_Module',
            procedure: 'Test_Passes'
          },
          {
            type: 'testFinished',
            document: 'Book1',
            module: 'Test_Module',
            procedure: 'Test_Passes',
            outcome: 'passed',
            message: ''
          }
        );

      return {
        onStdout: (listener) => listener(stdout),
        onStderr: (listener) => listener(''),
        onExit: (listener) => listener(0, null),
        kill: () => undefined
      };
    }
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];
  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  controller.runs.splice(0, controller.runs.length);
  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const procedureItem = documentItem.children.items[0].children.items[0];
  assert.ok(controller.runs[0].events.includes(`started:${procedureItem.id}`));
});

test('failed testFinished outcomes mark TestProcedure failed with message and source location', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Fails',
      outcome: 'failed',
      message: 'Expected 1 but was 2',
      location: {
        file: sourcePath,
        line: 12,
        character: 4
      }
    }),
    exitCode: 1
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const procedureItem = documentItem.children.items[0].children.items[0];
  assert.deepEqual(procedureItem.range, {
    start: { line: 12, character: 4 },
    end: { line: 12, character: 4 }
  });
  assert.ok(controller.runs[0].events.includes(`failed:${procedureItem.id}:Expected 1 but was 2:${sourcePath}:12:4`));
});

test('failed TestProcedure items and messages share the canonical declaration range', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'Test_Module.bas');
  const range = {
    start: { line: 3, character: 11 },
    end: { line: 3, character: 21 }
  };
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stdout: ndjson({
      type: 'testFinished',
      document: 'Book1',
      module: 'Test_Module',
      procedure: 'Test_Fails',
      outcome: 'failed',
      message: 'Expected 1 but was 2',
      location: {
        uri: pathToFileURL(sourcePath).href,
        range
      }
    }),
    exitCode: 1
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await explorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const procedureItem = documentItem.children.items[0].children.items[0];
  assert.deepEqual(procedureItem.range, range);
  assert.deepEqual(controller.runs[0].failureLocations, [
    {
      itemId: procedureItem.id,
      location: { uriPath: sourcePath, range }
    }
  ]);
});

test('CLI command failures are reported as project-level or document-level TestRunError', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const projectController = new FakeTestController();
  const projectExplorer = createExplorer(projectController, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stderr: 'Build failed\n',
    exitCode: 1
  });
  await projectExplorer.refresh();
  await projectExplorer.run({ include: [projectController.items[0]] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  const documentController = new FakeTestController();
  const documentExplorer = createExplorer(documentController, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stderr: 'Reference was not found\n',
    exitCode: 1
  });
  await documentExplorer.refresh();
  const documentItem = documentController.items[0].children.items[0];
  await documentExplorer.run({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.ok(projectController.runs[0].events.includes(`errored:${projectController.items[0].id}:Build failed`));
  assert.ok(documentController.runs[0].events.includes(`errored:${documentItem.id}:Reference was not found`));
});

test('No-build Test Explorer profile reports unusable generated output as TestRunError', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const calls: Array<{ file: string; args: readonly string[] }> = [];
  const controller = new FakeTestController();
  const explorer = createExplorer(controller, {
    calls,
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
    ]),
    stderr: 'Bin workbook was not found\n',
    exitCode: 1
  });
  await explorer.refresh();
  const documentItem = controller.items[0].children.items[0];

  await controller.runProfiles[1].runHandler({ include: [documentItem] }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) });

  assert.deepEqual(calls.map((call) => call.args), [
    ['capabilities', '--format', 'json'],
    ['test', '--project', projectRoot, '--document', 'Book1', '--no-build', '--format', 'ndjson']
  ]);
  assert.ok(controller.runs[0].events.includes(`errored:${documentItem.id}:Bin workbook was not found`));
});

test('Cancelled no-build Test Explorer runs terminate the spawned CLI process', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  let killed = false;
  let cancelListener: (() => void) | undefined;
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
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

  const runPromise = controller.runProfiles[1].runHandler(
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
    `cancelled:${controller.items[0].id}`,
    'end'
  ]);
});

test('Cancelled VS Code test runs terminate the spawned CLI process', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const controller = new FakeTestController();
  let killed = false;
  let cancelListener: (() => void) | undefined;
  const explorer = createExplorer(controller, {
    manifests: new Map([
      [path.join(projectRoot, 'vba-project.json'), manifestJson('BookProject', ['Book1'])]
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
    `cancelled:${controller.items[0].id}`,
    'end'
  ]);
});

function createExplorer(
  controller: FakeTestController,
  options: {
    manifests: Map<string, string>;
    calls?: Array<{ file: string; args: readonly string[] }>;
    output?: string[];
    stdout?: string;
    stderr?: string;
    exitCode?: number;
    startProcess?: TestControllerStartProcess;
    openTextDocuments?: () => readonly {
      uriPath: string;
      isDirty: boolean;
      save(): Promise<boolean>;
    }[];
    errorMessages?: string[];
  }
) {
  const calls = options.calls ?? [];
  const output = options.output ?? [];
  return createWorkbookBackedTestExplorer({
    controller,
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    configuredDevToolPath: path.join('D:', 'tools', 'vba-dev.exe'),
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
            test: { outputSchemaVersion: '1.2' }
          }
        }),
        stderr: ''
      };
    },
    requiredContract: {
      contractVersion: '1.0',
      commandSchemaVersions: {
        test: '1.2'
      }
    },
    startProcess: options.startProcess ?? ((file, args) => {
      calls.push({ file, args });
      return {
        onStdout: (listener) => listener(options.stdout ?? '{"type":"runStarted","project":"BookProject","document":"Book1"}\n{"type":"runFinished","project":"BookProject","document":"Book1","outcome":"passed","total":0,"passed":0,"failed":0,"errors":0}\n'),
        onStderr: (listener) => listener(options.stderr ?? ''),
        onExit: (listener) => listener(options.exitCode ?? 0, null),
        kill: () => undefined
      };
    }),
    outputChannel: {
      append: (value) => output.push(value),
      appendLine: (value) => output.push(`${value}\n`),
      show: () => undefined
    },
    openTextDocuments: options.openTextDocuments ?? (() => []),
    showErrorMessage: async (message) => {
      options.errorMessages?.push(message);
    }
  });
}

function completedProcess() {
  return {
    onStdout: (listener: (value: string) => void) => listener(
      '{"type":"runFinished","project":"BookProject","document":"Book1","outcome":"passed","total":0,"passed":0,"failed":0,"errors":0}\n'
    ),
    onStderr: (listener: (value: string) => void) => listener(''),
    onExit: (listener: (exitCode: number | null, signal: NodeJS.Signals | null) => void) => listener(0, null),
    kill: () => undefined
  };
}

function uncancelledToken(): CommandCancellationToken {
  return {
    isCancellationRequested: false,
    onCancellationRequested: () => ({ dispose: () => undefined })
  };
}

function ndjson(...records: readonly Record<string, unknown>[]): string {
  return records.map((record) => JSON.stringify(record)).join('\n') + '\n';
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
        binPath: `bin/${documentName}.xlsm`,
        publishPath: `publish/${documentName}.xlsm`,
        commonModules: [],
        references: []
      }
    ]))
  });
}

type TestControllerStartProcess = Parameters<typeof createWorkbookBackedTestExplorer>[0]['startProcess'];

class FakeTestController implements TestControllerAdapter {
  public readonly items: FakeTestItem[] = [];
  public readonly runProfiles: Array<{
    label: string;
    runHandler: (request: TestRunRequestLike, token: CommandCancellationToken) => Promise<void>;
    isDefault: boolean;
  }> = [];
  public readonly runs: FakeTestRun[] = [];

  public createTestItem(
    id: string,
    label: string,
    uriPath?: string | undefined,
    range?: {
      start: { line: number; character: number };
      end: { line: number; character: number };
    } | undefined
  ): TestExplorerItem {
    return new FakeTestItem(id, label, uriPath, range);
  }

  public replaceItems(items: readonly TestExplorerItem[]): void {
    this.items.splice(0, this.items.length, ...(items as FakeTestItem[]));
  }

  public createRunProfile(
    label: string,
    runHandler: (request: TestRunRequestLike, token: CommandCancellationToken) => Promise<void>,
    isDefault: boolean
  ): void {
    this.runProfiles.push({ label, runHandler, isDefault });
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
    public readonly uriPath?: string | undefined,
    public readonly range?: {
      start: { line: number; character: number };
      end: { line: number; character: number };
    } | undefined
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
  public readonly failureLocations: Array<{ itemId: string; location: unknown }> = [];
  public readonly outputs: string[] = [];

  public started(item: TestExplorerItem): void {
    this.events.push(`started:${item.id}`);
  }

  public passed(item: TestExplorerItem): void {
    this.events.push(`passed:${item.id}`);
  }

  public failed(
    item: TestExplorerItem,
    message = '',
    location?: {
      uriPath: string;
      range: {
        start: { line: number; character: number };
        end: { line: number; character: number };
      };
    } | undefined
  ): void {
    if (location) {
      this.failureLocations.push({ itemId: item.id, location });
      this.events.push(
        `failed:${item.id}:${message}:${location.uriPath}:${location.range.start.line}:${location.range.start.character}`
      );
      return;
    }

    this.events.push(message.length > 0 ? `failed:${item.id}:${message}` : `failed:${item.id}`);
  }

  public errored(item: TestExplorerItem, message: string): void {
    this.events.push(`errored:${item.id}:${message.trim()}`);
  }

  public cancelled(item: TestExplorerItem): void {
    this.events.push(`cancelled:${item.id}`);
  }

  public appendOutput(output: string): void {
    if (output.startsWith('Source location unavailable:')) {
      this.outputs.push(output);
    }
  }

  public end(): void {
    this.events.push('end');
  }
}
