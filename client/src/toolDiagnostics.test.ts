import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';
import { VbaDevOutputContractError } from './vbaDevOutputContract';

import {
  VbaDevDiagnosticReporter,
  parseVbaDevDiagnostics
} from './toolDiagnostics';

test('tool diagnostics map machine-readable records with severity uri range message and code', () => {
  const filePath = path.join('C:', 'work', 'BookProject', 'src', 'Book1', 'Module1.bas');

  const diagnostics = parseVbaDevDiagnostics(JSON.stringify({
    type: 'diagnostic',
    owner: 'vba-dev',
    severity: 'warning',
    uri: filePath,
    range: {
      start: { line: 3, character: 2 },
      end: { line: 3, character: 12 }
    },
    message: 'Reference was not found.',
    code: 'VBAREF001'
  }));

  assert.deepEqual(diagnostics, [
    {
      owner: 'vba-dev',
      severity: 'warning',
      uriPath: filePath,
      range: {
        start: { line: 3, character: 2 },
        end: { line: 3, character: 12 }
      },
      message: 'Reference was not found.',
      code: 'VBAREF001'
    }
  ]);
});

test('tool diagnostics map diagnostic arrays with source aliases and file URIs', () => {
  const filePath = path.join('C:', 'work', 'BookProject', 'vba-project.json');

  const diagnostics = parseVbaDevDiagnostics(JSON.stringify({
    diagnostics: [
      {
        type: 'diagnostic',
        source: 'vba-dev',
        severity: 'error',
        file: pathToFileURL(filePath).toString(),
        range: {
          start: { line: 1, character: 0 },
          end: { line: 1, character: 10 }
        },
        message: 'Invalid project manifest.',
        code: 'VBAPRJ001'
      }
    ]
  }));

  assert.deepEqual(diagnostics, [
    {
      owner: 'vba-dev',
      severity: 'error',
      uriPath: filePath,
      range: {
        start: { line: 1, character: 0 },
        end: { line: 1, character: 10 }
      },
      message: 'Invalid project manifest.',
      code: 'VBAPRJ001'
    }
  ]);
});

test('tool diagnostics omit plain text and records missing required mapping fields', () => {
  const diagnostics = parseVbaDevDiagnostics([
    '[FAIL] CommonModules (Book1/Missing): Unknown CommonModuleName',
    JSON.stringify({
      type: 'diagnostic',
      owner: 'vba-dev',
      severity: 'error',
      message: 'Missing URI and range.',
      code: 'VBACOMMON001'
    })
  ].join('\n'));

  assert.deepEqual(diagnostics, []);
});

test('tool diagnostics map severity aliases', () => {
  const filePath = path.join('C:', 'work', 'BookProject', 'vba-project.json');
  const diagnostics = parseVbaDevDiagnostics([
    diagnosticJson(filePath, 'error', 'E001'),
    diagnosticJson(filePath, 'warning', 'W001'),
    diagnosticJson(filePath, 'information', 'I001'),
    diagnosticJson(filePath, 'hint', 'H001')
  ].join('\n'));

  assert.deepEqual(diagnostics.map((diagnostic) => diagnostic.severity), [
    'error',
    'warning',
    'information',
    'hint'
  ]);
});

test('tool diagnostic reporter clears stale diagnostics when a scope is refreshed', () => {
  const firstPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1', 'First.bas');
  const secondPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1', 'Second.bas');
  const collection = new FakeDiagnosticCollection();
  const reporter = new VbaDevDiagnosticReporter(collection);

  reporter.refresh('project:C:/work/BookProject', diagnosticJson(firstPath, 'error', 'E001'));
  reporter.refresh('project:C:/work/BookProject', diagnosticJson(secondPath, 'warning', 'W001'));

  assert.deepEqual(collection.deleted, [firstPath]);
  assert.equal(collection.entries.has(firstPath), false);
  assert.deepEqual([...collection.entries.keys()], [secondPath]);
});

