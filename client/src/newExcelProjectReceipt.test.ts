import assert from 'node:assert/strict';
import * as path from 'node:path';
import test from 'node:test';

import {
  NewExcelProjectReceiptError,
  parseNewExcelProjectReceipt
} from './newExcelProjectReceipt';

const projectName = 'ExampleProject';
const projectRoot = String.raw`C:\work\ExampleProject`;
const manifestPath = String.raw`C:\work\ExampleProject\vba-project.json`;

test('rejects malformed JSON and values other than one receipt object', () => {
  for (const stdout of ['', '{', 'null', '[]', '1', '{}']) {
    assert.throws(
      () => parseNewExcelProjectReceipt(stdout, { projectName, projectRoot }),
      NewExcelProjectReceiptError,
      stdout
    );
  }
});

test('accepts a request-matching complete receipt without a CommonModules repository', () => {
  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(createReceipt()),
    { projectName, projectRoot }
  );

  assert.equal(receipt.project, projectRoot);
  assert.equal(receipt.document, projectName);
  assert.equal(receipt.manifest.projectName, projectName);
  assert.equal(receipt.manifest.documents[projectName]?.kind, 'excel');
  assert.deepEqual(receipt.warnings.map((warning) => warning.code), [
    'commonModulesRepositoryNotFound'
  ]);
});

test('rejects a receipt with the wrong command schema version', () => {
  const candidate = createReceipt();
  candidate.schemaVersion = '2.0';

  assert.throws(
    () => parseNewExcelProjectReceipt(
      JSON.stringify(candidate),
      { projectName, projectRoot }
    ),
    /schemaVersion/
  );
});

test('requires the fixed command discriminants and invocation context', () => {
  const cases: ReadonlyArray<{
    readonly property: string;
    readonly value: unknown;
  }> = [
    { property: 'scope', value: 'environment' },
    { property: 'project', value: String.raw`C:\work\OtherProject` },
    { property: 'document', value: 'OtherProject' },
    { property: 'operation', value: 'update' },
    { property: 'template', value: 'word' },
    { property: 'complete', value: false },
    { property: 'manifestPath', value: String.raw`C:\work\vba-project.json` }
  ];

  for (const testCase of cases) {
    const candidate = createReceipt();
    candidate[testCase.property] = testCase.value;

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      new RegExp(testCase.property),
      testCase.property
    );
  }
});

test('rejects every omitted required envelope property', () => {
  for (const property of [
    'schemaVersion',
    'scope',
    'project',
    'document',
    'operation',
    'template',
    'complete',
    'warnings',
    'manifestPath',
    'manifest'
  ]) {
    const candidate = createReceipt();
    delete candidate[property];

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      NewExcelProjectReceiptError,
      property
    );
  }
});

test('requires normalized absolute Windows paths for the request and receipt context', () => {
  const noncanonicalProject = createReceipt();
  noncanonicalProject.project = String.raw`C:\work\segment\..\ExampleProject`;
  assert.throws(
    () => parseNewExcelProjectReceipt(
      JSON.stringify(noncanonicalProject),
      { projectName, projectRoot }
    ),
    NewExcelProjectReceiptError
  );

  const noncanonicalManifest = createReceipt();
  noncanonicalManifest.manifestPath = 'C:/work/ExampleProject/vba-project.json';
  assert.throws(
    () => parseNewExcelProjectReceipt(
      JSON.stringify(noncanonicalManifest),
      { projectName, projectRoot }
    ),
    NewExcelProjectReceiptError
  );

  assert.throws(
    () => parseNewExcelProjectReceipt(
      JSON.stringify(createReceipt()),
      {
        projectName,
        projectRoot: String.raw`C:\work\segment\..\ExampleProject`
      }
    ),
    NewExcelProjectReceiptError
  );
});

test('matches receipt paths with .NET OrdinalIgnoreCase semantics', () => {
  const requestedRoot = String.raw`C:\Μ\ExampleProject`;
  const returnedRoot = String.raw`C:\µ\ExampleProject`;
  const candidate = createReceipt();
  candidate.project = returnedRoot;
  candidate.manifestPath = path.win32.join(returnedRoot, 'vba-project.json');

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot: requestedRoot }
  );

  assert.equal(receipt.project, returnedRoot);
});

test('rejects an unknown nested manifest property', () => {
  const candidate = createReceipt();
  manifestOf(candidate).unexpected = true;

  assert.throws(
    () => parseNewExcelProjectReceipt(
      JSON.stringify(candidate),
      { projectName, projectRoot }
    ),
    /manifest.*unexpected/
  );
});

