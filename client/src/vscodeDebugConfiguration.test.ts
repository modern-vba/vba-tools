import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import { VscodeDebugIntegration } from './vscodeDebugIntegration';
import type { VbaDebugSourceBreakpoint } from './vscodeDebugConfiguration';

test('F5 from one active exported VBA source resolves a zero-configuration source snapshot', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sourceText = [
    'Attribute VB_Name = "DebugModule"',
    'Option Explicit',
    '',
    'Public Sub RunTarget()',
    'End Sub',
    ''
  ].join('\r\n');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 3, character: 12 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, sourceText]])
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration, {
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure',
    project: projectRoot,
    document: 'Book1',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: [{ path: sourcePath, text: sourceText }],
      activeSource: { path: sourcePath, line: 3, character: 12 },
      breakpoints: []
    }
  });
});

test('source snapshots use UTF-16 ordinal canonical path order across punctuation and case', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const underscoreSource = path.join(projectRoot, 'src', 'Book1', 'A_B.bas');
  const digitSource = path.join(projectRoot, 'src', 'Book1', 'A0.bas');
  const lowerCaseSource = path.join(projectRoot, 'src', 'Book1', 'aZ.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: underscoreSource, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [lowerCaseSource, 'Public Sub LowerCaseTarget()\r\nEnd Sub\r\n'],
      [underscoreSource, 'Public Sub UnderscoreTarget()\r\nEnd Sub\r\n'],
      [digitSource, 'Public Sub DigitTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [
      { path: digitSource, text: 'Public Sub DigitTarget()\r\nEnd Sub\r\n' },
      { path: underscoreSource, text: 'Public Sub UnderscoreTarget()\r\nEnd Sub\r\n' },
      { path: lowerCaseSource, text: 'Public Sub LowerCaseTarget()\r\nEnd Sub\r\n' }
    ],
    activeSource: { path: underscoreSource, line: 0, character: 0 },
    breakpoints: []
  });
});

test('a saved launch narrows project and document and resolves an explicit procedure pair without an active editor', async () => {
  const firstRoot = path.join('C:', 'work', 'FirstProject');
  const selectedRoot = path.join('C:', 'work', 'SelectedProject');
  const firstManifest = path.join(firstRoot, 'vba-project.json');
  const selectedManifest = path.join(selectedRoot, 'vba-project.json');
  const firstSource = path.join(firstRoot, 'src', 'Book1', 'First.bas');
  const selectedSource = path.join(selectedRoot, 'src', 'Book2', 'DebugModule.bas');
  const integration = createIntegration({
    manifests: new Map([
      [firstManifest, manifestJson('FirstProject', ['Book1'])],
      [selectedManifest, manifestJson('SelectedProject', ['Book1', 'Book2'])]
    ]),
    sources: new Map([
      [firstSource, 'Public Sub FirstTarget()\r\nEnd Sub\r\n'],
      [selectedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({
    type: 'vba',
    request: 'launch',
    name: 'Saved VBA target',
    project: selectedRoot,
    document: 'book2',
    module: 'DebugModule',
    procedure: 'RunTarget'
  });

  assert.deepEqual(configuration, {
    type: 'vba',
    request: 'launch',
    name: 'Saved VBA target',
    project: selectedRoot,
    document: 'Book2',
    module: 'DebugModule',
    procedure: 'RunTarget',
    sourceSnapshot: {
      schemaVersion: 1,
      sources: [{
        path: selectedSource,
        text: 'Public Sub RunTarget()\r\nEnd Sub\r\n'
      }],
      breakpoints: []
    }
  });
});

test('a saved launch rejects module and procedure unless both selectors are supplied', async () => {
  const integration = createIntegration({
    manifests: new Map(),
    sources: new Map()
  });

  for (const configuration of [
    { module: 'DebugModule' },
    { procedure: 'RunTarget' },
    { module: '  ', procedure: 'RunTarget' }
  ]) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      /module.*procedure.*together/i
    );
  }
});

test('a saved launch rejects invalid project and document selectors instead of treating them as omitted', async () => {
  let hostWasTouched = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [],
      getActiveEditor: () => {
        hostWasTouched = true;
        return undefined;
      },
      getOpenTextDocuments: () => [],
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => [],
      readTextFile: async () => '',
      readSourceText: async () => '',
      findExportedSourceFiles: async () => []
    }
  });

  for (const [configuration, expectedError] of [
    [{ project: '  ' }, /project.*non-empty string/i],
    [{ document: 42 }, /document.*non-empty string/i]
  ] as const) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      expectedError
    );
  }
  assert.equal(hostWasTouched, false);
});

