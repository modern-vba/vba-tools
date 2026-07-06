import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildVbaProject,
  getDocumentFormattingEdits,
  getCompletions,
  getDefinition,
  getHover,
  getModuleIdentities,
  getModuleMemberRanges,
  getRenameEdits,
  getRenameTarget,
  getSemanticTokens,
  getSignatureHelp,
  getSyntaxDiagnostics,
  getTypeFields,
  resolveName,
  updateVbaProjectFile
} from './vbaProject';
import { getBundledExcelHostDefinitions } from './excelHostCatalog';

test('VbaProject loads the bundled Excel HostDefinition catalog by default', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit'
      ].join('\n')
    }
  ]);
  const catalog = getBundledExcelHostDefinitions();
  const host_names = project.hostDefinitions.map((definition) => definition.name);

  assert.deepEqual(
    catalog.map((definition) => definition.name),
    ['Application', 'Workbook', 'Worksheet', 'Range', 'XlDirection']
  );
  assert.deepEqual(host_names, ['Application', 'Workbook', 'Worksheet', 'Range', 'XlDirection']);
});

test('syntax diagnostics report invalid trailing-comment code continuations', () => {
  const invalid_line = '        "needle", _ \' comment';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.invalidTrailingCommentContinuation',
      message: 'Code line-continuation marker cannot be followed by a comment.',
      range: {
        start: { line: 5, character: invalid_line.indexOf('_') },
        end: { line: 5, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report continuation markers missing preceding whitespace', () => {
  const invalid_line = '    ReadValue(_';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        '        "needle")',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.invalidContinuationMarkerSpacing',
      message: 'Code line-continuation marker must be preceded by whitespace.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('_') },
        end: { line: 4, character: invalid_line.indexOf('_') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report continuation markers followed by source text', () => {
  const invalid_line = '    Dim value _ As Long';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.invalidContinuationMarkerText',
      message: 'Code line-continuation marker cannot be followed by source text.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('_') },
        end: { line: 4, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report continuation markers without a continued fragment', () => {
  const invalid_line = '    ReadValue _';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        '',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.incompleteContinuation',
      message: 'Code line-continuation marker must be followed by continued source text.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('_') },
        end: { line: 4, character: invalid_line.indexOf('_') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report missing continuation markers before continued source text', () => {
  const invalid_line = '    ReadValue(';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        '        "needle")',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.missingContinuationMarker',
      message: 'Continued source text requires a code line-continuation marker.',
      range: {
        start: { line: 4, character: invalid_line.length },
        end: { line: 4, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics return multiple invalid trailing-comment code continuations', () => {
  const first_invalid_line = '        "needle", _ \' first';
  const second_invalid_line = '        "haystack", _ \' second';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        first_invalid_line,
        second_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.invalidTrailingCommentContinuation',
      message: 'Code line-continuation marker cannot be followed by a comment.',
      range: {
        start: { line: 5, character: first_invalid_line.indexOf('_') },
        end: { line: 5, character: first_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.invalidTrailingCommentContinuation',
      message: 'Code line-continuation marker cannot be followed by a comment.',
      range: {
        start: { line: 6, character: second_invalid_line.indexOf('_') },
        end: { line: 6, character: second_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid continuations and apostrophe comments containing underscores', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "needle", _',
        '        "haystack") \' comment _',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
});

test('syntax diagnostics ignore Rem comments containing continuation markers', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Rem comment _',
        '    Dim example_val As Long',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
});

test('syntax diagnostics ignore Rem comments ending with continuation markers at end of file', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Rem module comment _'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
});

test('physical-line syntax diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = '    ReadValue(_';
  const form_invalid_line = '    Dim value _ As Long';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        class_invalid_line,
        '        "needle")',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "needle" _ As Long',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.invalidContinuationMarkerSpacing',
      message: 'Code line-continuation marker must be preceded by whitespace.',
      range: {
        start: { line: 5, character: class_invalid_line.indexOf('_') },
        end: { line: 5, character: class_invalid_line.indexOf('_') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.invalidContinuationMarkerText',
      message: 'Code line-continuation marker cannot be followed by source text.',
      range: {
        start: { line: 8, character: form_invalid_line.indexOf('_') },
        end: { line: 8, character: form_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('physical-line syntax diagnostics preserve valid regions and invalid fail-closed behavior', () => {
  const invalid_line = '    ReadValue(';
  const active_line = '        ';
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        invalid_line,
        '        "id",',
        active_line,
        'End Sub',
        '',
        'Public Sub ValidRegion()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(
    getSyntaxDiagnostics(project, 'file:///project/Worker.bas').map((diagnostic) => diagnostic.code),
    ['syntax.missingContinuationMarker']
  );
  assert.equal(
    getSignatureHelp(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 9, character: active_line.length }
    }),
    undefined
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 13, character: chain_line.length }
    }).map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('syntax diagnostics report unterminated string literals', () => {
  const invalid_line = '    value = "unterminated';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.unterminatedStringLiteral',
      message: 'String literal is missing a closing double quote.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('"') },
        end: { line: 4, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed escaped string literal quotes', () => {
  const invalid_line = '    value = "malformed ""escape""';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.unterminatedStringLiteral',
      message: 'String literal is missing a closing double quote.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('"') },
        end: { line: 4, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed date literals and invalid source characters', () => {
  const malformed_date_line = '    started = #not-a-date#';
  const invalid_character_line = '    value = `bad';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        malformed_date_line,
        invalid_character_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedDateLiteral',
      message: 'Date literal is malformed.',
      range: {
        start: { line: 4, character: malformed_date_line.indexOf('#') },
        end: { line: 4, character: malformed_date_line.lastIndexOf('#') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.invalidSourceCharacter',
      message: 'Character cannot begin a supported VBA token.',
      range: {
        start: { line: 5, character: invalid_character_line.indexOf('`') },
        end: { line: 5, character: invalid_character_line.indexOf('`') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report unterminated date literals', () => {
  const invalid_line = '    started = #1/2/2024';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.unterminatedDateLiteral',
      message: 'Date literal is missing a closing # delimiter.',
      range: {
        start: { line: 4, character: invalid_line.indexOf('#') },
        end: { line: 4, character: invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid lexical forms and comments', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim ordinary_identifier As String',
        '    ordinary_identifier = "a ""quoted"" value"',
        '    Dim started As Date',
        '    started = #1/2/2024#',
        '    value = 1 \' #not-a-date# "unterminated `',
        '    Rem #not-a-date# "unterminated `',
        '#If VBA7 Then',
        '    Debug.Print #1',
        '#End If',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
});

test('lexical syntax diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = '    value = "unterminated';
  const form_invalid_line = '    value = `bad';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        class_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "unterminated `',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.unterminatedStringLiteral',
      message: 'String literal is missing a closing double quote.',
      range: {
        start: { line: 5, character: class_invalid_line.indexOf('"') },
        end: { line: 5, character: class_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.invalidSourceCharacter',
      message: 'Character cannot begin a supported VBA token.',
      range: {
        start: { line: 8, character: form_invalid_line.indexOf('`') },
        end: { line: 8, character: form_invalid_line.indexOf('`') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('lexical syntax diagnostics preserve valid regions and invalid fail-closed behavior', () => {
  const invalid_line = '    value = "unterminated';
  const active_line = '        ';
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue(',
        invalid_line,
        active_line,
        'End Sub',
        '',
        'Public Sub ValidRegion()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getSyntaxDiagnostics(project, 'file:///project/Worker.bas').length, 2);
  assert.equal(
    getSignatureHelp(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 9, character: active_line.length }
    }),
    undefined
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 13, character: chain_line.length }
    }).map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('syntax diagnostics report unexpected tokens after complete statements', () => {
  const option_line = 'Option Explicit extra';
  const exit_line = '    Exit Sub extra';
  const next_line = '    Next index extra';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        option_line,
        '',
        'Public Sub Run()',
        exit_line,
        next_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token after a complete statement.',
      range: {
        start: { line: 1, character: option_line.indexOf('extra') },
        end: { line: 1, character: option_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token after a complete statement.',
      range: {
        start: { line: 4, character: exit_line.indexOf('extra') },
        end: { line: 4, character: exit_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token after a complete statement.',
      range: {
        start: { line: 5, character: next_line.indexOf('extra') },
        end: { line: 5, character: next_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed statement separators and stray punctuation', () => {
  const separator_line = '    Debug.Print "x" :: Debug.Print "y"';
  const punctuation_line = '    , value = 1';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        separator_line,
        punctuation_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.invalidStatementSeparator',
      message: 'Statement separator cannot create an empty statement.',
      range: {
        start: { line: 4, character: separator_line.lastIndexOf(':') },
        end: { line: 4, character: separator_line.lastIndexOf(':') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token at statement start.',
      range: {
        start: { line: 5, character: punctuation_line.indexOf(',') },
        end: { line: 5, character: punctuation_line.indexOf(',') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid statement boundaries labels line numbers and comments', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'Start:',
        '10 Debug.Print "x": Debug.Print "y": GoTo Start',
        '    value = 1 \' End Sub extra :: ,',
        '    Rem End Sub extra :: ,',
        '    For index = 1 To 1',
        '    Next index',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
});

test('unexpected-token diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = '    Wend extra';
  const form_invalid_line = '    End If extra';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        class_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "End If extra"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token after a complete statement.',
      range: {
        start: { line: 5, character: class_invalid_line.indexOf('extra') },
        end: { line: 5, character: class_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.unexpectedToken',
      message: 'Unexpected token after a complete statement.',
      range: {
        start: { line: 8, character: form_invalid_line.indexOf('extra') },
        end: { line: 8, character: form_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('unexpected-token diagnostics preserve valid statements before and after recovery', () => {
  const invalid_line = '    Exit Sub extra';
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Debug.Print "before"',
        invalid_line,
        'End Sub',
        '',
        'Public Sub ValidRegion()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getSyntaxDiagnostics(project, 'file:///project/Worker.bas').length, 1);
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 9, character: chain_line.length }
    }).map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('syntax diagnostics report malformed Attribute and Option statements', () => {
  const attribute_line = 'Attribute VB_Name = Worker';
  const option_base_line = 'Option Base 2';
  const option_compare_line = 'Option Compare Locale';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        attribute_line,
        option_base_line,
        option_compare_line,
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedAttribute',
      message: 'Attribute statement is malformed.',
      range: {
        start: { line: 0, character: attribute_line.indexOf('Worker') },
        end: { line: 0, character: attribute_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedOption',
      message: 'Option Base must be 0 or 1.',
      range: {
        start: { line: 1, character: option_base_line.indexOf('2') },
        end: { line: 1, character: option_base_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedOption',
      message: 'Option Compare must be Binary, Text, or Database.',
      range: {
        start: { line: 2, character: option_compare_line.indexOf('Locale') },
        end: { line: 2, character: option_compare_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('ModuleIdentity falls back when Attribute VB_Name is absent or malformed', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Fallback.bas',
      text: [
        'Attribute VB_Name = FallbackBroken',
        'Option Explicit'
      ].join('\n')
    },
    {
      uri: 'file:///project/Absent.bas',
      text: 'Option Explicit'
    }
  ]);

  assert.deepEqual(getModuleIdentities(project), ['Fallback', 'Absent']);
});

test('syntax diagnostics ignore valid exported bas cls and frm headers', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Attribute VB_Description = "Worker module"',
        'Option Explicit',
        'Option Base 1',
        'Option Compare Text',
        'Option Private Module',
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Attribute VB_PredeclaredId = False',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "Dialog"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Attribute VB_PredeclaredId = False',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Customer.cls'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), []);
});

test('syntax diagnostics report misplaced module header statements', () => {
  const misplaced_option_line = 'Option Explicit';
  const misplaced_attribute_line = 'Attribute VB_Name = "LateName"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        '',
        'Public Sub Run()',
        'End Sub',
        misplaced_option_line,
        misplaced_attribute_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.misplacedHeaderStatement',
      message: 'Module header statement must appear before code members.',
      range: {
        start: { line: 4, character: 0 },
        end: { line: 4, character: misplaced_option_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.misplacedHeaderStatement',
      message: 'Module header statement must appear before code members.',
      range: {
        start: { line: 5, character: 0 },
        end: { line: 5, character: misplaced_attribute_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('frm header boundary ignores designer text and diagnoses code after Attribute boundary', () => {
  const malformed_attribute_line = 'Attribute VB_Name = Dialog';
  const invalid_code_line = 'Option Base 2';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "unterminated `',
        'End',
        malformed_attribute_line,
        invalid_code_line,
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedAttribute',
      message: 'Attribute statement is malformed.',
      range: {
        start: { line: 4, character: malformed_attribute_line.indexOf('Dialog') },
        end: { line: 4, character: malformed_attribute_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedOption',
      message: 'Option Base must be 0 or 1.',
      range: {
        start: { line: 5, character: invalid_code_line.indexOf('2') },
        end: { line: 5, character: invalid_code_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed callable declaration signatures', () => {
  const missing_name_line = 'Public Sub';
  const missing_paren_line = 'Public Function ReadValue(ByVal Key As String';
  const missing_return_type_line = 'Public Function Convert() As';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        missing_name_line,
        missing_paren_line,
        'End Function',
        missing_return_type_line,
        'End Function'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable declaration is missing a name.',
      range: {
        start: { line: 3, character: missing_name_line.length },
        end: { line: 3, character: missing_name_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable parameter list is missing a closing parenthesis.',
      range: {
        start: { line: 4, character: missing_paren_line.indexOf('(') },
        end: { line: 4, character: missing_paren_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable return type is missing after As.',
      range: {
        start: { line: 6, character: missing_return_type_line.indexOf('As') },
        end: { line: 6, character: missing_return_type_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed callable parameter lists and Declare statements', () => {
  const invalid_paramarray_line = 'Public Sub Run(Optional ParamArray Values() As Variant)';
  const empty_default_line = 'Public Function Read(Optional ByVal Fallback As String = ) As String';
  const missing_lib_line = 'Public Declare PtrSafe Function FindWindow(ByVal Caption As String) As LongPtr';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        invalid_paramarray_line,
        'End Sub',
        empty_default_line,
        'End Function',
        missing_lib_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'ParamArray cannot be combined with Optional.',
      range: {
        start: { line: 3, character: invalid_paramarray_line.indexOf('ParamArray') },
        end: { line: 3, character: invalid_paramarray_line.indexOf('ParamArray') + 'ParamArray'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Optional parameter default value is missing.',
      range: {
        start: { line: 5, character: empty_default_line.indexOf('=') },
        end: { line: 5, character: empty_default_line.indexOf('=') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Declare statement must specify Lib "library".',
      range: {
        start: { line: 7, character: missing_lib_line.indexOf('FindWindow') },
        end: { line: 7, character: missing_lib_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report invalid callable declaration modifiers', () => {
  const invalid_order_line = 'Static Public Sub Run()';
  const incompatible_visibility_line = 'Public Private Function Read() As String';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        invalid_order_line,
        'End Sub',
        incompatible_visibility_line,
        'End Function'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Visibility modifier must precede Static in a callable declaration.',
      range: {
        start: { line: 3, character: invalid_order_line.indexOf('Public') },
        end: { line: 3, character: invalid_order_line.indexOf('Public') + 'Public'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable declaration has incompatible visibility modifiers.',
      range: {
        start: { line: 5, character: incompatible_visibility_line.indexOf('Private') },
        end: { line: 5, character: incompatible_visibility_line.indexOf('Private') + 'Private'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid callable declarations and preserve signatures', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Declare PtrSafe Function FindWindow Lib "user32" Alias "FindWindowA" (ByVal Caption As String) As LongPtr',
        'Public Event Completed(ByVal Result As String)',
        'Public Property Get DisplayName() As String',
        'End Property',
        'Public Property Let DisplayName(ByVal Value As String)',
        'End Property',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function',
        'Public Sub Run()',
        '    ReadValue("id", ',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
  assert.deepEqual(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: 20 }
  })?.label, 'ReadValue(Key, Optional Fallback) As String');
});

test('callable declaration diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = 'Public Property Set (ByVal Value As Object)';
  const form_invalid_line = 'Public Event Click(ByVal Button As Integer';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        class_invalid_line,
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "Public Event Click(ByVal Button As Integer"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        form_invalid_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Customer.cls'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable declaration is missing a name.',
      range: {
        start: { line: 4, character: class_invalid_line.indexOf('(') },
        end: { line: 4, character: class_invalid_line.indexOf('(') }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable parameter list is missing a closing parenthesis.',
      range: {
        start: { line: 7, character: form_invalid_line.indexOf('(') },
        end: { line: 7, character: form_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('malformed callable declarations keep surrounding ModuleMember ranges usable', () => {
  const malformed_line = 'Public Function ReadValue(ByVal Key As String';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        malformed_line,
        'End Function'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedCallableDeclaration',
      message: 'Callable parameter list is missing a closing parenthesis.',
      range: {
        start: { line: 3, character: malformed_line.indexOf('(') },
        end: { line: 3, character: malformed_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getModuleMemberRanges(project, 'file:///project/Worker.bas'), [
    {
      start: { line: 3, character: 0 },
      end: { line: 4, character: 'End Function'.length }
    }
  ]);
});

test('syntax diagnostics report malformed declaration statements', () => {
  const missing_name_line = 'Dim As String';
  const multiple_malformed_line = 'Dim first As, second(1 To ) As Long';
  const missing_initializer_line = 'Const RATE As Double =';
  const redim_bounds_line = 'ReDim Preserve values(1 To )';
  const missing_static_type_line = 'Static cache As';
  const malformed_def_type_line = 'DefInt A-, 1-Z';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        `    ${missing_name_line}`,
        `    ${multiple_malformed_line}`,
        `    ${missing_initializer_line}`,
        `    ${redim_bounds_line}`,
        `    ${missing_static_type_line}`,
        `    ${malformed_def_type_line}`,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedDeclaration',
      message: 'Declaration is missing an identifier.',
      range: {
        start: { line: 4, character: 4 + missing_name_line.indexOf('As') },
        end: { line: 4, character: 4 + missing_name_line.indexOf('As') + 'As'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'Declaration type annotation is missing a type.',
      range: {
        start: { line: 5, character: 4 + multiple_malformed_line.indexOf('As') },
        end: { line: 5, character: 4 + multiple_malformed_line.indexOf(',') }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'Array bounds are malformed.',
      range: {
        start: { line: 5, character: 4 + multiple_malformed_line.indexOf('1 To') },
        end: { line: 5, character: 4 + multiple_malformed_line.indexOf(')') }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'Constant initializer is missing.',
      range: {
        start: { line: 6, character: 4 + missing_initializer_line.indexOf('=') },
        end: { line: 6, character: 4 + missing_initializer_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'Array bounds are malformed.',
      range: {
        start: { line: 7, character: 4 + redim_bounds_line.indexOf('1 To') },
        end: { line: 7, character: 4 + redim_bounds_line.indexOf(')') }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'Declaration type annotation is missing a type.',
      range: {
        start: { line: 8, character: 4 + missing_static_type_line.indexOf('As') },
        end: { line: 8, character: 4 + missing_static_type_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'DefType declaration range is malformed.',
      range: {
        start: { line: 9, character: 4 + malformed_def_type_line.indexOf('A-') },
        end: { line: 9, character: 4 + malformed_def_type_line.indexOf('A-') + 'A-'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclaration',
      message: 'DefType declaration range is malformed.',
      range: {
        start: { line: 9, character: 4 + malformed_def_type_line.indexOf('1-Z') },
        end: { line: 9, character: 4 + malformed_def_type_line.indexOf('1-Z') + '1-Z'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed constant initializer expressions', () => {
  const invalid_initializer_line = 'Const Broken = 1 +';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        `    ${invalid_initializer_line}`,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedDeclaration',
      message: 'Constant initializer is malformed.',
      range: {
        start: { line: 4, character: 4 + invalid_initializer_line.indexOf('1 +') },
        end: { line: 4, character: 4 + invalid_initializer_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid declarations and preserve type resolution', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        'Private moduleWs As Worksheet',
        'Public Const Version As String = "1.0"',
        'DefInt A-Z',
        '',
        'Public Sub Run()',
        '    Dim ws As Worksheet',
        '    Static names(0 To 2) As String',
        '    Dim created As New Customer',
        '    Const Limit As Long = 10',
        '    ReDim values(1 To 10) As Long',
        '    ws.Na',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        'Private WithEvents Button As CommandButton',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "Dim As String"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        'Private formValue As String'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Customer.cls'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), []);

  const completions = getCompletions(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: 9 }
  });
  assert.deepEqual(
    completions.map((item) => item.label),
    ['Name']
  );
});

test('malformed declarations fail closed without guessed type metadata', () => {
  const malformed_type_line = 'Dim ws As';
  const malformed_with_events_line = 'Private WithEvents As CommandButton';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        `    ${malformed_type_line}`,
        '    ws.Na',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/FormModule.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "FormModule"',
        'Option Explicit',
        malformed_with_events_line,
        '',
        'Private Sub Button_Click()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.bas'), [
    {
      code: 'syntax.malformedDeclaration',
      message: 'Declaration type annotation is missing a type.',
      range: {
        start: { line: 4, character: 4 + malformed_type_line.indexOf('As') },
        end: { line: 4, character: 4 + malformed_type_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/FormModule.cls'), [
    {
      code: 'syntax.malformedDeclaration',
      message: 'WithEvents declaration is missing an identifier.',
      range: {
        start: { line: 3, character: malformed_with_events_line.indexOf('As') },
        end: { line: 3, character: malformed_with_events_line.indexOf('As') + 'As'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 9 }
  }), []);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 5, character: 20 }
  }), {
    uri: 'file:///project/FormModule.cls',
    range: {
      start: { line: 5, character: 12 },
      end: { line: 5, character: 24 }
    }
  });
});

test('syntax diagnostics report malformed enum and type declaration blocks', () => {
  const missing_enum_name_line = 'Public Enum';
  const bad_enum_initializer_line = '    Broken = 1 +';
  const invalid_enum_statement_line = '    Dim NotAllowed As Long';
  const invalid_type_visibility_line = 'Friend Type BadRecord';
  const missing_field_type_line = '    Name As';
  const bad_field_bounds_line = '    Scores(1 To ) As Long';
  const invalid_type_statement_line = '    Sub Bad()';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Records.bas',
      text: [
        'Attribute VB_Name = "Records"',
        'Option Explicit',
        '',
        missing_enum_name_line,
        bad_enum_initializer_line,
        invalid_enum_statement_line,
        'End Enum',
        invalid_type_visibility_line,
        missing_field_type_line,
        bad_field_bounds_line,
        invalid_type_statement_line,
        'End Type'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Records.bas'), [
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Enum declaration is missing a name.',
      range: {
        start: { line: 3, character: missing_enum_name_line.length },
        end: { line: 3, character: missing_enum_name_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Enum member initializer is malformed.',
      range: {
        start: { line: 4, character: bad_enum_initializer_line.indexOf('1 +') },
        end: { line: 4, character: bad_enum_initializer_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Statement is not valid inside an Enum block.',
      range: {
        start: { line: 5, character: invalid_enum_statement_line.search(/\S/) },
        end: { line: 5, character: invalid_enum_statement_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Type declaration has an invalid visibility modifier.',
      range: {
        start: { line: 7, character: 0 },
        end: { line: 7, character: 'Friend'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Declaration type annotation is missing a type.',
      range: {
        start: { line: 8, character: missing_field_type_line.indexOf('As') },
        end: { line: 8, character: missing_field_type_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Array bounds are malformed.',
      range: {
        start: { line: 9, character: bad_field_bounds_line.indexOf('1 To') },
        end: { line: 9, character: bad_field_bounds_line.indexOf(')') }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Statement is not valid inside a Type block.',
      range: {
        start: { line: 10, character: invalid_type_statement_line.search(/\S/) },
        end: { line: 10, character: invalid_type_statement_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report missing unexpected and mismatched enum type closers', () => {
  const unexpected_closer_line = 'End Enum';
  const enum_header_line = 'Public Enum RunMode';
  const mismatched_closer_line = 'End Type';
  const missing_type_header_line = 'Public Type MissingRecord';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Records.bas',
      text: [
        'Attribute VB_Name = "Records"',
        'Option Explicit',
        '',
        unexpected_closer_line,
        enum_header_line,
        '    Manual',
        mismatched_closer_line,
        missing_type_header_line,
        '    Value As String'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Records.bas'), [
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Unexpected End Enum without a matching Enum block.',
      range: {
        start: { line: 3, character: 0 },
        end: { line: 3, character: unexpected_closer_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Mismatched declaration block closer; expected End Enum.',
      range: {
        start: { line: 6, character: 0 },
        end: { line: 6, character: mismatched_closer_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Type block is missing End Type.',
      range: {
        start: { line: 7, character: missing_type_header_line.indexOf('Type') },
        end: { line: 7, character: missing_type_header_line.indexOf('Type') + 'Type'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('declaration block diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_bad_field_line = '    Value As';
  const form_bad_enum_line = '    Missing = 1 +';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        'Private Type LocalRecord',
        class_bad_field_line,
        'End Type'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "Public Enum"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        'Public Enum FormMode',
        form_bad_enum_line,
        'End Enum'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Customer.cls'), [
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Declaration type annotation is missing a type.',
      range: {
        start: { line: 4, character: class_bad_field_line.indexOf('As') },
        end: { line: 4, character: class_bad_field_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedDeclarationBlock',
      message: 'Enum member initializer is malformed.',
      range: {
        start: { line: 7, character: form_bad_enum_line.indexOf('1 +') },
        end: { line: 7, character: form_bad_enum_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid enum and type blocks while preserving definitions', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Manual',
        '    CustomerRecord',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Records.bas',
      text: [
        'Attribute VB_Name = "Records"',
        'Option Explicit',
        '',
        'Public Enum RunMode',
        '    Automatic = 0',
        '    Manual',
        'End Enum',
        '',
        'Public Type CustomerRecord',
        '    Id As Long',
        '    Name As String * 20',
        'End Type'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Records.bas'), []);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 6 }
  }), {
    uri: 'file:///project/Records.bas',
    range: {
      start: { line: 5, character: 4 },
      end: { line: 5, character: 10 }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), {
    uri: 'file:///project/Records.bas',
    range: {
      start: { line: 8, character: 12 },
      end: { line: 8, character: 26 }
    }
  });
  assert.deepEqual(getTypeFields(project, 'CustomerRecord'), [
    {
      name: 'Id',
      range: {
        start: { line: 9, character: 4 },
        end: { line: 9, character: 6 }
      }
    },
    {
      name: 'Name',
      range: {
        start: { line: 10, character: 4 },
        end: { line: 10, character: 8 }
      }
    }
  ]);
});

test('syntax diagnostics report missing unexpected and mismatched executable block closers', () => {
  const mismatched_line = '    End If';
  const unexpected_line = '    Next';
  const missing_function_line = 'Public Function Missing() As String';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Blocks.bas',
      text: [
        'Attribute VB_Name = "Blocks"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    If ready Then',
        '        With target',
        mismatched_line,
        unexpected_line,
        'End Sub',
        missing_function_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Blocks.bas'), [
    {
      code: 'syntax.malformedBlockStructure',
      message: 'Mismatched block closer; expected End With.',
      range: {
        start: { line: 6, character: mismatched_line.search(/\S/) },
        end: { line: 6, character: mismatched_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedBlockStructure',
      message: 'Unexpected Next without a matching For block.',
      range: {
        start: { line: 7, character: unexpected_line.search(/\S/) },
        end: { line: 7, character: unexpected_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedBlockStructure',
      message: 'Function block is missing End Function.',
      range: {
        start: { line: 9, character: missing_function_line.indexOf('Function') },
        end: { line: 9, character: missing_function_line.indexOf('Function') + 'Function'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getModuleMemberRanges(project, 'file:///project/Blocks.bas'), [
    {
      start: { line: 3, character: 0 },
      end: { line: 8, character: 'End Sub'.length }
    },
    {
      start: { line: 9, character: 0 },
      end: { line: 9, character: missing_function_line.length }
    }
  ]);
});

test('syntax diagnostics ignore valid nested executable block structure', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Blocks.bas',
      text: [
        'Attribute VB_Name = "Blocks"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    For Each item In items',
        '        If item.Enabled Then',
        '            Do',
        '            Loop',
        '        ElseIf item.Pending Then',
        '            While item.Ready',
        '            Wend',
        '        Else',
        '            With item',
        '                Select Case item.Kind',
        '                Case 1',
        '                    item.Value = 1',
        '                Case Else',
        '                    item.Value = 0',
        '                End Select',
        '            End With',
        '        End If',
        '    Next',
        'End Sub',
        '',
        'Public Function Read() As String',
        'End Function',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Blocks.bas'), []);
});

test('block structure diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_mismatched_line = 'End Sub';
  const form_unexpected_line = 'Wend';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Runner.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Runner"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Do',
        class_mismatched_line
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "Wend"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        form_unexpected_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Runner.cls'), [
    {
      code: 'syntax.malformedBlockStructure',
      message: 'Mismatched block closer; expected Loop.',
      range: {
        start: { line: 6, character: 0 },
        end: { line: 6, character: class_mismatched_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedBlockStructure',
      message: 'Unexpected Wend without a matching While block.',
      range: {
        start: { line: 6, character: 0 },
        end: { line: 6, character: form_unexpected_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed control-flow openers and clauses', () => {
  const if_line = '    If ready';
  const elseif_line = '    ElseIf pending';
  const select_line = '    Select Case';
  const case_line = '    Case';
  const for_line = '    For index = 1';
  const for_each_line = '    For Each item In';
  const loop_line = '    Loop While';
  const with_line = '    With';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Flow.bas',
      text: [
        'Attribute VB_Name = "Flow"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        if_line,
        elseif_line,
        select_line,
        case_line,
        for_line,
        for_each_line,
        loop_line,
        with_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Flow.bas'), [
    {
      code: 'syntax.malformedControlFlow',
      message: 'If block opener must include Then.',
      range: {
        start: { line: 4, character: if_line.search(/\S/) },
        end: { line: 4, character: if_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'ElseIf clause must include Then.',
      range: {
        start: { line: 5, character: elseif_line.search(/\S/) },
        end: { line: 5, character: elseif_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'Select Case opener must include an expression.',
      range: {
        start: { line: 6, character: select_line.search(/\S/) },
        end: { line: 6, character: select_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'Case clause must include an expression or Else.',
      range: {
        start: { line: 7, character: case_line.search(/\S/) },
        end: { line: 7, character: case_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'For opener must include a start expression and To expression.',
      range: {
        start: { line: 8, character: for_line.search(/\S/) },
        end: { line: 8, character: for_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'For Each opener must include an item and collection expression.',
      range: {
        start: { line: 9, character: for_each_line.search(/\S/) },
        end: { line: 9, character: for_each_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'Loop While clause must include a condition.',
      range: {
        start: { line: 10, character: loop_line.search(/\S/) },
        end: { line: 10, character: loop_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'With opener must include a receiver expression.',
      range: {
        start: { line: 11, character: with_line.search(/\S/) },
        end: { line: 11, character: with_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report out-of-order Else and Case clauses', () => {
  const late_elseif_line = '    ElseIf pending Then';
  const duplicate_else_line = '    Else';
  const late_case_line = '    Case 1';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Flow.bas',
      text: [
        'Attribute VB_Name = "Flow"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    If ready Then',
        '    Else',
        late_elseif_line,
        duplicate_else_line,
        '    End If',
        '    Select Case mode',
        '    Case Else',
        late_case_line,
        '    End Select',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Flow.bas'), [
    {
      code: 'syntax.malformedControlFlow',
      message: 'ElseIf cannot appear after Else in the same If block.',
      range: {
        start: { line: 6, character: late_elseif_line.search(/\S/) },
        end: { line: 6, character: late_elseif_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'Else cannot appear more than once in the same If block.',
      range: {
        start: { line: 7, character: duplicate_else_line.search(/\S/) },
        end: { line: 7, character: duplicate_else_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedControlFlow',
      message: 'Case cannot appear after Case Else in the same Select block.',
      range: {
        start: { line: 11, character: late_case_line.search(/\S/) },
        end: { line: 11, character: late_case_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid control-flow forms in bas cls and frm code', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Flow.bas',
      text: [
        'Attribute VB_Name = "Flow"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    If ready Then value = 1 Else value = 2',
        '    If ready Then',
        '    ElseIf pending Then',
        '    Else',
        '    End If',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Runner.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Runner"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    For Each item In items',
        '        Do While item.Ready',
        '        Loop Until item.Done',
        '        While item.Pending',
        '        Wend',
        '    Next item',
        '    With item',
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "If ready"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        'Public Sub Run()',
        '    Select Case mode',
        '    Case 1',
        '    Case Else',
        '    End Select',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Flow.bas'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Runner.cls'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), []);
});

test('malformed With receiver syntax fails closed without guessed member completion', () => {
  const with_line = '    With';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Flow.bas',
      text: [
        'Attribute VB_Name = "Flow"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        with_line,
        '        .Na',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Flow.bas'), [
    {
      code: 'syntax.malformedControlFlow',
      message: 'With opener must include a receiver expression.',
      range: {
        start: { line: 4, character: with_line.search(/\S/) },
        end: { line: 4, character: with_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Flow.bas',
    position: { line: 5, character: 11 }
  }), []);
});

test('syntax diagnostics report malformed expressions operators and parentheses', () => {
  const trailing_operator_line = '    value = 1 +';
  const missing_paren_line = '    other = (1 + 2';
  const unexpected_paren_line = '    third = )1';
  const missing_condition_operand_line = '    If value > Then';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Expressions.bas',
      text: [
        'Attribute VB_Name = "Expressions"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        trailing_operator_line,
        missing_paren_line,
        unexpected_paren_line,
        missing_condition_operand_line,
        '    End If',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Expressions.bas'), [
    {
      code: 'syntax.malformedExpression',
      message: 'Expression is missing an operand after this operator.',
      range: {
        start: { line: 4, character: trailing_operator_line.indexOf('+') },
        end: { line: 4, character: trailing_operator_line.indexOf('+') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedExpression',
      message: 'Parenthesized expression is missing a closing parenthesis.',
      range: {
        start: { line: 5, character: missing_paren_line.indexOf('(') },
        end: { line: 5, character: missing_paren_line.indexOf('(') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedExpression',
      message: 'Unexpected closing parenthesis in expression.',
      range: {
        start: { line: 6, character: unexpected_paren_line.indexOf(')') },
        end: { line: 6, character: unexpected_paren_line.indexOf(')') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedExpression',
      message: 'Expression is missing an operand after this operator.',
      range: {
        start: { line: 7, character: missing_condition_operand_line.indexOf('>') },
        end: { line: 7, character: missing_condition_operand_line.indexOf('>') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid expressions in declarations conditions assignments calls and chains', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Expressions.bas',
      text: [
        'Attribute VB_Name = "Expressions"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim value As Long',
        '    value = (1 + 2) * 3',
        '    If value > 0 And Not IsEmpty(value) Then',
        '        value = Len(CStr(value)) + 1',
        '    End If',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Expressions.bas'), []);
});

test('malformed expression regions fail closed without guessed member completion', () => {
  const malformed_line = '    value = (Application';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Expressions.bas',
      text: [
        'Attribute VB_Name = "Expressions"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        malformed_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Expressions.bas'), [
    {
      code: 'syntax.malformedExpression',
      message: 'Parenthesized expression is missing a closing parenthesis.',
      range: {
        start: { line: 4, character: malformed_line.indexOf('(') },
        end: { line: 4, character: malformed_line.indexOf('(') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Expressions.bas',
    position: { line: 4, character: malformed_line.length }
  }), []);
  const application_position = { line: 4, character: malformed_line.indexOf('Application') + 1 };
  assert.equal(getHover(project, {
    uri: 'file:///project/Expressions.bas',
    position: application_position
  }), undefined);
  assert.equal(resolveName(project, {
    uri: 'file:///project/Expressions.bas',
    position: application_position
  }), undefined);
});

test('syntax diagnostics report malformed call statements and argument lists', () => {
  const missing_paren_line = '    MissingCall(';
  const call_without_parens_line = '    Call DoWork 1';
  const trailing_comma_line = '    DoWork(1,)';
  const raise_named_arg_line = '    RaiseEvent Completed(message:="ok")';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Calls.bas',
      text: [
        'Attribute VB_Name = "Calls"',
        'Option Explicit',
        '',
        'Private Event Completed(ByVal message As String)',
        '',
        'Private Sub DoWork(Optional ByVal First As Variant, Optional ByVal Second As Variant)',
        'End Sub',
        '',
        'Public Sub Run()',
        missing_paren_line,
        call_without_parens_line,
        trailing_comma_line,
        raise_named_arg_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Calls.bas'), [
    {
      code: 'syntax.malformedCall',
      message: 'Call argument list is missing a closing parenthesis.',
      range: {
        start: { line: 9, character: missing_paren_line.indexOf('(') },
        end: { line: 9, character: missing_paren_line.indexOf('(') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCall',
      message: 'Call statement arguments must be enclosed in parentheses.',
      range: {
        start: { line: 10, character: call_without_parens_line.indexOf('1') },
        end: { line: 10, character: call_without_parens_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCall',
      message: 'Call argument list has a missing argument after this comma.',
      range: {
        start: { line: 11, character: trailing_comma_line.indexOf(',') },
        end: { line: 11, character: trailing_comma_line.indexOf(',') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedCall',
      message: 'RaiseEvent arguments cannot use named-argument syntax.',
      range: {
        start: { line: 12, character: raise_named_arg_line.indexOf(':=') },
        end: { line: 12, character: raise_named_arg_line.indexOf(':=') + 2 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Calls.bas',
    position: { line: 10, character: call_without_parens_line.length }
  }), undefined);
});

test('syntax diagnostics report malformed continued call argument lists', () => {
  const continued_trailing_comma_line = '        1,)';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Calls.bas',
      text: [
        'Attribute VB_Name = "Calls"',
        'Option Explicit',
        '',
        'Private Sub DoWork(Optional ByVal First As Variant, Optional ByVal Second As Variant)',
        'End Sub',
        '',
        'Public Sub Run()',
        '    DoWork( _',
        continued_trailing_comma_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Calls.bas'), [
    {
      code: 'syntax.malformedCall',
      message: 'Call argument list has a missing argument after this comma.',
      range: {
        start: { line: 8, character: continued_trailing_comma_line.indexOf(',') },
        end: { line: 8, character: continued_trailing_comma_line.indexOf(',') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid call statements arguments and signature help fixtures', () => {
  const host_call_line = '    rng.Find(What:="needle", After:=Nothing)';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Calls.bas',
      text: [
        'Attribute VB_Name = "Calls"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function',
        '',
        'Private Sub DoWork(Optional ByVal First As Variant, Optional ByVal Second As Variant, Optional ByVal Third As Variant)',
        'End Sub',
        '',
        'Private Event Completed(ByVal message As String)',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    DoWork 1, , 3',
        '    Call DoWork(1, , Third:=3)',
        '    DoWork First:=1, Third:=3',
        '    RaiseEvent Completed("ok")',
        '    ReadValue(Key:="id", Fallback:="n/a")',
        host_call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Calls.bas'), []);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Calls.bas',
    position: { line: 19, character: host_call_line.indexOf('After') + 'After'.length }
  })?.label, 'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range');
});

test('call syntax diagnostics cover bas cls and frm code while ignoring frm designer text', () => {
  const bas_invalid_line = '    Call DoWork 1';
  const class_invalid_line = '    DoWork(1,)';
  const form_invalid_line = '    MissingCall(';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Calls.bas',
      text: [
        'Attribute VB_Name = "Calls"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        bas_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        class_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "MissingCall("',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Calls.bas'), [
    {
      code: 'syntax.malformedCall',
      message: 'Call statement arguments must be enclosed in parentheses.',
      range: {
        start: { line: 4, character: bas_invalid_line.indexOf('1') },
        end: { line: 4, character: bas_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.malformedCall',
      message: 'Call argument list has a missing argument after this comma.',
      range: {
        start: { line: 5, character: class_invalid_line.indexOf(',') },
        end: { line: 5, character: class_invalid_line.indexOf(',') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedCall',
      message: 'Call argument list is missing a closing parenthesis.',
      range: {
        start: { line: 8, character: form_invalid_line.indexOf('(') },
        end: { line: 8, character: form_invalid_line.indexOf('(') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed member access and leading-dot expressions', () => {
  const missing_dot_member_line = '    Application.ActiveWorkbook.';
  const missing_bang_member_line = '    recordset!';
  const leading_dot_line = '    .Find "needle"';
  const terminated_dot_line = '    Application.ActiveWorkbook. + 1';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Members.bas',
      text: [
        'Attribute VB_Name = "Members"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        missing_dot_member_line,
        missing_bang_member_line,
        leading_dot_line,
        terminated_dot_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Members.bas'), [
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Member access is missing a member name.',
      range: {
        start: { line: 4, character: missing_dot_member_line.lastIndexOf('.') },
        end: { line: 4, character: missing_dot_member_line.lastIndexOf('.') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Member access is missing a member name.',
      range: {
        start: { line: 5, character: missing_bang_member_line.indexOf('!') },
        end: { line: 5, character: missing_bang_member_line.indexOf('!') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Leading-dot member access is only valid inside a With block or continued member chain.',
      range: {
        start: { line: 6, character: leading_dot_line.indexOf('.') },
        end: { line: 6, character: leading_dot_line.indexOf('.') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Member access is missing a member name.',
      range: {
        start: { line: 7, character: terminated_dot_line.lastIndexOf('.') },
        end: { line: 7, character: terminated_dot_line.lastIndexOf('.') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid source host continued and With member chains', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Find "needle"',
        '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Find "needle"',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        '        .Range("A1").Find "needle"',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        '        .Find "needle"',
        '    End With',
        '    CustomerFactory.CreateCustomer().LookupOrder "A001"',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Function LookupOrder(ByVal Key As String) As String',
        'End Function'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Caller.bas'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/CustomerFactory.bas'), []);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Customer.cls'), []);
});

test('malformed member access regions fail closed without guessed language services', () => {
  const malformed_completion_line = '    Application.ActiveWorkbook.';
  const malformed_signature_line = '    .Find("needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Members.bas',
      text: [
        'Attribute VB_Name = "Members"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        malformed_completion_line,
        malformed_signature_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getSyntaxDiagnostics(project, 'file:///project/Members.bas').length, 2);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Members.bas',
    position: { line: 4, character: malformed_completion_line.length }
  }), []);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Members.bas',
    position: { line: 5, character: malformed_signature_line.length }
  }), undefined);
});

test('member access diagnostics cover bas cls and frm code while ignoring frm designer text', () => {
  const bas_invalid_line = '    Application.ActiveWorkbook.';
  const class_invalid_line = '    recordset!';
  const form_invalid_line = '    .Find "needle"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Members.bas',
      text: [
        'Attribute VB_Name = "Members"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        bas_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        class_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = ".Find needle"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Members.bas'), [
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Member access is missing a member name.',
      range: {
        start: { line: 4, character: bas_invalid_line.lastIndexOf('.') },
        end: { line: 4, character: bas_invalid_line.lastIndexOf('.') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Member access is missing a member name.',
      range: {
        start: { line: 5, character: class_invalid_line.indexOf('!') },
        end: { line: 5, character: class_invalid_line.indexOf('!') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedMemberAccess',
      message: 'Leading-dot member access is only valid inside a With block or continued member chain.',
      range: {
        start: { line: 8, character: form_invalid_line.indexOf('.') },
        end: { line: 8, character: form_invalid_line.indexOf('.') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed executable statements and branch forms', () => {
  const set_missing_equals_line = '    Set target';
  const let_missing_equals_line = '    Let value';
  const goto_missing_target_line = '    GoTo';
  const on_error_missing_target_line = '    On Error GoTo';
  const exit_missing_kind_line = '    Exit';
  const resume_extra_line = '    Resume Next extra';
  const date_missing_equals_line = '    Date #1/1/2024#';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Statements.bas',
      text: [
        'Attribute VB_Name = "Statements"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        set_missing_equals_line,
        let_missing_equals_line,
        goto_missing_target_line,
        on_error_missing_target_line,
        exit_missing_kind_line,
        resume_extra_line,
        date_missing_equals_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Statements.bas'), [
    {
      code: 'syntax.malformedStatement',
      message: 'Set statement must assign with =.',
      range: {
        start: { line: 4, character: set_missing_equals_line.length },
        end: { line: 4, character: set_missing_equals_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Let statement must assign with =.',
      range: {
        start: { line: 5, character: let_missing_equals_line.length },
        end: { line: 5, character: let_missing_equals_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'GoTo statement is missing a branch target.',
      range: {
        start: { line: 6, character: goto_missing_target_line.indexOf('GoTo') },
        end: { line: 6, character: goto_missing_target_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'On Error GoTo statement is missing a branch target.',
      range: {
        start: { line: 7, character: on_error_missing_target_line.indexOf('GoTo') },
        end: { line: 7, character: on_error_missing_target_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Exit statement must specify Sub, Function, Property, For, or Do.',
      range: {
        start: { line: 8, character: exit_missing_kind_line.indexOf('Exit') },
        end: { line: 8, character: exit_missing_kind_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Resume Next cannot include an additional target.',
      range: {
        start: { line: 9, character: resume_extra_line.indexOf('extra') },
        end: { line: 9, character: resume_extra_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Date statement must assign with =.',
      range: {
        start: { line: 10, character: date_missing_equals_line.indexOf('Date') },
        end: { line: 10, character: date_missing_equals_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics report malformed file and host-action statements', () => {
  const open_missing_mode_line = '    Open "data.txt" For As #1';
  const close_missing_number_line = '    Close #';
  const input_missing_comma_line = '    Input #1';
  const name_missing_as_line = '    Name "old.txt" "new.txt"';
  const filecopy_missing_arg_line = '    FileCopy "old.txt"';
  const beep_with_arg_line = '    Beep 1';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Statements.bas',
      text: [
        'Attribute VB_Name = "Statements"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        open_missing_mode_line,
        close_missing_number_line,
        input_missing_comma_line,
        name_missing_as_line,
        filecopy_missing_arg_line,
        beep_with_arg_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Statements.bas'), [
    {
      code: 'syntax.malformedStatement',
      message: 'Open statement must include For mode and As # file number.',
      range: {
        start: { line: 4, character: open_missing_mode_line.indexOf('Open') },
        end: { line: 4, character: open_missing_mode_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Close statement has a malformed file number list.',
      range: {
        start: { line: 5, character: close_missing_number_line.indexOf('#') },
        end: { line: 5, character: close_missing_number_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Input # statement must include a file number and target list.',
      range: {
        start: { line: 6, character: input_missing_comma_line.indexOf('Input') },
        end: { line: 6, character: input_missing_comma_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Name statement must include As newPath.',
      range: {
        start: { line: 7, character: name_missing_as_line.indexOf('Name') },
        end: { line: 7, character: name_missing_as_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'FileCopy statement must include source and destination expressions.',
      range: {
        start: { line: 8, character: filecopy_missing_arg_line.indexOf('FileCopy') },
        end: { line: 8, character: filecopy_missing_arg_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedStatement',
      message: 'Beep statement does not take arguments.',
      range: {
        start: { line: 9, character: beep_with_arg_line.indexOf('1') },
        end: { line: 9, character: beep_with_arg_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid executable statement catalog forms', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Statements.bas',
      text: [
        'Attribute VB_Name = "Statements"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'StartLabel:',
        '100',
        '    Let value = 1',
        '    Set target = Nothing',
        '    LSet leftValue = rightValue',
        '    RSet rightValue = leftValue',
        '    Mid$(textValue, 1, 1) = "x"',
        '    GoTo StartLabel',
        '    GoSub 100',
        '    Return',
        '    On Error GoTo Handler',
        '    On Error Resume Next',
        '    On value GoTo First, Second',
        '    On value GoSub First, Second',
        '    Resume Next',
        '    Resume Handler',
        '    Exit Sub',
        '    Error 5',
        '    Load Form1',
        '    Unload Form1',
        '    Erase values',
        '    Randomize value',
        '    Reset',
        '    Seek #1, 10',
        '    Open "data.txt" For Input As #1',
        '    Close #1',
        '    Get #1, , value',
        '    Put #1, , value',
        '    Input #1, value',
        '    Line Input #1, textValue',
        '    Print #1, value',
        '    Write #1, value',
        '    Lock #1',
        '    Unlock #1',
        '    Name "old.txt" As "new.txt"',
        '    Kill "old.txt"',
        '    FileCopy "old.txt", "new.txt"',
        '    MkDir "folder"',
        '    RmDir "folder"',
        '    ChDir "folder"',
        '    ChDrive "C"',
        '    SetAttr "data.txt", vbReadOnly',
        '    Date = #1/1/2024#',
        '    Time = #12:00:00#',
        '    Beep',
        '    AppActivate "Excel"',
        '    SendKeys "%{F4}", True',
        'Handler:',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Statements.bas'), []);
});

test('syntax diagnostics report malformed conditional compilation directives', () => {
  const missing_const_name_line = '#Const = 1';
  const missing_then_line = '#If DEBUG';
  const missing_elseif_expression_line = '#ElseIf Then';
  const else_trailing_line = '#Else extra';
  const unexpected_end_line = '#End If';
  const missing_end_line = '#If DEBUG Then';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Directives.bas',
      text: [
        'Attribute VB_Name = "Directives"',
        'Option Explicit',
        missing_const_name_line,
        missing_then_line,
        '#If DEBUG Then',
        missing_elseif_expression_line,
        else_trailing_line,
        '#End If',
        unexpected_end_line,
        missing_end_line,
        '',
        'Public Sub StillIndexed()',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Directives.bas'), [
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#Const directive is missing a name.',
      range: {
        start: { line: 2, character: missing_const_name_line.indexOf('=') },
        end: { line: 2, character: missing_const_name_line.indexOf('=') + 1 }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#If directive must include Then.',
      range: {
        start: { line: 3, character: missing_then_line.indexOf('#If') },
        end: { line: 3, character: missing_then_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#ElseIf directive is missing an expression.',
      range: {
        start: { line: 5, character: missing_elseif_expression_line.indexOf('Then') },
        end: { line: 5, character: missing_elseif_expression_line.indexOf('Then') + 'Then'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#Else directive cannot include trailing text.',
      range: {
        start: { line: 6, character: else_trailing_line.indexOf('extra') },
        end: { line: 6, character: else_trailing_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedConditionalCompilation',
      message: 'Unexpected #End If without matching #If.',
      range: {
        start: { line: 8, character: unexpected_end_line.indexOf('#End') },
        end: { line: 8, character: unexpected_end_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    },
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#If directive is missing #End If.',
      range: {
        start: { line: 9, character: missing_end_line.indexOf('#If') },
        end: { line: 9, character: missing_end_line.indexOf('#If') + '#If'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.equal(getModuleMemberRanges(project, 'file:///project/Directives.bas').length, 1);
});

test('syntax diagnostics report malformed conditional compilation expressions', () => {
  const trailing_operator_line = '#If DEBUG And Then';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Directives.bas',
      text: [
        'Attribute VB_Name = "Directives"',
        'Option Explicit',
        trailing_operator_line,
        '#End If'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Directives.bas'), [
    {
      code: 'syntax.malformedConditionalCompilation',
      message: 'Conditional compilation expression is missing an operand after this operator.',
      range: {
        start: { line: 2, character: trailing_operator_line.indexOf('And') },
        end: { line: 2, character: trailing_operator_line.indexOf('And') + 'And'.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics ignore valid conditional compilation directives and inactive-looking text', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Directives.bas',
      text: [
        'Attribute VB_Name = "Directives"',
        'Option Explicit',
        '#Const DEBUG = True',
        '#If DEBUG Then',
        'Public Sub Active()',
        '    value = "#End If"',
        "    '#Else",
        'End Sub',
        '#ElseIf VBA7 Then',
        'Public Sub Alternate()',
        'End Sub',
        '#Else',
        'Public Sub Fallback()',
        'End Sub',
        '#End If'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Directives.bas'), []);
});

test('conditional compilation diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = '#Else';
  const form_invalid_line = '#If FORM_FLAG';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        class_invalid_line
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "#If DESIGNER Then"',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        form_invalid_line
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.malformedConditionalCompilation',
      message: 'Unexpected #Else without matching #If.',
      range: {
        start: { line: 3, character: class_invalid_line.indexOf('#Else') },
        end: { line: 3, character: class_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.malformedConditionalCompilation',
      message: '#If directive must include Then.',
      range: {
        start: { line: 6, character: form_invalid_line.indexOf('#If') },
        end: { line: 6, character: form_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics cover cls and frm code while ignoring frm designer text', () => {
  const class_invalid_line = '        "needle", _ \' class';
  const form_invalid_line = '        "needle", _ \' form';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        class_invalid_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Dialog.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form Dialog',
        '  Caption = "needle", _ \' designer',
        'End',
        'Attribute VB_Name = "Dialog"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        form_invalid_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Worker.cls'), [
    {
      code: 'syntax.invalidTrailingCommentContinuation',
      message: 'Code line-continuation marker cannot be followed by a comment.',
      range: {
        start: { line: 6, character: class_invalid_line.indexOf('_') },
        end: { line: 6, character: class_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/Dialog.frm'), [
    {
      code: 'syntax.invalidTrailingCommentContinuation',
      message: 'Code line-continuation marker cannot be followed by a comment.',
      range: {
        start: { line: 9, character: form_invalid_line.indexOf('_') },
        end: { line: 9, character: form_invalid_line.length }
      },
      severity: 'error',
      source: 'vba-language-server'
    }
  ]);
});

test('syntax diagnostics are additive to valid regions and preserve invalid fail-closed behavior', () => {
  const invalid_line = '        "id", _ \' invalid continuation';
  const active_line = '        ';
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        invalid_line,
        active_line,
        'End Sub',
        '',
        'Public Sub ValidRegion()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getSyntaxDiagnostics(project, 'file:///project/Worker.bas').length, 1);
  assert.equal(
    getSignatureHelp(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 9, character: active_line.length }
    }),
    undefined
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 13, character: chain_line.length }
    }).map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('bundled Excel HostDefinitions appear in completion and hover', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });
  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Application', detail: 'Excel.Application' }]
  );
  assert.deepEqual(hover, {
    contents: 'Excel.Application\n\nRepresents the Microsoft Excel application.'
  });
});

test('bundled Excel HostEnumMember appears in completion and hover', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    xl',
        '    xlUp',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 6 }
  });
  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 6 }
  });

  assert.deepEqual(
    completions
      .filter((item) => item.label === 'XlDirection' || item.label === 'xlUp')
      .map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'XlDirection', kind: 'enum', detail: 'Excel.XlDirection' },
      { label: 'xlUp', kind: 'enumMember', detail: 'Excel.XlDirection.xlUp' }
    ]
  );
  assert.deepEqual(hover, {
    contents: 'Excel.XlDirection.xlUp\n\nValue: -4162\n\nUp.'
  });
});

test('bundled HostEnumMembers cover Word PowerPoint and Access HostApplications', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    wd',
        '    pp',
        '    ac',
        '    wdStory',
        '    ppLayoutText',
        '    acForm',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'word',
    additionalHostApplications: ['powerpoint', 'access']
  });

  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 4, character: 6 }
    })
      .filter((item) => item.label === 'WdUnits' || item.label === 'wdStory')
      .map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'WdUnits', kind: 'enum', detail: 'Word.WdUnits' },
      { label: 'wdStory', kind: 'enumMember', detail: 'Word.WdUnits.wdStory' }
    ]
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 5, character: 6 }
    })
      .filter((item) => item.label === 'PpSlideLayout' || item.label === 'ppLayoutText')
      .map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'PpSlideLayout', kind: 'enum', detail: 'PowerPoint.PpSlideLayout' },
      { label: 'ppLayoutText', kind: 'enumMember', detail: 'PowerPoint.PpSlideLayout.ppLayoutText' }
    ]
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 6, character: 6 }
    })
      .filter((item) => item.label === 'AcObjectType' || item.label === 'acForm')
      .map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'AcObjectType', kind: 'enum', detail: 'Access.AcObjectType' },
      { label: 'acForm', kind: 'enumMember', detail: 'Access.AcObjectType.acForm' }
    ]
  );
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: 6 }
  }), {
    contents: 'Word.WdUnits.wdStory\n\nValue: 6\n\nA story.'
  });
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 8, character: 6 }
  }), {
    contents: 'PowerPoint.PpSlideLayout.ppLayoutText\n\nValue: 2\n\nText.'
  });
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 9, character: 6 }
  }), {
    contents: 'Access.AcObjectType.acForm\n\nValue: 2\n\nForm.'
  });
});

test('host enum qualified references resolve HostEnumMembers', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Excel.xlUp',
        '    XlDirection.xlUp',
        '    Excel.XlDirection.xlUp',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 10 }
  }), {
    contents: 'Excel.XlDirection.xlUp\n\nValue: -4162\n\nUp.'
  });
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 18 }
  }), {
    contents: 'Excel.XlDirection.xlUp\n\nValue: -4162\n\nUp.'
  });
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 22 }
  }), {
    contents: 'Excel.XlDirection.xlUp\n\nValue: -4162\n\nUp.'
  });
});

test('host enum qualified completion offers HostEnumMembers', () => {
  const enum_line = '    MyDirection.';
  const host_enum_line = '    Excel.MyDirection.';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        enum_line,
        host_enum_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'MyDirection',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'myUp',
            kind: 'enumMember',
            documentation: 'Up.'
          },
          {
            name: 'myDown',
            kind: 'enumMember',
            documentation: 'Down.'
          }
        ]
      }
    ]
  });

  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 4, character: enum_line.length }
    }).map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'myUp', kind: 'enumMember', detail: 'Excel.MyDirection.myUp' },
      { label: 'myDown', kind: 'enumMember', detail: 'Excel.MyDirection.myDown' }
    ],
    'unqualified host enum qualifier completion'
  );
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 5, character: host_enum_line.length }
    }).map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [
      { label: 'myUp', kind: 'enumMember', detail: 'Excel.MyDirection.myUp' },
      { label: 'myDown', kind: 'enumMember', detail: 'Excel.MyDirection.myDown' }
    ],
    'host-plus-enum qualifier completion'
  );
});

test('source definitions outrank host enum qualifier names', () => {
  const enum_line = '    MyDirection.';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    MyDirection.myUp',
        '    Excel.MyDirection.myUp',
        enum_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/MyDirection.bas',
      text: [
        'Attribute VB_Name = "MyDirection"',
        'Option Explicit',
        '',
        'Public Function otherMember() As Long',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Excel.bas',
      text: [
        'Attribute VB_Name = "Excel"',
        'Option Explicit',
        '',
        'Public Function otherMember() As Long',
        'End Function'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'MyDirection',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'myUp',
            kind: 'enumMember',
            documentation: 'Host enum member.'
          }
        ]
      }
    ]
  });

  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 18 }
  }), undefined);
  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 24 }
  }), undefined);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: enum_line.length }
  }), []);
});

test('host enum qualified references follow host ambiguity rules', () => {
  const non_main_line = '    SharedNonMain.';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    SharedMain.sharedMember',
        '    SharedNonMain.sharedMember',
        non_main_line,
        '    DuplicateExcel.sharedMember',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'excel',
    additionalHostApplications: ['word', 'powerpoint'],
    hostDefinitions: [
      {
        name: 'SharedMain',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'Excel member.'
          }
        ]
      },
      {
        name: 'SharedMain',
        kind: 'enum',
        hostApplication: 'word',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'Word member.'
          }
        ]
      },
      {
        name: 'SharedNonMain',
        kind: 'enum',
        hostApplication: 'word',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'Word member.'
          }
        ]
      },
      {
        name: 'SharedNonMain',
        kind: 'enum',
        hostApplication: 'powerpoint',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'PowerPoint member.'
          }
        ]
      },
      {
        name: 'DuplicateExcel',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'First Excel member.'
          }
        ]
      },
      {
        name: 'DuplicateExcel',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'sharedMember',
            kind: 'enumMember',
            documentation: 'Second Excel member.'
          }
        ]
      }
    ]
  });

  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 20 }
  }), {
    contents: 'Excel.SharedMain.sharedMember\n\nExcel member.'
  });
  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 23 }
  }), undefined);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: non_main_line.length }
  }), []);
  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: 25 }
  }), undefined);
});

test('HostConstant metadata participates in completion hover and semantic tokens', () => {
  const constant_line = '    xlHostFlag';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    xl',
        constant_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'xlHostFlag',
        kind: 'constant',
        hostApplication: 'excel',
        documentation: 'Host flag.',
        value: '99'
      }
    ]
  });

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 6 }
  });
  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: constant_line.indexOf('Host') }
  });
  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, kind: item.kind, detail: item.detail })),
    [{ label: 'xlHostFlag', kind: 'constant', detail: 'Excel.xlHostFlag' }]
  );
  assert.deepEqual(hover, {
    contents: 'Excel.xlHostFlag\n\nValue: 99\n\nHost flag.'
  });
  assertSemanticToken(tokens, 5, 4, 14, 'variable', ['readonly']);
});

test('SourceFormatting and SemanticTokens handle HostEnums and HostEnumMembers', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'dim direction as xldirection',
        'direction = xlup',
        'xldirection.xlup',
        'End Sub'
      ].join('\n')
    }
  ]);

  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');

  assertSemanticToken(tokens, 4, 17, 28, 'enum');
  assertSemanticToken(tokens, 5, 12, 16, 'enumMember');
  assertSemanticToken(tokens, 6, 0, 11, 'enum');
  assertSemanticToken(tokens, 6, 12, 16, 'enumMember');
  assert.equal(formatText(project, 'file:///project/Caller.bas'), [
    'Attribute VB_Name = "Caller"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    '    Dim direction As XlDirection',
    '    direction = xlUp',
    '    XlDirection.xlUp',
    'End Sub'
  ].join('\n'));
});

test('main HostApplication selects bundled Word HostDefinitions', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'word'
  });

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });
  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Application', detail: 'Word.Application' }]
  );
  assert.deepEqual(hover, {
    contents: 'Word.Application\n\nRepresents the Microsoft Word application.'
  });
});

test('main Word HostApplication enables bundled Word member completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim doc As Document',
        '    doc.Ra',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'word'
  });

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 10 }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Range', detail: 'Word.Range' }]
  );
});

test('additional HostApplication preserves main host completion and enables host-qualified root completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application',
        '    Word.',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'excel',
    additionalHostApplications: ['word']
  });

  const unqualified_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });
  const qualified_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 10 }
  });

  assert.deepEqual(
    unqualified_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Application', detail: 'Excel.Application' }]
  );
  assert.deepEqual(
    qualified_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [
      { label: 'Application', detail: 'Word.Application' },
      { label: 'Documents', detail: 'Word.Documents' },
      { label: 'Document', detail: 'Word.Document' },
      { label: 'Range', detail: 'Word.Range' },
      { label: 'Selection', detail: 'Word.Selection' },
      { label: 'WdUnits', detail: 'Word.WdUnits' },
      { label: 'wdStory', detail: 'Word.WdUnits.wdStory' }
    ]
  );
});

test('host-qualified references resolve through enabled HostApplications', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Word.Application',
        '    Word.Application.Active',
        'End Sub'
      ].join('\n')
    }
  ], {
    additionalHostApplications: ['word']
  });

  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 12 }
  });
  const member_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 27 }
  });

  assert.deepEqual(hover, {
    contents: 'Word.Application\n\nRepresents the Microsoft Word application.'
  });
  assert.deepEqual(
    member_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'ActiveDocument', detail: 'Word.ActiveDocument' }]
  );
});

test('host-qualified type annotations enable typed member completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim app As Word.Application',
        '    app.Active',
        'End Sub'
      ].join('\n')
    }
  ], {
    additionalHostApplications: ['word']
  });

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 14 }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'ActiveDocument', detail: 'Word.ActiveDocument' }]
  );
});

test('source definitions outrank HostApplication qualifier names', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Word.Application',
        '    Word.',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Word.bas',
      text: [
        'Attribute VB_Name = "Word"',
        'Option Explicit',
        '',
        'Public Function Application() As String',
        'End Function'
      ].join('\n')
    }
  ], {
    additionalHostApplications: ['word']
  });

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 12 }
  });
  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 10 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Word.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 27 }
    }
  });
  assert.deepEqual(completions, []);
});

test('disabled HostApplication qualifiers do not resolve', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Word.Application',
        '    Word.',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 12 }
  });
  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 10 }
  });

  assert.equal(hover, undefined);
  assert.deepEqual(completions, []);
});

test('disabled HostApplications do not contribute HostEnumMembers', () => {
  const disabled_enum_line = '    Word.WdUnits.';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    wdStory',
        '    Word.wdStory',
        '    Word.WdUnits.wdStory',
        disabled_enum_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const unqualified_hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 6 }
  });
  const qualified_hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 11 }
  });
  const host_enum_qualified_hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 22 }
  });
  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });
  const host_enum_qualified_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: disabled_enum_line.length }
  });

  assert.equal(unqualified_hover, undefined);
  assert.equal(qualified_hover, undefined);
  assert.equal(host_enum_qualified_hover, undefined);
  assert.deepEqual(completions, []);
  assert.deepEqual(host_enum_qualified_completions, []);
});

test('host-plus enum qualified references require matching host metadata', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Word.XlDirection.xlUp',
        'End Sub'
      ].join('\n')
    }
  ], {
    additionalHostApplications: ['word']
  });

  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 22 }
  }), undefined);
});

test('same-name non-main HostDefinitions remain ambiguous for unqualified references and completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    SharedOnly',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'excel',
    additionalHostApplications: ['word', 'powerpoint'],
    hostDefinitions: [
      {
        name: 'SharedOnly',
        kind: 'class',
        hostApplication: 'word',
        documentation: 'Word-only definition.'
      },
      {
        name: 'SharedOnly',
        kind: 'class',
        hostApplication: 'powerpoint',
        documentation: 'PowerPoint-only definition.'
      }
    ]
  });

  const hover = getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });
  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 10 }
  });

  assert.equal(hover, undefined);
  assert.deepEqual(completions, []);
});

