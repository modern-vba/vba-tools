import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { INITIAL, Registry, parseRawGrammar, type IToken } from 'vscode-textmate';
import { loadWASM, createOnigScanner, createOnigString } from 'vscode-oniguruma';

const onigLib = loadWASM(fs.readFileSync(
  require.resolve('vscode-oniguruma/release/onig.wasm')
)).then(() => ({ createOnigScanner, createOnigString }));

test('the contributed VBA grammar distinguishes class metadata from executable code', async () => {
  const lines = await tokenizeVba([
    'VERSION 1.0 CLASS',
    'BEGIN',
    "  MultiUse = -1  'True",
    'END',
    'Attribute VB_Name = "Worker"',
    'Public Sub Run()',
    '    End',
    'End Sub'
  ]);

  for (const line of lines.slice(0, 4)) {
    assert.ok(scopesAt(line, 0).includes('meta.class-header.vba'));
    assert.ok(line.every((token) => !token.scopes.includes('keyword.control.vba')));
  }
  assert.ok(scopesAt(lines[4], 0).includes('meta.attribute.vba'));
  assert.ok(scopesAt(lines[5], 0).includes('keyword.vba'));
  assert.ok(scopesAt(lines[6], 4).includes('keyword.control.vba'));
  assert.ok(scopesAt(lines[7], 0).includes('keyword.vba'));
  assert.ok(!scopesAt(lines[7], 0).includes('meta.class-header.vba'));
});

test('an incomplete class header recovers before attributes and executable code', async () => {
  for (const boundary of [
    { text: 'Attribute VB_Name = "Worker"', scope: 'meta.attribute.vba' },
    { text: 'Option Explicit', scope: 'keyword.vba' },
    { text: 'Public Sub Run()', scope: 'keyword.vba' },
    { text: 'value = Abs(-1)', scope: 'source.vba' },
    { text: 'value = True : End', scope: 'source.vba' },
    { text: 'value = "caption" : End', scope: 'source.vba' },
    { text: 'VERSION 1.0 CLASS', scope: 'source.vba' },
    { text: 'value = True _', scope: 'source.vba' }
  ]) {
    const lines = await tokenizeVba([
      'VERSION 1.0 CLASS',
      'BEGIN',
      "  MultiUse = -1  'True",
      boundary.text,
      'End Sub'
    ]);

    assert.ok(scopesAt(lines[2], 0).includes('meta.class-header.vba'));
    assert.ok(scopesAt(lines[3], 0).includes(boundary.scope), boundary.text);
    assert.ok(lines[3].every((token) => !token.scopes.includes('meta.class-header.vba')), boundary.text);
    assert.ok(scopesAt(lines[4], 0).includes('keyword.vba'), boundary.text);
  }
});

test('an unfinished metadata string cannot carry string state into the VBA body', async () => {
  const lines = await tokenizeVba([
    'VERSION 1.0 CLASS',
    'BEGIN',
    '  Caption = "unfinished',
    'Attribute VB_Name = "Worker"',
    'Option Explicit',
    'End Sub'
  ]);

  assert.ok(scopesAt(lines[2], 0).includes('meta.class-header.vba'));
  assert.ok(scopesAt(lines[3], 0).includes('meta.attribute.vba'));
  assert.ok(scopesAt(lines[4], 0).includes('keyword.vba'));
  assert.ok(scopesAt(lines[5], 0).includes('keyword.vba'));
  assert.ok(lines.slice(3).flat().every((token) => !token.scopes.includes('meta.class-header.vba')));
});

test('Unicode VBA whitespace cannot combine body tokens into a metadata property name', async () => {
  for (const whitespace of ['\u0019', '\u1680', '\u180e', '\u2002', '\u202f', '\u205f', '\u3000']) {
    const lines = await tokenizeVba([
      'VERSION 1.0 CLASS',
      'BEGIN',
      `Let${whitespace}value = True`,
      'End Sub'
    ]);

    assert.ok(lines[2].every((token) => !token.scopes.includes('meta.class-header.vba')),
      `U+${whitespace.charCodeAt(0).toString(16)}`);
    assert.ok(scopesAt(lines[3], 0).includes('keyword.vba'));
  }
});

test('leading trivia permits a class header without recognizing header text in comments', async () => {
  const lines = await tokenizeVba([
    '',
    "' VERSION 1.0 CLASS is an export signature",
    '  ',
    'VERSION 1.0 CLASS',
    'BEGIN',
    'END',
    'Option Explicit',
    'End'
  ]);

  assert.ok(scopesAt(lines[1], 0).includes('comment.line.apostrophe.vba'));
  assert.ok(!scopesAt(lines[1], 0).includes('meta.class-header.vba'));
  assert.ok(scopesAt(lines[3], 0).includes('meta.class-header.vba'));
  assert.ok(scopesAt(lines[5], 0).includes('meta.class-header.vba'));
  assert.ok(scopesAt(lines[6], 0).includes('keyword.vba'));
  assert.ok(scopesAt(lines[7], 0).includes('keyword.control.vba'));
});

