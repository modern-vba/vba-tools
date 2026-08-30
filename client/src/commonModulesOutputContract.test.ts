import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  CommonModulesMutationOutputContractError,
  parseCommonModulesMutationOutput
} from './commonModulesOutputContract';

const projectRoot = path.join('C:', 'work', 'BookProject');

test('CommonModules Add output accepts an exhaustive dependency-expanded result', () => {
  const output = parseCommonModulesMutationOutput(
    JSON.stringify(createAddOutput()),
    projectRoot,
    'Book2',
    'add',
    ['Feature']
  );

  assert.equal(output.operation, 'add');
  assert.equal(output.document, 'Book2');
  assert.deepEqual(output.documents[0]?.modules.map((module) => module.name), [
    'Base',
    'Feature'
  ]);
  assert.deepEqual(output.documents[0]?.referenceChanges, [{
    kind: 'added',
    name: 'Microsoft Scripting Runtime',
    requested: false
  }]);
});

test('CommonModules Update output accepts an empty project-wide target set', () => {
  const candidate = createUpdateOutput();
  candidate.documents = [];
  candidate.warnings = [];

  const output = parseCommonModulesMutationOutput(
    JSON.stringify(candidate),
    projectRoot,
    null,
    'update',
    []
  );

  assert.deepEqual(output.documents, []);
});

test('CommonModules output accepts additive warning codes without using them as outcomes', () => {
  const candidate = createAddOutput();
  candidate.warnings = [{ code: 'futureWarning', message: 'Future detail.' }];

  const output = parseCommonModulesMutationOutput(
    JSON.stringify(candidate),
    projectRoot,
    'Book2',
    'add',
    ['Feature']
  );

  assert.deepEqual(output.warnings, [{ code: 'futureWarning', message: 'Future detail.' }]);
});

test('CommonModules output accepts CP2 module identities and rejects Unicode-folded extensions', () => {
  const cp2Name = '\u00a0';
  const cp2 = createAddOutput();
  const cp2Module = documentOf(cp2).modules[1]!;
  cp2Module.name = cp2Name;
  cp2Module.moduleFile = `${cp2Name}.bas`;
  cp2Module.changes[0].sourceSetRelativePath = `common-modules/${cp2Name}.bas`;

  const output = parseCommonModulesMutationOutput(
    JSON.stringify(cp2),
    projectRoot,
    'Book2',
    'add',
    [cp2Name]
  );
  assert.equal(output.documents[0]?.modules[1]?.name, cp2Name);

  const unicodeFolded = createAddOutput();
  documentOf(unicodeFolded).modules[1]!.moduleFile = 'Feature.ba\u017f';
  documentOf(unicodeFolded).modules[1]!.changes[0].sourceSetRelativePath =
    'common-modules/Feature.ba\u017f';
  assertRejectedAdd(unicodeFolded);
});

test('CommonModules output rejects untrusted envelopes and request mismatches', () => {
  const cases: Array<{
    name: string;
    mutate(candidate: Record<string, unknown>): void;
  }> = [
    { name: 'schema', mutate: (candidate) => { candidate.schemaVersion = '2.0'; } },
    { name: 'scope', mutate: (candidate) => { candidate.scope = 'document'; } },
    { name: 'project', mutate: (candidate) => { candidate.project = `${projectRoot}-other`; } },
    { name: 'document', mutate: (candidate) => { candidate.document = 'Book1'; } },
    { name: 'operation', mutate: (candidate) => { candidate.operation = 'update'; } },
    { name: 'complete', mutate: (candidate) => { candidate.complete = false; } },
    { name: 'missing warnings', mutate: (candidate) => { delete candidate.warnings; } },
    { name: 'missing documents', mutate: (candidate) => { delete candidate.documents; } }
  ];

  for (const testCase of cases) {
    const candidate = clone(createAddOutput());
    testCase.mutate(candidate);
    assert.throws(
      () => parseCommonModulesMutationOutput(
        JSON.stringify(candidate),
        projectRoot,
        'Book2',
        'add',
        ['Feature']
      ),
      CommonModulesMutationOutputContractError,
      testCase.name
    );
  }
});