test('source definitions outrank unqualified HostEnumMembers and HostConstants', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim xlHostFlag As Long',
        '    xlUp',
        '    xlHostFlag = 2',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Overrides.bas',
      text: [
        'Attribute VB_Name = "Overrides"',
        'Option Explicit',
        '',
        'Public Function xlUp() As Long',
        'End Function'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'XlTestEnum',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'xlUp',
            kind: 'enumMember',
            documentation: 'Host enum member.',
            value: '-4162'
          }
        ]
      },
      {
        name: 'xlHostFlag',
        kind: 'constant',
        hostApplication: 'excel',
        documentation: 'Host constant.',
        value: '99'
      }
    ]
  });

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 6 }
  }), {
    uri: 'file:///project/Overrides.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 20 }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 8 }
  }), {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 4, character: 8 },
      end: { line: 4, character: 18 }
    }
  });
});

test('same-name HostEnumMembers remain ambiguous across enabled hosts and within a host', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    SharedAcrossHosts',
        '    SharedWithinHost',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'excel',
    additionalHostApplications: ['word', 'powerpoint'],
    hostDefinitions: [
      {
        name: 'WordEnum',
        kind: 'enum',
        hostApplication: 'word',
        members: [
          {
            name: 'SharedAcrossHosts',
            kind: 'enumMember',
            documentation: 'Word member.'
          }
        ]
      },
      {
        name: 'PowerPointEnum',
        kind: 'enum',
        hostApplication: 'powerpoint',
        members: [
          {
            name: 'SharedAcrossHosts',
            kind: 'enumMember',
            documentation: 'PowerPoint member.'
          }
        ]
      },
      {
        name: 'FirstExcelEnum',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'SharedWithinHost',
            kind: 'enumMember',
            documentation: 'First Excel member.'
          }
        ]
      },
      {
        name: 'SecondExcelEnum',
        kind: 'enum',
        hostApplication: 'excel',
        members: [
          {
            name: 'SharedWithinHost',
            kind: 'enumMember',
            documentation: 'Second Excel member.'
          }
        ]
      }
    ]
  });

  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  }), undefined);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 15 }
  }), []);
  assert.equal(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), undefined);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 15 }
  }), []);
});

