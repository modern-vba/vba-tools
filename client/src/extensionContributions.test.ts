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

test('extension associates only the canonical project manifest basename with its schema', () => {
  const packageJson = readPackageJson<{
    contributes?: {
      jsonValidation?: Array<{
        fileMatch?: string | string[];
        url?: string;
      }>;
    };
  }>();

  assert.deepEqual(packageJson.contributes?.jsonValidation, [
    {
      fileMatch: '**/vba-project.json',
      url: './schemas/project-manifest.schema.json'
    }
  ]);
});

test('extension declares limited Restricted Mode support and restricts executable overrides', () => {
  const packageJson = readPackageJson<{
    capabilities?: {
      untrustedWorkspaces?: {
        supported?: boolean | 'limited';
        description?: string;
        restrictedConfigurations?: string[];
      };
    };
  }>();

  assert.equal(packageJson.capabilities?.untrustedWorkspaces?.supported, 'limited');
  assert.match(
    packageJson.capabilities?.untrustedWorkspaces?.description ?? '',
    /language assistance/i
  );
  const restrictedConfigurations =
    packageJson.capabilities?.untrustedWorkspaces?.restrictedConfigurations ?? [];
  assert.ok(restrictedConfigurations.includes('vbaTools.devtool.path'));
  assert.ok(restrictedConfigurations.includes('vbaTools.debugAdapter.path'));
});

