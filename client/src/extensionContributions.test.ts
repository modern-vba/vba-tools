import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

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

test('VBA TextMate grammar has lexical scopes for representative VBA fixtures', () => {
  const grammar = readGrammar();
  const patterns = flattenPatterns(grammar);

  assert.equal(grammar.scopeName, 'source.vba');
  assertPatternMatches(patterns, 'comment.block.documentation.vba', "'* @brief Reads a value.");
  assertPatternMatches(patterns, 'comment.line.apostrophe.vba', "' ordinary comment");
  assertPatternMatches(patterns, 'string.quoted.double.vba', '"a ""quoted"" value"');
  assertPatternMatches(patterns, 'keyword.control.vba', 'If value Then');
  assertPatternMatches(patterns, 'keyword.control.vba', 'End If');
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'End Sub');
  assertPatternMatches(patterns, 'keyword.vba', 'Public Function BuildValue() As String');
  assertPatternMatches(patterns, 'keyword.vba', 'End Sub');
  assertPatternMatches(patterns, 'keyword.vba', 'End Function');
  assertPatternMatches(patterns, 'keyword.vba', 'End Property');
  assertPatternMatches(patterns, 'storage.type.intrinsic.vba', 'Dim value As String');
  assertPatternMatches(patterns, 'constant.language.vba', 'Set target = Nothing');
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
  return expression !== undefined && new RegExp(expression).test(fixture);
}