test('PowerPoint and Access bundled HostApplications provide root completion', () => {
  const powerpoint_line = '    Application';
  const access_line = '    Access.';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        powerpoint_line,
        access_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'powerpoint',
    additionalHostApplications: ['access']
  });

  const powerpoint_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: powerpoint_line.length }
  });
  const access_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: access_line.length }
  });

  assert.deepEqual(
    powerpoint_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Application', detail: 'PowerPoint.Application' }]
  );
  assert.deepEqual(
    access_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [
      { label: 'Application', detail: 'Access.Application' },
      { label: 'DoCmd', detail: 'Access.DoCmd' },
      { label: 'Form', detail: 'Access.Form' },
      { label: 'Report', detail: 'Access.Report' },
      { label: 'AcObjectType', detail: 'Access.AcObjectType' },
      { label: 'acForm', detail: 'Access.AcObjectType.acForm' }
    ]
  );
});

test('PowerPoint and Access bundled HostApplications provide host-qualified member completion', () => {
  const powerpoint_line = '    PowerPoint.Application.Active';
  const access_line = '    Access.DoCmd.Open';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        powerpoint_line,
        access_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'powerpoint',
    additionalHostApplications: ['access']
  });

  const powerpoint_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: powerpoint_line.length }
  });
  const access_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: access_line.length }
  });

  assert.deepEqual(
    powerpoint_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'ActivePresentation', detail: 'PowerPoint.ActivePresentation' }]
  );
  assert.deepEqual(
    access_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [
      { label: 'OpenForm', detail: 'Access.OpenForm' },
      { label: 'OpenReport', detail: 'Access.OpenReport' }
    ]
  );
});

