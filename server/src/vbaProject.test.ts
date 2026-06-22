import test from 'node:test';
import assert from 'node:assert/strict';

import {
  buildVbaProject,
  getCompletions,
  getDefinition,
  getModuleIdentities,
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
