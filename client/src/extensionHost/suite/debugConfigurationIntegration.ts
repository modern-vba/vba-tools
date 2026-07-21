import assert from 'node:assert/strict';
import * as path from 'node:path';
import {
  Position,
  Selection,
  Uri,
  WorkspaceEdit,
  commands,
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
    "' captured after save\r\n"
  );
  assert.equal(await workspace.applyEdit(sourceEdit), true);
  const activePosition = new Position(4, 4);
  editor.selection = new Selection(activePosition, activePosition);
  const extension = extensions.getExtension('modern-vba.vba-tools');
  assert.ok(extension, 'The VBA Tools development extension must be available.');
  await extension.activate();

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
      readonly sources: readonly { readonly path: string; readonly text: string }[];
      readonly activeSource: {
        readonly path: string;
        readonly line: number;
        readonly character: number;
      };
    };

    assert.equal(String(configuration.project).toLowerCase(), fixtureRoot.toLowerCase());
    assert.equal(configuration.document, 'Book1');
    assert.equal(snapshot.schemaVersion, 1);
    assert.equal(snapshot.sources.length, 2);
    const sourcesByPath = new Map(snapshot.sources.map((source) => [
      source.path.toLowerCase(),
      source.text
    ]));
    assert.equal(sourcesByPath.get(sourcePath.toLowerCase()), sourceDocument.getText());
    assert.equal(sourcesByPath.get(encodedSourcePath.toLowerCase()), [
      'Attribute VB_Name = "EncodedModule"',
      'Option Explicit',
      '',
      'Public Sub EncodedTarget()',
      '    Debug.Print "日本語"',
      'End Sub',
      ''
    ].join('\r\n'));
    assert.equal(snapshot.activeSource.path.toLowerCase(), sourcePath.toLowerCase());
    assert.equal(snapshot.activeSource.line, activePosition.line);
    assert.equal(snapshot.activeSource.character, activePosition.character);
    assert.equal(sourceDocument.isDirty, false);
    assert.equal(outsideDocument.isDirty, true);
    console.log('PASS F5 resolves and saves only the active workbook-backed VBA project');
  } finally {
    observer.dispose();
  }
}