test('Access bundled HostApplication excludes external reference libraries', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Access.',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'access'
  });

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 11 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['Application', 'DoCmd', 'Form', 'Report', 'AcObjectType', 'acForm']
  );
});

test('disabled PowerPoint and Access HostApplication qualifiers do not resolve', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    PowerPoint.',
        '    Access.',
        'End Sub'
      ].join('\n')
    }
  ]);

  const powerpoint_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 15 }
  });
  const access_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 11 }
  });

  assert.deepEqual(powerpoint_completions, []);
  assert.deepEqual(access_completions, []);
});

test('bundled Excel HostDefinitions are not source definition or rename targets', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Range',
        'End Sub'
      ].join('\n')
    }
  ]);

  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  };

  assert.equal(getDefinition(project, request), undefined);
  assert.equal(getRenameTarget(project, request), undefined);
});

test('explicit Worksheet variable type enables host member dot completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim ws As Worksheet',
        '    ws.Na',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 9 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['Name']
  );
});

test('explicit procedure parameter type enables member dot completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run(ByVal ws As Worksheet)',
        '    ws.Ra',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 9 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['Range']
  );
});

test('project-local class variable type exposes public class members in dot completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim customer As Customer',
        '    customer.Dis',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property',
        '',
        'Private Property Get DiscountCode() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 16 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['DisplayName']
  );
});

test('function and property return types enable chained member dot completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function',
        '',
        'Public Property Get ActiveCustomer() As Customer',
        'End Property',
        '',
        'Public Sub Run()',
        '    CreateCustomer().Dis',
        '    ActiveCustomer.Dis',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const function_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 10, character: 24 }
  });
  const property_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 11, character: 22 }
  });

  assert.deepEqual(
    function_completions.map((item) => item.label),
    ['DisplayName']
  );
  assert.deepEqual(
    property_completions.map((item) => item.label),
    ['DisplayName']
  );
});

test('hover and definition resolve source members reached through member chains', () => {
  const chain_line = '    CustomerFactory.CreateCustomer().Address.City';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        "'* @brief City documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/Order.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Order"',
        'Option Explicit',
        '',
        'Public Property Get Address() As OrderAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/OrderAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "OrderAddress"',
        'Option Explicit',
        '',
        "'* @brief Order city documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'City documentation.'
  });
  assert.deepEqual(getDefinition(project, request), {
    uri: 'file:///project/CustomerAddress.cls',
    range: {
      start: { line: 5, character: 20 },
      end: { line: 5, character: 24 }
    }
  });
});

test('ContinuedMemberChain resolves source member hover and definition', () => {
  const middle_chain_line = '        .CreateCustomer() _';
  const final_chain_line = '        .Address.City';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    CustomerFactory _',
        middle_chain_line,
        final_chain_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        "'* @brief City documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: final_chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'City documentation.'
  });
  assert.deepEqual(getDefinition(project, request), {
    uri: 'file:///project/CustomerAddress.cls',
    range: {
      start: { line: 5, character: 20 },
      end: { line: 5, character: 24 }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: middle_chain_line.indexOf('CreateCustomer') + 2 }
  }), {
    uri: 'file:///project/CustomerFactory.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 30 }
    }
  });
});

test('Excel host member chains use declared return types for completion', () => {
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('ContinuedMemberChain enables completion for host member chains', () => {
  const final_chain_line = '        .Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        final_chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: final_chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('ContinuedMemberChain fails closed for invalid continuation chains', () => {
  const final_chain_line = '        .Range("A1").Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        Bogus _',
        final_chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);
  const comment_continuation_project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        "    Dim example_val As Integer ' comment _",
        final_chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: final_chain_line.length }
  }), []);
  assert.deepEqual(getCompletions(comment_continuation_project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: final_chain_line.length }
  }), []);
});

test('WithReceiver enables leading-dot completion for host member chains', () => {
  const chain_line = '    .Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('continued WithReceiver enables leading-dot completion for host member chains', () => {
  const chain_line = '        .Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        '        .Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('WithReceiver returns receiver members for bare leading-dot completion', () => {
  const chain_line = '    .';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [
      { label: 'Address', detail: 'Excel.Address' },
      { label: 'Value', detail: 'Excel.Value' },
      { label: 'Value2', detail: 'Excel.Value2' },
      { label: 'Find', detail: 'Excel.Find' }
    ]
  );
});

test('nested WithReceiver uses the nearest active receiver for leading-dot completion', () => {
  const chain_line = '            .Fi';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        '        With .Find("needle")',
        chain_line,
        '        End With',
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: chain_line.length }
  });

  assert.deepEqual(
    completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Find', detail: 'Excel.Find' }]
  );
});

test('nested continued WithReceiver uses the outer receiver and pops back after End With', () => {
  const inner_chain_line = '            .Nu';
  const outer_chain_line = '        .Na';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        '        With .Address _',
        '            .PrimaryOrder()',
        inner_chain_line,
        '        End With',
        outer_chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property',
        '',
        'Public Property Get Name() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        'Public Function PrimaryOrder() As Order',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Order.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Order"',
        'Option Explicit',
        '',
        'Public Property Get Number() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: inner_chain_line.length }
  }).map((item) => item.label), ['Number']);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 9, character: outer_chain_line.length }
  }).map((item) => item.label), ['Name']);
});

test('continued WithReceiver declaration lines do not use the outer receiver as body', () => {
  const declaration_line = '            .Ci';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        '        With MissingReceiver _',
        declaration_line,
        '        End With',
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: declaration_line.length }
  }), []);
});

test('WithReceiver host members provide hover but not definition or rename targets', () => {
  const chain_line = '    .Find';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);
  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'Excel.Find\n\nFinds specific information in a range.'
  });
  assert.equal(getDefinition(project, request), undefined);
  assert.equal(getRenameTarget(project, request), undefined);
});

test('continued WithReceiver host members provide hover and signature help', () => {
  const chain_line = '        .Find("needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        '        .Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: 12 }
  }), {
    contents: 'Excel.Find\n\nFinds specific information in a range.'
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: chain_line.length }
  });
  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('WithReceiver resolves source members reached through leading-dot chains', () => {
  const chain_line = '    .Address.City';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        "'* @brief City documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/Order.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Order"',
        'Option Explicit',
        '',
        'Public Property Get Address() As OrderAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/OrderAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "OrderAddress"',
        'Option Explicit',
        '',
        "'* @brief Order city documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);
  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'City documentation.'
  });
  assert.deepEqual(getDefinition(project, request), {
    uri: 'file:///project/CustomerAddress.cls',
    range: {
      start: { line: 5, character: 20 },
      end: { line: 5, character: 24 }
    }
  });
  assert.deepEqual(getRenameTarget(project, request), {
    uri: 'file:///project/CustomerAddress.cls',
    range: {
      start: { line: 5, character: 20 },
      end: { line: 5, character: 24 }
    }
  });
});

test('continued WithReceiver resolves source member hover and definition', () => {
  const chain_line = '        .Address.City';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory _',
        '        .CreateCustomer()',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        "'* @brief City documentation.",
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);
  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'City documentation.'
  });
  assert.deepEqual(getDefinition(project, request), {
    uri: 'file:///project/CustomerAddress.cls',
    range: {
      start: { line: 5, character: 20 },
      end: { line: 5, character: 24 }
    }
  });
});

test('WithReceiver fails closed for missing and ambiguous receiver types', () => {
  const missing_type_line = '        .Dis';
  const ambiguous_type_line = '        .Ci';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer()',
        'End Function',
        '',
        'Public Function CreateCustomerWithAmbiguousAddress() As Customer',
        'End Function',
        '',
        'Public Sub Run()',
        '    With CreateCustomer()',
        missing_type_line,
        '    End With',
        '',
        '    With CreateCustomerWithAmbiguousAddress().Address',
        ambiguous_type_line,
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property',
        '',
        'Public Function Address() As CustomerAddress',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 11, character: missing_type_line.length }
  }), []);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 15, character: ambiguous_type_line.length }
  }), []);
});

test('continued WithReceiver fails closed for comment-continuation receiver declarations', () => {
  const chain_line = '            .Ci';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        "        With .Address ' comment _",
        chain_line,
        '        End With',
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: chain_line.length }
  }), []);
});

test('continued WithReceiver fails closed for invalid receiver continuations', () => {
  const chain_line = '            .Ci';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        '        With .Address _',
        '            .',
        chain_line,
        '        End With',
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: chain_line.length }
  }), []);
});

test('host members reached through member chains provide hover but not definition or rename targets', () => {
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Find';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);
  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'Excel.Find\n\nFinds specific information in a range.'
  });
  assert.equal(getDefinition(project, request), undefined);
  assert.equal(getRenameTarget(project, request), undefined);
});

test('host members reached through ContinuedMemberChain provide hover but not definition or rename targets', () => {
  const final_chain_line = '        .Range("A1").Find';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        final_chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);
  const request = {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: final_chain_line.length - 2 }
  };

  assert.deepEqual(getHover(project, request), {
    contents: 'Excel.Find\n\nFinds specific information in a range.'
  });
  assert.equal(getDefinition(project, request), undefined);
  assert.equal(getRenameTarget(project, request), undefined);
});

test('Me resolves as a current-instance member chain root only in class and form modules', () => {
  const class_chain_line = '    Me.CreateCustomer().Dis';
  const standard_chain_line = '    Me.CreateCustomer().Dis';
  const project = buildVbaProject([
    {
      uri: 'file:///project/CustomerPresenter.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerPresenter"',
        'Option Explicit',
        '',
        'Private Function CreateCustomer() As Customer',
        'End Function',
        '',
        'Public Sub Run()',
        class_chain_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        standard_chain_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const class_completions = getCompletions(project, {
    uri: 'file:///project/CustomerPresenter.cls',
    position: { line: 8, character: class_chain_line.length }
  });
  const standard_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: standard_chain_line.length }
  });

  assert.deepEqual(class_completions.map((item) => item.label), ['DisplayName']);
  assert.deepEqual(standard_completions, []);
});

test('member chain type resolution preserves source-first precedence and host-qualified overrides', () => {
  const source_line = '    localRange.Sour';
  const host_line = '    hostRange.Addr';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim localRange As Range',
        '    Dim hostRange As Excel.Range',
        source_line,
        host_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Range.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Range"',
        'Option Explicit',
        '',
        'Public Property Get SourceOnly() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  const source_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: source_line.length }
  });
  const host_completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: host_line.length }
  });

  assert.deepEqual(source_completions.map((item) => item.label), ['SourceOnly']);
  assert.deepEqual(
    host_completions.map((item) => ({ label: item.label, detail: item.detail })),
    [{ label: 'Address', detail: 'Excel.Address' }]
  );
});

test('member chains fail closed for missing result types, ambiguous members, comments, and strings', () => {
  const missing_type_line = '    CreateCustomer().Dis';
  const ambiguous_line = '    CreateCustomerWithAmbiguousAddress().Address.Ci';
  const comment_line = "' Application.ActiveWorkbook.Worksheets(1).Range(\"A1\").Fi";
  const string_line = '    text = "Application.ActiveWorkbook.Worksheets(1).Range(""A1"").Fi"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer()',
        'End Function',
        '',
        'Public Function CreateCustomerWithAmbiguousAddress() As Customer',
        'End Function',
        '',
        'Public Sub Run()',
        '    Dim text As String',
        missing_type_line,
        ambiguous_line,
        comment_line,
        string_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get Address() As CustomerAddress',
        'End Property',
        '',
        'Public Function Address() As CustomerAddress',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerAddress.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CustomerAddress"',
        'Option Explicit',
        '',
        'Public Property Get City() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 11, character: missing_type_line.length }
  }), []);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 12, character: ambiguous_line.length }
  }), []);
  assert.deepEqual(getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 13, character: comment_line.length }
  }), []);
  assert.deepEqual(getHover(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 14, character: string_line.length - 2 }
  }), undefined);
});

test('assignment-based object inference does not enable member dot completion', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim ws',
        '    Set ws = Worksheets(1)',
        '    ws.Na',
        'End Sub'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 9 }
  });

  assert.deepEqual(completions, []);
});

test('rename edits source declarations and resolved references without comments or strings', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        "'* @brief BuildValue documentation.",
        'Public Function BuildValue() As String',
        '    BuildValue = "BuildValue"',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        "' BuildValue is an ordinary comment.",
        'Public Sub Run()',
        '    Dim text As String',
        '    text = "BuildValue"',
        '    BuildValue',
        'End Sub'
      ].join('\n')
    }
  ]);

  const edits = getRenameEdits(
    project,
    {
      uri: 'file:///project/Caller.bas',
      position: { line: 6, character: 8 }
    },
    'MakeValue'
  );

  assert.deepEqual(edits, [
    {
      uri: 'file:///project/Builder.bas',
      range: {
        start: { line: 3, character: 16 },
        end: { line: 3, character: 26 }
      },
      newText: 'MakeValue'
    },
    {
      uri: 'file:///project/Builder.bas',
      range: {
        start: { line: 4, character: 4 },
        end: { line: 4, character: 14 }
      },
      newText: 'MakeValue'
    },
    {
      uri: 'file:///project/Caller.bas',
      range: {
        start: { line: 6, character: 4 },
        end: { line: 6, character: 14 }
      },
      newText: 'MakeValue'
    }
  ]);
});

test('rename supports local variables and parameters', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run(ByVal oldName As String)',
        '    Dim localValue As String',
        '    localValue = oldName',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(
    getRenameEdits(
      project,
      {
        uri: 'file:///project/Worker.bas',
        position: { line: 5, character: 20 }
      },
      'newName'
    ),
    [
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 3, character: 21 },
          end: { line: 3, character: 28 }
        },
        newText: 'newName'
      },
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 5, character: 17 },
          end: { line: 5, character: 24 }
        },
        newText: 'newName'
      }
    ]
  );
  assert.deepEqual(
    getRenameEdits(
      project,
      {
        uri: 'file:///project/Worker.bas',
        position: { line: 5, character: 8 }
      },
      'nextValue'
    ),
    [
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 4, character: 8 },
          end: { line: 4, character: 18 }
        },
        newText: 'nextValue'
      },
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 5, character: 4 },
          end: { line: 5, character: 14 }
        },
        newText: 'nextValue'
      }
    ]
  );
});

test('comma-separated local Dim declarators behave as separate SourceVariables', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim firstValue, secondValue As Long',
        '    firstValue = secondValue',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 19 }
  }), {
    uri: 'file:///project/Worker.bas',
    range: {
      start: { line: 4, character: 20 },
      end: { line: 4, character: 31 }
    }
  });

  const tokens = getSemanticTokens(project, 'file:///project/Worker.bas');
  assertSemanticToken(tokens, 4, 8, 18, 'variable');
  assertSemanticToken(tokens, 4, 20, 31, 'variable');
  assertSemanticToken(tokens, 5, 4, 14, 'variable');
  assertSemanticToken(tokens, 5, 17, 28, 'variable');

  assert.deepEqual(
    getRenameEdits(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 5, character: 19 }
    }, 'renamedValue'),
    [
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 4, character: 20 },
          end: { line: 4, character: 31 }
        },
        newText: 'renamedValue'
      },
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 5, character: 17 },
          end: { line: 5, character: 28 }
        },
        newText: 'renamedValue'
      }
    ]
  );
});

test('completion includes comma-separated local variable and constant declarators', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim firstValue, secondValue As Long',
        '    Const FirstLimit = 1, SecondLimit = 2',
        '    Sec',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 6, character: 7 }
    })
      .filter((item) => item.label === 'SecondLimit' || item.label === 'secondValue')
      .map((item) => ({ label: item.label, kind: item.kind }))
      .sort((left, right) => left.label.localeCompare(right.label)),
    [
      { label: 'SecondLimit', kind: 'constant' },
      { label: 'secondValue', kind: 'variable' }
    ]
  );
});

test('comma-separated ReDim declarators do not create SourceVariables', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    ReDim values(1 To 10), otherValues(1 To 10)',
        '    values',
        '    otherValues',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 8 }
  }), undefined);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 8 }
  }), undefined);
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 6, character: 9 }
    }).filter((item) => item.label === 'otherValues'),
    []
  );
});

test('comma-separated Static declarators keep type annotations per declarator', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Static firstCustomer, secondCustomer As Customer',
        '    firstCustomer.DisplayName',
        '    secondCustomer.DisplayName',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 19 }
  }), undefined);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 20 }
  }), {
    uri: 'file:///project/Customer.cls',
    range: {
      start: { line: 4, character: 20 },
      end: { line: 4, character: 31 }
    }
  });
});