test('debug launch saves every dirty exported source in the selected project and leaves other projects untouched', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const otherRoot = path.join('C:', 'work', 'OtherProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const otherManifestPath = path.join(otherRoot, 'vba-project.json');
  const activeSource = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const peerSource = path.join(projectRoot, 'src', 'Book2', 'PeerModule.cls');
  const outsideSource = path.join(otherRoot, 'src', 'OtherBook', 'Outside.bas');
  const sources = new Map([
    [activeSource, 'Public Sub BeforeSave()\r\nEnd Sub\r\n'],
    [peerSource, 'Public Sub PeerBeforeSave()\r\nEnd Sub\r\n'],
    [outsideSource, 'Public Sub OutsideBeforeSave()\r\nEnd Sub\r\n']
  ]);
  const saved: string[] = [];
  const dirtyDocument = (uriPath: string, savedText: string) => ({
    uriPath,
    isDirty: true,
    save: async () => {
      saved.push(uriPath);
      sources.set(uriPath, savedText);
      return true;
    }
  });
  const integration = createIntegration({
    activeEditor: { uriPath: activeSource, line: 0, character: 11 },
    manifests: new Map([
      [manifestPath, manifestJson('BookProject', ['Book1', 'Book2'])],
      [otherManifestPath, manifestJson('OtherProject', ['OtherBook'])]
    ]),
    sources,
    openTextDocuments: () => [
      dirtyDocument(outsideSource, 'Public Sub OutsideAfterSave()\r\nEnd Sub\r\n'),
      dirtyDocument(peerSource, 'Public Sub PeerAfterSave()\r\nEnd Sub\r\n'),
      dirtyDocument(activeSource, 'Public Sub AfterSave()\r\nEnd Sub\r\n')
    ]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(saved, [activeSource, peerSource]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: activeSource,
      text: 'Public Sub AfterSave()\r\nEnd Sub\r\n'
    }],
    activeSource: { path: activeSource, line: 0, character: 11 },
    breakpoints: []
  });
  assert.equal(sources.get(outsideSource), 'Public Sub OutsideBeforeSave()\r\nEnd Sub\r\n');
});

test('debug launch awaits save participants and re-resolves membership and source position before snapshot capture', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sources = new Map([[sourcePath, 'Public Sub BeforeSave()\r\nEnd Sub\r\n']]);
  const events: string[] = [];
  let activeEditor = { uriPath: sourcePath, line: 0, character: 11 };
  let manifestReads = 0;
  const integration = createIntegration({
    getActiveEditor: () => {
      events.push(`active:${activeEditor.line}:${activeEditor.character}`);
      return activeEditor;
    },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources,
    readTextFile: async (filePath) => {
      if (filePath === manifestPath) {
        manifestReads += 1;
        events.push(`manifest:${manifestReads}`);
        return manifestJson('BookProject', ['Book1']);
      }
      return sources.get(filePath) ?? '';
    },
    readSourceText: async (filePath) => {
      events.push('snapshot-source');
      return sources.get(filePath) ?? '';
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => {
        events.push('save-start');
        await Promise.resolve();
        sources.set(sourcePath, 'Public Sub AfterSave()\r\nEnd Sub\r\n');
        activeEditor = { uriPath: sourcePath, line: 1, character: 7 };
        events.push('save-participants-finished');
        return true;
      }
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(events, [
    'active:0:11',
    'manifest:1',
    'save-start',
    'save-participants-finished',
    'active:1:7',
    'manifest:2',
    'snapshot-source'
  ]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: sourcePath,
      text: 'Public Sub AfterSave()\r\nEnd Sub\r\n'
    }],
    activeSource: { path: sourcePath, line: 1, character: 7 },
    breakpoints: []
  });
});

