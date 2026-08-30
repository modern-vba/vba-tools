import assert from 'node:assert/strict';
import test from 'node:test';

import {
  ReferenceOutputContractError,
  parseAvailableReferenceInventoryOutput,
  parseReferenceMutationOutput,
  parseReferenceSelectionInventoryOutput
} from './referenceOutputContract';

const projectRoot = String.raw`C:\work\Project`;
const documentName = 'Book1';

test('available inventory returns resolved candidates in inventory order', () => {
  const parsed = parseAvailableReferenceInventoryOutput(JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    mode: 'available',
    complete: true,
    warnings: [],
    references: [
      {
        name: 'Zulu Library',
        status: 'resolved',
        identity: {
          guid: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
          major: 2,
          minor: 1
        }
      },
      {
        name: 'Alpha Library',
        status: 'resolved',
        identity: {
          guid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
          major: 1,
          minor: 0
        }
      }
    ]
  }), projectRoot, documentName);

  assert.deepEqual(
    parsed.resolvedReferences.map((reference) => reference.name),
    ['Zulu Library', 'Alpha Library']
  );
  assert.equal(parsed.resolvedReferences[0]?.identity.major, 2);
});

test('selection inventory preserves manifest order and stored spelling', () => {
  const parsed = parseReferenceSelectionInventoryOutput(JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    mode: 'selection',
    complete: true,
    warnings: [],
    references: [
      { name: 'MiXeD Library' },
      { name: 'Broken Library' }
    ]
  }), projectRoot, documentName);

  assert.deepEqual(parsed.references, [
    { name: 'MiXeD Library' },
    { name: 'Broken Library' }
  ]);
});

test('add mutation returns one ordered trusted result per submitted name', () => {
  const submittedNames = ['Alpha Library', 'Beta Library', 'Gamma Library'];
  const parsed = parseReferenceMutationOutput(JSON.stringify({
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    operation: 'add',
    complete: true,
    warnings: [],
    results: [
      {
        requestedName: 'Alpha Library',
        storedName: 'Alpha Library',
        status: 'added'
      },
      {
        requestedName: 'Beta Library',
        storedName: 'BETA LIBRARY',
        status: 'promoted'
      },
      {
        requestedName: 'Gamma Library',
        storedName: 'Gamma Library',
        status: 'alreadyPresent'
      }
    ]
  }), projectRoot, documentName, 'add', submittedNames);

  assert.deepEqual(
    parsed.results.map((result) => result.status),
    ['added', 'promoted', 'alreadyPresent']
  );
});