test('restricted activation keeps language assistance safe without eager managed tooling or Output', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(extensionSource, /const extensionOutputChannel = createLazyOutputChannel\(/);
  assert.match(extensionSource, /outputChannel = extensionOutputChannel;/);
  assert.match(
    extensionSource,
    /resolveCompanionExecutableForLanguageActivation\(\s*workspace\.isTrusted,\s*\(\) => vbaDevResolver\.resolve\(\)/
  );
  assert.match(
    extensionSource,
    /createVbaLanguageServerOptions\(\{[\s\S]*?vbaDevExecutablePath: initialVbaDevResolution\?\.executablePath/
  );
});

test('extension wires Workspace Trust into every non-palette managed launch surface', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /invalidateManagedToolingState: \(\) => \{\s*vbaDevResolver\.invalidate\(\);\s*backgroundVbaDevResolver\.invalidate\(\);\s*newExcelProjectCommand\.invalidatePreflight\(\);\s*\}/
  );
  assert.match(
    extensionSource,
    /new VscodeDebugIntegration\(\{[\s\S]*?requireTrustedWorkspace: \(\) => \(\s*workspaceTrustGate\.requireTrusted\('managed-tooling'\)/
  );
  assert.match(
    extensionSource,
    /createVbaDebugConfigurationProvider\([\s\S]*?\(\) => workspaceTrustGate\.requireTrusted\('managed-tooling'\)/
  );
  assert.match(
    extensionSource,
    /createWorkbookBackedTestExplorer\(\{[\s\S]*?requireTrustedWorkspace: \(\) => \(\s*workspaceTrustGate\.requireTrusted\('managed-tooling'\)/
  );
  assert.match(
    extensionSource,
    /promptForActiveWorkbookBackedProject\([\s\S]*?command\.commandId === 'vbaTools\.doctor'[\s\S]*?\?\.handler/
  );
});

test('registered Command Palette workflows capture one invocation target before UI and pass its resolver', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );
  const managedOperations = extensionSource.slice(
    extensionSource.indexOf('const managedToolingOperations = {'),
    extensionSource.indexOf('} satisfies ManagedToolingCommandOperations;')
  );
  const registrations = [
    ['vbaTools.doctor', 'runDoctorWithProgress'],
    ['vbaTools.export', 'runExportCommandWithConsent'],
    ['vbaTools.build', 'runWorkbookBackedProjectCommandWithProgress'],
    ['vbaTools.test', 'runWorkbookBackedProjectCommandWithProgress'],
    ['vbaTools.publish', 'runWorkbookBackedProjectCommandWithProgress'],
    ['vbaTools.commonModules.add', 'runCommonModulesCommandWithProgress'],
    ['vbaTools.commonModules.list', 'runCommonModulesCommandWithProgress'],
    ['vbaTools.commonModules.update', 'runCommonModulesCommandWithProgress'],
    ['vbaTools.references.list', 'runReferenceCommandWithProgress'],
    ['vbaTools.references.add', 'runReferenceCommandWithProgress'],
    ['vbaTools.references.remove', 'runReferenceCommandWithProgress']
  ] as const;

  for (const [commandId, wrapperName] of registrations) {
    const escapedCommandId = commandId.replace(/[.]/gu, '\\.');
    assert.match(
      managedOperations,
      new RegExp(`'${escapedCommandId}':[\\s\\S]*?${wrapperName}\\(`),
      `${commandId} must route through ${wrapperName}`
    );
  }

  const wrappers = [
    extractFunctionSource(extensionSource, 'runDoctorWithProgress', 'openVbaDevTerminalCommand'),
    extractFunctionSource(
      extensionSource,
      'runWorkbookBackedProjectCommandWithProgress',
      'runExportCommandWithConsent'
    ),
    extractFunctionSource(
      extensionSource,
      'runExportCommandWithConsent',
      'runCommonModulesCommandWithProgress'
    ),
    extractFunctionSource(
      extensionSource,
      'runCommonModulesCommandWithProgress',
      'promptForCommonModuleNames'
    ),
    extractFunctionSource(
      extensionSource,
      'runReferenceCommandWithProgress',
      'chooseCommandPaletteProject'
    )
  ];

  for (const wrapper of wrappers) {
    const capture = /const targetSnapshot = capture(?:Vscode)?CommandPaletteInvocationSnapshot\(\);/
      .exec(wrapper);
    const captureIndex = capture?.index ?? -1;
    const firstAwaitIndex = wrapper.indexOf('await ');
    assert.ok(captureIndex >= 0, 'palette wrapper must capture an invocation snapshot');
    assert.ok(
      firstAwaitIndex < 0 || captureIndex < firstAwaitIndex,
      'palette wrapper must capture before its first asynchronous UI boundary'
    );
    assert.match(
      wrapper,
      /const resolveTarget = createCommandPaletteTargetResolver\(targetSnapshot\);/
    );
    assert.match(wrapper, /resolveCommandPaletteTarget: resolveTarget/);
    assert.match(wrapper, /activeFilePath: targetSnapshot\.activeFilePath/);
    assert.match(wrapper, /workspaceRoots: targetSnapshot\.workspaceRoots \?\? \[\]/);
  }
});

test('extension watches selected Host Event paths through absolute RelativePattern bases', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /new HostClassProjectionWatcherRegistry\(\{[\s\S]*?workspace\.createFileSystemWatcher\(\s*new RelativePattern\(Uri\.file\(basePath\), pattern\)\s*\)[\s\S]*?onActiveDocumentsChanged: \(documents\) => hostClassWatchers\.synchronize\(documents\)/
  );
});

test('extension reconciles Host Event documents when workspace folders change', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /workspace\.onDidChangeWorkspaceFolders\([\s\S]*?hostWorkspace\.reconcileWorkspaceFolders\(\s*workspace\.workspaceFolders/
  );
});

test('extension contributes optional VBA debug selectors with an atomic procedure pair', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      debuggers?: Array<{
        type?: string;
        label?: string;
        languages?: string[];
        configurationAttributes?: Record<string, {
          required?: string[];
          properties?: Record<string, { type?: string }>;
          dependencies?: Record<string, string[]>;
        }>;
      }>;
    };
  }>();
  const debuggerContribution = packageJson.contributes?.debuggers?.find(
    (candidate) => candidate.type === 'vba'
  );
  const launch = debuggerContribution?.configurationAttributes?.launch;

  assert.equal(debuggerContribution?.label, 'VBA');
  assert.deepEqual(debuggerContribution?.languages, ['vba']);
  assert.deepEqual(launch?.required ?? [], []);
  for (const propertyName of ['project', 'document', 'module', 'procedure']) {
    assert.equal(launch?.properties?.[propertyName]?.type, 'string');
  }
  assert.deepEqual(launch?.dependencies, {
    module: ['procedure'],
    procedure: ['module']
  });
  assert.equal(debuggerContribution?.configurationAttributes?.attach, undefined);
  assert.ok(packageJson.activationEvents?.includes('onDebugResolve:vba'));
});

test('VBA debug activation disables VS Code save-before-start', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      configurationDefaults?: Record<string, Record<string, unknown>>;
    };
  }>();

  assert.ok(packageJson.activationEvents?.includes('onDebugDynamicConfigurations'));
  assert.ok(packageJson.activationEvents?.includes('onDebugResolve:vba'));
  assert.equal(
    packageJson.contributes?.configurationDefaults?.['[vba]']?.['debug.saveBeforeStart'],
    'none'
  );
});

test('VBA source inventory discovery includes FRX and bypasses user file excludes', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /workspace\.findFiles\(\s*new RelativePattern\(sourceSetPath, '\*\*\/\*\.\{bas,cls,frm,frx\}'\),\s*null\s*\)/
  );
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

test('extension contributes the strict debug adapter path override configuration', () => {
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

  assert.deepEqual(
    packageJson.contributes?.configuration?.properties?.['vbaTools.debugAdapter.path'],
    {
      scope: 'machine-overridable',
      type: 'string',
      default: '',
      description: 'Overrides the bundled vba-debug-adapter executable path. An invalid override prevents VBA debugging.'
    }
  );
});

test('extension activation wires the strict debug adapter path into VBA F5 startup', () => {
  const extensionSource = fs.readFileSync(
    path.resolve(__dirname, '..', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /new VscodeDebugIntegration\(\{[\s\S]*?getConfiguredDebugAdapterPath,[\s\S]*?\}\)/
  );
  assert.match(
    extensionSource,
    /getConfiguration\('vbaTools'\)\.get<string>\('debugAdapter\.path'\)/
  );
});

test('extension activation shares one invocation-time source capture adapter across Debug and Test Explorer', () => {
  const extensionSource = fs.readFileSync(
    path.resolve(__dirname, '..', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /const captureSnapshotSourceInventoryFromVscode = createSnapshotSourceInventoryVscodeAdapter\(\{/
  );
  assert.match(
    extensionSource,
    /captureSourceInventory:\s*captureSnapshotSourceInventoryFromVscode/
  );
  assert.match(
    extensionSource,
    /createCallerOwnedSourceSnapshotCapture\(\s*captureSnapshotSourceInventoryFromVscode,/
  );
  assert.match(
    extensionSource,
    /new RelativePattern\(sourceSetPath, '\*\*\/\*\.\{bas,cls,frm,frx\}'\)/
  );
  assert.equal(
    extensionSource.match(/getOpenTextDocuments:\s*\(\)\s*=>\s*workspace\.textDocuments\.map/g)?.length,
    1
  );
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

test('extension wires the configured debug adapter path into Doctor', () => {
  const extensionSource = fs.readFileSync(
    path.resolve(__dirname, '..', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /runDoctorCommand\(\{[\s\S]*?configuredDebugAdapterPath:\s*getConfiguredDebugAdapterPath\(\)[\s\S]*?\}\)/
  );
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

test('extension keeps Create Excel VBA Project discoverable without a context condition', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
        enablement?: string;
      }>;
      menus?: {
        commandPalette?: Array<{ command?: string; when?: string }>;
      };
    };
  }>();

  assert.deepEqual(
    packageJson.contributes?.commands?.find(
      (command) => command.command === 'vbaTools.newExcel'
    ),
    {
      command: 'vbaTools.newExcel',
      title: 'VBA Tools: Create Excel VBA Project'
    }
  );
  assert.ok(packageJson.activationEvents?.includes('onCommand:vbaTools.newExcel'));
  assert.equal(
    packageJson.contributes?.menus?.commandPalette?.some(
      (item) => item.command === 'vbaTools.newExcel' && item.when !== undefined
    ) ?? false,
    false
  );
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

  assert.equal(commands.some((command) => command.command === 'vbaTools.capabilities'), false);
  assert.equal(commands.some((command) => command.command === 'vbaTools.testNoBuild'), false);
});

test('extension contributes the explicit Host Events refresh command exactly', () => {
  const packageJson = readPackageJson<{
    activationEvents?: string[];
    contributes?: {
      commands?: Array<{
        command?: string;
        title?: string;
        enablement?: string;
      }>;
    };
  }>();

  assert.deepEqual(
    packageJson.contributes?.commands?.find((command) =>
      command.command === 'vbaTools.hostClasses.refresh'
    ),
    {
      command: 'vbaTools.hostClasses.refresh',
      title: 'VBA Tools: Refresh Host Events'
    }
  );
  assert.ok(packageJson.activationEvents?.includes(
    'onCommand:vbaTools.hostClasses.refresh'
  ));
});

test('extension forwards export command request arguments to dedicated orchestration', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );

  assert.match(
    extensionSource,
    /'vbaTools\.export': async \(request\?: unknown\) => \{\s*await runExportCommandWithConsent\(\s*context,\s*vbaDevResolver,\s*request as ExportCommandRequest \| undefined/
  );
  assert.match(
    extensionSource,
    /createManagedToolingCommandHandlers\(\s*workspaceTrustGate,\s*managedToolingOperations\s*\)/
  );
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

test('reference mutation commands use multi-select QuickPicks without a free-text fallback', () => {
  const extensionSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'extension.ts'),
    'utf8'
  );
  const quickPickSource = fs.readFileSync(
    path.join(process.cwd(), 'client', 'src', 'referenceQuickPick.ts'),
    'utf8'
  );

  assert.doesNotMatch(extensionSource, /promptForReferenceName|Enter the exact Reference\.Description name/);
  assert.match(extensionSource, /showReferenceQuickPick/);
  assert.match(extensionSource, /window\.createQuickPick<ReferenceQuickPickItem>\(\)/);
  assert.match(extensionSource, /runMutationWithProgress/);
  assert.match(quickPickSource, /quickPick\.canSelectMany = true/);
  assert.match(quickPickSource, /quickPick\.matchOnDescription = true/);
  assert.match(quickPickSource, /quickPick\.busy = true/);
  assert.match(quickPickSource, /quickPick\.enabled = false/);
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
      description: 'Inserts an indented body and matching terminator when Enter follows an eligible complete VBA block header; otherwise preserves native Enter.'
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
  assertPatternDoesNotMatch(patterns, 'keyword.control.vba', 'If亜 = 1');
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
  assertPatternMatches(patterns, 'meta.attribute.vba', 'Attribute\u3000VB_Name = "Module1"');
  assertPatternDoesNotMatch(patterns, 'meta.attribute.vba', 'Attribute\u00A0VB_Name = "Module1"');
  assertPatternDoesNotMatch(patterns, 'meta.attribute.vba', 'Attribute VB_Name注文 = "Module1"');
});

function readPackageJson<T>(): T {
  return JSON.parse(
    fs.readFileSync(path.join(process.cwd(), 'package.json'), 'utf8')
  ) as T;
}

function extractFunctionSource(source: string, name: string, nextName: string): string {
  const start = source.indexOf(`async function ${name}(`);
  const end = source.indexOf(`async function ${nextName}(`, start + 1);
  assert.ok(start >= 0, `Expected extension function ${name}`);
  assert.ok(end > start, `Expected extension function ${nextName} after ${name}`);
  return source.slice(start, end);
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
