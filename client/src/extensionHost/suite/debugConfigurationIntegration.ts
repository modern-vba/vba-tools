import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { readFile } from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
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

export async function runDebugConfigurationIntegrationTests(): Promise<void> {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'The debug configuration fixture root must be provided.');
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
    assert.equal(snapshot.schemaVersion, 1);
    assert.equal(snapshot.sources.length, 2);
    const sourcesByRelativePath = new Map(snapshot.sources.map((source) => [
      source.relativePath.toLowerCase(),
      source
    ]));
    const dirtySource = sourcesByRelativePath.get('debugmodule.bas');
    assert.ok(dirtySource);
    assert.equal(fileURLToPath(dirtySource.sourceUri!).toLowerCase(), sourcePath.toLowerCase());
    assert.equal(dirtySource.encoding, 'utf8');
    assert.deepEqual(
      Buffer.from(dirtySource.contentBase64, 'base64'),
      Buffer.from(sourceDocument.getText(), 'utf8')
    );
    assert.match(sourceDocument.getText(), /captured without saving/);
    const encodedSource = sourcesByRelativePath.get('encodedmodule.bas');
    assert.ok(encodedSource);
    assert.equal(
      fileURLToPath(encodedSource.sourceUri!).toLowerCase(),
      encodedSourcePath.toLowerCase()
    );
    assert.equal(encodedSource.encoding, 'windows-932');
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
}
