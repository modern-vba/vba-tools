import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { minimumSupportedVscodeVersion } from './extensionHost/configuration';

interface GrammarPattern {
  name?: string;
  match?: string;
  begin?: string;
  patterns?: GrammarPattern[];
}

interface TextMateGrammar {
  scopeName: string;
  patterns: GrammarPattern[];
  repository?: Record<string, GrammarPattern>;
}

test('extension contributes a VBA TextMate grammar for the vba language', () => {
  const packageJson = readPackageJson<{
    contributes?: {
      grammars?: Array<{
        language?: string;
        scopeName?: string;
        path?: string;
      }>;
    };
  }>();

  assert.deepEqual(packageJson.contributes?.grammars, [
    {
      language: 'vba',
      scopeName: 'source.vba',
      path: './syntaxes/vba.tmLanguage.json'
    }
  ]);
});

test('extension maps VBA semantic tokens to TextMate fallback scopes', () => {
  const packageJson = readPackageJson<{
    contributes?: {
      semanticTokenTypes?: Array<{
        id?: string;
        superType?: string;
      }>;
      semanticTokenScopes?: Array<{
        language?: string;
        scopes?: Record<string, string[]>;
      }>;
    };
  }>();
  const mapping = packageJson.contributes?.semanticTokenScopes?.find(
    (entry) => entry.language === 'vba'
  )?.scopes;

  assert.deepEqual(mapping?.class, ['entity.name.type.class.vba']);
  assert.deepEqual(mapping?.variable, ['variable.other.readwrite.vba']);
  assert.deepEqual(mapping?.property, ['variable.other.property.vba']);
  assert.deepEqual(mapping?.field, ['entity.name.variable.field.vba']);
  assert.deepEqual(mapping?.parameter, ['variable.parameter.vba']);
  assert.deepEqual(mapping?.function, ['entity.name.function.vba']);
  assert.deepEqual(mapping?.method, ['entity.name.function.member.vba']);

  const fieldTokenType = packageJson.contributes?.semanticTokenTypes?.find(
    (tokenType) => tokenType.id === 'field'
  );
  assert.equal(fieldTokenType?.id, 'field');
  assert.equal(fieldTokenType?.superType, 'property');
});

test('extension does not contribute obsolete HostApplication settings', () => {
  const packageJson = readPackageJson<{
    contributes?: {
      configuration?: {
        properties?: Record<string, unknown>;
      };
    };
  }>();
  const properties = packageJson.contributes?.configuration?.properties ?? {};

  assert.equal(Object.hasOwn(properties, 'vbaLanguageServer.mainHostApplication'), false);
  assert.equal(Object.hasOwn(properties, 'vbaLanguageServer.additionalHostApplications'), false);
});

test('extension activates for workspaces containing a VBA project manifest', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
  }>();

  assert.ok(packageJson.activationEvents?.includes('workspaceContains:**/vba-project.json'));
  assert.equal(packageJson.activationEvents?.includes('onLanguage:json'), false);
});

test('extension contributes VbaDev path override configuration', () => {
  const packageJson = readPackageJson<{
    contributes?: {
      configuration?: {
        properties?: Record<string, {
          scope?: string;
          type?: string;
          default?: string;
          description?: string;
        }>;
      };
    };
  }>();
  const devtoolPathSetting = packageJson.contributes?.configuration?.properties?.[
    'vbaTools.devtool.path'
  ];

  assert.deepEqual(devtoolPathSetting, {
    scope: 'machine-overridable',
    type: 'string',
    default: '',
    description: 'Overrides the bundled vba-dev executable path for development or diagnostics.'
  });
});

test('extension contributes the Doctor command', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  }>();

  assert.deepEqual(packageJson.contributes?.commands?.find((command) => command.command === 'vbaTools.doctor'), {
    command: 'vbaTools.doctor',
    title: 'VBA Tools: Doctor'
  });
  assert.ok(packageJson.activationEvents?.includes('onCommand:vbaTools.doctor'));
});

test('extension contributes the vba-dev Terminal command', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  }>();

  assert.deepEqual(packageJson.contributes?.commands?.find((command) => command.command === 'vbaTools.openVbaDevTerminal'), {
    command: 'vbaTools.openVbaDevTerminal',
    title: 'VBA Tools: Open vba-dev Terminal'
  });
  assert.ok(packageJson.activationEvents?.includes('onCommand:vbaTools.openVbaDevTerminal'));
});

