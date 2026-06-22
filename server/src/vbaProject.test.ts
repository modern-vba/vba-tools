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
    completions.map((item) => item.label),
    ['Application']
  );
  assert.deepEqual(hover, {
    contents: 'Represents the Microsoft Excel application.'
  });
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