test('CommonModules output enforces closed objects at every schema level', () => {
  const mutations: Array<(candidate: Record<string, unknown>) => void> = [
    (candidate) => { candidate.future = true; },
    (candidate) => { warningOf(candidate).future = true; },
    (candidate) => { documentOf(candidate).future = true; },
    (candidate) => { moduleOf(candidate).future = true; },
    (candidate) => { changeOf(candidate).future = true; },
    (candidate) => { referenceChangeOf(candidate).future = true; }
  ];

  for (const mutate of mutations) {
    const candidate = clone(createAddOutput());
    candidate.warnings = [{ code: 'futureWarning', message: 'Future detail.' }];
    mutate(candidate);
    assert.throws(() => parseCommonModulesMutationOutput(
      JSON.stringify(candidate),
      projectRoot,
      'Book2',
      'add',
      ['Feature']
    ), CommonModulesMutationOutputContractError);
  }
});

test('CommonModules output validates document ordering and unique identities', () => {
  const duplicateDocument = createUpdateOutput();
  duplicateDocument.documents = [
    ...duplicateDocument.documents,
    clone(duplicateDocument.documents[0]!)
  ];
  assertRejectedUpdate(duplicateDocument);

  const wrongDocumentOrder = createUpdateOutput();
  wrongDocumentOrder.documents = [...wrongDocumentOrder.documents].reverse();
  assertRejectedUpdate(wrongDocumentOrder);

  const duplicateModuleName = createAddOutput();
  documentOf(duplicateModuleName).modules.push({
    ...moduleOf(duplicateModuleName),
    moduleFile: 'Other.bas'
  });
  assertRejectedAdd(duplicateModuleName);

  const duplicateModuleFile = createAddOutput();
  documentOf(duplicateModuleFile).modules.push({
    ...moduleOf(duplicateModuleFile),
    name: 'Other'
  });
  assertRejectedAdd(duplicateModuleFile);
});

test('CommonModules output validates fixed change kinds, payloads, order, and status', () => {
  const invalidCases: Array<(candidate: Record<string, unknown>) => void> = [
    (candidate) => { changeOf(candidate).kind = 'copied'; },
    (candidate) => { delete changeOf(candidate).sourceSetRelativePath; },
    (candidate) => { changeOf(candidate).testOnly = false; },
    (candidate) => { moduleOf(candidate).status = 'unchanged'; },
    (candidate) => { moduleOf(candidate).changes = []; },
    (candidate) => {
      moduleOf(candidate).changes = [
        { kind: 'directRequestPromoted' },
        { kind: 'sourceUpdated', sourceSetRelativePath: 'common-modules/Base.bas' }
      ];
    },
    (candidate) => {
      moduleOf(candidate).changes = [
        { kind: 'installed', sourceSetRelativePath: 'common-modules/Base.bas' },
        { kind: 'directRequestPromoted' }
      ];
    }
  ];

  for (const mutate of invalidCases) {
    const candidate = clone(createAddOutput());
    mutate(candidate);
    assertRejectedAdd(candidate);
  }

  const unchangedWithChanges = createUpdateOutput();
  const unchanged = documentOf(unchangedWithChanges).modules[1]!;
  unchanged.changes = [{ kind: 'sourceUpdated', sourceSetRelativePath: 'Feature.bas' }];
  assertRejectedUpdate(unchangedWithChanges);

  const changedWithoutChanges = createUpdateOutput();
  const changed = documentOf(changedWithoutChanges).modules[1]!;
  changed.status = 'changed';
  assertRejectedUpdate(changedWithoutChanges);

  for (const updateOnlyChange of [
    { kind: 'sourceUpdated', sourceSetRelativePath: 'common-modules/Base.bas' },
    { kind: 'testOnlyChanged', testOnly: false },
    { kind: 'orphanedChanged', orphaned: false }
  ]) {
    const addWithUpdateChange = createAddOutput();
    moduleOf(addWithUpdateChange).changes = [updateOnlyChange];
    assertRejectedAdd(addWithUpdateChange);
  }

  const requestedUpdateDependency = createUpdateOutput();
  const installedDependency = moduleOf(requestedUpdateDependency);
  installedDependency.requested = true;
  installedDependency.changes = [{
    kind: 'installed',
    sourceSetRelativePath: 'common-modules/Base.bas'
  }];
  assertRejectedUpdate(requestedUpdateDependency);
});