test('refreshing one Build document replaces only its contribution to shared diagnostic URIs', () => {
  const projectOne = path.join('C:', 'work', 'ProjectOne');
  const projectTwo = path.join('C:', 'work', 'ProjectTwo');
  const sharedPath = path.join('C:', 'work', 'Shared.bas');
  const buildOnlyPath = path.join(projectOne, 'src', 'BookA', 'BuildOnly.bas');
  const referenceOnlyPath = path.join(projectOne, 'src', 'BookA', 'ReferenceOnly.bas');
  const buildScope = JSON.stringify(['vba-dev', 'build', projectOne, 'BookA']);
  const referenceScope = JSON.stringify(['vba-dev', 'reference list', projectOne, 'BookA']);
  const otherDocumentScope = JSON.stringify(['vba-dev', 'build', projectOne, 'BookB']);
  const otherProjectScope = JSON.stringify(['vba-dev', 'build', projectTwo, 'BookA']);
  const collection = new FakeDiagnosticCollection();
  const reporter = new VbaDevDiagnosticReporter(collection);
  const visibleCodes = (uriPath: string) => (collection.entries.get(uriPath) ?? []).map(value => {
    assert.ok(typeof value === 'object' && value !== null && 'code' in value);
    return value.code;
  });

  reporter.refresh(buildScope, [
    diagnosticJson(sharedPath, 'error', 'BUILD_A'),
    diagnosticJson(buildOnlyPath, 'error', 'BUILD_ONLY')
  ].join('\n'));
  reporter.refresh(referenceScope, [
    diagnosticJson(sharedPath, 'warning', 'REFERENCE_A'),
    diagnosticJson(referenceOnlyPath, 'warning', 'REFERENCE_ONLY')
  ].join('\n'));
  reporter.refresh(otherDocumentScope, diagnosticJson(sharedPath, 'error', 'BUILD_B'));
  reporter.refresh(otherProjectScope, diagnosticJson(sharedPath, 'error', 'OTHER_PROJECT_A'));

  assert.deepEqual(visibleCodes(sharedPath), [
    'BUILD_A', 'REFERENCE_A', 'BUILD_B', 'OTHER_PROJECT_A'
  ]);
  assert.deepEqual(visibleCodes(buildOnlyPath), ['BUILD_ONLY']);
  assert.deepEqual(visibleCodes(referenceOnlyPath), ['REFERENCE_ONLY']);

  const refreshedDiagnostics = reporter.refresh(buildScope, '');

  assert.deepEqual(refreshedDiagnostics, []);
  assert.deepEqual(visibleCodes(sharedPath), [
    'REFERENCE_A', 'BUILD_B', 'OTHER_PROJECT_A'
  ]);
  assert.equal(collection.entries.has(buildOnlyPath), false);
  assert.deepEqual(visibleCodes(referenceOnlyPath), ['REFERENCE_ONLY']);
});

test('unsupported sourceAnalysis versions fail before replacing existing Problems', () => {
  const sourcePath = path.join('C:', 'work', 'Project', 'src', 'BookA', 'Source.bas');
  const collection = new FakeDiagnosticCollection();
  const reporter = new VbaDevDiagnosticReporter(collection);
  const scope = 'source-analysis-version-scope';
  reporter.refresh(scope, diagnosticJson(sourcePath, 'error', 'EXISTING_SOURCE_ERROR'));
  const previousDiagnostics = collection.entries.get(sourcePath);

  assert.throws(() => reporter.refresh(scope, JSON.stringify({
    type: 'sourceAnalysis',
    schemaVersion: '1.0',
    complete: true,
    diagnostics: [],
    failures: []
  })), VbaDevOutputContractError);

  assert.deepEqual(collection.entries.get(sourcePath), previousDiagnostics);
  assert.deepEqual(collection.deleted, []);
});