test('comma-separated module SourceVariables respect declaration visibility', () => {
  const public_line = 'Public publicFirst As String, publicSecond As Long';
  const private_line = 'Private privateFirst As String, privateSecond As Long';
  const bare_line = 'Dim moduleFirst As String, moduleSecond As Long';
  const local_reference_line = '    privateSecond = moduleSecond';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Globals.bas',
      text: [
        'Attribute VB_Name = "Globals"',
        'Option Explicit',
        '',
        public_line,
        private_line,
        bare_line,
        '',
        'Public Sub Run()',
        local_reference_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    publicSecond',
        '    privateSecond',
        '    moduleSecond',
        'End Sub'
      ].join('\n')
    }
  ]);

  const public_second_start = public_line.indexOf('publicSecond');
  const private_second_start = private_line.indexOf('privateSecond');
  const module_second_start = bare_line.indexOf('moduleSecond');

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 3, character: public_second_start },
      end: { line: 3, character: public_second_start + 'publicSecond'.length }
    }
  });
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), undefined);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 8 }
  }), undefined);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Globals.bas',
    position: { line: 8, character: local_reference_line.indexOf('privateSecond') }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 4, character: private_second_start },
      end: { line: 4, character: private_second_start + 'privateSecond'.length }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Globals.bas',
    position: { line: 8, character: local_reference_line.indexOf('moduleSecond') }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 5, character: module_second_start },
      end: { line: 5, character: module_second_start + 'moduleSecond'.length }
    }
  });

  const tokens = getSemanticTokens(project, 'file:///project/Globals.bas');
  assertSemanticToken(tokens, 3, public_line.indexOf('publicFirst'), public_line.indexOf('publicFirst') + 'publicFirst'.length, 'variable');
  assertSemanticToken(tokens, 3, public_second_start, public_second_start + 'publicSecond'.length, 'variable');
  assertSemanticToken(tokens, 4, private_line.indexOf('privateFirst'), private_line.indexOf('privateFirst') + 'privateFirst'.length, 'variable');
  assertSemanticToken(tokens, 4, private_second_start, private_second_start + 'privateSecond'.length, 'variable');
  assertSemanticToken(tokens, 5, bare_line.indexOf('moduleFirst'), bare_line.indexOf('moduleFirst') + 'moduleFirst'.length, 'variable');
  assertSemanticToken(tokens, 5, module_second_start, module_second_start + 'moduleSecond'.length, 'variable');
});

test('continued local Dim and Static declarators behave as source variables', () => {
  const second_line = '        secondValue As Long';
  const second_customer_line = '           secondCustomer As Customer';
  const assignment_line = '    secondvalue = firstValue';
  const member_line = '    secondCustomer.DisplayName';
  const completion_line = '    seco';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim _',
        '        firstValue As Long, _',
        second_line,
        '    Static firstCustomer As Customer, _',
        second_customer_line,
        assignment_line,
        member_line,
        completion_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: assignment_line.indexOf('secondvalue') }
  }), {
    uri: 'file:///project/Worker.bas',
    range: {
      start: { line: 6, character: second_line.indexOf('secondValue') },
      end: { line: 6, character: second_line.indexOf('secondValue') + 'secondValue'.length }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: member_line.indexOf('DisplayName') }
  }), {
    uri: 'file:///project/Customer.cls',
    range: {
      start: { line: 4, character: 20 },
      end: { line: 4, character: 31 }
    }
  });
  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 11, character: completion_line.length }
    }).filter((item) => item.label === 'secondValue'),
    [{ label: 'secondValue', kind: 'variable' }]
  );

  const tokens = getSemanticTokens(project, 'file:///project/Worker.bas');
  assertSemanticToken(tokens, 6, second_line.indexOf('secondValue'), second_line.indexOf('secondValue') + 'secondValue'.length, 'variable');
  assertSemanticToken(tokens, 8, second_customer_line.indexOf('secondCustomer'), second_customer_line.indexOf('secondCustomer') + 'secondCustomer'.length, 'variable');
  assertSemanticToken(tokens, 9, assignment_line.indexOf('secondvalue'), assignment_line.indexOf('secondvalue') + 'secondvalue'.length, 'variable');

  assert.deepEqual(
    getRenameEdits(project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 9, character: assignment_line.indexOf('secondvalue') }
    }, 'renamedValue'),
    [
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 6, character: second_line.indexOf('secondValue') },
          end: { line: 6, character: second_line.indexOf('secondValue') + 'secondValue'.length }
        },
        newText: 'renamedValue'
      },
      {
        uri: 'file:///project/Worker.bas',
        range: {
          start: { line: 9, character: assignment_line.indexOf('secondvalue') },
          end: { line: 9, character: assignment_line.indexOf('secondvalue') + 'secondvalue'.length }
        },
        newText: 'renamedValue'
      }
    ]
  );
  assert.equal(formatText(project, 'file:///project/Worker.bas').split('\n')[9], '    secondValue = firstValue');
});

test('continued module variable and Const declarators preserve visibility ranges and readonly tokens', () => {
  const public_second_line = '       publicSecond As Long';
  const private_second_line = '        privateSecond As Long';
  const module_second_line = '    moduleSecond As Long';
  const second_limit_line = '             SecondLimit As Long = 2';
  const local_reference_line = '    privateSecond = moduleSecond + SecondLimit';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Globals.bas',
      text: [
        'Attribute VB_Name = "Globals"',
        'Option Explicit',
        '',
        'Public publicFirst As String, _',
        public_second_line,
        'Private privateFirst As String, _',
        private_second_line,
        'Dim _',
        '    moduleFirst As String, _',
        module_second_line,
        'Public Const FirstLimit As Long = 1, _',
        second_limit_line,
        '',
        'Public Sub Run()',
        local_reference_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    publicSecond',
        '    privateSecond',
        '    SecondLimit',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 4, character: public_second_line.indexOf('publicSecond') },
      end: { line: 4, character: public_second_line.indexOf('publicSecond') + 'publicSecond'.length }
    }
  });
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), undefined);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 8 }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 11, character: second_limit_line.indexOf('SecondLimit') },
      end: { line: 11, character: second_limit_line.indexOf('SecondLimit') + 'SecondLimit'.length }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Globals.bas',
    position: { line: 14, character: local_reference_line.indexOf('privateSecond') }
  }), {
    uri: 'file:///project/Globals.bas',
    range: {
      start: { line: 6, character: private_second_line.indexOf('privateSecond') },
      end: { line: 6, character: private_second_line.indexOf('privateSecond') + 'privateSecond'.length }
    }
  });
  assert.deepEqual(getModuleMemberRanges(project, 'file:///project/Globals.bas').map((range) => [
    range.start.line,
    range.end.line
  ]), [
    [3, 4],
    [5, 6],
    [7, 9],
    [10, 11],
    [13, 15]
  ]);

  const tokens = getSemanticTokens(project, 'file:///project/Globals.bas');
  assertSemanticToken(tokens, 4, public_second_line.indexOf('publicSecond'), public_second_line.indexOf('publicSecond') + 'publicSecond'.length, 'variable');
  assertSemanticToken(tokens, 6, private_second_line.indexOf('privateSecond'), private_second_line.indexOf('privateSecond') + 'privateSecond'.length, 'variable');
  assertSemanticToken(tokens, 9, module_second_line.indexOf('moduleSecond'), module_second_line.indexOf('moduleSecond') + 'moduleSecond'.length, 'variable');
  assertSemanticToken(tokens, 11, second_limit_line.indexOf('SecondLimit'), second_limit_line.indexOf('SecondLimit') + 'SecondLimit'.length, 'variable', ['readonly']);
});

test('continued WithEvents declarators resolve handlers and source variable references', () => {
  const declaration_line = '                   TextBox1 As TextBox';
  const handler_line = 'Private Sub TextBox1_Change()';
  const project = buildVbaProject([
    {
      uri: 'file:///project/FormModule.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "FormModule"',
        'Option Explicit',
        '',
        'Private WithEvents Button As CommandButton, _',
        declaration_line,
        '',
        handler_line,
        'End Sub',
        '',
        'Private Sub Run()',
        '    TextBox1',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    },
    {
      uri: 'file:///project/TextBox.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "TextBox"',
        'Option Explicit',
        '',
        'Public Event Change()'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/FormModule.cls'), []);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 7, character: handler_line.indexOf('Change') }
  }), {
    uri: 'file:///project/TextBox.cls',
    range: {
      start: { line: 4, character: 13 },
      end: { line: 4, character: 19 }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 11, character: 8 }
  }), {
    uri: 'file:///project/FormModule.cls',
    range: {
      start: { line: 5, character: declaration_line.indexOf('TextBox1') },
      end: { line: 5, character: declaration_line.indexOf('TextBox1') + 'TextBox1'.length }
    }
  });
});

test('continued declaration lists stop at colons and keep ReDim declarators unindexed', () => {
  const second_line = '        secondValue As Long: Dim thirdValue As Long';
  const redim_second_line = '          otherValues(1 To 10)';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim firstValue As Long, _',
        second_line,
        '    firstValue = secondValue',
        '    thirdValue',
        '    ReDim values(1 To 10), _',
        redim_second_line,
        '    otherValues',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 18 }
  }), {
    uri: 'file:///project/Worker.bas',
    range: {
      start: { line: 5, character: second_line.indexOf('secondValue') },
      end: { line: 5, character: second_line.indexOf('secondValue') + 'secondValue'.length }
    }
  });
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 7, character: 8 }
  }), undefined);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: 8 }
  }), undefined);
});

test('invalid continued declaration lists fail closed for continued declarators', () => {
  const invalid_head_line = "    Dim firstValue As Long, _ ' trailing comment";
  const continued_line = '        secondValue As Long';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        invalid_head_line,
        continued_line,
        '    secondValue',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getDefinition(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 8 }
  }), undefined);
  assert.deepEqual(
    getSyntaxDiagnostics(project, 'file:///project/Worker.bas').map((diagnostic) => diagnostic.code),
    ['syntax.invalidTrailingCommentContinuation']
  );
});

test('rename supports properties, enums, user-defined types, and events', () => {
  const property_project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Customer.DisplayName',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        'End Property'
      ].join('\n')
    }
  ]);
  const enum_type_project = buildVbaProject([
    {
      uri: 'file:///project/Model.bas',
      text: [
        'Attribute VB_Name = "Model"',
        'Option Explicit',
        'Public Enum CustomerState',
        '    Active',
        'End Enum',
        'Public Type CustomerRecord',
        '    State As CustomerState',
        'End Type',
        'Public Sub Run()',
        '    Dim state As CustomerState',
        '    Dim record As CustomerRecord',
        'End Sub'
      ].join('\n')
    }
  ]);
  const event_project = buildVbaProject([
    {
      uri: 'file:///project/Publisher.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Publisher"',
        'Option Explicit',
        'Public Event Completed(ByVal Result As String)',
        'Public Sub Run()',
        '    RaiseEvent Completed("ok")',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getRenameEdits(
    property_project,
    {
      uri: 'file:///project/Caller.bas',
      position: { line: 4, character: 16 }
    },
    'NameText'
  ), [
    {
      uri: 'file:///project/Caller.bas',
      range: {
        start: { line: 4, character: 13 },
        end: { line: 4, character: 24 }
      },
      newText: 'NameText'
    },
    {
      uri: 'file:///project/Customer.cls',
      range: {
        start: { line: 4, character: 20 },
        end: { line: 4, character: 31 }
      },
      newText: 'NameText'
    }
  ]);
  assert.deepEqual(getRenameEdits(
    enum_type_project,
    {
      uri: 'file:///project/Model.bas',
      position: { line: 2, character: 18 }
    },
    'CustomerStatus'
  ), [
    {
      uri: 'file:///project/Model.bas',
      range: {
        start: { line: 2, character: 12 },
        end: { line: 2, character: 25 }
      },
      newText: 'CustomerStatus'
    },
    {
      uri: 'file:///project/Model.bas',
      range: {
        start: { line: 6, character: 13 },
        end: { line: 6, character: 26 }
      },
      newText: 'CustomerStatus'
    },
    {
      uri: 'file:///project/Model.bas',
      range: {
        start: { line: 9, character: 17 },
        end: { line: 9, character: 30 }
      },
      newText: 'CustomerStatus'
    }
  ]);
  assert.deepEqual(getRenameEdits(
    enum_type_project,
    {
      uri: 'file:///project/Model.bas',
      position: { line: 5, character: 18 }
    },
    'CustomerSnapshot'
  ), [
    {
      uri: 'file:///project/Model.bas',
      range: {
        start: { line: 5, character: 12 },
        end: { line: 5, character: 26 }
      },
      newText: 'CustomerSnapshot'
    },
    {
      uri: 'file:///project/Model.bas',
      range: {
        start: { line: 10, character: 18 },
        end: { line: 10, character: 32 }
      },
      newText: 'CustomerSnapshot'
    }
  ]);
  assert.deepEqual(getRenameEdits(
    event_project,
    {
      uri: 'file:///project/Publisher.cls',
      position: { line: 5, character: 18 }
    },
    'Finished'
  ), [
    {
      uri: 'file:///project/Publisher.cls',
      range: {
        start: { line: 3, character: 13 },
        end: { line: 3, character: 22 }
      },
      newText: 'Finished'
    },
    {
      uri: 'file:///project/Publisher.cls',
      range: {
        start: { line: 5, character: 15 },
        end: { line: 5, character: 24 }
      },
      newText: 'Finished'
    }
  ]);
});

test('rename excludes HostDefinitions and ambiguous references', () => {
  const host_project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Range',
        'End Sub'
      ].join('\n')
    }
  ]);
  const ambiguous_project = buildVbaProject([
    {
      uri: 'file:///project/FirstBuilder.bas',
      text: [
        'Attribute VB_Name = "FirstBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/SecondBuilder.bas',
      text: [
        'Attribute VB_Name = "SecondBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    BuildValue',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getRenameEdits(
    host_project,
    {
      uri: 'file:///project/Caller.bas',
      position: { line: 4, character: 8 }
    },
    'Cells'
  ), []);
  assert.deepEqual(getRenameEdits(
    ambiguous_project,
    {
      uri: 'file:///project/FirstBuilder.bas',
      position: { line: 3, character: 20 }
    },
    'MakeValue'
  ), [
    {
      uri: 'file:///project/FirstBuilder.bas',
      range: {
        start: { line: 3, character: 16 },
        end: { line: 3, character: 26 }
      },
      newText: 'MakeValue'
    }
  ]);
});

test('incremental update records ModuleMember ranges and replaces a changed member', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Worker.bas',
        text: [
          'Attribute VB_Name = "Worker"',
          'Option Explicit',
          '',
          'Public Function FirstValue() As String',
          'End Function',
          '',
          'Public Function SecondValue() As String',
          'End Function',
          '',
          'Public Sub Run()',
          '    Ren',
          '    Sec',
          'End Sub'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: []
    }
  );

  assert.deepEqual(
    getModuleMemberRanges(project, 'file:///project/Worker.bas').map((range) => [
      range.start.line,
      range.end.line
    ]),
    [
      [3, 4],
      [6, 7],
      [9, 12]
    ]
  );

  const result = updateVbaProjectFile(project, 'file:///project/Worker.bas', {
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 26 }
    },
    text: 'RenamedValue'
  });

  assert.equal(result.strategy, 'moduleMember');
  assert.deepEqual(
    getCompletions(result.project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 10, character: 7 }
    }).map((item) => item.label),
    ['RenamedValue']
  );
  assert.deepEqual(
    getCompletions(result.project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 11, character: 7 }
    }).map((item) => item.label),
    ['SecondValue']
  );
});

test('incremental update falls back to full rebuild outside members and during parser recovery', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Worker.bas',
        text: [
          'Attribute VB_Name = "Worker"',
          'Option Explicit',
          '',
          'Public Function FirstValue() As String',
          'End Function',
          '',
          'Public Function SecondValue() As String',
          'End Function',
          '',
          'Public Sub Run()',
          '    Sec',
          'End Sub'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: []
    }
  );

  const outside_member_result = updateVbaProjectFile(project, 'file:///project/Worker.bas', {
    range: {
      start: { line: 2, character: 0 },
      end: { line: 2, character: 0 }
    },
    text: "' inserted outside a member\n"
  });
  const parser_recovery_result = updateVbaProjectFile(project, 'file:///project/Worker.bas', {
    range: {
      start: { line: 3, character: 0 },
      end: { line: 3, character: 38 }
    },
    text: 'This is not a member declaration'
  });

  assert.equal(outside_member_result.strategy, 'fullRebuild');
  assert.equal(parser_recovery_result.strategy, 'fullRebuild');
  assert.deepEqual(
    getCompletions(parser_recovery_result.project, {
      uri: 'file:///project/Worker.bas',
      position: { line: 10, character: 7 }
    }).map((item) => item.label),
    ['SecondValue']
  );
});

test('completion includes a Public Function from a sibling module in the same VbaProject', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Bui',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "ok"',
        'End Function'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['BuildValue']
  );
});

test('VbaProject includes sibling cls and frm files as source modules', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Bui',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/ClassBuilder.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "ClassBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildClassValue() As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/FormBuilder.frm',
      text: [
        'VERSION 5.00',
        'Attribute VB_Name = "FormBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildFormValue() As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['BuildClassValue', 'BuildFormValue']
  );
});

test('ModuleIdentity uses Attribute VB_Name before falling back to file name', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/FileName.bas',
      text: [
        'Attribute VB_Name = "DeclaredModule"',
        'Option Explicit'
      ].join('\n')
    },
    {
      uri: 'file:///project/FallbackModule.bas',
      text: 'Option Explicit'
    }
  ]);

  assert.deepEqual(getModuleIdentities(project), ['DeclaredModule', 'FallbackModule']);
});

test('definition jumps from a sibling module reference to the Public Function declaration range', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    BuildValue',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "ok"',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Builder.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 26 }
    }
  });
});

test('definition prefers a procedure local variable over a project Public Function with the same name', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim BuildValue As String',
        '    BuildValue = "local"',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "project"',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 4, character: 8 },
      end: { line: 4, character: 18 }
    }
  });
});

test('definition prefers a procedure parameter over a project Public Function with the same name', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run(ByVal BuildValue As String)',
        '    BuildValue = "parameter"',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "project"',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 3, character: 21 },
      end: { line: 3, character: 31 }
    }
  });
});

test('definition prefers a current module definition over a sibling Public Function with the same name', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    BuildValue',
        'End Sub',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "current"',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        '    BuildValue = "sibling"',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 7, character: 16 },
      end: { line: 7, character: 26 }
    }
  });
});

test('definition matching is case-insensitive', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    buildvalue',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/Builder.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 26 }
    }
  });
});

test('definition returns no target for ambiguous equal-rank project matches', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    BuildValue',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/FirstBuilder.bas',
      text: [
        'Attribute VB_Name = "FirstBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/SecondBuilder.bas',
      text: [
        'Attribute VB_Name = "SecondBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.equal(definition, undefined);
});

test('NameResolution prefers a project definition over a HostDefinition with the same name', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Caller.bas',
        text: [
          'Attribute VB_Name = "Caller"',
          'Option Explicit',
          '',
          'Public Sub Run()',
          '    BuildValue',
          'End Sub'
        ].join('\n')
      },
      {
        uri: 'file:///project/Builder.bas',
        text: [
          'Attribute VB_Name = "Builder"',
          'Option Explicit',
          '',
          'Public Function BuildValue() As String',
          'End Function'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: [{ name: 'BuildValue' }]
    }
  );

  const resolution = resolveName(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(resolution, {
    source: 'vba',
    definition: {
      uri: 'file:///project/Builder.bas',
      range: {
        start: { line: 3, character: 16 },
        end: { line: 3, character: 26 }
      }
    }
  });
});

test('definition resolves a ModuleIdentity-qualified reference to the qualified public member', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    SecondBuilder.BuildValue',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/FirstBuilder.bas',
      text: [
        'Attribute VB_Name = "FirstBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/SecondBuilder.bas',
      text: [
        'Attribute VB_Name = "SecondBuilder"',
        'Option Explicit',
        '',
        'Public Function BuildValue() As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 23 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/SecondBuilder.bas',
    range: {
      start: { line: 3, character: 16 },
      end: { line: 3, character: 26 }
    }
  });
});

test('FormDesignerBlock content does not create completion candidates', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Caller.bas',
        text: [
          'Attribute VB_Name = "Caller"',
          'Option Explicit',
          '',
          'Public Sub Run()',
          '    ',
          'End Sub'
        ].join('\n')
      },
      {
        uri: 'file:///project/SampleForm.frm',
        text: [
          'VERSION 5.00',
          'Begin VB.Form SampleForm',
          '   Caption = "Sample"',
          '   Public Function DesignerValue() As String',
          'End',
          'Attribute VB_Name = "SampleForm"',
          'Option Explicit',
          '',
          'Public Function CodeValue() As String',
          'End Function'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: []
    }
  );

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 4 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['Run', 'CodeValue']
  );
});

test('public properties declared in sibling class modules appear in completion and definition lookup', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    DisplayName',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        'Public Property Get DisplayName() As String',
        '    DisplayName = "customer"',
        'End Property'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 11 }
  });
  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['DisplayName']
  );
  assert.deepEqual(definition, {
    uri: 'file:///project/Customer.cls',
    range: {
      start: { line: 4, character: 20 },
      end: { line: 4, character: 31 }
    }
  });
});

test('qualified access to another module does not expose private members', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Builder.HiddenValue',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Builder.bas',
      text: [
        'Attribute VB_Name = "Builder"',
        'Option Explicit',
        '',
        'Private Function HiddenValue() As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 16 }
  });

  assert.equal(definition, undefined);
});

test('enum declarations and enum members participate in completion and definition lookup', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Manual',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Modes.bas',
      text: [
        'Attribute VB_Name = "Modes"',
        'Option Explicit',
        '',
        'Public Enum RunMode',
        '    Automatic = 0',
        '    Manual',
        'End Enum'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 7 }
  });
  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 6 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['Manual']
  );
  assert.deepEqual(definition, {
    uri: 'file:///project/Modes.bas',
    range: {
      start: { line: 5, character: 4 },
      end: { line: 5, character: 10 }
    }
  });
});