test('extension contributes daily WorkbookBackedProject commands only', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  }>();
  const commands = packageJson.contributes?.commands ?? [];

  for (const expected of [
    ['vbaTools.build', 'VBA Tools: Build'],
    ['vbaTools.test', 'VBA Tools: Test'],
    ['vbaTools.publish', 'VBA Tools: Publish'],
    ['vbaTools.export', 'VBA Tools: Export']
  ]) {
    assert.deepEqual(commands.find((command) => command.command === expected[0]), {
      command: expected[0],
      title: expected[1]
    });
    assert.ok(packageJson.activationEvents?.includes(`onCommand:${expected[0]}`));
  }

  assert.equal(commands.some((command) => command.command === 'vbaTools.newExcel'), false);
  assert.equal(commands.some((command) => command.command === 'vbaTools.capabilities'), false);
  assert.equal(commands.some((command) => command.command === 'vbaTools.testNoBuild'), false);
});

test('extension contributes CommonModules commands', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  }>();
  const commands = packageJson.contributes?.commands ?? [];

  for (const expected of [
    ['vbaTools.commonModules.add', 'VBA Tools: Add Common Module'],
    ['vbaTools.commonModules.list', 'VBA Tools: List Common Modules'],
    ['vbaTools.commonModules.update', 'VBA Tools: Update Common Modules']
  ]) {
    assert.deepEqual(commands.find((command) => command.command === expected[0]), {
      command: expected[0],
      title: expected[1]
    });
    assert.ok(packageJson.activationEvents?.includes(`onCommand:${expected[0]}`));
  }
});

test('extension contributes VbaProjectReference commands', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  }>();
  const commands = packageJson.contributes?.commands ?? [];

  for (const expected of [
    ['vbaTools.references.list', 'VBA Tools: List References'],
    ['vbaTools.references.add', 'VBA Tools: Add Reference'],
    ['vbaTools.references.remove', 'VBA Tools: Remove Reference']
  ]) {
    assert.deepEqual(commands.find((command) => command.command === expected[0]), {
      command: expected[0],
      title: expected[1]
    });
    assert.ok(packageJson.activationEvents?.includes(`onCommand:${expected[0]}`));
  }
});

test('extension contributes the guarded VBA Enter command setting and editor-owned state guards', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    engines?: { vscode?: string };
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
      keybindings?: Array<{
        command?: string;
        key?: string;
        when?: string;
        args?: unknown;
      }>;
      configuration?: {
        properties?: Record<string, unknown>;
      };
    };
  }>();
  const afterNativeCommand = 'vbaTools.blockSkeletonInsertion.afterNativeEnter';

  assert.equal(
    packageJson.engines?.vscode,
    `^${minimumSupportedVscodeVersion}`
  );

  assert.equal(
    packageJson.contributes?.commands?.some(
      (candidate) => candidate.command === afterNativeCommand
    ),
    false
  );
  assert.ok(
    packageJson.activationEvents?.includes(`onCommand:${afterNativeCommand}`)
  );
  assert.deepEqual(
    packageJson.contributes?.configuration?.properties?.[
      'vbaLanguageServer.blockSkeletonInsertion.enabled'
    ],
    {
      scope: 'resource',
      type: 'boolean',
      default: true,
      description: 'Inserts a complete VBA block skeleton when Enter follows an eligible header.'
    }
  );
  assert.deepEqual(packageJson.contributes?.keybindings, [
    {
      command: 'runCommands',
      key: 'enter',
      args: {
        commands: [
          'lineBreakInsert',
          afterNativeCommand
        ]
      },
      when: [
        'editorTextFocus',
        'editorLangId == vba',
        'config.vbaLanguageServer.blockSkeletonInsertion.enabled',
        '!editorReadonly',
        '!editorHasSelection',
        '!editorHasMultipleSelections',
        '!suggestWidgetVisible',
        '!inlineSuggestionVisible',
        '!inSnippetMode',
        '!renameInputVisible',
        '!isComposing'
      ].join(' && ')
    }
  ]);
});

test('Extension Host test artifacts are excluded from the packaged extension', () => {
  const vscodeIgnore = fs.readFileSync(
    path.join(process.cwd(), '.vscodeignore'),
    'utf8'
  );

  assert.match(vscodeIgnore, /^client\/out\/extensionHost\/\*\*$/m);
});