test('malformed sourceAnalysis payloads reject the whole report without replacing Problems', () => {
  const sourcePath = path.join('C:', 'work', 'Project', 'src', 'BookA', 'Source.bas');
  const sourceUri = 'file:///C:/work/Project/src/BookA/Source.bas';
  const diagnostic = {
    type: 'diagnostic',
    owner: 'vba-dev',
    uri: sourceUri,
    code: 'syntax.unterminatedStringLiteral',
    message: 'String literal is missing a closing double quote.',
    severity: 'error',
    range: {
      start: { line: 2, character: 12 },
      end: { line: 2, character: 25 }
    }
  };
  const completeReport = {
    type: 'sourceAnalysis',
    schemaVersion: '3.0',
    complete: true,
    diagnostics: [diagnostic],
    failures: []
  };
  const sourceFailure = { scope: 'source', uri: sourceUri, message: 'Source could not be decoded.' };
  const withDiagnostic = (invalid: unknown) => ({ ...completeReport, diagnostics: [diagnostic, invalid] });
  const withRange = (range: unknown) => withDiagnostic({ ...diagnostic, range });
  const withFailure = (invalid: unknown) => ({ ...completeReport, complete: false, failures: [invalid] });
  const malformed: Array<readonly [string, unknown]> = [
    ['missing completeness', { ...completeReport, complete: undefined }],
    ['non-Boolean completeness', { ...completeReport, complete: 'true' }],
    ['missing diagnostic array', { ...completeReport, diagnostics: undefined }],
    ['non-array diagnostics', { ...completeReport, diagnostics: diagnostic }],
    ['missing failure array', { ...completeReport, failures: undefined }],
    ['non-array failures', { ...completeReport, failures: sourceFailure }],
    ['complete report with a failure', { ...completeReport, failures: [sourceFailure] }],
    ['incomplete report without a reason', { ...completeReport, complete: false }],
    ['null diagnostic after a valid one', withDiagnostic(null)],
    ['wrong diagnostic type', withDiagnostic({ ...diagnostic, type: 'finding' })],
    ['missing diagnostic owner', withDiagnostic({ ...diagnostic, owner: undefined })],
    ['foreign diagnostic owner', withDiagnostic({ ...diagnostic, owner: 'another-tool' })],
    ['missing diagnostic URI', withDiagnostic({ ...diagnostic, uri: undefined })],
    ['relative diagnostic URI', withDiagnostic({ ...diagnostic, uri: 'Source.bas' })],
    ['non-file diagnostic URI', withDiagnostic({ ...diagnostic, uri: 'https://example.test/Source.bas' })],
    ['relative file diagnostic URI', withDiagnostic({ ...diagnostic, uri: 'file:Source.bas' })],
    ['empty diagnostic code', withDiagnostic({ ...diagnostic, code: '' })],
    ['empty diagnostic message', withDiagnostic({ ...diagnostic, message: '' })],
    ['unknown severity', withDiagnostic({ ...diagnostic, severity: 'fatal' })],
    ['noncanonical severity', withDiagnostic({ ...diagnostic, severity: 'Error' })],
    ['missing diagnostic range', withRange(undefined)],
    ['missing start position', withRange({ end: diagnostic.range.end })],
    ['missing end position', withRange({ start: diagnostic.range.start })],
    ['negative start line', withRange({ ...diagnostic.range, start: { line: -1, character: 12 } })],
    ['fractional character', withRange({ ...diagnostic.range, start: { line: 2, character: 12.5 } })],
    ['unsafe line integer', withRange({ ...diagnostic.range, end: { line: Number.MAX_SAFE_INTEGER + 1, character: 25 } })],
    ['negative end character', withRange({ ...diagnostic.range, end: { line: 2, character: -1 } })],
    ['text position value', withRange({ ...diagnostic.range, start: { line: '2', character: 12 } })],
    ['reversed line range', withRange({ ...diagnostic.range, end: { line: 1, character: 25 } })],
    ['reversed character range', withRange({ ...diagnostic.range, end: { line: 2, character: 11 } })],
    ['null failure', withFailure(null)],
    ['unknown failure scope', withFailure({ ...sourceFailure, scope: 'workspace' })],
    ['empty failure message', withFailure({ ...sourceFailure, message: '' })],
    ['missing source failure URI', withFailure({ ...sourceFailure, uri: undefined })],
    ['null source failure URI', withFailure({ ...sourceFailure, uri: null })],
    ['relative source failure URI', withFailure({ ...sourceFailure, uri: 'Source.bas' })],
    ['non-file source failure URI', withFailure({ ...sourceFailure, uri: 'https://example.test/Source.bas' })],
    ['project failure has a source URI', withFailure({ ...sourceFailure, scope: 'project' })],
    ['project failure omits explicit null URI', withFailure({ ...sourceFailure, scope: 'project', uri: undefined })]
  ];

  for (const [name, report] of malformed) {
    const collection = new FakeDiagnosticCollection();
    const reporter = new VbaDevDiagnosticReporter(collection);
    const scope = 'source-analysis-payload-scope';
    reporter.refresh(scope, diagnosticJson(sourcePath, 'error', 'EXISTING_SOURCE_ERROR'));
    const previousDiagnostics = collection.entries.get(sourcePath);

    assert.throws(() => reporter.refresh(scope, JSON.stringify(report)), VbaDevOutputContractError, name);

    assert.deepEqual(collection.entries.get(sourcePath), previousDiagnostics, name);
    assert.deepEqual(collection.deleted, [], name);
  }
});