test('user-defined Type declarations participate in completion and expose fields in the AST', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    CustomerRecord',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Records.bas',
      text: [
        'Attribute VB_Name = "Records"',
        'Option Explicit',
        '',
        'Public Type CustomerRecord',
        '    Id As Long',
        '    Name As String',
        'End Type'
      ].join('\n')
    }
  ]);

  const completions = getCompletions(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 12 }
  });
  const definition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  });

  assert.deepEqual(
    completions.map((item) => item.label),
    ['CustomerRecord']
  );
  assert.deepEqual(definition, {
    uri: 'file:///project/Records.bas',
    range: {
      start: { line: 3, character: 12 },
      end: { line: 3, character: 26 }
    }
  });
  assert.deepEqual(getTypeFields(project, 'CustomerRecord'), [
    {
      name: 'Id',
      range: {
        start: { line: 4, character: 4 },
        end: { line: 4, character: 6 }
      }
    },
    {
      name: 'Name',
      range: {
        start: { line: 5, character: 4 },
        end: { line: 5, character: 8 }
      }
    }
  ]);
});

test('private enum and type declarations resolve inside the current module only', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Private Enum LocalMode',
        '    HiddenMode',
        'End Enum',
        '',
        'Private Type LocalRecord',
        '    Value As String',
        'End Type',
        '',
        'Public Sub Run()',
        '    LocalMode',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Other.bas',
      text: [
        'Attribute VB_Name = "Other"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Local',
        'End Sub'
      ].join('\n')
    }
  ]);

  const currentModuleDefinition = getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 12, character: 8 }
  });
  const otherModuleCompletions = getCompletions(project, {
    uri: 'file:///project/Other.bas',
    position: { line: 4, character: 9 }
  });

  assert.deepEqual(currentModuleDefinition, {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 3, character: 13 },
      end: { line: 3, character: 22 }
    }
  });
  assert.deepEqual(
    otherModuleCompletions.map((item) => item.label),
    []
  );
  assert.deepEqual(getTypeFields(project, 'LocalRecord'), [
    {
      name: 'Value',
      range: {
        start: { line: 8, character: 4 },
        end: { line: 8, character: 9 }
      }
    }
  ]);
});

test('RaiseEvent resolves to an event declaration in the current module', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/TaskRunner.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "TaskRunner"',
        'Option Explicit',
        '',
        'Public Event Completed(ByVal Result As String)',
        '',
        'Public Sub Run()',
        '    RaiseEvent Completed("ok")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/TaskRunner.cls',
    position: { line: 7, character: 17 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/TaskRunner.cls',
    range: {
      start: { line: 4, character: 13 },
      end: { line: 4, character: 22 }
    }
  });
});

test('WithEvents handler names resolve to the declared type event', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/FormModule.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "FormModule"',
        'Option Explicit',
        '',
        'Private WithEvents Button As CommandButton',
        '',
        'Private Sub Button_Click()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 6, character: 20 }
  });

  assert.deepEqual(definition, {
    uri: 'file:///project/CommandButton.cls',
    range: {
      start: { line: 4, character: 13 },
      end: { line: 4, character: 18 }
    }
  });
});

test('comma-separated WithEvents declarators resolve handlers and source variable references', () => {
  const declaration_line = 'Private WithEvents Button As CommandButton, TextBox1 As TextBox';
  const handler_line = 'Private Sub TextBox1_Change()';
  const project = buildVbaProject([
    {
      uri: 'file:///project/FormModule.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "FormModule"',
        'Option Explicit',
        '',
        declaration_line,
        '',
        handler_line,
        'End Sub',
        '',
        'Private Sub Run()',
        '    TextBox1',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    },
    {
      uri: 'file:///project/TextBox.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "TextBox"',
        'Option Explicit',
        '',
        'Public Event Change()'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getSyntaxDiagnostics(project, 'file:///project/FormModule.cls'), []);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 6, character: handler_line.indexOf('Change') }
  }), {
    uri: 'file:///project/TextBox.cls',
    range: {
      start: { line: 4, character: 13 },
      end: { line: 4, character: 19 }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 10, character: 8 }
  }), {
    uri: 'file:///project/FormModule.cls',
    range: {
      start: { line: 4, character: declaration_line.indexOf('TextBox1') },
      end: { line: 4, character: declaration_line.indexOf('TextBox1') + 'TextBox1'.length }
    }
  });
});

test('unresolved RaiseEvent and WithEvents handler references return no definition target', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/FormModule.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "FormModule"',
        'Option Explicit',
        '',
        'Private WithEvents Button As CommandButton',
        '',
        'Private Sub Run()',
        '    RaiseEvent Missing',
        'End Sub',
        '',
        'Private Sub Button_Missing()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    }
  ]);

  const missingRaiseEvent = getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 7, character: 17 }
  });
  const missingHandlerEvent = getDefinition(project, {
    uri: 'file:///project/FormModule.cls',
    position: { line: 10, character: 20 }
  });

  assert.equal(missingRaiseEvent, undefined);
  assert.equal(missingHandlerEvent, undefined);
});

test('form designer controls are not inferred for WithEvents handler resolution', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/SampleForm.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form SampleForm',
        '   Begin VB.CommandButton Button',
        '   End',
        'End',
        'Attribute VB_Name = "SampleForm"',
        'Option Explicit',
        '',
        'Private Sub Button_Click()',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CommandButton.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "CommandButton"',
        'Option Explicit',
        '',
        'Public Event Click()'
      ].join('\n')
    }
  ]);

  const definition = getDefinition(project, {
    uri: 'file:///project/SampleForm.frm',
    position: { line: 8, character: 20 }
  });

  assert.notDeepEqual(definition, {
    uri: 'file:///project/CommandButton.cls',
    range: {
      start: { line: 4, character: 13 },
      end: { line: 4, character: 18 }
    }
  });
});

test('hover displays a Doxygen-style DocumentationComment for a private helper', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'*",
        "'* @details Uses the configured source.",
        "'* @param Key Key to read.",
        "'* @return The configured value.",
        'Private Function ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue("id")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: 8 }
  });

  assert.deepEqual(hover, {
    contents: [
      'Reads a value.',
      '',
      'Uses the configured source.',
      '',
      '@param Key Key to read.',
      '@return The configured value.'
    ].join('\n')
  });
});

test('signature help displays documented parameters and return value for parenthesized calls', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'* @param Key Key to read.",
        "'* @param Fallback Value used when the key is missing.",
        "'* @return The configured value.",
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue("id", ',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 11, character: 20 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key, Fallback) As String',
    activeParameter: 1,
    documentation: [
      'Reads a value.',
      '',
      '@return The configured value.'
    ].join('\n'),
    parameters: [
      {
        label: 'Key',
        documentation: 'Key to read.'
      },
      {
        label: 'Fallback',
        documentation: 'Value used when the key is missing.'
      }
    ]
  });
});

test('signature help activates for parenthesized RaiseEvent statements', () => {
  const raise_line = '    RaiseEvent Completed("ok", 200)';
  const project = buildVbaProject([
    {
      uri: 'file:///project/TaskRunner.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "TaskRunner"',
        'Option Explicit',
        '',
        "'* @brief Raised when work completes.",
        "'* @param Result Completion result.",
        "'* @param StatusCode Completion status.",
        'Public Event Completed(ByVal Result As String, Optional ByVal StatusCode As Long = 0)',
        '',
        'Public Sub Run()',
        raise_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/TaskRunner.cls',
    position: { line: 10, character: raise_line.length - 1 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Completed(Result, Optional StatusCode)',
    activeParameter: 1,
    documentation: 'Raised when work completes.',
    parameters: [
      { label: 'Result', documentation: 'Completion result.' },
      { label: 'Optional StatusCode', documentation: 'Completion status.' }
    ]
  });
});

test('signature help counts RaiseEvent arguments without accepting unsupported forms', () => {
  const open_line = '    RaiseEvent Completed(';
  const nested_line = '    RaiseEvent Completed(BuildValue("a,b", 1), "ok")';
  const parenthesis_free_line = '    RaiseEvent Completed "ok"';
  const named_argument_line = '    RaiseEvent Completed(Result:="ok")';
  const unresolved_line = '    RaiseEvent Missing("ok")';
  const project = buildVbaProject([
    {
      uri: 'file:///project/TaskRunner.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "TaskRunner"',
        'Option Explicit',
        '',
        'Public Event Completed(ByVal Result As Variant, Optional ByVal Message As String = "")',
        '',
        'Public Function BuildValue(ByVal Left As String, ByVal Right As Long) As Variant',
        'End Function',
        '',
        'Public Sub Run()',
        open_line,
        nested_line,
        parenthesis_free_line,
        named_argument_line,
        unresolved_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const uri = 'file:///project/TaskRunner.cls';
  const openSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 10, character: open_line.length }
  });
  const nestedSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 11, character: nested_line.length - 1 }
  });

  assert.equal(openSignatureHelp?.label, 'Completed(Result, Optional Message)');
  assert.equal(openSignatureHelp?.activeParameter, 0);
  assert.equal(nestedSignatureHelp?.label, 'Completed(Result, Optional Message)');
  assert.equal(nestedSignatureHelp?.activeParameter, 1);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 12, character: parenthesis_free_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 13, character: named_argument_line.length - 1 }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 14, character: unresolved_line.length - 1 }
  }), undefined);
});

test('signature help selects source parameters by named argument', () => {
  const call_line = '    ReadValue("id", matchcase:=True';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a", Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 7, character: call_line.length }
  });

  assert.equal(signatureHelp?.activeParameter, 2);
});

test('signature help selects HostDefinition parameters by named argument', () => {
  const call_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Find("needle", LookAt:=xlWhole';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: call_line.length }
  });

  assert.equal(signatureHelp?.activeParameter, 3);
});

test('signature help preserves comma-count fallback for unknown and ambiguous named arguments', () => {
  const source_call_line = '    ReadValue("id", Unknown:=True';
  const host_call_line = '    thing.Configure("id", Target:=True';
  const missing_name_call_line = '    thing.ConfigureMissing("id", Target:=True';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a", Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Sub Run()',
        source_call_line,
        '    Dim thing As TestThing',
        host_call_line,
        missing_name_call_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'TestThing',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Configure',
            kind: 'function',
            hostApplication: 'excel',
            signature: {
              label: 'Configure(Key, Target, Target)',
              parameters: [
                { name: 'Key' },
                { name: 'Target' },
                { name: 'Target' }
              ]
            }
          },
          {
            name: 'ConfigureMissing',
            kind: 'function',
            hostApplication: 'excel',
            signature: {
              label: 'ConfigureMissing(Key, Target)',
              parameters: [
                { name: 'Key' },
                { name: '', label: 'Target' }
              ]
            }
          }
        ]
      }
    ]
  });

  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 7, character: source_call_line.length }
  })?.activeParameter, 1);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: host_call_line.length }
  })?.activeParameter, 1);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: missing_name_call_line.length }
  })?.activeParameter, 1);
});

test('signature help preserves comma-count fallback until named argument syntax is complete before the cursor', () => {
  const call_line = '    ReadValue("id", matchcase:=True';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a", Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const cursor_before_separator = call_line.indexOf(':');
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 7, character: cursor_before_separator }
  })?.activeParameter, 1);
});

test('signature help ignores named-argument separators outside the active argument prefix', () => {
  const string_call_line = '    ReadValue("MatchCase:=True", ';
  const comment_call_line = '    ReadValue("id", \' comment, MatchCase:=True';
  const nested_call_line = '    ReadValue(BuildValue(MatchCase:=True), ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a", Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Function BuildValue(Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Sub Run()',
        string_call_line,
        comment_call_line,
        nested_call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: string_call_line.length }
  })?.activeParameter, 1);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 11, character: comment_call_line.length }
  })?.activeParameter, 1);
  assert.equal(getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: nested_call_line.length }
  })?.activeParameter, 1);
});

test('signature help selects source parameters by named argument in continued argument lists', () => {
  const active_line = '        matchcase:=True';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a", Optional ByVal MatchCase As Boolean = False) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "id", _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(signatureHelp?.activeParameter, 2);
});

test('signature help remains active for source continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'* @param Key Key to read.",
        "'* @param Fallback Value used when the key is missing.",
        "'* @return The configured value.",
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "id", _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 13, character: active_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key, Fallback) As String',
    activeParameter: 1,
    documentation: [
      'Reads a value.',
      '',
      '@return The configured value.'
    ].join('\n'),
    parameters: [
      {
        label: 'Key',
        documentation: 'Key to read.'
      },
      {
        label: 'Fallback',
        documentation: 'Value used when the key is missing.'
      }
    ]
  });
});

test('signature help counts only top-level continued argument separators before the cursor', () => {
  const cursor_prefix = '        ';
  const active_line = `${cursor_prefix}BuildTail(1, 2), "after"`;
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Invoke(ByVal First As String, ByVal Second As Variant, ByVal Third As Variant, ByVal Fourth As Variant, ByVal Fifth As Variant)',
        'End Sub',
        '',
        'Public Function BuildValue(ByVal Left As Integer, ByVal Right As Integer) As Variant',
        'End Function',
        '',
        'Public Function BuildTail(ByVal Left As Integer, ByVal Right As Integer) As Variant',
        'End Function',
        '',
        'Public Sub Run()',
        '    Invoke( _',
        '        "a,b", _',
        '        BuildValue(1, 2), _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 16, character: cursor_prefix.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Invoke(First, Second, Third, Fourth, Fifth)',
    activeParameter: 2,
    documentation: undefined,
    parameters: [
      { label: 'First', documentation: undefined },
      { label: 'Second', documentation: undefined },
      { label: 'Third', documentation: undefined },
      { label: 'Fourth', documentation: undefined },
      { label: 'Fifth', documentation: undefined }
    ]
  });
});

test('signature help uses the innermost continued nested call at the cursor', () => {
  const active_line = '            ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Outer(ByVal First As Variant, ByVal Second As Variant)',
        'End Sub',
        '',
        'Public Function Inner(ByVal Key As String, ByVal Fallback As String) As Variant',
        'End Function',
        '',
        'Public Sub Run()',
        '    Outer( _',
        '        Inner( _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: active_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Inner(Key, Fallback) As Variant',
    activeParameter: 0,
    documentation: undefined,
    parameters: [
      { label: 'Key', documentation: undefined },
      { label: 'Fallback', documentation: undefined }
    ]
  });
});

test('signature help fails closed for unresolved continued nested calls', () => {
  const active_line = '            ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Outer(ByVal First As Variant, ByVal Second As Variant)',
        'End Sub',
        '',
        'Public Sub Run()',
        '    Outer( _',
        '        MissingInner( _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(signatureHelp, undefined);
});

test('signature help fails closed for code continuation markers followed by comments', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "id", _ \' invalid continuation',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(signatureHelp, undefined);
});

test('signature help fails closed when a continued argument list is missing a code continuation marker', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "id",',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(signatureHelp, undefined);
});

test('signature help fails closed for comment continuation markers in continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue( _',
        '        "id" \' comment _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(signatureHelp, undefined);
});

test('signature help displays source CallableSignature parameter metadata when available', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'* @param Key Key to read.",
        "'* @return The configured value.",
        'Public Function ReadValue(ByVal Key As String, Optional ByRef Fallback As String = "n/a") As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue("id", ',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: 20 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key, Optional Fallback) As String',
    activeParameter: 1,
    documentation: [
      'Reads a value.',
      '',
      '@return The configured value.'
    ].join('\n'),
    parameters: [
      {
        label: 'Key',
        documentation: 'Key to read.'
      },
      {
        label: 'Optional Fallback',
        documentation: 'String Optional. Default: "n/a".'
      }
    ]
  });
});

test('signature help displays HostDefinition CallableSignature for typed host method calls', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Find "outside signature help"',
        '    rng.Find("needle", ',
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Find',
            kind: 'function',
            hostApplication: 'excel',
            typeName: 'Range',
            signature: {
              label: 'Find(What, Optional After) As Range',
              returnTypeName: 'Range',
              documentation: 'Finds specific information in a range.',
              parameters: [
                {
                  name: 'What',
                  typeName: 'Variant',
                  documentation: 'The data to search for.'
                },
                {
                  name: 'After',
                  label: 'Optional After',
                  optional: true,
                  typeName: 'Variant',
                  documentation: 'The cell after which the search begins.'
                }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 23 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After) As Range',
    activeParameter: 1,
    documentation: 'Finds specific information in a range.',
    parameters: [
      {
        label: 'What',
        documentation: 'The data to search for.'
      },
      {
        label: 'Optional After',
        documentation: 'The cell after which the search begins.'
      }
    ]
  });
});

test('signature help remains active for host continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Find "outside signature help"',
        '    rng.Find( _',
        '        "needle", _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Find',
            kind: 'function',
            hostApplication: 'excel',
            typeName: 'Range',
            signature: {
              label: 'Find(What, Optional After) As Range',
              returnTypeName: 'Range',
              documentation: 'Finds specific information in a range.',
              parameters: [
                {
                  name: 'What',
                  typeName: 'Variant',
                  documentation: 'The data to search for.'
                },
                {
                  name: 'After',
                  label: 'Optional After',
                  optional: true,
                  typeName: 'Variant',
                  documentation: 'The cell after which the search begins.'
                }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 8, character: active_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After) As Range',
    activeParameter: 1,
    documentation: 'Finds specific information in a range.',
    parameters: [
      {
        label: 'What',
        documentation: 'The data to search for.'
      },
      {
        label: 'Optional After',
        documentation: 'The cell after which the search begins.'
      }
    ]
  });
});

test('signature help uses bundled HostSignatureDiscovery snapshot metadata', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Find("needle", ',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 23 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range',
    activeParameter: 1,
    documentation: 'Finds specific information in a range.',
    parameters: [
      {
        label: 'What',
        documentation: 'Variant. The data to search for.'
      },
      {
        label: 'Optional After',
        documentation: 'Variant. The cell after which the search begins.'
      },
      {
        label: 'Optional LookIn',
        documentation: 'Variant. The type of information to search.'
      },
      {
        label: 'Optional LookAt',
        documentation: 'Variant. Can be xlWhole or xlPart.'
      },
      {
        label: 'Optional SearchOrder',
        documentation: 'Variant. Can be xlByRows or xlByColumns.'
      },
      {
        label: 'Optional SearchDirection',
        documentation: 'Variant. Can be xlNext or xlPrevious.'
      },
      {
        label: 'Optional MatchCase',
        documentation: 'Variant. True to make the search case-sensitive.'
      },
      {
        label: 'Optional MatchByte',
        documentation: 'Variant. Used for double-byte language support.'
      },
      {
        label: 'Optional SearchFormat',
        documentation: 'Variant. The search format.'
      }
    ]
  });
});

test('signature help resolves host methods reached through member chains', () => {
  const chain_line = '    Application.ActiveWorkbook.Worksheets(1).Range("A1").Find("needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range',
    activeParameter: 1,
    documentation: 'Finds specific information in a range.',
    parameters: [
      { label: 'What', documentation: 'Variant. The data to search for.' },
      { label: 'Optional After', documentation: 'Variant. The cell after which the search begins.' },
      { label: 'Optional LookIn', documentation: 'Variant. The type of information to search.' },
      { label: 'Optional LookAt', documentation: 'Variant. Can be xlWhole or xlPart.' },
      { label: 'Optional SearchOrder', documentation: 'Variant. Can be xlByRows or xlByColumns.' },
      { label: 'Optional SearchDirection', documentation: 'Variant. Can be xlNext or xlPrevious.' },
      { label: 'Optional MatchCase', documentation: 'Variant. True to make the search case-sensitive.' },
      { label: 'Optional MatchByte', documentation: 'Variant. Used for double-byte language support.' },
      { label: 'Optional SearchFormat', documentation: 'Variant. The search format.' }
    ]
  });
});

test('signature help resolves host methods reached through ContinuedMemberChain', () => {
  const final_chain_line = '        .Range("A1").Find("needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        final_chain_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: final_chain_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help remains active for ContinuedMemberChain continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        '        .Range("A1").Find( _',
        '            "needle", _',
        active_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 8, character: active_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help resolves host methods reached through WithReceiver leading-dot chains', () => {
  const chain_line = '        .Find("needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help remains active for WithReceiver continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        '        .Find( _',
        '            "needle", _',
        active_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: active_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help remains active for continued WithReceiver continued argument lists', () => {
  const active_line = '        ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook _',
        '        .Worksheets(1) _',
        '        .Range("A1")',
        '        .Find( _',
        '            "needle", _',
        active_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 9, character: active_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help resolves source methods reached through member chains', () => {
  const chain_line = '    CustomerFactory.CreateCustomer().LookupOrder("A001", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        "'* @brief Looks up an order.",
        "'* @param Key Order key.",
        "'* @return Order name.",
        'Public Function LookupOrder(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'LookupOrder(Key, Optional Fallback) As String',
    activeParameter: 1,
    documentation: 'Looks up an order.\n\n@return Order name.',
    parameters: [
      { label: 'Key', documentation: 'Order key.' },
      { label: 'Optional Fallback', documentation: 'String Optional. Default: "n/a".' }
    ]
  });
});

test('signature help resolves source methods reached through WithReceiver leading-dot chains', () => {
  const chain_line = '        .LookupOrder("A001", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With CustomerFactory.CreateCustomer()',
        chain_line,
        '    End With',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        "'* @brief Looks up a customer order.",
        "'* @param Key Customer order key.",
        "'* @return Customer order name.",
        'Public Function LookupOrder(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Order.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Order"',
        'Option Explicit',
        '',
        "'* @brief Looks up a standalone order.",
        'Public Function LookupOrder(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: chain_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'LookupOrder(Key, Optional Fallback) As String',
    activeParameter: 1,
    documentation: 'Looks up a customer order.\n\n@return Customer order name.',
    parameters: [
      { label: 'Key', documentation: 'Customer order key.' },
      { label: 'Optional Fallback', documentation: 'String Optional. Default: "n/a".' }
    ]
  });
});