test('requires the exact initial manifest identity and sole Excel document', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly mutate: (candidate: Record<string, unknown>) => void;
  }> = [
    {
      name: 'schema version',
      mutate: (candidate) => { manifestOf(candidate).schemaVersion = 2; }
    },
    {
      name: 'project name',
      mutate: (candidate) => { manifestOf(candidate).projectName = 'OtherProject'; }
    },
    {
      name: 'primary document',
      mutate: (candidate) => { manifestOf(candidate).primaryDocument = 'OtherProject'; }
    },
    {
      name: 'sole exact document key',
      mutate: (candidate) => {
        const documents = documentsOf(candidate);
        documents.OtherProject = documents[projectName];
      }
    },
    {
      name: 'exact document kind',
      mutate: (candidate) => { documentOf(candidate).kind = 'word'; }
    },
    {
      name: 'omitted command defaults',
      mutate: (candidate) => { manifestOf(candidate).commandDefaults = { test: { format: 'text' } }; }
    }
  ];

  for (const testCase of cases) {
    const candidate = createReceipt();
    testCase.mutate(candidate);

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      /manifest/,
      testCase.name
    );
  }
});

test('requires a closed document with the conventional initial paths', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly mutate: (document: Record<string, unknown>) => void;
  }> = [
    {
      name: 'unknown property',
      mutate: (document) => { document.unexpected = true; }
    },
    {
      name: 'source path',
      mutate: (document) => { document.sourcePath = `src/${projectName}/modules`; }
    },
    {
      name: 'template path',
      mutate: (document) => { document.templatePath = `src/${projectName}/Template.xlsm`; }
    },
    {
      name: 'bin path',
      mutate: (document) => { document.binPath = `build/${projectName}.xlsm`; }
    },
    {
      name: 'publish path',
      mutate: (document) => { document.publishPath = `dist/${projectName}.xlsm`; }
    }
  ];

  for (const testCase of cases) {
    const candidate = createReceipt();
    testCase.mutate(documentOf(candidate));

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      /manifest document/,
      testCase.name
    );
  }
});

test('preserves complete ordered CommonModules and reference selections', () => {
  const candidate = createReceiptWithRepository();

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );
  const document = receipt.manifest.documents[projectName];

  assert.deepEqual(document?.commonModules, [
    {
      name: 'Dependency',
      moduleFile: 'Dependency.bas',
      requested: false,
      testOnly: false,
      orphaned: false
    },
    {
      name: 'RequestedModule',
      moduleFile: 'RequestedModule.cls',
      requested: true,
      testOnly: true,
      orphaned: false
    }
  ]);
  assert.deepEqual(document?.references, [
    { name: 'Microsoft Excel 16.0 Object Library', requested: true },
    { name: 'Package Library', requested: false }
  ]);
});

test('accepts producer-authoritative MS-VBAL identifiers outside Unicode letter categories', () => {
  const candidate = createReceiptWithRepository();
  const name = '\u00a0value';
  commonModulesOf(candidate)[0]!.name = name;
  commonModulesOf(candidate)[0]!.moduleFile = `${name}.bas`;

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );

  assert.equal(
    receipt.manifest.documents[projectName]?.commonModules[0]?.name,
    name
  );
});