test('valid sourceAnalysis reports preserve findings and accept failures without invented positions', () => {
  const localPath = path.join('C:', 'work', 'Source Project', 'src', 'Dialog.frm');
  const uncPath = String.raw`\\server\share\Project\Source.bas`;
  const localUri = 'file:///C:/work/Source%20Project/src/Dialog.frm';
  const uncUri = 'file://server/share/Project/Source.bas';
  const diagnostics = [
    {
      type: 'diagnostic', owner: 'vba-dev', uri: localUri,
      code: 'syntax.unterminatedStringLiteral', severity: 'error',
      message: 'String literal is missing a closing double quote.',
      range: { start: { line: 8, character: 12 }, end: { line: 8, character: 25 } },
      futureDiagnosticMetadata: { revision: 1 }
    },
    {
      type: 'diagnostic', owner: 'vba-dev', uri: uncUri,
      code: 'validation.exampleWarning', severity: 'warning',
      message: 'Warning on a continued declaration.',
      range: { start: { line: 4, character: 10 }, end: { line: 4, character: 13 } }
    },
    {
      type: 'diagnostic', owner: 'vba-dev', uri: localUri,
      code: 'validation.exampleInformation', severity: 'information',
      message: 'Information spanning original source lines.',
      range: { start: { line: 2, character: 0 }, end: { line: 3, character: 2 } }
    },
    {
      type: 'diagnostic', owner: 'vba-dev', uri: uncUri,
      code: 'validation.exampleHint', severity: 'hint',
      message: 'Hint at the start of the source.',
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 0 } }
    }
  ];
  const expected = [
    {
      owner: 'vba-dev', uriPath: localPath,
      code: 'syntax.unterminatedStringLiteral', severity: 'error',
      message: 'String literal is missing a closing double quote.',
      range: { start: { line: 8, character: 12 }, end: { line: 8, character: 25 } }
    },
    {
      owner: 'vba-dev', uriPath: uncPath,
      code: 'validation.exampleWarning', severity: 'warning',
      message: 'Warning on a continued declaration.',
      range: { start: { line: 4, character: 10 }, end: { line: 4, character: 13 } }
    },
    {
      owner: 'vba-dev', uriPath: localPath,
      code: 'validation.exampleInformation', severity: 'information',
      message: 'Information spanning original source lines.',
      range: { start: { line: 2, character: 0 }, end: { line: 3, character: 2 } }
    },
    {
      owner: 'vba-dev', uriPath: uncPath,
      code: 'validation.exampleHint', severity: 'hint',
      message: 'Hint at the start of the source.',
      range: { start: { line: 0, character: 0 }, end: { line: 0, character: 0 } }
    }
  ];
  const failures = [
    { scope: 'source', uri: uncUri, message: 'Source could not be read.' },
    { scope: 'project', uri: null, message: 'Project source inventory could not be completed.',
      futureFailureMetadata: { retryable: false } }
  ];
  const reports = [
    { name: 'complete findings', complete: true, diagnostics, failures: [], expected },
    { name: 'incomplete findings', complete: false, diagnostics, failures, expected },
    { name: 'incomplete without findings', complete: false, diagnostics: [], failures, expected: [] },
    { name: 'clean complete report', complete: true, diagnostics: [], failures: [], expected: [] }
  ];

  for (const report of reports) {
    const collection = new FakeDiagnosticCollection();
    const reporter = new VbaDevDiagnosticReporter(collection);
    reporter.refresh('source-analysis-positive-scope', diagnosticJson(localPath, 'error', 'STALE'));

    const actual = reporter.refresh('source-analysis-positive-scope', JSON.stringify({
      type: 'sourceAnalysis', schemaVersion: '3.0', complete: report.complete,
      diagnostics: report.diagnostics, failures: report.failures,
      futureReportMetadata: { producer: 'compatible-provider' }
    }));

    assert.deepEqual(actual, report.expected, report.name);
    assert.deepEqual(collection.entries.get(localPath) ?? [],
      report.expected.filter(diagnostic => diagnostic.uriPath === localPath), report.name);
    assert.deepEqual(collection.entries.get(uncPath) ?? [],
      report.expected.filter(diagnostic => diagnostic.uriPath === uncPath), report.name);
    assert.equal(collection.entries.size, report.expected.length === 0 ? 0 : 2, report.name);
  }
});