test('debug launch freezes one enabled ordinary BAS breakpoint after save participants finish', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const sources = new Map([[sourcePath, 'Public Sub BeforeSave()\r\nEnd Sub\r\n']]);
  const events: string[] = [];
  let sourceBreakpoints = [{
    uriPath: sourcePath,
    line: 0,
    enabled: true
  }];
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources,
    readTextFile: async (filePath) => {
      events.push('manifest');
      return filePath === manifestPath
        ? manifestJson('BookProject', ['Book1'])
        : sources.get(filePath) ?? '';
    },
    getSourceBreakpoints: () => {
      events.push(`breakpoints:${sourceBreakpoints[0].line}`);
      return sourceBreakpoints;
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => {
        events.push('save-start');
        await Promise.resolve();
        sources.set(sourcePath, 'Public Sub AfterSave()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n');
        sourceBreakpoints = [{
          uriPath: sourcePath,
          line: 1,
          enabled: true
        }];
        events.push('save-participants-finished');
        return true;
      }
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(events, [
    'manifest',
    'save-start',
    'save-participants-finished',
    'manifest',
    'breakpoints:1'
  ]);
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: sourcePath,
      text: 'Public Sub AfterSave()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'
    }],
    activeSource: { path: sourcePath, line: 0, character: 11 },
    breakpoints: [{ path: sourcePath, line: 1 }]
  });
});

test('debug launch rejects an enabled in-scope conditional breakpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      condition: 'ready'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /conditional breakpoint.*unsupported/i
  );
});

test('debug launch rejects an enabled in-scope hit-count breakpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      hitCondition: '3'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /hit-count breakpoint.*unsupported/i
  );
});

test('debug launch rejects an enabled in-scope logpoint instead of downgrading it', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 0,
      enabled: true,
      logMessage: 'hit'
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /logpoint.*unsupported/i
  );
});

test('debug launch serializes enabled ordinary exported-source breakpoints in canonical order', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const moduleSource = path.join(projectRoot, 'src', 'Book1', 'A_Module.bas');
  const classSource = path.join(projectRoot, 'src', 'Book1', 'B_Class.cls');
  const formSource = path.join(projectRoot, 'src', 'Book1', 'C_Form.frm');
  const integration = createIntegration({
    activeEditor: { uriPath: moduleSource, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [formSource, 'Begin VB.UserForm C_Form\r\nEnd\r\nPublic Sub FormTarget()\r\nEnd Sub\r\n'],
      [classSource, 'Public Sub ClassTarget()\r\nEnd Sub\r\n'],
      [moduleSource, 'Public Sub ModuleTarget()\r\n  Debug.Print "one"\r\nEnd Sub\r\n']
    ]),
    getSourceBreakpoints: () => [{
      uriPath: formSource,
      line: 2,
      enabled: true
    }, {
      uriPath: moduleSource,
      line: 1,
      enabled: true
    }, {
      uriPath: classSource,
      line: 0,
      enabled: true
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(
    (configuration.sourceSnapshot as { breakpoints: unknown }).breakpoints,
    [
      { path: moduleSource, line: 1 },
      { path: classSource, line: 0 },
      { path: formSource, line: 2 }
    ]
  );
});

test('debug launch rejects duplicate enabled breakpoints at one canonical source line', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[
      sourcePath,
      'Public Sub RunTarget()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'
    ]]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 1,
      enabled: true
    }, {
      uriPath: sourcePath.toUpperCase(),
      line: 1,
      enabled: true
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /duplicate enabled VBA breakpoint.*DebugModule\.bas:2/i
  );
});