test('available inventory retains conclusive issues but exposes only resolved candidates', () => {
  const root = availableEnvelope([
    resolvedEntry('Alpha Library', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 1, 0),
    {
      name: 'Bravo Ambiguous',
      status: 'ambiguous',
      reasonCode: 'multipleUsableIdentities',
      candidates: [
        identity('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 1, 0),
        identity('cccccccc-cccc-cccc-cccc-cccccccccccc', 2, 0)
      ],
      message: 'Multiple usable identities remain.'
    },
    {
      name: 'Charlie Unavailable',
      status: 'unavailable',
      reasonCode: 'notRegistered',
      candidates: [],
      message: 'No registration matched.'
    },
    {
      name: 'Delta Unusable',
      status: 'unavailable',
      reasonCode: 'noUsableIdentity',
      candidates: [identity('dddddddd-dddd-dddd-dddd-dddddddddddd', 3, 0)],
      message: 'The registration had no usable identity.'
    },
    resolvedEntry('Echo Library', 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee', 4, 2)
  ]);

  const parsed = parseAvailableReferenceInventoryOutput(
    JSON.stringify(root),
    projectRoot,
    documentName
  );

  assert.deepEqual(
    parsed.references.map((reference) => reference.status),
    ['resolved', 'ambiguous', 'unavailable', 'unavailable', 'resolved']
  );
  assert.deepEqual(
    parsed.resolvedReferences.map((reference) => reference.name),
    ['Alpha Library', 'Echo Library']
  );
});

test('available inventory accepts additive properties and unknown warning codes', () => {
  const root = availableEnvelope([
    resolvedEntry('Alpha Library', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
  ]);
  root.project = projectRoot.toLowerCase();
  root.futureRoot = { enabled: true };
  root.warnings = [{
    code: 'futureWarning',
    message: 'A future warning remains informational.',
    futureWarningField: 1
  }];
  const entry = firstArrayRecord(root.references);
  entry.futureEntry = 'ignored';
  record(entry.identity).futureIdentity = 'ignored';

  const parsed = parseAvailableReferenceInventoryOutput(
    JSON.stringify(root),
    projectRoot,
    documentName
  );

  assert.deepEqual(parsed.warnings, [{
    code: 'futureWarning',
    message: 'A future warning remains informational.'
  }]);
});

test('available inventory rejects malformed, mismatched, incomplete, or diagnostic output', () => {
  assertContractFailure(() => parseAvailableReferenceInventoryOutput(
    '{not-json', projectRoot, documentName
  ));

  const scenarios: Array<(root: Record<string, unknown>) => void> = [
    (root) => { root.schemaVersion = '2.0'; },
    (root) => { root.scope = 'environment'; },
    (root) => { root.project = String.raw`C:\work\Other`; },
    (root) => { root.document = 'Book2'; },
    (root) => { root.mode = 'configured'; },
    (root) => { root.complete = false; },
    (root) => { root.diagnostics = []; },
    (root) => { root.diagnostics = [{ code: 'incomplete', message: 'Not complete.' }]; }
  ];
  for (const mutate of scenarios) {
    const root = availableEnvelope([
      resolvedEntry('Alpha Library', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
    ]);
    mutate(root);
    assertContractFailure(() => parseAvailableReferenceInventoryOutput(
      JSON.stringify(root), projectRoot, documentName
    ));
  }
});

test('available inventory rejects unverified and inconsistent status discriminants', () => {
  const invalidEntries: Record<string, unknown>[] = [
    {
      name: 'Unverified Library',
      status: 'unverified',
      reasonCode: 'probeTimeout',
      candidates: [identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')],
      message: 'The probe timed out.'
    },
    { name: 'Unknown Library', status: 'future', identity: identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa') },
    { name: 'Resolved Library', status: 'resolved' },
    resolvedEntry(' padded', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
    {
      ...resolvedEntry('Resolved Library', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
      candidates: []
    },
    {
      name: 'Ambiguous Library',
      status: 'ambiguous',
      reasonCode: 'futureReason',
      candidates: [
        identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
        identity('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
      ],
      message: 'Ambiguous.'
    },
    {
      name: 'Ambiguous Library',
      status: 'ambiguous',
      reasonCode: 'multipleUsableIdentities',
      candidates: [identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')],
      message: 'Ambiguous.'
    },
    {
      name: 'Unavailable Library',
      status: 'unavailable',
      reasonCode: 'notRegistered',
      candidates: [identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')],
      message: 'Unavailable.'
    },
    {
      name: 'Unavailable Library',
      status: 'unavailable',
      reasonCode: 'futureReason',
      candidates: [],
      message: 'Unavailable.'
    }
  ];
  for (const entry of invalidEntries) {
    assertContractFailure(() => parseAvailableReferenceInventoryOutput(
      JSON.stringify(availableEnvelope([entry])), projectRoot, documentName
    ));
  }
});

test('available inventory strictly validates identities, candidate order, and names', () => {
  const invalidIdentities: Record<string, unknown>[] = [
    identity('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA'),
    identity('not-a-guid'),
    identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', -1, 0),
    identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 65_536, 0),
    identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 1.5, 0)
  ];
  for (const invalidIdentity of invalidIdentities) {
    const entry = resolvedEntry(
      'Alpha Library',
      'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
    );
    entry.identity = invalidIdentity;
    assertContractFailure(() => parseAvailableReferenceInventoryOutput(
      JSON.stringify(availableEnvelope([entry])), projectRoot, documentName
    ));
  }

  const duplicateNames = availableEnvelope([
    resolvedEntry('Σ Library', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'),
    resolvedEntry('ς Library', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb')
  ]);
  assertContractFailure(() => parseAvailableReferenceInventoryOutput(
    JSON.stringify(duplicateNames), projectRoot, documentName
  ));

  const unsortedCandidates = availableEnvelope([{
    name: 'Ambiguous Library',
    status: 'ambiguous',
    reasonCode: 'multipleUsableIdentities',
    candidates: [
      identity('bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'),
      identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
    ],
    message: 'Ambiguous.'
  }]);
  assertContractFailure(() => parseAvailableReferenceInventoryOutput(
    JSON.stringify(unsortedCandidates), projectRoot, documentName
  ));
});

test('selection inventory accepts empty selection, additive properties, and unknown warnings', () => {
  const empty = selectionEnvelope([]);
  assert.deepEqual(
    parseReferenceSelectionInventoryOutput(
      JSON.stringify(empty), projectRoot, documentName
    ).references,
    []
  );

  const root = selectionEnvelope([{ name: 'Stored Spelling', futureEntry: true }]);
  root.futureRoot = 1;
  root.warnings = [{
    code: 'futureWarning',
    message: 'A future warning remains informational.',
    futureWarningField: true
  }];
  const parsed = parseReferenceSelectionInventoryOutput(
    JSON.stringify(root), projectRoot, documentName
  );
  assert.deepEqual(parsed.references, [{ name: 'Stored Spelling' }]);
  assert.equal(parsed.warnings[0]?.code, 'futureWarning');
});

test('selection inventory rejects malformed, mismatched, incomplete, or resolving output', () => {
  assertContractFailure(() => parseReferenceSelectionInventoryOutput(
    '{not-json', projectRoot, documentName
  ));
  const scenarios: Array<(root: Record<string, unknown>) => void> = [
    (root) => { root.project = String.raw`C:\work\Other`; },
    (root) => { root.document = 'Book2'; },
    (root) => { root.mode = 'configured'; },
    (root) => { root.complete = false; },
    (root) => { root.diagnostics = []; }
  ];
  for (const mutate of scenarios) {
    const root = selectionEnvelope([{ name: 'Alpha Library' }]);
    mutate(root);
    assertContractFailure(() => parseReferenceSelectionInventoryOutput(
      JSON.stringify(root), projectRoot, documentName
    ));
  }
  for (const field of ['status', 'identity', 'reasonCode', 'candidates', 'message']) {
    const entry: Record<string, unknown> = { name: 'Alpha Library' };
    entry[field] = field === 'identity'
      ? identity('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa')
      : field === 'candidates'
        ? []
        : 'unexpected';
    assertContractFailure(() => parseReferenceSelectionInventoryOutput(
      JSON.stringify(selectionEnvelope([entry])), projectRoot, documentName
    ));
  }
});

test('selection inventory requires canonical unique non-reserved names', () => {
  for (const references of [
    [{ name: ' padded' }],
    [{ name: 'Σ Library' }, { name: 'ς Library' }],
    [{ name: 'visual basic for applications' }]
  ]) {
    assertContractFailure(() => parseReferenceSelectionInventoryOutput(
      JSON.stringify(selectionEnvelope(references)), projectRoot, documentName
    ));
  }
});

test('remove mutation models removed and alreadyAbsent stored-name rules', () => {
  const parsed = parseReferenceMutationOutput(JSON.stringify(mutationEnvelope(
    'remove',
    [
      { requestedName: 'Alpha Library', storedName: 'ALPHA LIBRARY', status: 'removed' },
      { requestedName: 'Missing Library', storedName: null, status: 'alreadyAbsent' }
    ]
  )), projectRoot, documentName, 'remove', ['Alpha Library', 'Missing Library']);

  assert.deepEqual(parsed.results, [
    { requestedName: 'Alpha Library', storedName: 'ALPHA LIBRARY', status: 'removed' },
    { requestedName: 'Missing Library', storedName: null, status: 'alreadyAbsent' }
  ]);
});

test('mutation accepts additive properties and unknown warning codes', () => {
  const root = mutationEnvelope('add', [{
    requestedName: 'Alpha Library',
    storedName: 'Alpha Library',
    status: 'added',
    futureResult: true
  }]);
  root.futureRoot = true;
  root.project = projectRoot.toLowerCase();
  root.warnings = [{
    code: 'futureWarning',
    message: 'A future warning remains informational.',
    futureWarningField: true
  }];
  const parsed = parseReferenceMutationOutput(
    JSON.stringify(root), projectRoot, documentName, 'add', ['Alpha Library']
  );
  assert.equal(parsed.warnings[0]?.code, 'futureWarning');
});

test('mutation rejects malformed, mismatched, incomplete, or invalid envelope output', () => {
  assertContractFailure(() => parseReferenceMutationOutput(
    '{not-json', projectRoot, documentName, 'add', ['Alpha Library']
  ));
  const scenarios: Array<(root: Record<string, unknown>) => void> = [
    (root) => { root.schemaVersion = '2.0'; },
    (root) => { root.scope = 'environment'; },
    (root) => { root.project = String.raw`C:\work\Other`; },
    (root) => { root.document = 'Book2'; },
    (root) => { root.operation = 'remove'; },
    (root) => { root.complete = false; },
    (root) => { root.results = {}; }
  ];
  for (const mutate of scenarios) {
    const root = mutationEnvelope('add', [{
      requestedName: 'Alpha Library',
      storedName: 'Alpha Library',
      status: 'added'
    }]);
    mutate(root);
    assertContractFailure(() => parseReferenceMutationOutput(
      JSON.stringify(root), projectRoot, documentName, 'add', ['Alpha Library']
    ));
  }
});

test('mutation enforces operation-specific status and storedName discriminants', () => {
  const invalid: Array<{
    operation: 'add' | 'remove';
    result: Record<string, unknown>;
  }> = [
    { operation: 'add', result: { requestedName: 'Alpha Library', storedName: null, status: 'added' } },
    { operation: 'add', result: { requestedName: 'Alpha Library', storedName: ' ', status: 'added' } },
    { operation: 'add', result: { requestedName: 'Alpha Library', storedName: 'Alpha Library', status: 'removed' } },
    { operation: 'remove', result: { requestedName: 'Alpha Library', storedName: null, status: 'removed' } },
    { operation: 'remove', result: { requestedName: 'Alpha Library', storedName: 'Alpha Library', status: 'alreadyAbsent' } },
    { operation: 'remove', result: { requestedName: 'Alpha Library', status: 'alreadyAbsent' } },
    { operation: 'remove', result: { requestedName: 'Alpha Library', storedName: null, status: 'future' } }
  ];
  for (const scenario of invalid) {
    assertContractFailure(() => parseReferenceMutationOutput(
      JSON.stringify(mutationEnvelope(scenario.operation, [scenario.result])),
      projectRoot,
      documentName,
      scenario.operation,
      ['Alpha Library']
    ));
  }
});

test('mutation requires the exact ordered one-to-one submitted-name partition', () => {
  const baseResults = [
    { requestedName: 'Alpha Library', storedName: 'Alpha Library', status: 'added' },
    { requestedName: 'Beta Library', storedName: 'Beta Library', status: 'added' }
  ];
  const invalidResults: Record<string, unknown>[][] = [
    [baseResults[0]!],
    [...baseResults, { requestedName: 'Extra Library', storedName: 'Extra Library', status: 'added' }],
    [baseResults[1]!, baseResults[0]!],
    [baseResults[0]!, { ...baseResults[1], requestedName: 'BETA LIBRARY' }]
  ];
  for (const results of invalidResults) {
    assertContractFailure(() => parseReferenceMutationOutput(
      JSON.stringify(mutationEnvelope('add', results)),
      projectRoot,
      documentName,
      'add',
      ['Alpha Library', 'Beta Library']
    ));
  }
  const ordinalDuplicateResults = [
    { requestedName: 'Σ Library', storedName: 'Σ Library', status: 'added' },
    { requestedName: 'ς Library', storedName: 'ς Library', status: 'added' }
  ];
  assertContractFailure(() => parseReferenceMutationOutput(
    JSON.stringify(mutationEnvelope('add', ordinalDuplicateResults)),
    projectRoot,
    documentName,
    'add',
    ['Σ Library', 'ς Library']
  ));
  assertContractFailure(() => parseReferenceMutationOutput(
    JSON.stringify(mutationEnvelope('add', [])),
    projectRoot,
    documentName,
    'add',
    []
  ));
});

test('all reference contracts require complete warning shapes', () => {
  const invalidWarnings: unknown[] = [
    undefined,
    null,
    {},
    [{ code: '', message: 'Message.' }],
    [{ code: 'warning', message: '' }],
    [{ code: 1, message: 'Message.' }]
  ];
  for (const warnings of invalidWarnings) {
    const available = availableEnvelope([]);
    available.warnings = warnings;
    assertContractFailure(() => parseAvailableReferenceInventoryOutput(
      JSON.stringify(available), projectRoot, documentName
    ));

    const selection = selectionEnvelope([]);
    selection.warnings = warnings;
    assertContractFailure(() => parseReferenceSelectionInventoryOutput(
      JSON.stringify(selection), projectRoot, documentName
    ));

    const mutation = mutationEnvelope('add', [{
      requestedName: 'Alpha Library',
      storedName: 'Alpha Library',
      status: 'added'
    }]);
    mutation.warnings = warnings;
    assertContractFailure(() => parseReferenceMutationOutput(
      JSON.stringify(mutation), projectRoot, documentName, 'add', ['Alpha Library']
    ));
  }
});

function availableEnvelope(
  references: readonly Record<string, unknown>[]
): Record<string, unknown> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    mode: 'available',
    complete: true,
    warnings: [],
    references
  };
}

function selectionEnvelope(
  references: readonly Record<string, unknown>[]
): Record<string, unknown> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    mode: 'selection',
    complete: true,
    warnings: [],
    references
  };
}

function mutationEnvelope(
  operation: 'add' | 'remove',
  results: readonly Record<string, unknown>[]
): Record<string, unknown> {
  return {
    schemaVersion: '1.0',
    scope: 'project',
    project: projectRoot,
    document: documentName,
    operation,
    complete: true,
    warnings: [],
    results
  };
}

function resolvedEntry(
  name: string,
  guid: string,
  major = 1,
  minor = 0
): Record<string, unknown> {
  return {
    name,
    status: 'resolved',
    identity: identity(guid, major, minor)
  };
}

function identity(guid: string, major = 1, minor = 0): Record<string, unknown> {
  return { guid, major, minor };
}

function firstArrayRecord(value: unknown): Record<string, unknown> {
  assert.ok(Array.isArray(value));
  return record(value[0]);
}

function record(value: unknown): Record<string, unknown> {
  assert.ok(typeof value === 'object' && value !== null && !Array.isArray(value));
  return value as Record<string, unknown>;
}

function assertContractFailure(run: () => unknown): void {
  assert.throws(run, ReferenceOutputContractError);
}
