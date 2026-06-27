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
    ['Application', 'Workbook', 'Worksheet', 'Range']
  );
  assert.deepEqual(host_names, ['Application', 'Workbook', 'Worksheet', 'Range']);
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
      { label: 'Selection', detail: 'Word.Selection' }
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
      { label: 'Report', detail: 'Access.Report' }
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
    ['Application', 'DoCmd', 'Form', 'Report']
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

test('signature help ignores parenthesis-free calls', () => {
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

  assert.equal(signatureHelp, undefined);
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

function assertSemanticToken(
  tokens: ReturnType<typeof getSemanticTokens>,
  line: number,
  startCharacter: number,
  endCharacter: number,
  tokenType: ReturnType<typeof getSemanticTokens>[number]['tokenType']
): void {
  assert.ok(
    tokens.some((token) =>
      token.range.start.line === line
        && token.range.start.character === startCharacter
        && token.range.end.line === line
        && token.range.end.character === endCharacter
        && token.tokenType === tokenType
    ),
    `Expected ${tokenType} token at ${line}:${startCharacter}-${endCharacter}`
  );
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