test('class metadata accepts casing, whitespace, trivia, and scalar export properties', async () => {
  const header = [
    "VERSION 1.0 CLASS 'export",
    '\tBEGIN',
    "\tMultiUse = -1 'True",
    '',
    "  'metadata comment",
    '  Enabled = True',
    '  Hidden = False',
    '  Count = +2',
    '  Ratio = 1.5',
    '  Flags = &HFF',
    '  Caption = "say ""END"""',
    '  Pending =',
    "\tEND 'metadata boundary"
  ];
  const mixedCase = header.map((line) => [...line].map((character, index) => index % 2
    ? character.toUpperCase() : character.toLowerCase()).join(''));
  for (const variant of [header, header.map((line) => line.toLowerCase()), header.map((line) => line.toUpperCase()), mixedCase]) {
    const lines = await tokenizeVba([...variant, 'Option Explicit']);
    for (const [index, text] of variant.entries()) {
      if (text.length > 0) {
        assert.ok(lines[index].every((token) => token.scopes.includes('meta.class-header.vba')), text);
      }
    }
    assert.ok(lines.slice(0, -1).flat().every((token) => !token.scopes.includes('keyword.control.vba')));
    assert.ok(scopesAt(lines.at(-1)!, 0).includes('keyword.vba'));
  }
});

test('all existing End scopes resume after a class header', async () => {
  const body = ['End', 'End If', 'End With', 'End Select', 'End Sub', 'End Function', 'End Property'];
  const lines = await tokenizeVba(['VERSION 1.0 CLASS', 'BEGIN', 'END', ...body]);

  for (const [index, statement] of body.entries()) {
    const scope = index < 4 ? 'keyword.control.vba' : 'keyword.vba';
    assert.ok(scopesAt(lines[index + 3], 0).includes(scope), statement);
    assert.ok(lines[index + 3].every((token) => !token.scopes.includes('meta.class-header.vba')), statement);
  }
});

test('a completed class header cannot reopen before the first VBA statement', async () => {
  const lines = await tokenizeVba([
    'VERSION 1.0 CLASS',
    'BEGIN',
    'END',
    '',
    "'body comment",
    'VERSION 1.0 CLASS',
    'BEGIN',
    'End'
  ]);

  assert.ok(scopesAt(lines[2], 0).includes('meta.class-header.vba'));
  assert.ok(lines.slice(3).flat().every((token) => !token.scopes.includes('meta.class-header.vba')));
  assert.ok(scopesAt(lines[7], 0).includes('keyword.control.vba'));
});

test('headerless classes, standard modules, forms, and body lookalikes keep their existing scopes', async () => {
  const fixtures = [
    { name: 'headerless .cls', source: ['Attribute VB_Name = "Worker"', 'Public Sub Run()', 'End', 'End Sub'] },
    { name: '.bas', source: ['Attribute VB_Name = "Module1"', 'Option Explicit', 'End'] },
    { name: '.frm', source: ['VERSION 5.00', 'Begin VB.UserForm Form1', 'End', 'Attribute VB_Name = "Form1"'] },
    { name: 'comment only lookalike', source: ["' VERSION 1.0 CLASS", "' BEGIN", 'End'] },
    { name: 'string lookalike', source: ['"VERSION 1.0 CLASS"', 'End'] },
    { name: 'body lookalike', source: ['Option Explicit', 'VERSION 1.0 CLASS', 'BEGIN', 'End'] },
    { name: 'body after leading trivia', source: ['', "' comment", 'Public Sub Run()', 'VERSION 1.0 CLASS', 'End'] }
  ];
  for (const { name, source } of fixtures) {
    const lines = await tokenizeVba(source);
    assert.ok(lines.flat().every((token) => !token.scopes.includes('meta.class-header.vba')), name);
    const endLine = source.indexOf('End');
    assert.ok(scopesAt(lines[endLine], 0).includes('keyword.control.vba'), name);
  }
});

async function tokenizeVba(lines: readonly string[]): Promise<IToken[][]> {
  const manifest = JSON.parse(fs.readFileSync(
    path.join(process.cwd(), 'package.json'), 'utf8'
  )) as { contributes: { grammars: Array<{ language: string; scopeName: string; path: string }> } };
  const contribution = manifest.contributes.grammars.find((entry) => entry.language === 'vba');
  assert.ok(contribution, 'The extension must contribute a grammar for VBA documents');
  const grammarPath = path.resolve(process.cwd(), contribution.path);
  const registry = new Registry({
    onigLib,
    loadGrammar: async (scopeName) => scopeName === contribution.scopeName
      ? parseRawGrammar(fs.readFileSync(grammarPath, 'utf8'), grammarPath)
      : null
  });
  try {
    const grammar = await registry.loadGrammar(contribution.scopeName);
    assert.ok(grammar);
    let state = INITIAL;
    return lines.map((line) => {
      const result = grammar.tokenizeLine(line, state);
      state = result.ruleStack;
      return result.tokens;
    });
  } finally {
    registry.dispose();
  }
}

function scopesAt(tokens: readonly IToken[], column: number): readonly string[] {
  const token = tokens.find((candidate) => candidate.startIndex <= column && column < candidate.endIndex);
  assert.ok(token, `Expected a token at column ${column}`);
  return token.scopes;
}