test('debug launch ignores disabled, out-of-scope, and non-source breakpoints before unsupported-feature checks', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const otherRoot = path.join('C:', 'work', 'OtherProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const formSidecarPath = path.join(projectRoot, 'src', 'Book1', 'Dialog.frx');
  const outsidePath = path.join(otherRoot, 'src', 'Book2', 'Outside.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([
      [sourcePath, 'Public Sub RunTarget()\r\n  Debug.Print "hit"\r\nEnd Sub\r\n'],
      [formSidecarPath, 'binary form sidecar placeholder'],
      [outsidePath, 'Public Sub OutsideTarget()\r\nEnd Sub\r\n']
    ]),
    getSourceBreakpoints: () => [{
      uriPath: sourcePath,
      line: 1,
      enabled: true
    }, {
      uriPath: sourcePath,
      line: 0,
      enabled: false,
      condition: 'unsupported but disabled'
    }, {
      uriPath: outsidePath,
      line: 0,
      enabled: true,
      logMessage: 'unsupported but outside the selected source set'
    }, {
      uriPath: formSidecarPath,
      line: 0,
      enabled: true,
      condition: 'unsupported but not an exported source'
    }]
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(
    (configuration.sourceSnapshot as { breakpoints: unknown }).breakpoints,
    [{ path: sourcePath, line: 1 }]
  );
});

test('debug launch aborts before re-resolution and snapshot capture when a selected source cannot be saved', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  let manifestReadCount = 0;
  let sourceWasRead = false;
  const integration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('BookProject', ['Book1'])]]),
    sources: new Map([[sourcePath, 'Public Sub RunTarget()\r\nEnd Sub\r\n']]),
    readTextFile: async () => {
      manifestReadCount += 1;
      return manifestJson('BookProject', ['Book1']);
    },
    readSourceText: async () => {
      sourceWasRead = true;
      return '';
    },
    openTextDocuments: () => [{
      uriPath: sourcePath,
      isDirty: true,
      save: async () => false
    }]
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    (error: unknown) => (
      error instanceof Error
      && /could not save exported VBA source/i.test(error.message)
      && error.message.includes(sourcePath)
    )
  );
  assert.equal(manifestReadCount, 1);
  assert.equal(sourceWasRead, false);
});