test('VBA TextMate grammar has lexical scopes for representative VBA fixtures', () => {
  const grammar = readGrammar();
  const patterns = flattenPatterns(grammar);

  assert.equal(grammar.scopeName, 'source.vba');
  assertPatternMatches(patterns, 'comment.block.documentation.vba', "'* @brief Reads a value.");
  assertPatternMatches(patterns, 'comment.line.apostrophe.vba', "' ordinary comment");
  assertPatternMatches(patterns, 'string.quoted.double.vba', '"a ""quoted"" value"');
  assertPatternMatches(patterns, 'keyword.control.vba', 'If value Then');
  assertPatternMatches(patterns, 'keyword.control.vba', 'End If');
  assertPatternMatches(patterns, 'keyword.control.vba', 'end if');
  assertPatternMatches(patterns, 'keyword.control.vba', 'eLsE');
  assertPatternMatches(patterns, 'keyword.control.vba', 'select case value');
  assertPatternMatches(patterns, 'keyword.control.vba', 'End With');
  assertPatternMatches(patterns, 'keyword.control.vba', 'End Select');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'End Sub');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'End Function');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'End Property');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'end sub');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'eNd fUnCtIoN');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'END PROPERTY');
  assertPatternMatches(patterns, 'keyword.vba', 'Public Function BuildValue() As String');
  assertPatternMatches(patterns, 'keyword.vba', 'private function BuildValue() as string');
  assertPatternMatches(patterns, 'keyword.vba', 'pUbLiC pRoPeRtY Get Value() As Long');
  assertPatternMatches(patterns, 'keyword.vba', 'End Sub');
  assertPatternMatches(patterns, 'keyword.vba', 'End Function');
  assertPatternMatches(patterns, 'keyword.vba', 'End Property');
  assertPatternMatches(patterns, 'keyword.vba', 'end sub');
  assertPatternMatches(patterns, 'keyword.vba', 'eNd fUnCtIoN');
  assertPatternMatches(patterns, 'keyword.vba', 'END PROPERTY');
  assertPatternDoesNotMatch(patterns, 'keyword.vba', 'End If');
  assertPatternDoesNotMatch(patterns, 'keyword.vba', 'End With');
  assertPatternDoesNotMatch(patterns, 'keyword.vba', 'End Select');
  assertPatternDoesNotMatch(patterns, 'keyword.vba', 'end if');
  assertPatternMatches(patterns, 'storage.type.intrinsic.vba', 'Dim value As String');
  assertPatternMatches(patterns, 'storage.type.intrinsic.vba', 'dim value as long');
  assertPatternMatches(patterns, 'constant.language.vba', 'Set target = Nothing');
  assertPatternMatches(patterns, 'constant.language.vba', 'set ready = true');
  assertPatternMatches(patterns, 'constant.language.vba', 'Set ready = FALSE');
  assertPatternMatches(patterns, 'constant.numeric.vba', 'value = &HFF');
  assertPatternMatches(patterns, 'keyword.operator.vba', 'If left_value <> right_value Then');
  assertPatternMatches(patterns, 'meta.attribute.vba', 'Attribute VB_Name = "Module1"');
});

function readPackageJson<T>(): T {
  return JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as T;
}

function readGrammar(): TextMateGrammar {
  return JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'syntaxes', 'vba.tmLanguage.json'), 'utf8')
  ) as TextMateGrammar;
}

function flattenPatterns(grammar: TextMateGrammar): GrammarPattern[] {
  const result: GrammarPattern[] = [];
  const visit = (pattern: GrammarPattern): void => {
    result.push(pattern);
    pattern.patterns?.forEach(visit);
  };

  grammar.patterns.forEach(visit);
  Object.values(grammar.repository ?? {}).forEach(visit);
  return result;
}

function assertPatternMatches(patterns: GrammarPattern[], scopeName: string, fixture: string): void {
  assert.ok(
    patterns.some((candidate) => patternMatches(candidate, scopeName, fixture)),
    `Expected grammar scope ${scopeName} to match ${fixture}`
  );
}

function assertPatternDoesNotMatch(patterns: GrammarPattern[], scopeName: string, fixture: string): void {
  assert.ok(
    !patterns.some((candidate) => patternMatches(candidate, scopeName, fixture)),
    `Expected grammar scope ${scopeName} not to match ${fixture}`
  );
}

function patternMatches(pattern: GrammarPattern, scopeName: string, fixture: string): boolean {
  if (pattern.name !== scopeName) {
    return false;
  }

  const expression = pattern.match ?? pattern.begin;
  return expression !== undefined && createGrammarRegExp(expression).test(fixture);
}

function createGrammarRegExp(expression: string): RegExp {
  if (expression.startsWith('(?i)')) {
    return new RegExp(expression.slice(4), 'i');
  }

  return new RegExp(expression);
}