test('sourceAnalysis 3.0 preserves related declaration navigation and replaces it on a clean rerun', () => {
  const sourcePath = String.raw`C:\work\Caller.bas`;
  const targetPath = String.raw`C:\work\Target.bas`;
  const collection = new FakeDiagnosticCollection();
  const reporter = new VbaDevDiagnosticReporter(collection);
  const report = semanticReport();

  const diagnostics = reporter.refresh('semantic-build', JSON.stringify(report));

  assert.deepEqual(diagnostics, [{
    owner: 'vba-dev', uriPath: sourcePath, severity: 'error',
    code: 'validation.incompatibleCallArgumentList',
    message: 'No available callable signature accepts this argument list.',
    range: report.diagnostics[0]!.range,
    relatedInformation: [{
      location: { uriPath: targetPath, range: report.diagnostics[0]!.relatedInformation[0]!.location.range },
      message: "Candidate signature: Sub AcceptValue(ByRef value As Long). Mismatches: argument 1 for parameter 'value' ByRef type: expected Long, found Integer."
    }]
  }]);
  assert.deepEqual(collection.entries.get(sourcePath), diagnostics);
  reporter.refresh('semantic-build', JSON.stringify({ ...report, diagnostics: [] }));
  assert.equal(collection.entries.size, 0);
  assert.deepEqual(collection.deleted, [sourcePath]);
});

test('sourceAnalysis 3.0 validates every related location before changing existing Problems', () => {
  const report = semanticReport();
  const original = report.diagnostics[0]!;
  const related = original.relatedInformation[0]!;
  const location = related.location;
  const malformed: unknown[] = [
    null, {}, '',
    [null], [{ ...related, message: '' }], [{ ...related, message: undefined }],
    [{ ...related, location: null }],
    [{ ...related, location: { ...location, uri: 'Target.bas' } }],
    [{ ...related, location: { ...location, uri: 'vba-reference://library/Target' } }],
    [{ ...related, location: { ...location, range: null } }],
    [{ ...related, location: { ...location, range: { ...location.range, start: { line: -1, character: 0 } } } }],
    [{ ...related, location: { ...location, range: { ...location.range, end: { line: 1, character: 10 } } } }],
    [{ ...related, location: { ...location, range: { ...location.range, end: { line: 1, character: 12.5 } } } }],
    [{ ...related, location: { ...location, range: { ...location.range, end: { line: Number.MAX_SAFE_INTEGER + 1, character: 22 } } } }]
  ];
  for (const invalid of malformed) {
    const collection = new FakeDiagnosticCollection();
    const reporter = new VbaDevDiagnosticReporter(collection);
    const sourcePath = String.raw`C:\work\Caller.bas`;
    reporter.refresh('semantic-build', diagnosticJson(sourcePath, 'error', 'PREVIOUS'));
    const previous = collection.entries.get(sourcePath);
    assert.throws(() => reporter.refresh('semantic-build', JSON.stringify({
      ...report, diagnostics: [original, { ...original, relatedInformation: invalid }]
    })), VbaDevOutputContractError);
    assert.deepEqual(collection.entries.get(sourcePath), previous);
    assert.deepEqual(collection.deleted, []);
  }
});

function semanticReport() {
  return {
    type: 'sourceAnalysis', schemaVersion: '3.0', complete: true, failures: [],
    diagnostics: [{
      type: 'diagnostic', owner: 'vba-dev', uri: 'file:///C:/work/Caller.bas',
      code: 'validation.incompatibleCallArgumentList', severity: 'error',
      message: 'No available callable signature accepts this argument list.',
      range: { start: { line: 3, character: 16 }, end: { line: 3, character: 20 } },
      relatedInformation: [{
        location: {
          uri: 'file:///C:/work/Target.bas',
          range: { start: { line: 1, character: 11 }, end: { line: 1, character: 22 } }
        },
        message: "Candidate signature: Sub AcceptValue(ByRef value As Long). Mismatches: argument 1 for parameter 'value' ByRef type: expected Long, found Integer."
      }]
    }]
  };
}

function diagnosticJson(uriPath: string, severity: string, code: string): string {
  return JSON.stringify({
    type: 'diagnostic',
    owner: 'vba-dev',
    severity,
    uri: uriPath,
    range: {
      start: { line: 0, character: 0 },
      end: { line: 0, character: 1 }
    },
    message: `${code} message`,
    code
  });
}

class FakeDiagnosticCollection {
  public readonly entries = new Map<string, unknown[]>();
  public readonly deleted: string[] = [];

  public set(uriPath: string, diagnostics: readonly unknown[]): void {
    this.entries.set(uriPath, [...diagnostics]);
  }

  public delete(uriPath: string): void {
    this.deleted.push(uriPath);
    this.entries.delete(uriPath);
  }
}
