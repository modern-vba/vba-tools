import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import * as path from 'node:path';
import {
  Position,
  Uri,
  WorkspaceEdit,
  commands,
  extensions,
  window,
  workspace
} from 'vscode';

export async function runModuleRenameIntegrationTests(): Promise<void> {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'The module Rename fixture root must be provided.');
  const outsideRoot = path.join(fixtureRoot, 'outside');
  const sourcePath = path.join(outsideRoot, 'InvoiceModule.bas');
  const finalPath = path.join(outsideRoot, 'INVOICEMODULE.bas');
  const sourceUri = Uri.file(sourcePath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);
  assert.equal(sourceDocument.languageId, 'vba');

  const extension = extensions.getExtension('modern-vba.vba-tools');
  assert.ok(extension, 'The VBA Tools development extension must be available.');
  await extension.activate();

  const edit = await requestRename(
    sourceUri,
    new Position(0, 'Attribute VB_Name = "'.length),
    'INVOICEMODULE');
  assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

  const renamedEntries = await waitForEntryCasing(
    outsideRoot,
    'INVOICEMODULE.bas',
    'InvoiceModule.bas');
  assert.ok(renamedEntries.includes('INVOICEMODULE.bas'));
  assert.ok(!renamedEntries.includes('InvoiceModule.bas'));
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "INVOICEMODULE"/);
  assert.match(sourceDocument.getText(), /INVOICEMODULE\.Run/);

  await verifyApplicationFailureRecovery(outsideRoot);
  await verifyCaseOnlyFormSourceUnitRename(outsideRoot);
}

async function verifyCaseOnlyFormSourceUnitRename(
  outsideRoot: string
): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'Dialog.frm');
  const sourceUri = Uri.file(sourcePath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);

  const edit = await requestRename(
    sourceUri,
    new Position(3, 'Attribute VB_Name = "'.length),
    'DIALOG'
  );
  assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

  const deadline = Date.now() + 5_000;
  let entries: string[] = [];
  while (Date.now() < deadline) {
    entries = await readEntryNames(outsideRoot);
    if (entries.includes('DIALOG.frm')
        && entries.includes('DIALOG.frx')
        && !entries.includes('Dialog.frm')
        && !entries.includes('Dialog.frx')) {
      break;
    }
    await new Promise(resolve => setTimeout(resolve, 25));
  }

  assert.ok(entries.includes('DIALOG.frm'));
  assert.ok(entries.includes('DIALOG.frx'));
  assert.ok(!entries.includes('Dialog.frm'));
  assert.ok(!entries.includes('Dialog.frx'));
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "DIALOG"/);
}

async function verifyApplicationFailureRecovery(outsideRoot: string): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'ApplicationModule.bas');
  const destinationPath = path.join(outsideRoot, 'BillingModule.bas');
  const sourceUri = Uri.file(sourcePath);
  const destinationUri = Uri.file(destinationPath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);

  const stalePlan = await requestRename(
    sourceUri,
    new Position(0, 'Attribute VB_Name = "'.length),
    'BillingModule');
  await workspace.fs.writeFile(
    destinationUri,
    Buffer.from('Attribute VB_Name = "Existing"\r\n', 'utf8')
  );

  assert.equal(
    await workspace.applyEdit(stalePlan, { isRefactoring: true }),
    false
  );
  let entries = await readEntryNames(outsideRoot);
  assert.ok(entries.includes('ApplicationModule.bas'));
  assert.ok(entries.includes('BillingModule.bas'));
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "ApplicationModule"/);

  await window.showTextDocument(sourceDocument);
  await commands.executeCommand('undo');
  await workspace.fs.delete(destinationUri, { recursive: false, useTrash: false });
  const retryPlan = await requestRename(
    sourceUri,
    new Position(0, 'Attribute VB_Name = "'.length),
    'BillingModule');
  assert.ok(retryPlan.entries().length > 0);
  entries = await readEntryNames(outsideRoot);
  assert.ok(entries.includes('ApplicationModule.bas'));
  assert.ok(!entries.includes('BillingModule.bas'));
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "ApplicationModule"/);
}

async function requestRename(
  uri: Uri,
  position: Position,
  newName: string
): Promise<WorkspaceEdit> {
  const deadline = Date.now() + 15_000;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const edit = await commands.executeCommand<WorkspaceEdit | undefined>(
        'vscode.executeDocumentRenameProvider',
        uri,
        position,
        newName
      );
      if (edit !== undefined) {
        return edit;
      }
    } catch (error: unknown) {
      lastError = error;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }

  throw new Error(
    `The VBA module Rename provider did not become ready: ${String(lastError)}`
  );
}

async function readEntryNames(directoryPath: string): Promise<string[]> {
  const entries = await workspace.fs.readDirectory(Uri.file(directoryPath));
  return entries.map(([name]) => name);
}

async function waitForEntryCasing(
  directoryPath: string,
  expectedName: string,
  rejectedName: string
): Promise<string[]> {
  const deadline = Date.now() + 5_000;
  let entries: string[] = [];
  while (Date.now() < deadline) {
    entries = await readEntryNames(directoryPath);
    if (entries.includes(expectedName) && !entries.includes(rejectedName)) {
      return entries;
    }
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  return entries;
}
