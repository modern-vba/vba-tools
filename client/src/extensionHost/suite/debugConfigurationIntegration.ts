import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { readFile } from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ConfigurationTarget,
  Location,
  Position,
  Selection,
  SourceBreakpoint,
  Uri,
  WorkspaceEdit,
  commands,
  debug,
  extensions,
  window,
  workspace
} from 'vscode';
import {
  useVbaDebugConfigurationObserverForTest
} from '../../vscodeDebugIntegration';
import type { VbaDebugConfiguration } from '../../vscodeDebugConfiguration';
import { VbaDevSessionResolver } from '../../devtool';

export async function runDebugConfigurationIntegrationTests(): Promise<void> {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'The debug configuration fixture root must be provided.');
  const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
  const resolver = new VbaDevSessionResolver({ extensionRoot });
  const activeWindowsCodePage = await resolver.readActiveWindowsCodePage();
  const sourcePath = path.join(fixtureRoot, 'src', 'Book1', 'DebugModule.bas');
  const encodedSourcePath = path.join(fixtureRoot, 'src', 'Book1', 'EncodedModule.bas');
  const outsidePath = path.join(fixtureRoot, 'outside', 'Outside.bas');
  const savedSourceBytes = await readFile(sourcePath);
  const savedEncodedSourceBytes = await readFile(encodedSourcePath);
  const savedOutsideBytes = await readFile(outsidePath);
  const outsideDocument = await workspace.openTextDocument(Uri.file(outsidePath));
  const outsideEdit = new WorkspaceEdit();
  outsideEdit.insert(
    outsideDocument.uri,
    outsideDocument.positionAt(outsideDocument.getText().length),
    "' remains dirty\r\n"
  );
  assert.equal(await workspace.applyEdit(outsideEdit), true);
  assert.equal(outsideDocument.isDirty, true);

  const sourceDocument = await workspace.openTextDocument(Uri.file(sourcePath));
  const editor = await window.showTextDocument(sourceDocument);
  const sourceEdit = new WorkspaceEdit();
  sourceEdit.insert(
    sourceDocument.uri,
    sourceDocument.positionAt(sourceDocument.getText().length),
    "' captured without saving\r\n"
  );
  assert.equal(await workspace.applyEdit(sourceEdit), true);
  const activePosition = new Position(4, 4);
  editor.selection = new Selection(activePosition, activePosition);
  const sourceBreakpoint = new SourceBreakpoint(
    new Location(sourceDocument.uri, new Position(4, 0)),
    true
  );
  const extension = extensions.getExtension('modern-vba.vba-tools');
  assert.ok(extension, 'The VBA Tools development extension must be available.');
  await extension.activate();
  debug.addBreakpoints([sourceBreakpoint]);

  let capture: ((configuration: VbaDebugConfiguration) => void) | undefined;
  const captured = new Promise<VbaDebugConfiguration>((resolve) => {
    capture = resolve;
  });
  const observer = useVbaDebugConfigurationObserverForTest((configuration) => {
    capture?.(configuration);
  });
  try {
    await commands.executeCommand('workbench.action.debug.start');
    const configuration = await Promise.race([
      captured,
      new Promise<never>((_resolve, reject) => {
        setTimeout(() => reject(new Error('F5 did not resolve a VBA debug configuration.')), 10_000);
      })
    ]);
    const snapshot = configuration.sourceSnapshot as {
      readonly schemaVersion: number;
      readonly sources: readonly {
        readonly relativePath: string;
        readonly sourceUri?: string;
        readonly encoding?: string;
        readonly contentBase64: string;
      }[];
      readonly activeSource: {
        readonly sourceUri: string;
        readonly line: number;
        readonly character: number;
      };
      readonly breakpoints: readonly {
        readonly sourceUri: string;
        readonly line: number;
      }[];
    };

    assert.equal(String(configuration.project).toLowerCase(), fixtureRoot.toLowerCase());
    assert.equal(configuration.document, 'Book1');
    assert.equal(configuration.__vbaDebugWorkbookFileName, 'Book1.xlsm');
    assert.equal(
      String(configuration.__vbaDebugSourceWorkbookPath).toLowerCase(),
      path.join(fixtureRoot, 'src', 'Book1', 'Book1.xlsm').toLowerCase()
    );
    assert.notEqual(
      String(configuration.__vbaDebugSourceWorkbookPath).toLowerCase(),
      path.join(fixtureRoot, 'bin', 'Book1.xlsm').toLowerCase()
    );
    assert.equal(snapshot.schemaVersion, 2);
    assert.equal(snapshot.sources.length, 2);
    const sourcesByRelativePath = new Map(snapshot.sources.map((source) => [
      source.relativePath.toLowerCase(),
      source
    ]));
    const dirtySource = sourcesByRelativePath.get('debugmodule.bas');
    assert.ok(dirtySource);
    assert.equal(fileURLToPath(dirtySource.sourceUri!).toLowerCase(), sourcePath.toLowerCase());
    assert.equal(sourceDocument.encoding, 'utf8bom');
    assert.equal(dirtySource.encoding, 'utf8bom');
    assert.deepEqual(
      Buffer.from(dirtySource.contentBase64, 'base64'),
      Buffer.from(`\ufeff${sourceDocument.getText()}`, 'utf8')
    );
    assert.match(sourceDocument.getText(), /captured without saving/);
    const encodedSource = sourcesByRelativePath.get('encodedmodule.bas');
    assert.ok(encodedSource);
    assert.equal(
      fileURLToPath(encodedSource.sourceUri!).toLowerCase(),
      encodedSourcePath.toLowerCase()
    );
    // Clean BOMless sources use the companion's active Windows code page, not
    // the Japanese locale of the machine that originally authored this fixture.
    assert.equal(encodedSource.encoding,
      activeWindowsCodePage === 65001 ? 'utf8' : `windows-${activeWindowsCodePage}`);
    assert.deepEqual(
      Buffer.from(encodedSource.contentBase64, 'base64'),
      savedEncodedSourceBytes
    );
    assert.equal(
      fileURLToPath(snapshot.activeSource.sourceUri).toLowerCase(),
      sourcePath.toLowerCase()
    );
    assert.equal(snapshot.activeSource.line, activePosition.line);
    assert.equal(snapshot.activeSource.character, activePosition.character);
    assert.equal(snapshot.breakpoints.length, 1);
    assert.equal(
      fileURLToPath(snapshot.breakpoints[0].sourceUri).toLowerCase(),
      sourcePath.toLowerCase()
    );
    assert.equal(snapshot.breakpoints[0].line, 4);
    assert.equal(sourceDocument.isDirty, true);
    assert.equal(outsideDocument.isDirty, true);
    assert.deepEqual(await readFile(sourcePath), savedSourceBytes);
    assert.deepEqual(await readFile(encodedSourcePath), savedEncodedSourceBytes);
    assert.deepEqual(await readFile(outsidePath), savedOutsideBytes);
    console.log('PASS F5 captures the active workbook-backed VBA project without saving');
  } finally {
    observer.dispose();
    debug.removeBreakpoints([sourceBreakpoint]);
  }

  const folder = workspace.getWorkspaceFolder(Uri.file(sourcePath));
  assert.ok(folder);
  const pinnedConfiguration = {
    type: 'vba',
    request: 'launch',
    name: 'Pinned unsaved-source regression',
    project: fixtureRoot,
    document: 'Book1',
    module: 'DebugModule',
    procedure: 'DebugMe'
  };
  const launch = workspace.getConfiguration('launch', folder.uri);
  const previousConfigurations = launch.inspect('configurations')?.workspaceFolderValue;
  const debugSettings = workspace.getConfiguration('debug', folder.uri);
  const previousSaveBeforeStart = debugSettings.inspect('saveBeforeStart')?.workspaceFolderValue;
  await launch.update('configurations', [pinnedConfiguration], ConfigurationTarget.WorkspaceFolder);
  // Standard F5 saves before invoking the extension. The workspace setting is
  // required when a non-VBA editor is active, including for a pinned target.
  await debugSettings.update('saveBeforeStart', 'none', ConfigurationTarget.WorkspaceFolder);
  const launchDocument = await workspace.openTextDocument(
    Uri.joinPath(folder.uri, '.vscode', 'launch.json')
  );
  await window.showTextDocument(launchDocument);
  assert.equal(window.activeTextEditor?.document, launchDocument);
  assert.equal(sourceDocument.isDirty, true);
  const saveEvents: string[] = [];
  const saves = workspace.onDidSaveTextDocument(document => saveEvents.push(document.uri.toString()));
  let capturePinned: ((configuration: VbaDebugConfiguration) => void) | undefined;
  const pinnedCaptured = new Promise<VbaDebugConfiguration>(resolve => { capturePinned = resolve; });
  const pinnedObserver = useVbaDebugConfigurationObserverForTest(configuration => {
    capturePinned?.(configuration);
  });
  try {
    assert.equal(launchDocument.languageId, 'jsonc');
    assert.equal(workspace.getConfiguration('debug', launchDocument).get('saveBeforeStart'), 'none');
    await commands.executeCommand('workbench.action.debug.start', { config: pinnedConfiguration });
    const configuration = await Promise.race([
      pinnedCaptured,
      new Promise<never>((_resolve, reject) => {
        setTimeout(() => reject(new Error('Pinned F5 did not resolve a VBA debug configuration.')), 10_000);
      })
    ]);
    assert.equal(configuration.module, 'DebugModule');
    assert.equal(configuration.procedure, 'DebugMe');
    assert.deepEqual(await readFile(sourcePath), savedSourceBytes,
      'F5 with launch.json active must not save the dirty VBA source.');
    assert.deepEqual(await readFile(outsidePath), savedOutsideBytes,
      'VBA F5 must not save unrelated dirty editors.');
    assert.equal(sourceDocument.isDirty, true);
    assert.equal(outsideDocument.isDirty, true);
    assert.deepEqual(saveEvents, []);
    console.log('PASS pinned F5 with launch.json active preserves dirty editor and disk bytes');
  } finally {
    pinnedObserver.dispose();
    saves.dispose();
    await launch.update('configurations', previousConfigurations, ConfigurationTarget.WorkspaceFolder);
    await debugSettings.update('saveBeforeStart', previousSaveBeforeStart, ConfigurationTarget.WorkspaceFolder);
  }
}