test('an active source belonging to more than one workbook-backed project reports project ambiguity', async () => {
  const workspaceRoot = path.join('C:', 'work');
  const firstRoot = path.join(workspaceRoot, 'FirstProject');
  const secondRoot = path.join(workspaceRoot, 'SecondProject');
  const sharedSource = path.join(workspaceRoot, 'Shared', 'DebugModule.bas');
  const manifests = new Map([
    [path.join(firstRoot, 'vba-project.json'), manifestJsonWithSourcePath(
      'FirstProject',
      'Book1',
      path.relative(firstRoot, path.dirname(sharedSource)))],
    [path.join(secondRoot, 'vba-project.json'), manifestJsonWithSourcePath(
      'SecondProject',
      'Book2',
      path.relative(secondRoot, path.dirname(sharedSource)))]
  ]);
  const integration = createIntegration({
    activeEditor: { uriPath: sharedSource, line: 0, character: 0 },
    manifests,
    sources: new Map([[sharedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']])
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /project selection is ambiguous/i
  );
});

test('an active source belonging to more than one document source set reports document ambiguity', async () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sharedSourceRoot = path.join(projectRoot, 'src', 'Shared');
  const activeSource = path.join(sharedSourceRoot, 'DebugModule.bas');
  const manifest = JSON.stringify({
    schemaVersion: 1,
    projectName: 'BookProject',
    primaryDocument: 'Book1',
    documents: {
      Book1: {
        kind: 'excel',
        sourcePath: 'src/Shared',
        templatePath: 'Book1.xlsm',
        binPath: 'bin/Book1.xlsm',
        publishPath: 'publish/Book1.xlsm'
      },
      Book2: {
        kind: 'excel',
        sourcePath: 'src/Shared',
        templatePath: 'Book2.xlsm',
        binPath: 'bin/Book2.xlsm',
        publishPath: 'publish/Book2.xlsm'
      }
    }
  });
  const integration = createIntegration({
    activeEditor: { uriPath: activeSource, line: 0, character: 0 },
    manifests: new Map([[path.join(projectRoot, 'vba-project.json'), manifest]]),
    sources: new Map([[activeSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']])
  });

  await assert.rejects(
    () => integration.resolveDebugConfiguration({}),
    /document selection is ambiguous/i
  );
});

test('an explicit procedure pair uses active source membership to narrow omitted project and document selectors', async () => {
  const firstRoot = path.join('C:', 'work', 'FirstProject');
  const selectedRoot = path.join('C:', 'work', 'SelectedProject');
  const firstSource = path.join(firstRoot, 'src', 'Book1', 'First.bas');
  const selectedSource = path.join(selectedRoot, 'src', 'Book2', 'DebugModule.bas');
  const integration = createIntegration({
    activeEditor: { uriPath: selectedSource, line: 0, character: 0 },
    manifests: new Map([
      [path.join(firstRoot, 'vba-project.json'), manifestJson('FirstProject', ['Book1'])],
      [path.join(selectedRoot, 'vba-project.json'), manifestJson('SelectedProject', ['Book2'])]
    ]),
    sources: new Map([
      [firstSource, 'Public Sub FirstTarget()\r\nEnd Sub\r\n'],
      [selectedSource, 'Public Sub RunTarget()\r\nEnd Sub\r\n']
    ])
  });

  const configuration = await integration.resolveDebugConfiguration({
    module: 'DebugModule',
    procedure: 'RunTarget'
  });

  assert.equal(configuration.project, selectedRoot);
  assert.equal(configuration.document, 'Book2');
  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [{
      path: selectedSource,
      text: 'Public Sub RunTarget()\r\nEnd Sub\r\n'
    }],
    breakpoints: []
  });
});

test('source snapshots preserve decoded CP932 and UTF-16 text through the dedicated source-text host port', async () => {
  const projectRoot = path.join('C:', 'work', 'EncodedProject');
  const manifestPath = path.join(projectRoot, 'vba-project.json');
  const cp932Source = path.join(projectRoot, 'src', 'Book1', 'Cp932.bas');
  const utf16Source = path.join(projectRoot, 'src', 'Book1', 'Utf16.cls');
  const decodedSources = new Map([
    [cp932Source, 'Public Sub 実行()\r\nEnd Sub\r\n'],
    [utf16Source, 'Public Sub 検証()\r\nEnd Sub\r\n']
  ]);
  const integration = createIntegration({
    activeEditor: { uriPath: cp932Source, line: 0, character: 11 },
    manifests: new Map([[manifestPath, manifestJson('EncodedProject', ['Book1'])]]),
    sources: decodedSources,
    readSourceText: async (sourcePath) => decodedSources.get(sourcePath) ?? ''
  });

  const configuration = await integration.resolveDebugConfiguration({});

  assert.deepEqual(configuration.sourceSnapshot, {
    schemaVersion: 1,
    sources: [
      { path: cp932Source, text: 'Public Sub 実行()\r\nEnd Sub\r\n' },
      { path: utf16Source, text: 'Public Sub 検証()\r\nEnd Sub\r\n' }
    ],
    activeSource: { path: cp932Source, line: 0, character: 11 },
    breakpoints: []
  });
});

test('unsupported launch fields and request modes fail closed before project discovery or save', async () => {
  let hostTouched = false;
  const integration = new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [],
      getActiveEditor: () => {
        hostTouched = true;
        return undefined;
      },
      getOpenTextDocuments: () => {
        hostTouched = true;
        return [];
      },
      getSourceBreakpoints: () => [],
      findProjectManifests: async () => {
        hostTouched = true;
        return [];
      },
      readTextFile: async () => '',
      readSourceText: async () => '',
      findExportedSourceFiles: async () => []
    }
  });
  const unsupportedConfigurations: Array<[Record<string, unknown>, RegExp]> = [
    [{ args: ['value'] }, /unsupported.*args/i],
    [{ arguments: ['value'] }, /unsupported.*arguments/i],
    [{ noBuild: true }, /unsupported.*noBuild/i],
    [{ stopOnEntry: true }, /unsupported.*stopOnEntry/i],
    [{ request: 'attach' }, /only.*launch/i],
    [{ compound: ['one', 'two'] }, /unsupported.*compound/i],
    [{ concurrent: true }, /unsupported.*concurrent/i],
    [{ compilerConstants: { VBA7: true } }, /unsupported.*compilerConstants/i]
  ];

  for (const [configuration, expectedError] of unsupportedConfigurations) {
    await assert.rejects(
      () => integration.resolveDebugConfiguration(configuration),
      expectedError
    );
  }
  assert.equal(hostTouched, false);
});

test('dynamic debug configurations expose one transient active-procedure launch only for an exported VBA editor', () => {
  const projectRoot = path.join('C:', 'work', 'BookProject');
  const sourcePath = path.join(projectRoot, 'src', 'Book1', 'DebugModule.bas');
  const activeIntegration = createIntegration({
    activeEditor: { uriPath: sourcePath, line: 0, character: 0 },
    manifests: new Map(),
    sources: new Map()
  });
  const inactiveIntegration = createIntegration({
    activeEditor: {
      uriPath: path.join(projectRoot, 'README.md'),
      line: 0,
      character: 0
    },
    manifests: new Map(),
    sources: new Map()
  });

  assert.deepEqual(activeIntegration.provideDynamicDebugConfigurations(), [{
    type: 'vba',
    request: 'launch',
    name: 'VBA: Active Procedure'
  }]);
  assert.deepEqual(inactiveIntegration.provideDynamicDebugConfigurations(), []);
});

function createIntegration(options: {
  activeEditor?: { uriPath: string; line: number; character: number } | undefined;
  getActiveEditor?: () => { uriPath: string; line: number; character: number } | undefined;
  manifests: ReadonlyMap<string, string>;
  sources: ReadonlyMap<string, string>;
  readTextFile?: (filePath: string) => Promise<string>;
  readSourceText?: (filePath: string) => Promise<string>;
  getSourceBreakpoints?: () => readonly VbaDebugSourceBreakpoint[];
  openTextDocuments?: () => readonly {
    uriPath: string;
    isDirty: boolean;
    save(): Promise<boolean>;
  }[];
}): VscodeDebugIntegration {
  return new VscodeDebugIntegration({
    extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
    getConfiguredDevToolPath: () => undefined,
    debugConfigurationHost: {
      workspaceRoots: [path.join('C:', 'work')],
      getActiveEditor: options.getActiveEditor ?? (() => options.activeEditor),
      getOpenTextDocuments: options.openTextDocuments ?? (() => []),
      getSourceBreakpoints: options.getSourceBreakpoints ?? (() => []),
      findProjectManifests: async () => [...options.manifests.keys()],
      readTextFile: options.readTextFile ?? (async (filePath) => {
        const text = options.manifests.get(filePath) ?? options.sources.get(filePath);
        if (text === undefined) {
          throw new Error(`Missing fake file: ${filePath}`);
        }
        return text;
      }),
      readSourceText: options.readSourceText ?? (async (filePath) => {
        const text = options.sources.get(filePath);
        if (text === undefined) {
          throw new Error(`Missing fake source: ${filePath}`);
        }
        return text;
      }),
      findExportedSourceFiles: async (sourceSetPath) => (
        [...options.sources.keys()].filter((sourcePath) => isWithin(sourcePath, sourceSetPath))
      )
    }
  });
}

function manifestJson(projectName: string, documentNames: readonly string[]): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: documentNames[0],
    documents: Object.fromEntries(documentNames.map((documentName) => [
      documentName,
      {
        kind: 'excel',
        sourcePath: `src/${documentName}`,
        templatePath: `src/${documentName}/${documentName}.xlsm`,
        binPath: `bin/${documentName}.xlsm`,
        publishPath: `publish/${documentName}.xlsm`
      }
    ]))
  });
}

function manifestJsonWithSourcePath(
  projectName: string,
  documentName: string,
  sourcePath: string
): string {
  return JSON.stringify({
    schemaVersion: 1,
    projectName,
    primaryDocument: documentName,
    documents: {
      [documentName]: {
        kind: 'excel',
        sourcePath,
        templatePath: `${documentName}.xlsm`,
        binPath: `bin/${documentName}.xlsm`,
        publishPath: `publish/${documentName}.xlsm`
      }
    }
  });
}

function isWithin(filePath: string, directoryPath: string): boolean {
  const relative = path.relative(path.resolve(directoryPath), path.resolve(filePath));
  return relative.length > 0
    && relative !== '..'
    && !relative.startsWith(`..${path.sep}`)
    && !path.isAbsolute(relative);
}