test('CommonModules output validates change payload consistency with final metadata', () => {
  const candidate = createUpdateOutput();
  const module = moduleOf(candidate);
  module.changes = [
    { kind: 'sourceUpdated', sourceSetRelativePath: 'common-modules/Base.bas' },
    { kind: 'testOnlyChanged', testOnly: true },
    { kind: 'orphanedChanged', orphaned: false }
  ];
  module.testOnly = false;
  assertRejectedUpdate(candidate);

  const wrongOrphan = createUpdateOutput();
  const orphan = moduleOf(wrongOrphan);
  orphan.changes = [{ kind: 'orphanedChanged', orphaned: false }];
  orphan.orphaned = true;
  assertRejectedUpdate(wrongOrphan);

  const updatePromotion = createUpdateOutput();
  const promoted = moduleOf(updatePromotion);
  promoted.requested = true;
  promoted.changes = [{ kind: 'directRequestPromoted' }];
  assertRejectedUpdate(updatePromotion);

  const sourceUpdatedOrphan = createUpdateOutput();
  const updatedOrphan = moduleOf(sourceUpdatedOrphan);
  updatedOrphan.orphaned = true;
  updatedOrphan.changes = [
    { kind: 'sourceUpdated', sourceSetRelativePath: 'common-modules/Base.bas' },
    { kind: 'orphanedChanged', orphaned: true }
  ];
  assertRejectedUpdate(sourceUpdatedOrphan);

  const reclassifiedOrphan = createUpdateOutput();
  const classifiedOrphan = moduleOf(reclassifiedOrphan);
  classifiedOrphan.orphaned = true;
  classifiedOrphan.testOnly = true;
  classifiedOrphan.changes = [
    { kind: 'testOnlyChanged', testOnly: true },
    { kind: 'orphanedChanged', orphaned: true }
  ];
  assertRejectedUpdate(reclassifiedOrphan);
});

test('CommonModules output ties orphan-retention warnings to final orphaned modules', () => {
  const missingWarning = createUpdateOutput();
  missingWarning.warnings = [];
  assertRejectedUpdate(missingWarning);

  const unexpectedWarning = createAddOutput();
  unexpectedWarning.warnings = [{
    code: 'orphanedCommonModulesRetained',
    message: 'Unexpected orphan warning.'
  }];
  assertRejectedAdd(unexpectedWarning);
});

test('CommonModules output requires source changes when cancellation was deferred', () => {
  const candidate = createAddOutput();
  for (const module of documentOf(candidate).modules) {
    module.status = 'unchanged';
    module.changes = [];
  }
  candidate.warnings = [{
    code: 'cancellationDeferred',
    message: 'Cancellation was deferred through commit.'
  }];

  assertRejectedAdd(candidate);
});

test('CommonModules Add output proves every explicit request is a final requested module', () => {
  const missingRequest = createAddOutput();
  documentOf(missingRequest).modules = [moduleOf(missingRequest)];
  assertRejectedAdd(missingRequest);

  const notRequested = createAddOutput();
  documentOf(notRequested).modules[1]!.requested = false;
  assertRejectedAdd(notRequested);

  const duplicateRequests = parseCommonModulesMutationOutput(
    JSON.stringify(createAddOutput()),
    projectRoot,
    'Book2',
    'add',
    ['Feature', 'feature']
  );
  assert.equal(duplicateRequests.documents[0]?.modules[1]?.name, 'Feature');

  const fileNameRequest = parseCommonModulesMutationOutput(
    JSON.stringify(createAddOutput()),
    projectRoot,
    'Book2',
    'add',
    ['Feature.bas']
  );
  assert.equal(fileNameRequest.documents[0]?.modules[1]?.moduleFile, 'Feature.bas');

  const promotedDependency = createAddOutput();
  const dependencyPromotion = moduleOf(promotedDependency);
  dependencyPromotion.requested = true;
  dependencyPromotion.changes = [{ kind: 'directRequestPromoted' }];
  assertRejectedAdd(promotedDependency);

  const directlyInstalledDependency = createAddOutput();
  moduleOf(directlyInstalledDependency).requested = true;
  assertRejectedAdd(directlyInstalledDependency);
});

test('CommonModules Update output rejects a target without installed modules', () => {
  const candidate = createUpdateOutput();
  documentOf(candidate).modules = [];

  assertRejectedUpdate(candidate);
});

