import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildVbaProject,
  getCompletions,
  getDefinition,
  getHover,
  getModuleIdentities,
  getSignatureHelp,
  getTypeFields,
  resolveName
} from './vbaProject';

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
  const project = buildVbaProject([
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
  ]);

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