test('signature help resolves host-qualified Word member chains', () => {
  const chain_line = '    Word.Application.Documents.Open("C:\\Temp\\Document.docx", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        chain_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    additionalHostApplications: ['word']
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: chain_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Open(FileName, Optional ConfirmConversions) As Document',
    activeParameter: 1,
    documentation: 'Opens a Word document.',
    parameters: [
      { label: 'FileName', documentation: 'The document file name.' },
      { label: 'Optional ConfirmConversions', documentation: 'Variant Optional.' }
    ]
  });
});

test('signature help displays HostDefinition CallableSignature for host-qualified type annotations', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim app As Word.Application',
        '    app.Run "outside signature help"',
        '    app.Run("MacroName", ',
        'End Sub'
      ].join('\n')
    }
  ], {
    mainHostApplication: 'word',
    hostDefinitions: [
      {
        name: 'Application',
        kind: 'class',
        hostApplication: 'word',
        members: [
          {
            name: 'Run',
            kind: 'function',
            hostApplication: 'word',
            typeName: 'Variant',
            signature: {
              label: 'Run(MacroName, Optional Arg1) As Variant',
              returnTypeName: 'Variant',
              documentation: 'Runs a macro.',
              parameters: [
                {
                  name: 'MacroName',
                  typeName: 'String',
                  documentation: 'The name of the macro to run.'
                },
                {
                  name: 'Arg1',
                  label: 'Optional Arg1',
                  optional: true,
                  typeName: 'Variant'
                }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: 25 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Run(MacroName, Optional Arg1) As Variant',
    activeParameter: 1,
    documentation: 'Runs a macro.',
    parameters: [
      {
        label: 'MacroName',
        documentation: 'The name of the macro to run.'
      },
      {
        label: 'Optional Arg1',
        documentation: 'Variant Optional.'
      }
    ]
  });
});

test('signature help ignores host methods without CallableSignature metadata', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Clear(',
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Clear',
            kind: 'function',
            hostApplication: 'excel'
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: 14 }
  });

  assert.equal(signatureHelp, undefined);
});

test('ordinary apostrophe comments are ignored by hover and signature help', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "' @brief This ordinary comment must be ignored.",
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    ReadValue("id")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 8, character: 8 }
  });
  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 8, character: 15 }
  });

  assert.equal(hover, undefined);
  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key) As String',
    activeParameter: 0,
    documentation: undefined,
    parameters: [{ label: 'Key', documentation: undefined }]
  });
});

test('signature help activates for source ParenthesisFreeCall statements', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'* @param Key Key to read.",
        'Public Sub ReadValue(ByVal Key As String)',
        'End Sub',
        '',
        'Public Sub Run()',
        '    ReadValue "id"',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 9, character: 15 }
  });

  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key)',
    activeParameter: 0,
    documentation: 'Reads a value.',
    parameters: [
      {
        label: 'Key',
        documentation: 'Key to read.'
      }
    ]
  });
});

test('signature help remains active for source ContinuedParenthesisFreeCall statements', () => {
  const fallback_line = '        "fallback"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        "'* @brief Reads a value.",
        "'* @param Key Key to read.",
        "'* @param Fallback Fallback value.",
        'Public Sub ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a")',
        'End Sub',
        '',
        'Public Sub Run()',
        '    ReadValue _',
        '        "id", _',
        fallback_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 12, character: fallback_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'ReadValue(Key, Optional Fallback)',
    activeParameter: 1,
    documentation: 'Reads a value.',
    parameters: [
      { label: 'Key', documentation: 'Key to read.' },
      { label: 'Optional Fallback', documentation: 'Fallback value.' }
    ]
  });
});

test('signature help activates for host ParenthesisFreeCall statements', () => {
  const call_line = '    rng.Find "needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Find',
            kind: 'function',
            hostApplication: 'excel',
            typeName: 'Range',
            signature: {
              label: 'Find(What, Optional After) As Range',
              returnTypeName: 'Range',
              documentation: 'Finds specific information in a range.',
              parameters: [
                {
                  name: 'What',
                  typeName: 'Variant',
                  documentation: 'The data to search for.'
                },
                {
                  name: 'After',
                  label: 'Optional After',
                  optional: true,
                  typeName: 'Variant',
                  documentation: 'The cell after which the search begins.'
                }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: call_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After) As Range',
    activeParameter: 1,
    documentation: 'Finds specific information in a range.',
    parameters: [
      {
        label: 'What',
        documentation: 'The data to search for.'
      },
      {
        label: 'Optional After',
        documentation: 'The cell after which the search begins.'
      }
    ]
  });
});

test('signature help selects named host parameters in ContinuedParenthesisFreeCall statements', () => {
  const look_at_line = '        LookAt:=xlWhole';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        '    rng.Find "needle", _',
        look_at_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Find',
            kind: 'function',
            hostApplication: 'excel',
            signature: {
              label: 'Find(What, Optional After, Optional LookAt) As Range',
              returnTypeName: 'Range',
              parameters: [
                { name: 'What' },
                { name: 'After', label: 'Optional After', optional: true },
                { name: 'LookAt', label: 'Optional LookAt', optional: true }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 6, character: look_at_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'Find(What, Optional After, Optional LookAt) As Range',
    activeParameter: 2,
    documentation: undefined,
    parameters: [
      { label: 'What', documentation: undefined },
      { label: 'Optional After', documentation: 'Optional.' },
      { label: 'Optional LookAt', documentation: 'Optional.' }
    ]
  });
});

test('signature help preserves ContinuedParenthesisFreeCall parameters on continuation whitespace', () => {
  const continued_call_line = '    ReadValue "id", _';
  const indented_argument_line = '        "fallback"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a")',
        'End Sub',
        '',
        'Public Sub Run()',
        continued_call_line,
        indented_argument_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const uri = 'file:///project/Worker.bas';
  const markerSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 7, character: continued_call_line.length }
  });
  const indentationSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 8, character: indented_argument_line.indexOf('"') }
  });

  assert.equal(markerSignatureHelp?.label, 'ReadValue(Key, Optional Fallback)');
  assert.equal(markerSignatureHelp?.activeParameter, 1);
  assert.equal(indentationSignatureHelp?.label, markerSignatureHelp?.label);
  assert.equal(indentationSignatureHelp?.activeParameter, 1);
});

test('signature help activates for source member-chain ParenthesisFreeCall statements', () => {
  const call_line = '    CustomerFactory.CreateCustomer().LookupOrder "A001", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/CustomerFactory.bas',
      text: [
        'Attribute VB_Name = "CustomerFactory"',
        'Option Explicit',
        '',
        'Public Function CreateCustomer() As Customer',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Customer.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Customer"',
        'Option Explicit',
        '',
        "'* @brief Looks up an order.",
        "'* @param Key Order key.",
        "'* @return Order name.",
        'Public Function LookupOrder(ByVal Key As String, Optional ByVal Fallback As String = "n/a") As String',
        'End Function'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: call_line.length }
  });

  assert.deepEqual(signatureHelp, {
    label: 'LookupOrder(Key, Optional Fallback) As String',
    activeParameter: 1,
    documentation: 'Looks up an order.\n\n@return Order name.',
    parameters: [
      { label: 'Key', documentation: 'Order key.' },
      { label: 'Optional Fallback', documentation: 'String Optional. Default: "n/a".' }
    ]
  });
});

test('signature help activates for WithReceiver ParenthesisFreeCall statements', () => {
  const call_line = '        .Find "needle", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    With Application.ActiveWorkbook.Worksheets(1).Range("A1")',
        call_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: call_line.length }
  });

  assert.equal(
    signatureHelp?.label,
    'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range'
  );
  assert.equal(signatureHelp?.activeParameter, 1);
  assert.equal(signatureHelp?.documentation, 'Finds specific information in a range.');
});

test('signature help resolves ContinuedParenthesisFreeCall callables through continued receivers', () => {
  const member_chain_arg_line = '    LookAt:=xlWhole';
  const with_receiver_arg_line = '        LookAt:=xlWhole';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Application.ActiveWorkbook _',
        '        .Worksheets(1).Range("A1").Find "needle", _',
        member_chain_arg_line,
        '    With Application.ActiveWorkbook _',
        '        .Worksheets(1).Range("A1")',
        '        .Find "needle", _',
        with_receiver_arg_line,
        '    End With',
        'End Sub'
      ].join('\n')
    }
  ]);

  const uri = 'file:///project/Caller.bas';
  const memberChainSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 6, character: member_chain_arg_line.length }
  });
  const withReceiverSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 10, character: with_receiver_arg_line.length }
  });

  assert.equal(memberChainSignatureHelp?.label, 'Find(What, Optional After, Optional LookIn, Optional LookAt, Optional SearchOrder, Optional SearchDirection, Optional MatchCase, Optional MatchByte, Optional SearchFormat) As Range');
  assert.equal(memberChainSignatureHelp?.activeParameter, 3);
  assert.equal(withReceiverSignatureHelp?.label, memberChainSignatureHelp?.label);
  assert.equal(withReceiverSignatureHelp?.activeParameter, 3);
});

test('signature help selects ParenthesisFreeCall parameters by named argument', () => {
  const call_line = '    rng.Find "needle", LookAt:=xlWhole';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Find',
            kind: 'function',
            hostApplication: 'excel',
            signature: {
              label: 'Find(What, Optional After, Optional LookAt) As Range',
              returnTypeName: 'Range',
              parameters: [
                { name: 'What' },
                { name: 'After', label: 'Optional After', optional: true },
                { name: 'LookAt', label: 'Optional LookAt', optional: true }
              ]
            }
          }
        ]
      }
    ]
  });

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 5, character: call_line.length }
  });

  assert.equal(signatureHelp?.activeParameter, 2);
});

test('signature help counts only top-level ParenthesisFreeCall argument separators', () => {
  const call_line = '    Invoke (BuildValue(1, 2)), "a,b", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Invoke(ByVal First As String, ByVal Second As Variant, ByVal Third As Variant)',
        'End Sub',
        '',
        'Public Function BuildValue(ByVal Left As Integer, ByVal Right As Integer) As Variant',
        'End Function',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: call_line.length }
  });

  assert.equal(signatureHelp?.activeParameter, 2);
});

test('signature help counts ContinuedParenthesisFreeCall separators outside nested calls', () => {
  const nested_argument_line = '        2), _';
  const outer_argument_line = '        "third"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Invoke(ByVal First As Variant, ByVal Second As String, ByVal Third As String)',
        'End Sub',
        '',
        'Public Function BuildValue(ByVal Left As Integer, ByVal Right As Integer) As Variant',
        'End Function',
        '',
        'Public Sub Run()',
        '    Invoke (BuildValue(1, _',
        nested_argument_line,
        '        "a,b", _',
        outer_argument_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const uri = 'file:///project/Worker.bas';
  const nestedSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 11, character: nested_argument_line.indexOf('2') + 1 }
  });
  const outerSignatureHelp = getSignatureHelp(project, {
    uri,
    position: { line: 13, character: outer_argument_line.length }
  });

  assert.equal(nestedSignatureHelp?.label, 'BuildValue(Left, Right) As Variant');
  assert.equal(nestedSignatureHelp?.activeParameter, 1);
  assert.equal(outerSignatureHelp?.label, 'Invoke(First, Second, Third)');
  assert.equal(outerSignatureHelp?.activeParameter, 2);
});

test('signature help stops ContinuedParenthesisFreeCall arguments at top-level colons', () => {
  const colon_line = '        "fallback": ReadValue "other"';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Invoke(ByVal First As String, ByVal Second As String)',
        'End Sub',
        '',
        'Public Sub ReadValue(ByVal Key As String)',
        'End Sub',
        '',
        'Public Sub Run()',
        '    Invoke "id", _',
        colon_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 11, character: colon_line.length }
  });

  assert.equal(signatureHelp?.label, 'ReadValue(Key)');
  assert.equal(signatureHelp?.activeParameter, 0);
});

test('signature help fails closed for invalid ContinuedParenthesisFreeCall continuations', () => {
  const missing_marker_argument_line = '        "fallback"';
  const blank_interstitial_argument_line = '        "blank fallback"';
  const comment_interstitial_argument_line = '        "comment fallback"';
  const incomplete_continuation_line = '    ReadValue "id", _';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub ReadValue(ByVal Key As String, Optional ByVal Fallback As String = "n/a")',
        'End Sub',
        '',
        'Public Sub Run()',
        '    ReadValue "id",',
        missing_marker_argument_line,
        '    ReadValue "id", _',
        '',
        blank_interstitial_argument_line,
        '    ReadValue "id", _',
        '    \' comment',
        comment_interstitial_argument_line,
        incomplete_continuation_line,
        '',
        'End Sub'
      ].join('\n')
    }
  ]);

  const uri = 'file:///project/Worker.bas';
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 8, character: missing_marker_argument_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 11, character: blank_interstitial_argument_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 14, character: comment_interstitial_argument_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 15, character: incomplete_continuation_line.length }
  }), undefined);
});

test('signature help fails closed for unsupported ParenthesisFreeCall shapes', () => {
  const no_space_line = '    ReadValue';
  const assignment_line = '    ReadValue = "id"';
  const unresolved_line = '    MissingValue ';
  const keyword_line = '    If ReadValue ';
  const call_keyword_line = '    Call ReadValue ';
  const trailing_comment_continuation_line = '    ReadValue _ \' comment';
  const missing_signature_line = '    rng.Clear ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    Dim rng As Range',
        no_space_line,
        assignment_line,
        unresolved_line,
        keyword_line,
        call_keyword_line,
        trailing_comment_continuation_line,
        missing_signature_line,
        'End Sub'
      ].join('\n')
    }
  ], {
    hostDefinitions: [
      {
        name: 'Range',
        kind: 'class',
        hostApplication: 'excel',
        members: [
          {
            name: 'Clear',
            kind: 'function',
            hostApplication: 'excel'
          }
        ]
      }
    ]
  });

  const uri = 'file:///project/Worker.bas';
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 8, character: no_space_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 9, character: assignment_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 10, character: unresolved_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 11, character: keyword_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 12, character: call_keyword_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 13, character: trailing_comment_continuation_line.length }
  }), undefined);
  assert.equal(getSignatureHelp(project, {
    uri,
    position: { line: 14, character: missing_signature_line.length }
  }), undefined);
});

test('signature help uses the current ParenthesisFreeCall statement segment', () => {
  const call_line = '    ReadValue "a:b" : Invoke "id", ';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub ReadValue(ByVal Key As String)',
        'End Sub',
        '',
        'Public Sub Invoke(ByVal Key As String, ByVal Fallback As String)',
        'End Sub',
        '',
        'Public Sub Run()',
        call_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 10, character: call_line.length }
  });

  assert.equal(signatureHelp?.label, 'Invoke(Key, Fallback)');
  assert.equal(signatureHelp?.activeParameter, 1);
});

test('hover falls back to interface DocumentationComment through Implements', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/IReader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "IReader"',
        'Option Explicit',
        '',
        "'* @brief Reads a value from the interface contract.",
        "'* @param Key Key to read.",
        "'* @return The resolved value.",
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Reader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Reader"',
        'Option Explicit',
        'Implements IReader',
        '',
        'Private Function IReader_ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    IReader_ReadValue("id")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Reader.cls',
    position: { line: 9, character: 10 }
  });
  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Reader.cls',
    position: { line: 9, character: 23 }
  });

  assert.deepEqual(hover, {
    contents: [
      'Reads a value from the interface contract.',
      '',
      '@param Key Key to read.',
      '@return The resolved value.'
    ].join('\n')
  });
  assert.deepEqual(signatureHelp, {
    label: 'IReader_ReadValue(Key) As String',
    activeParameter: 0,
    documentation: [
      'Reads a value from the interface contract.',
      '',
      '@return The resolved value.'
    ].join('\n'),
    parameters: [{ label: 'Key', documentation: 'Key to read.' }]
  });
});

test('implementation DocumentationComment overrides Implements fallback for hover and signature help', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/IReader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "IReader"',
        'Option Explicit',
        '',
        "'* @brief Interface documentation.",
        "'* @param Key Interface key documentation.",
        "'* @return Interface return documentation.",
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Reader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Reader"',
        'Option Explicit',
        'Implements IReader',
        '',
        "'* @brief Implementation documentation.",
        'Private Function IReader_ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    IReader_ReadValue("id")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Reader.cls',
    position: { line: 10, character: 10 }
  });
  const signatureHelp = getSignatureHelp(project, {
    uri: 'file:///project/Reader.cls',
    position: { line: 10, character: 23 }
  });

  assert.deepEqual(hover, {
    contents: 'Implementation documentation.'
  });
  assert.deepEqual(signatureHelp, {
    label: 'IReader_ReadValue(Key) As String',
    activeParameter: 0,
    documentation: 'Implementation documentation.',
    parameters: [{ label: 'Key', documentation: undefined }]
  });
});

test('missing implementation and interface documentation produces no hover', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/IReader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "IReader"',
        'Option Explicit',
        '',
        'Public Function ReadValue(ByVal Key As String) As String',
        'End Function'
      ].join('\n')
    },
    {
      uri: 'file:///project/Reader.cls',
      text: [
        'VERSION 1.0 CLASS',
        'Attribute VB_Name = "Reader"',
        'Option Explicit',
        'Implements IReader',
        '',
        'Private Function IReader_ReadValue(ByVal Key As String) As String',
        'End Function',
        '',
        'Public Sub Run()',
        '    IReader_ReadValue("id")',
        'End Sub'
      ].join('\n')
    }
  ]);

  const hover = getHover(project, {
    uri: 'file:///project/Reader.cls',
    position: { line: 9, character: 10 }
  });

  assert.equal(hover, undefined);
});

test('SemanticTokens classify declarations and resolved references without unresolved or ambiguous identifiers', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Caller.bas',
        text: [
          'Attribute VB_Name = "Caller"',
          'Option Explicit',
          '',
          'Public Sub Run(ByVal Mode As RunMode)',
          '    Dim target As Range',
          '    BuildValue',
          '    MissingValue',
          '    Range',
          '    Mode = Manual',
          'End Sub'
        ].join('\n')
      },
      {
        uri: 'file:///project/Builder.bas',
        text: [
          'Attribute VB_Name = "Builder"',
          'Option Explicit',
          '',
          'Public Function BuildValue() As String',
          '    BuildValue = "ok"',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/FirstAmbiguous.bas',
        text: [
          'Attribute VB_Name = "FirstAmbiguous"',
          'Option Explicit',
          '',
          'Public Function MissingValue() As String',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/SecondAmbiguous.bas',
        text: [
          'Attribute VB_Name = "SecondAmbiguous"',
          'Option Explicit',
          '',
          'Public Function MissingValue() As String',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/Modes.bas',
        text: [
          'Attribute VB_Name = "Modes"',
          'Option Explicit',
          '',
          'Public Enum RunMode',
          '    Automatic = 0',
          '    Manual',
          'End Enum'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: [
        {
          name: 'Range',
          kind: 'class',
          members: [
            { name: 'Address', kind: 'property' }
          ]
        }
      ]
    }
  );

  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');

  assert.deepEqual(tokens, [
    {
      range: {
        start: { line: 0, character: 21 },
        end: { line: 0, character: 27 }
      },
      tokenType: 'namespace'
    },
    {
      range: {
        start: { line: 3, character: 11 },
        end: { line: 3, character: 14 }
      },
      tokenType: 'function'
    },
    {
      range: {
        start: { line: 3, character: 21 },
        end: { line: 3, character: 25 }
      },
      tokenType: 'parameter'
    },
    {
      range: {
        start: { line: 3, character: 29 },
        end: { line: 3, character: 36 }
      },
      tokenType: 'enum'
    },
    {
      range: {
        start: { line: 4, character: 8 },
        end: { line: 4, character: 14 }
      },
      tokenType: 'variable'
    },
    {
      range: {
        start: { line: 4, character: 18 },
        end: { line: 4, character: 23 }
      },
      tokenType: 'class'
    },
    {
      range: {
        start: { line: 5, character: 4 },
        end: { line: 5, character: 14 }
      },
      tokenType: 'function'
    },
    {
      range: {
        start: { line: 7, character: 4 },
        end: { line: 7, character: 9 }
      },
      tokenType: 'class'
    },
    {
      range: {
        start: { line: 8, character: 4 },
        end: { line: 8, character: 8 }
      },
      tokenType: 'parameter'
    },
    {
      range: {
        start: { line: 8, character: 11 },
        end: { line: 8, character: 17 }
      },
      tokenType: 'enumMember'
    }
  ]);
});

test('SemanticTokens classify class/form identity, properties, types, events, and skip designer text', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Customer.cls',
        text: [
          'VERSION 1.0 CLASS',
          'Attribute VB_Name = "Customer"',
          'Option Explicit',
          '',
          'Public Event Completed()',
          '',
          'Public Type CustomerRecord',
          '    Id As Long',
          'End Type',
          '',
          'Public Property Get DisplayName() As String',
          'End Property',
          '',
          'Public Sub Run(ByVal record As CustomerRecord)',
          '    Dim target As Range',
          '    RaiseEvent Completed',
          '    DisplayName',
          '    target.Address',
          '    unresolved = "Completed DisplayName"',
          "' DisplayName",
          'End Sub'
        ].join('\n')
      },
      {
        uri: 'file:///project/SampleForm.frm',
        text: [
          'VERSION 5.00',
          'Begin VB.Form SampleForm',
          '   Caption = "Run"',
          'End',
          'Attribute VB_Name = "SampleForm"',
          'Option Explicit',
          '',
          'Public Sub Run()',
          'End Sub'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: [
        {
          name: 'Range',
          kind: 'class',
          members: [
            { name: 'Address', kind: 'property' }
          ]
        }
      ]
    }
  );

  const class_tokens = getSemanticTokens(project, 'file:///project/Customer.cls');
  const form_tokens = getSemanticTokens(project, 'file:///project/SampleForm.frm');

  assertSemanticToken(class_tokens, 1, 21, 29, 'class');
  assertSemanticToken(class_tokens, 4, 13, 22, 'event');
  assertSemanticToken(class_tokens, 6, 12, 26, 'type');
  assertSemanticToken(class_tokens, 10, 20, 31, 'property');
  assertSemanticToken(class_tokens, 13, 11, 14, 'function');
  assertSemanticToken(class_tokens, 13, 21, 27, 'parameter');
  assertSemanticToken(class_tokens, 13, 31, 45, 'type');
  assertSemanticToken(class_tokens, 14, 8, 14, 'variable');
  assertSemanticToken(class_tokens, 14, 18, 23, 'class');
  assertSemanticToken(class_tokens, 15, 15, 24, 'event');
  assertSemanticToken(class_tokens, 16, 4, 15, 'property');
  assertSemanticToken(class_tokens, 17, 4, 10, 'variable');
  assertSemanticToken(class_tokens, 17, 11, 18, 'property');
  assertNoSemanticTokensOnLine(class_tokens, 18);
  assertNoSemanticTokensOnLine(class_tokens, 19);

  assertSemanticToken(form_tokens, 4, 21, 31, 'class');
  assertSemanticToken(form_tokens, 7, 11, 14, 'function');
  assert.ok(
    form_tokens.every((token) => token.range.start.line >= 4),
    'FormDesignerBlock lines before Attribute VB_Name must not produce semantic tokens'
  );
});