test('CommonModules output validates reference additions and source-set-relative paths', () => {
  const invalidReferences: Array<(reference: Record<string, unknown>) => void> = [
    (reference) => { reference.kind = 'promoted'; },
    (reference) => { reference.requested = true; },
    (reference) => { reference.name = ' Reference'; }
  ];
  for (const mutate of invalidReferences) {
    const candidate = clone(createAddOutput());
    mutate(referenceChangeOf(candidate));
    assertRejectedAdd(candidate);
  }

  const duplicateReference = createAddOutput();
  documentOf(duplicateReference).referenceChanges.push({
    kind: 'added',
    name: 'microsoft scripting runtime',
    requested: false
  });
  assertRejectedAdd(duplicateReference);

  for (const invalidPath of [
    'C:/common-modules/Base.bas',
    '../Base.bas',
    'common-modules\\Base.bas',
    'common-modules/Other.bas'
  ]) {
    const candidate = clone(createAddOutput());
    changeOf(candidate).sourceSetRelativePath = invalidPath;
    assertRejectedAdd(candidate);
  }
});

function createAddOutput(): Record<string, any> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: 'Book2',
    operation: 'add',
    complete: true,
    warnings: [],
    documents: [{
      document: 'Book2',
      modules: [
        {
          name: 'Base',
          moduleFile: 'Base.bas',
          requested: false,
          testOnly: false,
          orphaned: false,
          status: 'changed',
          changes: [{
            kind: 'installed',
            sourceSetRelativePath: 'common-modules/Base.bas'
          }]
        },
        {
          name: 'Feature',
          moduleFile: 'Feature.bas',
          requested: true,
          testOnly: false,
          orphaned: false,
          status: 'changed',
          changes: [{
            kind: 'installed',
            sourceSetRelativePath: 'common-modules/Feature.bas'
          }]
        }
      ],
      referenceChanges: [{
        kind: 'added',
        name: 'Microsoft Scripting Runtime',
        requested: false
      }]
    }]
  };
}

function createUpdateOutput(): Record<string, any> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: null,
    operation: 'update',
    complete: true,
    warnings: [{
      code: 'orphanedCommonModulesRetained',
      message: 'Retained 1 orphaned CommonModules entry across 1 document.'
    }],
    documents: [
      {
        document: 'Book1',
        modules: [
          {
            name: 'Base',
            moduleFile: 'Base.bas',
            requested: false,
            testOnly: false,
            orphaned: false,
            status: 'changed',
            changes: [{
              kind: 'sourceUpdated',
              sourceSetRelativePath: 'common-modules/Base.bas'
            }]
          },
          {
            name: 'Feature',
            moduleFile: 'Feature.bas',
            requested: true,
            testOnly: false,
            orphaned: false,
            status: 'unchanged',
            changes: []
          }
        ],
        referenceChanges: []
      },
      {
        document: 'Book2',
        modules: [{
          name: 'Other',
          moduleFile: 'Other.cls',
          requested: true,
          testOnly: true,
          orphaned: true,
          status: 'unchanged',
          changes: []
        }],
        referenceChanges: []
      }
    ]
  };
}

function documentOf(candidate: Record<string, any>): Record<string, any> {
  return candidate.documents[0]!;
}

function moduleOf(candidate: Record<string, any>): Record<string, any> {
  return documentOf(candidate).modules[0]!;
}

function changeOf(candidate: Record<string, any>): Record<string, any> {
  return moduleOf(candidate).changes[0]!;
}

function referenceChangeOf(candidate: Record<string, any>): Record<string, any> {
  return documentOf(candidate).referenceChanges[0]!;
}

function warningOf(candidate: Record<string, any>): Record<string, any> {
  return candidate.warnings[0]!;
}

function clone<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}

function assertRejectedAdd(candidate: Record<string, unknown>): void {
  assert.throws(() => parseCommonModulesMutationOutput(
    JSON.stringify(candidate),
    projectRoot,
    'Book2',
    'add',
    ['Feature']
  ), CommonModulesMutationOutputContractError);
}

function assertRejectedUpdate(candidate: Record<string, unknown>): void {
  assert.throws(() => parseCommonModulesMutationOutput(
    JSON.stringify(candidate),
    projectRoot,
    null,
    'update',
    []
  ), CommonModulesMutationOutputContractError);
}