test('rejects incomplete, duplicate, or non-final CommonModules entries', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly mutate: (candidate: Record<string, unknown>) => void;
  }> = [
    {
      name: 'missing selection array',
      mutate: (candidate) => { documentOf(candidate).commonModules = null; }
    },
    {
      name: 'null entry',
      mutate: (candidate) => { documentOf(candidate).commonModules = [null]; }
    },
    {
      name: 'unknown entry property',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.unexpected = true; }
    },
    {
      name: 'missing required field',
      mutate: (candidate) => { delete commonModulesOf(candidate)[0]!.requested; }
    },
    {
      name: 'empty name',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.name = ''; }
    },
    {
      name: 'nested module file',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.moduleFile = 'nested/Dependency.bas'; }
    },
    {
      name: 'name and module file mismatch',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.moduleFile = 'Other.bas'; }
    },
    {
      name: 'duplicate name',
      mutate: (candidate) => {
        commonModulesOf(candidate)[1]!.name = 'dependency';
        commonModulesOf(candidate)[1]!.moduleFile = 'dependency.cls';
      }
    },
    {
      name: 'duplicate module file',
      mutate: (candidate) => {
        commonModulesOf(candidate)[1]!.name = 'DEPENDENCY';
        commonModulesOf(candidate)[1]!.moduleFile = 'dependency.BAS';
      }
    },
    {
      name: 'OrdinalIgnoreCase duplicate name',
      mutate: (candidate) => {
        commonModulesOf(candidate)[0]!.name = 'µ';
        commonModulesOf(candidate)[0]!.moduleFile = 'µ.bas';
        commonModulesOf(candidate)[1]!.name = 'Μ';
        commonModulesOf(candidate)[1]!.moduleFile = 'Μ.cls';
      }
    },
    {
      name: 'non-Ordinal case-folded extension',
      mutate: (candidate) => {
        commonModulesOf(candidate)[0]!.moduleFile = 'Dependency.baſ';
      }
    },
    {
      name: 'non-final orphan state',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.orphaned = true; }
    },
    {
      name: 'invalid requested state',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.requested = 'yes'; }
    },
    {
      name: 'invalid test-only state',
      mutate: (candidate) => { commonModulesOf(candidate)[0]!.testOnly = 'no'; }
    }
  ];

  for (const testCase of cases) {
    const candidate = createReceiptWithRepository();
    testCase.mutate(candidate);

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      /CommonModules/,
      testCase.name
    );
  }
});

test('rejects incomplete or duplicate reference selections', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly mutate: (candidate: Record<string, unknown>) => void;
  }> = [
    {
      name: 'missing selection array',
      mutate: (candidate) => { documentOf(candidate).references = null; }
    },
    {
      name: 'null entry',
      mutate: (candidate) => { documentOf(candidate).references = [null]; }
    },
    {
      name: 'unknown entry property',
      mutate: (candidate) => { referencesOf(candidate)[0]!.unexpected = true; }
    },
    {
      name: 'missing requested state',
      mutate: (candidate) => { delete referencesOf(candidate)[0]!.requested; }
    },
    {
      name: 'empty name',
      mutate: (candidate) => { referencesOf(candidate)[0]!.name = ''; }
    },
    {
      name: 'untrimmed name',
      mutate: (candidate) => { referencesOf(candidate)[0]!.name = ' Package Library '; }
    },
    {
      name: '.NET whitespace boundary',
      mutate: (candidate) => { referencesOf(candidate)[0]!.name = '\u0085Package Library'; }
    },
    {
      name: 'always-active standard library',
      mutate: (candidate) => {
        referencesOf(candidate)[0]!.name = 'visual basic for applications';
      }
    },
    {
      name: 'duplicate name',
      mutate: (candidate) => {
        referencesOf(candidate)[1]!.name = 'MICROSOFT EXCEL 16.0 OBJECT LIBRARY';
      }
    },
    {
      name: 'OrdinalIgnoreCase duplicate name',
      mutate: (candidate) => {
        referencesOf(candidate)[0]!.name = '\u03a3';
        referencesOf(candidate)[1]!.name = '\u03c2';
      }
    },
    {
      name: 'invalid requested state',
      mutate: (candidate) => { referencesOf(candidate)[0]!.requested = 1; }
    }
  ];

  for (const testCase of cases) {
    const candidate = createReceiptWithRepository();
    testCase.mutate(candidate);

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      /references/,
      testCase.name
    );
  }
});

test('does not merge names that .NET OrdinalIgnoreCase keeps distinct', () => {
  const candidate = createReceiptWithRepository();
  referencesOf(candidate)[0]!.name = 'I';
  referencesOf(candidate)[1]!.name = '\u0131';

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );

  assert.deepEqual(
    receipt.manifest.documents[projectName]?.references.map((entry) => entry.name),
    ['I', '\u0131']
  );
});

test('uses .NET Trim boundaries for reference names', () => {
  const candidate = createReceiptWithRepository();
  referencesOf(candidate)[0]!.name = '\ufeffPackage Library';

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );

  assert.equal(
    receipt.manifest.documents[projectName]?.references[0]?.name,
    '\ufeffPackage Library'
  );
});

test('keeps the envelope and warning objects additive-open and preserves unknown warnings', () => {
  const candidate = createReceiptWithRepository();
  candidate.futureEnvelope = { version: 2 };
  candidate.warnings = [
    { code: 'futureWarning', message: 'First.', futureDetail: 1 },
    { code: 'futureWarning', message: 'Second.', futureDetail: 2 }
  ];

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );

  assert.deepEqual(receipt.futureEnvelope, { version: 2 });
  assert.deepEqual(receipt.warnings, [
    { code: 'futureWarning', message: 'First.', futureDetail: 1 },
    { code: 'futureWarning', message: 'Second.', futureDetail: 2 }
  ]);
});

