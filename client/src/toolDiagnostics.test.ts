import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  VbaDevToolDiagnosticReporter,
  parseVbaDevToolDiagnostics
} from './toolDiagnostics';

test('tool diagnostics map machine-readable records with severity uri range message and code', () => {
  const filePath = path.join('C:', 'work', 'BookProject', 'src', 'Book1', 'Module1.bas');

  const diagnostics = parseVbaDevToolDiagnostics(JSON.stringify({
    type: 'diagnostic',
    owner: 'vba-devtool',
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
      owner: 'vba-devtool',
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
  const filePath = path.join('C:', 'work', 'BookProject', 'project.json');

  const diagnostics = parseVbaDevToolDiagnostics(JSON.stringify({
    diagnostics: [
      {
        type: 'diagnostic',
        source: 'vba-devtool',
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
      owner: 'vba-devtool',
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
  const diagnostics = parseVbaDevToolDiagnostics([
    '[FAIL] CommonModules (Book1/Missing): Unknown CommonModuleName',
    JSON.stringify({
      type: 'diagnostic',
      owner: 'vba-devtool',
      severity: 'error',
      message: 'Missing URI and range.',
      code: 'VBACOMMON001'
    })
  ].join('\n'));

  assert.deepEqual(diagnostics, []);
});

test('tool diagnostics map severity aliases', () => {
  const filePath = path.join('C:', 'work', 'BookProject', 'project.json');
  const diagnostics = parseVbaDevToolDiagnostics([
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
  const reporter = new VbaDevToolDiagnosticReporter(collection);

  reporter.refresh('project:C:/work/BookProject', diagnosticJson(firstPath, 'error', 'E001'));
  reporter.refresh('project:C:/work/BookProject', diagnosticJson(secondPath, 'warning', 'W001'));

  assert.deepEqual(collection.deleted, [firstPath]);
  assert.equal(collection.entries.has(firstPath), false);
  assert.deepEqual([...collection.entries.keys()], [secondPath]);
});

function diagnosticJson(uriPath: string, severity: string, code: string): string {
  return JSON.stringify({
    type: 'diagnostic',
    owner: 'vba-devtool',
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
