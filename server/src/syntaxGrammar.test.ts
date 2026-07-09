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
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    contributes?: {
      grammars?: Array<{
        language?: string;
        scopeName?: string;
        path?: string;
      }>;
    };
  };

  assert.deepEqual(package_json.contributes?.grammars, [
    {
      language: 'vba',
      scopeName: 'source.vba',
      path: './syntaxes/vba.tmLanguage.json'
    }
  ]);
});

test('extension contributes main HostApplication configuration', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    contributes?: {
      configuration?: {
        properties?: Record<string, {
          scope?: string;
          type?: string;
          enum?: string[];
          default?: string;
        }>;
      };
    };
  };
  const main_host_setting = package_json.contributes?.configuration?.properties?.[
    'vbaLanguageServer.mainHostApplication'
  ];

  assert.deepEqual(main_host_setting, {
    scope: 'resource',
    type: 'string',
    enum: ['excel', 'word', 'powerpoint', 'access'],
    default: 'excel',
    description: 'Controls the primary Office host application for unqualified VBA host object model references.'
  });
});

test('extension contributes additional HostApplications configuration', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    contributes?: {
      configuration?: {
        properties?: Record<string, {
          scope?: string;
          type?: string;
          items?: {
            type?: string;
            enum?: string[];
          };
          default?: string[];
        }>;
      };
    };
  };
  const additional_hosts_setting = package_json.contributes?.configuration?.properties?.[
    'vbaLanguageServer.additionalHostApplications'
  ];

  assert.deepEqual(additional_hosts_setting, {
    scope: 'resource',
    type: 'array',
    items: {
      type: 'string',
      enum: ['excel', 'word', 'powerpoint', 'access']
    },
    default: [],
    description: 'Controls additional Office host applications enabled alongside the main VBA host application.'
  });
});

test('extension contributes VbaDevTool path override configuration', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
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
  };
  const devtool_path_setting = package_json.contributes?.configuration?.properties?.[
    'vbaTools.devtool.path'
  ];

  assert.deepEqual(devtool_path_setting, {
    scope: 'machine-overridable',
    type: 'string',
    default: '',
    description: 'Overrides the bundled vba-devtool executable path for development or diagnostics.'
  });
});

test('extension contributes the Doctor command', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  };

  assert.deepEqual(package_json.contributes?.commands?.find((command) => command.command === 'vbaTools.doctor'), {
    command: 'vbaTools.doctor',
    title: 'VBA Tools: Doctor'
  });
  assert.ok(package_json.activationEvents?.includes('onCommand:vbaTools.doctor'));
});

test('extension contributes daily WorkbookBackedProject commands only', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  };
  const commands = package_json.contributes?.commands ?? [];

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
    assert.ok(package_json.activationEvents?.includes(`onCommand:${expected[0]}`));
  }

  assert.equal(commands.some((command) => command.command === 'vbaTools.newExcel'), false);
  assert.equal(commands.some((command) => command.command === 'vbaTools.capabilities'), false);
  assert.equal(commands.some((command) => command.command === 'vbaTools.testNoBuild'), false);
});

test('extension contributes CommonModules commands', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  };
  const commands = package_json.contributes?.commands ?? [];

  for (const expected of [
    ['vbaTools.commonModules.add', 'VBA Tools: Add Common Module'],
    ['vbaTools.commonModules.list', 'VBA Tools: List Common Modules'],
    ['vbaTools.commonModules.update', 'VBA Tools: Update Common Modules']
  ]) {
    assert.deepEqual(commands.find((command) => command.command === expected[0]), {
      command: expected[0],
      title: expected[1]
    });
    assert.ok(package_json.activationEvents?.includes(`onCommand:${expected[0]}`));
  }
});

test('extension contributes VbaProjectReference commands', () => {
  const package_json = JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as {
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
      }>;
    };
  };
  const commands = package_json.contributes?.commands ?? [];

  for (const expected of [
    ['vbaTools.references.list', 'VBA Tools: List References'],
    ['vbaTools.references.add', 'VBA Tools: Add Reference'],
    ['vbaTools.references.remove', 'VBA Tools: Remove Reference']
  ]) {
    assert.deepEqual(commands.find((command) => command.command === expected[0]), {
      command: expected[0],
      title: expected[1]
    });
    assert.ok(package_json.activationEvents?.includes(`onCommand:${expected[0]}`));
  }
});

test('README documents host signature help metadata dependency', () => {
  const readme = fs.readFileSync(path.join(process.cwd(), 'README.md'), 'utf8');

  assert.match(
    readme,
    /Detailed host method signature help depends on available host catalog metadata\./
  );
  assert.match(
    readme,
    /When a host method has no signature\s+metadata, the server leaves signature help empty/
  );
});

test('VBA TextMate grammar has lexical scopes for representative VBA fixtures', () => {
  const grammar = readGrammar();
  const patterns = flattenPatterns(grammar);

  assert.equal(grammar.scopeName, 'source.vba');
  assertPatternMatches(patterns, 'comment.block.documentation.vba', "'* @brief Reads a value.");
  assertPatternMatches(patterns, 'comment.line.apostrophe.vba', "' ordinary comment");
  assertPatternMatches(patterns, 'string.quoted.double.vba', '"a ""quoted"" value"');
  assertPatternMatches(patterns, 'keyword.control.vba', 'Public Function BuildValue() As String');
  assertPatternMatches(patterns, 'storage.type.intrinsic.vba', 'Dim value As String');
  assertPatternMatches(patterns, 'constant.language.vba', 'Set target = Nothing');
  assertPatternMatches(patterns, 'constant.numeric.vba', 'value = &HFF');
  assertPatternMatches(patterns, 'keyword.operator.vba', 'If left_value <> right_value Then');
  assertPatternMatches(patterns, 'meta.attribute.vba', 'Attribute VB_Name = "Module1"');
});

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
  const pattern = patterns.find((candidate) => candidate.name === scopeName);
  assert.ok(pattern, `Expected grammar scope ${scopeName}`);

  const expression = pattern.match ?? pattern.begin;
  assert.ok(expression, `Expected grammar scope ${scopeName} to have a match or begin pattern`);
  assert.match(fixture, new RegExp(expression));
}