test('SemanticTokens classify SourceConstants as readonly variables', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Const ModuleLimit As Long = 10',
        '',
        'Public Sub Run()',
        '    Const ProcedureLimit As Long = 5',
        '    Dim mutableValue As Long',
        '    mutableValue = ModuleLimit + ProcedureLimit',
        'End Sub'
      ].join('\n')
    }
  ]);

  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');

  assertSemanticToken(tokens, 3, 13, 24, 'variable', ['readonly']);
  assertSemanticToken(tokens, 6, 10, 24, 'variable', ['readonly']);
  assertSemanticToken(tokens, 7, 8, 20, 'variable');
  assertSemanticToken(tokens, 8, 4, 16, 'variable');
  assertSemanticToken(tokens, 8, 19, 30, 'variable', ['readonly']);
  assertSemanticToken(tokens, 8, 33, 47, 'variable', ['readonly']);
});

test('comma-separated procedure Const declarators behave as separate SourceConstants', () => {
  const declaration_line = '    Const FirstLimit As Long = 1, SecondLimit As Long = 2';
  const reference_line = '    FirstLimit + SecondLimit';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        declaration_line,
        reference_line,
        'End Sub'
      ].join('\n')
    }
  ]);

  const second_start = declaration_line.indexOf('SecondLimit');
  const second_reference_start = reference_line.indexOf('SecondLimit');

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: second_reference_start }
  }), {
    uri: 'file:///project/Caller.bas',
    range: {
      start: { line: 4, character: second_start },
      end: { line: 4, character: second_start + 'SecondLimit'.length }
    }
  });

  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');
  assertSemanticToken(tokens, 4, declaration_line.indexOf('FirstLimit'), declaration_line.indexOf('FirstLimit') + 'FirstLimit'.length, 'variable', ['readonly']);
  assertSemanticToken(tokens, 4, second_start, second_start + 'SecondLimit'.length, 'variable', ['readonly']);
  assertSemanticToken(tokens, 5, reference_line.indexOf('FirstLimit'), reference_line.indexOf('FirstLimit') + 'FirstLimit'.length, 'variable', ['readonly']);
  assertSemanticToken(tokens, 5, second_reference_start, second_reference_start + 'SecondLimit'.length, 'variable', ['readonly']);

  assert.deepEqual(
    getRenameEdits(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 5, character: second_reference_start }
    }, 'MaxLimit'),
    [
      {
        uri: 'file:///project/Caller.bas',
        range: {
          start: { line: 4, character: second_start },
          end: { line: 4, character: second_start + 'SecondLimit'.length }
        },
        newText: 'MaxLimit'
      },
      {
        uri: 'file:///project/Caller.bas',
        range: {
          start: { line: 5, character: second_reference_start },
          end: { line: 5, character: second_reference_start + 'SecondLimit'.length }
        },
        newText: 'MaxLimit'
      }
    ]
  );
});

test('comma-separated module Const declarators respect visibility and casing', () => {
  const public_const_line = 'Public Const PublicA = Manual, PublicB As runmode = manual';
  const private_const_line = 'Private Const PrivateA = 1, PrivateB As Long = 2';
  const bare_const_line = 'Const ModuleA = 1, ModuleB As Long = 2';
  const project = buildVbaProject([
    {
      uri: 'file:///project/Constants.bas',
      text: [
        'Attribute VB_Name = "Constants"',
        'Option Explicit',
        '',
        'Public Enum RunMode',
        '    Manual',
        'End Enum',
        '',
        public_const_line,
        private_const_line,
        bare_const_line,
        '',
        'Public Sub Run()',
        '    PrivateB',
        '    ModuleB',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    PublicB',
        '    PrivateB',
        '    ModuleB',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 4, character: 8 }
  }), {
    uri: 'file:///project/Constants.bas',
    range: {
      start: { line: 7, character: public_const_line.indexOf('PublicB') },
      end: { line: 7, character: public_const_line.indexOf('PublicB') + 'PublicB'.length }
    }
  });
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), undefined);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 8 }
  }), undefined);
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Constants.bas',
    position: { line: 12, character: 8 }
  }), {
    uri: 'file:///project/Constants.bas',
    range: {
      start: { line: 8, character: private_const_line.indexOf('PrivateB') },
      end: { line: 8, character: private_const_line.indexOf('PrivateB') + 'PrivateB'.length }
    }
  });
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Constants.bas',
    position: { line: 13, character: 8 }
  }), {
    uri: 'file:///project/Constants.bas',
    range: {
      start: { line: 9, character: bare_const_line.indexOf('ModuleB') },
      end: { line: 9, character: bare_const_line.indexOf('ModuleB') + 'ModuleB'.length }
    }
  });

  const tokens = getSemanticTokens(project, 'file:///project/Constants.bas');
  assertSemanticToken(tokens, 7, public_const_line.indexOf('PublicA'), public_const_line.indexOf('PublicA') + 'PublicA'.length, 'variable', ['readonly']);
  assertSemanticToken(tokens, 7, public_const_line.indexOf('PublicB'), public_const_line.indexOf('PublicB') + 'PublicB'.length, 'variable', ['readonly']);
  assertSemanticToken(tokens, 12, 4, 12, 'variable', ['readonly']);
  assertSemanticToken(tokens, 13, 4, 11, 'variable', ['readonly']);
  assert.equal(formatText(project, 'file:///project/Constants.bas'), [
    'Attribute VB_Name = "Constants"',
    'Option Explicit',
    '',
    'Public Enum RunMode',
    '    Manual',
    'End Enum',
    '',
    'Public Const PublicA = Manual, PublicB As RunMode = Manual',
    'Private Const PrivateA = 1, PrivateB As Long = 2',
    'Const ModuleA = 1, ModuleB As Long = 2',
    '',
    'Public Sub Run()',
    '    PrivateB',
    '    ModuleB',
    'End Sub'
  ].join('\n'));
});

test('SourceConstants support source definition behavior and visibility', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Constants.bas',
      text: [
        'Attribute VB_Name = "Constants"',
        'Option Explicit',
        '',
        'Public Enum RunMode',
        '    Manual',
        'End Enum',
        '',
        'Public Const PublicMode As runmode = Manual',
        'Private Const PrivateLimit As Long = 2',
        'Const ModuleLimit As Long = 3',
        '',
        'Public Sub Run()',
        '    Const LocalLimit As Long = 4',
        '    LocalLimit',
        '    publicmode',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        '    Pu',
        '    PublicMode',
        '    PrivateLimit',
        '    ModuleLimit',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.deepEqual(
    getCompletions(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 4, character: 6 }
    }).map((item) => ({ label: item.label, kind: item.kind })),
    [{ label: 'PublicMode', kind: 'constant' }]
  );
  assert.deepEqual(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 5, character: 8 }
  }), {
    uri: 'file:///project/Constants.bas',
    range: {
      start: { line: 7, character: 13 },
      end: { line: 7, character: 23 }
    }
  });
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 6, character: 8 }
  }), undefined);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 7, character: 8 }
  }), undefined);

  const tokens = getSemanticTokens(project, 'file:///project/Constants.bas');
  assertSemanticToken(tokens, 7, 13, 23, 'variable', ['readonly']);
  assertSemanticToken(tokens, 7, 27, 34, 'enum');
  assertSemanticToken(tokens, 7, 37, 43, 'enumMember');

  assert.deepEqual(
    getRenameEdits(project, {
      uri: 'file:///project/Caller.bas',
      position: { line: 5, character: 8 }
    }, 'DefaultMode')
      .map((edit) => ({ uri: edit.uri, start: edit.range.start, end: edit.range.end, newText: edit.newText })),
    [
      {
        uri: 'file:///project/Constants.bas',
        start: { line: 7, character: 13 },
        end: { line: 7, character: 23 },
        newText: 'DefaultMode'
      },
      {
        uri: 'file:///project/Constants.bas',
        start: { line: 14, character: 4 },
        end: { line: 14, character: 14 },
        newText: 'DefaultMode'
      },
      {
        uri: 'file:///project/Caller.bas',
        start: { line: 5, character: 4 },
        end: { line: 5, character: 14 },
        newText: 'DefaultMode'
      }
    ]
  );
  assert.equal(formatText(project, 'file:///project/Constants.bas'), [
    'Attribute VB_Name = "Constants"',
    'Option Explicit',
    '',
    'Public Enum RunMode',
    '    Manual',
    'End Enum',
    '',
    'Public Const PublicMode As RunMode = Manual',
    'Private Const PrivateLimit As Long = 2',
    'Const ModuleLimit As Long = 3',
    '',
    'Public Sub Run()',
    '    Const LocalLimit As Long = 4',
    '    LocalLimit',
    '    PublicMode',
    'End Sub'
  ].join('\n'));
});

test('SemanticTokens classify ConditionalCompilationConstants as macros without ordinary NameResolution', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Caller.bas',
      text: [
        'Attribute VB_Name = "Caller"',
        'Option Explicit',
        '#Const DEBUG = True',
        '#If DEBUG Then',
        '#ElseIf DEBUG Then',
        '#End If',
        '',
        'Public Sub Run()',
        '    DEBUG',
        'End Sub'
      ].join('\n')
    }
  ]);

  const tokens = getSemanticTokens(project, 'file:///project/Caller.bas');

  assertSemanticToken(tokens, 2, 7, 12, 'macro');
  assertSemanticToken(tokens, 3, 4, 9, 'macro');
  assertSemanticToken(tokens, 4, 8, 13, 'macro');
  assertNoSemanticTokensOnLine(tokens, 8);
  assert.equal(getDefinition(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 8, character: 6 }
  }), undefined);
  assert.deepEqual(getRenameEdits(project, {
    uri: 'file:///project/Caller.bas',
    position: { line: 3, character: 5 }
  }, 'TRACE'), []);
});

function assertSemanticToken(
  tokens: ReturnType<typeof getSemanticTokens>,
  line: number,
  startCharacter: number,
  endCharacter: number,
  tokenType: ReturnType<typeof getSemanticTokens>[number]['tokenType'],
  tokenModifiers: readonly string[] = []
): void {
  assert.ok(
    tokens.some((token) =>
      token.range.start.line === line
        && token.range.start.character === startCharacter
        && token.range.end.line === line
        && token.range.end.character === endCharacter
        && token.tokenType === tokenType
        && assertTokenModifiersEqual(token.tokenModifiers ?? [], tokenModifiers)
    ),
    `Expected ${formatExpectedSemanticToken(tokenType, tokenModifiers)} token at ${line}:${startCharacter}-${endCharacter}`
  );
}

function assertTokenModifiersEqual(actual: readonly string[], expected: readonly string[]): boolean {
  return actual.length === expected.length
    && expected.every((modifier) => actual.includes(modifier));
}

function formatExpectedSemanticToken(tokenType: string, tokenModifiers: readonly string[]): string {
  return tokenModifiers.length === 0
    ? tokenType
    : `${tokenType}.${tokenModifiers.join('.')}`;
}

function assertNoSemanticTokensOnLine(tokens: ReturnType<typeof getSemanticTokens>, line: number): void {
  assert.equal(
    tokens.some((token) => token.range.start.line === line),
    false,
    `Expected no semantic tokens on line ${line}`
  );
}

test('SourceFormatting normalizes language and resolved identifier casing with block indentation', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Caller.bas',
        text: [
          'Attribute vb_name = "Caller"',
          'option explicit',
          '',
          'public sub Run()',
          'dim target as range',
          'buildvalue',
          'range',
          'if true then',
          "'* @brief buildvalue remains prose.",
          'else',
          "' buildvalue remains an ordinary comment.",
          'end if',
          'End Sub'
        ].join('\n')
      },
      {
        uri: 'file:///project/Builder.bas',
        text: [
          'Attribute VB_Name = "Builder"',
          'Option Explicit',
          '',
          'Public Function BuildValue() As String',
          'End Function'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: [{ name: 'Range', kind: 'class' }]
    }
  );

  assert.equal(formatText(project, 'file:///project/Caller.bas'), [
    'Attribute VB_Name = "Caller"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    '    Dim target As Range',
    '    BuildValue',
    '    Range',
    '    If True Then',
    "        '* @brief buildvalue remains prose.",
    '    Else',
    "        ' buildvalue remains an ordinary comment.",
    '    End If',
    'End Sub'
  ].join('\n'));
});

test('SourceFormatting leaves declarations, strings, unresolved identifiers, and ambiguous references unchanged', () => {
  const project = buildVbaProject(
    [
      {
        uri: 'file:///project/Caller.bas',
        text: [
          'Attribute VB_Name = "Caller"',
          'Option Explicit',
          '',
          'public function buildvalue() as string',
          'buildvalue = "buildvalue and mixedcase"',
          'mixedcase',
          'missingvalue',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/Other.bas',
        text: [
          'Attribute VB_Name = "Other"',
          'Option Explicit',
          '',
          'Public Function MixedCase() As String',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/FirstAmbiguous.bas',
        text: [
          'Attribute VB_Name = "FirstAmbiguous"',
          'Option Explicit',
          '',
          'Public Function MissingValue() As String',
          'End Function'
        ].join('\n')
      },
      {
        uri: 'file:///project/SecondAmbiguous.bas',
        text: [
          'Attribute VB_Name = "SecondAmbiguous"',
          'Option Explicit',
          '',
          'Public Function MissingValue() As String',
          'End Function'
        ].join('\n')
      }
    ],
    {
      hostDefinitions: []
    }
  );

  assert.equal(formatText(project, 'file:///project/Caller.bas'), [
    'Attribute VB_Name = "Caller"',
    'Option Explicit',
    '',
    'Public Function buildvalue() As String',
    '    buildvalue = "buildvalue and mixedcase"',
    '    MixedCase',
    '    missingvalue',
    'End Function'
  ].join('\n'));
});

test('SourceFormatting indents VBA block families and mid-block lines', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Blocks.bas',
      text: [
        'Attribute VB_Name = "Blocks"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'For Each item In items',
        'If item.Enabled Then',
        'Do',
        'Loop',
        'ElseIf item.Pending Then',
        'While item.Ready',
        'Wend',
        'Else',
        'With item',
        'Select Case item.Kind',
        'Case 1',
        'item.Value = 1',
        'Case Else',
        'item.Value = 0',
        'End Select',
        'End With',
        'End If',
        'Next',
        'End Sub',
        '',
        'Public Enum RunMode',
        'Manual',
        'End Enum',
        '',
        'Public Type CustomerRecord',
        'Name As String',
        'End Type'
      ].join('\n')
    }
  ]);

  assert.equal(formatText(project, 'file:///project/Blocks.bas'), [
    'Attribute VB_Name = "Blocks"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    '    For Each item In items',
    '        If item.Enabled Then',
    '            Do',
    '            Loop',
    '        ElseIf item.Pending Then',
    '            While item.Ready',
    '            Wend',
    '        Else',
    '            With item',
    '                Select Case item.Kind',
    '                Case 1',
    '                    item.Value = 1',
    '                Case Else',
    '                    item.Value = 0',
    '                End Select',
    '            End With',
    '        End If',
    '    Next',
    'End Sub',
    '',
    'Public Enum RunMode',
    '    Manual',
    'End Enum',
    '',
    'Public Type CustomerRecord',
    '    Name As String',
    'End Type'
  ].join('\n'));
});

test('SourceFormatting preserves FormDesignerBlock text and formats only frm code', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/SampleForm.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form SampleForm',
        ' Caption         =   "sample"',
        'End',
        'Attribute vb_name = "SampleForm"',
        'option explicit',
        '',
        'public sub Run()',
        'if true then',
        'End If',
        'End Sub'
      ].join('\n')
    }
  ]);

  assert.equal(formatText(project, 'file:///project/SampleForm.frm'), [
    'VERSION 5.00',
    'Begin VB.Form SampleForm',
    ' Caption         =   "sample"',
    'End',
    'Attribute VB_Name = "SampleForm"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    '    If True Then',
    '    End If',
    'End Sub'
  ].join('\n'));
});

test('SourceFormatting uses tab indentation when requested and skips indentation for incomplete blocks', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/TabBlock.bas',
      text: [
        'Attribute VB_Name = "TabBlock"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'If True Then',
        'End If',
        'End Sub'
      ].join('\n')
    },
    {
      uri: 'file:///project/Incomplete.bas',
      text: [
        'Attribute VB_Name = "Incomplete"',
        'Option Explicit',
        '',
        'public sub Run()',
        'if true then',
        'value = nothing'
      ].join('\n')
    }
  ]);

  assert.equal(formatText(project, 'file:///project/TabBlock.bas', { tabSize: 4, insertSpaces: false }), [
    'Attribute VB_Name = "TabBlock"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    '\tIf True Then',
    '\tEnd If',
    'End Sub'
  ].join('\n'));
  assert.equal(formatText(project, 'file:///project/Incomplete.bas', { tabSize: 4, insertSpaces: false }), [
    'Attribute VB_Name = "Incomplete"',
    'Option Explicit',
    '',
    'Public Sub Run()',
    'If True Then',
    'value = Nothing'
  ].join('\n'));
});

function formatText(
  project: ReturnType<typeof buildVbaProject>,
  uri: string,
  options = { tabSize: 4, insertSpaces: true }
): string {
  const edits = getDocumentFormattingEdits(project, uri, options);
  assert.equal(edits.length, 1);
  return edits[0].text;
}

test('EndStatementCompletion offers snippets for every supported block opener', () => {
  const cases: Array<[string, string]> = [
    ['Public Sub Run()', 'End Sub'],
    ['Public Function BuildValue() As String', 'End Function'],
    ['Public Property Get DisplayName() As String', 'End Property'],
    ['If ready Then', 'End If'],
    ['For index = 1 To 10', 'Next'],
    ['For Each item In items', 'Next'],
    ['Do', 'Loop'],
    ['While ready', 'Wend'],
    ['Select Case mode', 'End Select'],
    ['With target', 'End With'],
    ['Public Enum RunMode', 'End Enum'],
    ['Public Type CustomerRecord', 'End Type']
  ];

  for (const [opener, closer] of cases) {
    assert.deepEqual(endStatementCompletions(opener), [
      {
        label: `Insert ${closer}`,
        kind: 'snippet',
        insertText: `\n    $0\n${closer}`,
        insertTextFormat: 'snippet'
      }
    ]);
  }
});

test('EndStatementCompletion aligns snippet indentation with the opener line', () => {
  assert.deepEqual(endStatementCompletions('    If ready Then'), [
    {
      label: 'Insert End If',
      kind: 'snippet',
      insertText: '\n        $0\n    End If',
      insertTextFormat: 'snippet'
    }
  ]);
});

test('EndStatementCompletion is not offered in comments, strings, inline blocks, designer text, or before line end', () => {
  assert.deepEqual(endStatementCompletions("' Public Sub Run()"), []);
  assert.deepEqual(endStatementCompletions('"Public Sub Run()"'), []);
  assert.deepEqual(endStatementCompletions('If ready Then value = 1'), []);
  assert.deepEqual(endStatementCompletions('Public Sub Run()', 10), []);

  const form_project = buildVbaProject([
    {
      uri: 'file:///project/SampleForm.frm',
      text: [
        'VERSION 5.00',
        'Begin VB.Form SampleForm',
        'End',
        'Attribute VB_Name = "SampleForm"',
        'Option Explicit'
      ].join('\n')
    }
  ]);
  const snippets = getCompletions(form_project, {
    uri: 'file:///project/SampleForm.frm',
    position: { line: 1, character: 'Begin VB.Form SampleForm'.length }
  }).filter((completion) => completion.insertTextFormat === 'snippet');

  assert.deepEqual(snippets, []);
});

test('EndStatementCompletion avoids duplicate closers that already match the opener', () => {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        'Public Sub Run()',
        'End Sub'
      ].join('\n')
    }
  ]);
  const snippets = getCompletions(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 3, character: 'Public Sub Run()'.length }
  }).filter((completion) => completion.insertTextFormat === 'snippet');

  assert.deepEqual(snippets, []);
});

function endStatementCompletions(line: string, character = line.length): ReturnType<typeof getCompletions> {
  const project = buildVbaProject([
    {
      uri: 'file:///project/Worker.bas',
      text: [
        'Attribute VB_Name = "Worker"',
        'Option Explicit',
        '',
        line
      ].join('\n')
    }
  ]);

  return getCompletions(project, {
    uri: 'file:///project/Worker.bas',
    position: { line: 3, character }
  }).filter((completion) => completion.insertTextFormat === 'snippet');
}