test('requires a complete warning array with nonempty code and message strings', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly warnings: unknown;
  }> = [
    { name: 'missing array', warnings: null },
    { name: 'non-object entry', warnings: [null] },
    { name: 'empty code', warnings: [{ code: ' ', message: 'Details.' }] },
    { name: 'non-string code', warnings: [{ code: 1, message: 'Details.' }] },
    { name: 'empty message', warnings: [{ code: 'futureWarning', message: '' }] },
    { name: 'non-string message', warnings: [{ code: 'futureWarning', message: false }] }
  ];

  for (const testCase of cases) {
    const candidate = createReceiptWithRepository();
    candidate.warnings = testCase.warnings;

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      /warnings/,
      testCase.name
    );
  }
});

test('preserves unknown warnings around the fixed recognized-warning subsequence', () => {
  const candidate = createReceiptWithRepository();
  candidate.warnings = [
    { code: 'futureBefore', message: 'Before.' },
    {
      code: 'commonModulesSnapshotCleanupFailed',
      message: 'The project was created, but its non-authoritative CommonModules snapshot workspace could not be removed: "C:\\Temp\\vba-dev-snapshot".',
      futureDetail: 'snapshot'
    },
    { code: 'futureMiddle', message: 'Middle.' },
    {
      code: 'leaseMarkerCleanupFailed',
      message: `The project was created and its project lease was released, but the lease marker could not be removed: "${manifestPath}.vba-dev.lock".`,
      futureDetail: 'lease'
    },
    { code: 'futureAfter', message: 'After.' }
  ];

  const receipt = parseNewExcelProjectReceipt(
    JSON.stringify(candidate),
    { projectName, projectRoot }
  );

  assert.deepEqual(receipt.warnings.map((warning) => warning.code), [
    'futureBefore',
    'commonModulesSnapshotCleanupFailed',
    'futureMiddle',
    'leaseMarkerCleanupFailed',
    'futureAfter'
  ]);
  assert.equal(receipt.warnings.length, 5);
  assert.equal(receipt.warnings[1]?.futureDetail, 'snapshot');
  assert.equal(receipt.warnings[3]?.futureDetail, 'lease');
});

test('rejects broken repository and recognized-warning invariants', () => {
  const cases: ReadonlyArray<{
    readonly name: string;
    readonly create: () => Record<string, unknown>;
    readonly mutate: (candidate: Record<string, unknown>) => void;
  }> = [
    {
      name: 'noncanonical repository spelling',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        manifestOf(candidate).commonModulesRepository = '../COMMON_MODULES_REPO';
      }
    },
    {
      name: 'omitted repository without absence warning',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        delete manifestOf(candidate).commonModulesRepository;
        documentOf(candidate).commonModules = [];
      }
    },
    {
      name: 'absence warning with selected repository',
      create: createReceiptWithRepository,
      mutate: (candidate) => { candidate.warnings = [repositoryAbsentWarning()]; }
    },
    {
      name: 'absence warning with installed CommonModules',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        delete manifestOf(candidate).commonModulesRepository;
        candidate.warnings = [repositoryAbsentWarning()];
      }
    },
    {
      name: 'duplicate absence warning',
      create: createReceipt,
      mutate: (candidate) => {
        candidate.warnings = [repositoryAbsentWarning(), repositoryAbsentWarning()];
      }
    },
    {
      name: 'noncanonical absence message',
      create: createReceipt,
      mutate: (candidate) => {
        candidate.warnings = [{
          ...repositoryAbsentWarning(),
          message: 'The CommonModules repository is missing.'
        }];
      }
    },
    {
      name: 'snapshot warning without selected repository',
      create: createReceipt,
      mutate: (candidate) => {
        candidate.warnings = [
          repositoryAbsentWarning(),
          snapshotCleanupWarning(String.raw`C:\Temp\vba-dev-snapshot`)
        ];
      }
    },
    {
      name: 'duplicate snapshot warning',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        const warning = snapshotCleanupWarning(String.raw`C:\Temp\vba-dev-snapshot`);
        candidate.warnings = [warning, warning];
      }
    },
    {
      name: 'relative snapshot workspace',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        candidate.warnings = [snapshotCleanupWarning('relative-snapshot')];
      }
    },
    {
      name: 'noncanonical snapshot message',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        candidate.warnings = [{
          code: 'commonModulesSnapshotCleanupFailed',
          message: 'Snapshot cleanup failed.'
        }];
      }
    },
    {
      name: 'duplicate lease warning',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        const warning = leaseCleanupWarning(`${manifestPath}.vba-dev.lock`);
        candidate.warnings = [warning, warning];
      }
    },
    {
      name: 'wrong lease marker path',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        candidate.warnings = [leaseCleanupWarning(String.raw`C:\work\other.lock`)];
      }
    },
    {
      name: 'noncanonical lease message',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        candidate.warnings = [{
          code: 'leaseMarkerCleanupFailed',
          message: 'Lease cleanup failed.'
        }];
      }
    },
    {
      name: 'recognized warnings out of order',
      create: createReceiptWithRepository,
      mutate: (candidate) => {
        candidate.warnings = [
          leaseCleanupWarning(`${manifestPath}.vba-dev.lock`),
          snapshotCleanupWarning(String.raw`C:\Temp\vba-dev-snapshot`)
        ];
      }
    }
  ];

  for (const testCase of cases) {
    const candidate = testCase.create();
    testCase.mutate(candidate);

    assert.throws(
      () => parseNewExcelProjectReceipt(
        JSON.stringify(candidate),
        { projectName, projectRoot }
      ),
      NewExcelProjectReceiptError,
      testCase.name
    );
  }
});

function createReceipt(): Record<string, unknown> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: projectName,
    operation: 'new',
    template: 'excel',
    complete: true,
    warnings: [{
      code: 'commonModulesRepositoryNotFound',
      message: 'CommonModules repository was not found; the project was created without shared modules.'
    }],
    manifestPath,
    manifest: {
      schemaVersion: 1,
      projectName,
      primaryDocument: projectName,
      documents: {
        [projectName]: {
          kind: 'excel',
          sourcePath: `src/${projectName}`,
          templatePath: `src/${projectName}/${projectName}.xlsm`,
          binPath: `bin/${projectName}.xlsm`,
          publishPath: `publish/${projectName}.xlsm`,
          commonModules: [],
          references: []
        }
      }
    }
  };
}

function createReceiptWithRepository(): Record<string, unknown> {
  const candidate = createReceipt();
  const manifest = manifestOf(candidate);
  manifest.commonModulesRepository = '../common_modules_repo';
  candidate.warnings = [];
  const document = documentOf(candidate);
  document.commonModules = [
    {
      name: 'Dependency',
      moduleFile: 'Dependency.bas',
      requested: false,
      testOnly: false,
      orphaned: false
    },
    {
      name: 'RequestedModule',
      moduleFile: 'RequestedModule.cls',
      requested: true,
      testOnly: true,
      orphaned: false
    }
  ];
  document.references = [
    { name: 'Microsoft Excel 16.0 Object Library', requested: true },
    { name: 'Package Library', requested: false }
  ];
  return candidate;
}

function manifestOf(candidate: Record<string, unknown>): Record<string, unknown> {
  return candidate.manifest as Record<string, unknown>;
}

function documentsOf(candidate: Record<string, unknown>): Record<string, unknown> {
  return manifestOf(candidate).documents as Record<string, unknown>;
}

function documentOf(candidate: Record<string, unknown>): Record<string, unknown> {
  return documentsOf(candidate)[projectName] as Record<string, unknown>;
}

function commonModulesOf(candidate: Record<string, unknown>): Array<Record<string, unknown>> {
  return documentOf(candidate).commonModules as Array<Record<string, unknown>>;
}

function referencesOf(candidate: Record<string, unknown>): Array<Record<string, unknown>> {
  return documentOf(candidate).references as Array<Record<string, unknown>>;
}

function repositoryAbsentWarning(): Record<string, unknown> {
  return {
    code: 'commonModulesRepositoryNotFound',
    message: 'CommonModules repository was not found; the project was created without shared modules.'
  };
}

function snapshotCleanupWarning(retainedPath: string): Record<string, unknown> {
  return {
    code: 'commonModulesSnapshotCleanupFailed',
    message: 'The project was created, but its non-authoritative CommonModules snapshot workspace could not be removed: ' +
      `"${retainedPath}".`
  };
}

function leaseCleanupWarning(retainedPath: string): Record<string, unknown> {
  return {
    code: 'leaseMarkerCleanupFailed',
    message: 'The project was created and its project lease was released, but the lease marker could not be removed: ' +
      `"${retainedPath}".`
  };
}
