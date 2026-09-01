import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import * as path from 'node:path';
import {
  Position,
  Range,
  Uri,
  WorkspaceEdit,
  commands,
  extensions,
  window,
  workspace
} from 'vscode';
import { CaseOnlyVbaFileRenameAdapter } from '../../caseOnlyVbaFileRename';

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
  await verifyCancelledPlanDoesNotBlockSidecarOnlyCaseRename(outsideRoot);
  await verifyFormOnlyPlanIgnoresStaleSidecarBatch(outsideRoot);
  await verifyProductionSidecarOnlyCaseRename(outsideRoot);
  await verifyMismatchedOldFormAndSidecarCasing(outsideRoot);
  await verifyProductionMismatchedOldFormAndSidecarCasing(outsideRoot);
}

async function verifyFormOnlyPlanIgnoresStaleSidecarBatch(
  outsideRoot: string
): Promise<void> {
  const sourceUri = Uri.file(path.join(outsideRoot, 'StandaloneForm.frm'));
  const renamedSourceUri = Uri.file(path.join(outsideRoot, 'STANDALONEFORM.frm'));
  const staleSidecarUri = Uri.file(path.join(outsideRoot, 'StandaloneForm.frx'));
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);
  const failures: string[] = [];
  const adapter = new CaseOnlyVbaFileRenameAdapter(message => failures.push(message));
  try {
    await workspace.fs.writeFile(staleSidecarUri, Uint8Array.from([0x08, 0x09]));
    adapter.capture([
      {
        oldUri: sourceUri.toString(),
        newUri: Uri.file(path.join(outsideRoot, 'standaloneform.frm')).toString()
      },
      {
        oldUri: staleSidecarUri.toString(),
        newUri: Uri.file(path.join(outsideRoot, 'standaloneform.frx')).toString()
      }
    ]);
    await workspace.fs.delete(staleSidecarUri, { recursive: false, useTrash: false });
    adapter.capture([{
      oldUri: sourceUri.toString(),
      newUri: renamedSourceUri.toString()
    }]);

    const renamedSource = sourceDocument.getText()
      .replace('StandaloneForm', 'STANDALONEFORM')
      .replace('StandaloneForm', 'STANDALONEFORM');
    const edit = new WorkspaceEdit();
    edit.replace(
      sourceUri,
      new Range(
        sourceDocument.positionAt(0),
        sourceDocument.positionAt(sourceDocument.getText().length)),
      renamedSource);
    assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

    let entries = await waitForEntryCasing(
      outsideRoot,
      'STANDALONEFORM.frm',
      'StandaloneForm.frm');
    await new Promise(resolve => setTimeout(resolve, 350));
    assert.ok(entries.includes('STANDALONEFORM.frm'));
    assert.ok(!entries.some(name => name.toLowerCase() === 'standaloneform.frx'));
    assert.deepEqual(failures, []);

    await window.showTextDocument(sourceDocument);
    await commands.executeCommand('undo');
    entries = await waitForEntryCasing(
      outsideRoot,
      'StandaloneForm.frm',
      'STANDALONEFORM.frm');
    await new Promise(resolve => setTimeout(resolve, 350));
    assert.ok(entries.includes('StandaloneForm.frm'));
    assert.ok(!entries.some(name => name.toLowerCase() === 'standaloneform.frx'));
    assert.deepEqual(failures, []);
  } finally {
    adapter.dispose();
  }
}

async function verifyCancelledPlanDoesNotBlockSidecarOnlyCaseRename(
  outsideRoot: string
): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'Dialog.frm');
  const sourceUri = Uri.file(sourcePath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);
  const oldSidecarUri = Uri.file(path.join(outsideRoot, 'DIALOG.FRX'));
  const newSidecarUri = Uri.file(path.join(outsideRoot, 'Dialog.FRX'));
  const failures: string[] = [];
  const adapter = new CaseOnlyVbaFileRenameAdapter(message => failures.push(message));
  try {
    adapter.capture([{
      oldUri: sourceUri.toString(),
      newUri: Uri.file(path.join(outsideRoot, 'DIALOG.frm')).toString()
    }]);
    adapter.capture([{
      oldUri: oldSidecarUri.toString(),
      newUri: newSidecarUri.toString()
    }]);
    const renamedSource = sourceDocument.getText()
      .replace('dIaLoG', 'Dialog')
      .replace('DIALOG.FRX', 'Dialog.FRX')
      .replace('dIaLoG', 'Dialog');
    const edit = new WorkspaceEdit();
    edit.replace(
      sourceUri,
      new Range(
        sourceDocument.positionAt(0),
        sourceDocument.positionAt(sourceDocument.getText().length)),
      renamedSource);
    assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

    const entries = await waitForEntryCasing(
      outsideRoot,
      'Dialog.FRX',
      'DIALOG.FRX');

    assert.ok(entries.includes('Dialog.frm'));
    assert.ok(entries.includes('Dialog.FRX'));
    assert.ok(!entries.includes('DIALOG.FRX'));
    assert.match(sourceDocument.getText(), /Begin VB\.UserForm Dialog/);
    assert.match(sourceDocument.getText(), /OleObjectBlob = "Dialog\.FRX":0000/);
    assert.match(sourceDocument.getText(), /Attribute VB_Name = "Dialog"/);
    assert.deepEqual(
      Array.from(await workspace.fs.readFile(newSidecarUri)),
      [0x00, 0x01, 0x02, 0x03]
    );

    await window.showTextDocument(sourceDocument);
    await commands.executeCommand('undo');
    const undoneEntries = await waitForEntryCasing(
      outsideRoot,
      'DIALOG.FRX',
      'Dialog.FRX');
    assert.ok(undoneEntries.includes('Dialog.frm'));
    assert.ok(undoneEntries.includes('DIALOG.FRX'));
    assert.ok(!undoneEntries.includes('Dialog.FRX'));
    assert.match(sourceDocument.getText(), /Begin VB\.UserForm dIaLoG/);
    assert.match(sourceDocument.getText(), /OleObjectBlob = "DIALOG\.FRX":0000/);
    assert.match(sourceDocument.getText(), /Attribute VB_Name = "dIaLoG"/);
    assert.deepEqual(
      Array.from(await workspace.fs.readFile(oldSidecarUri)),
      [0x00, 0x01, 0x02, 0x03]
    );
    assert.deepEqual(failures, []);
  } finally {
    adapter.dispose();
  }
}

async function verifyProductionSidecarOnlyCaseRename(
  outsideRoot: string
): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'Dialog.frm');
  const sourceUri = Uri.file(sourcePath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);

  const edit = await requestRename(
    sourceUri,
    new Position(4, 'Attribute VB_Name = "'.length),
    'Dialog'
  );
  assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

  const entries = await waitForEntryCasing(
    outsideRoot,
    'Dialog.FRX',
    'DIALOG.FRX');
  assert.ok(entries.includes('Dialog.frm'));
  assert.ok(entries.includes('Dialog.FRX'));
  assert.ok(!entries.includes('DIALOG.FRX'));
  assert.match(sourceDocument.getText(), /Begin VB\.UserForm Dialog/);
  assert.match(sourceDocument.getText(), /OleObjectBlob = "Dialog\.FRX":0000/);
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "Dialog"/);
  assert.deepEqual(
    Array.from(await workspace.fs.readFile(Uri.file(path.join(
      outsideRoot,
      'Dialog.FRX'
    )))),
    [0x00, 0x01, 0x02, 0x03]
  );
}

async function verifyMismatchedOldFormAndSidecarCasing(
  outsideRoot: string
): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'MixedCaseForm.frm');
  const sourceUri = Uri.file(sourcePath);
  const renamedSourceUri = Uri.file(path.join(outsideRoot, 'MIXEDCASEFORM.frm'));
  const sidecarUri = Uri.file(path.join(outsideRoot, 'mixedcaseform.frx'));
  const renamedSidecarUri = Uri.file(path.join(outsideRoot, 'MIXEDCASEFORM.frx'));
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);
  const failures: string[] = [];
  const adapter = new CaseOnlyVbaFileRenameAdapter(message => failures.push(message));
  try {
    adapter.capture([
      {
        oldUri: sourceUri.toString(),
        newUri: renamedSourceUri.toString()
      },
      {
        oldUri: sidecarUri.toString(),
        newUri: renamedSidecarUri.toString()
      }
    ]);
    const renamedSource = sourceDocument.getText()
      .replace('mIxEdCaSeFoRm', 'MIXEDCASEFORM')
      .replace('mixedcaseform.frx', 'MIXEDCASEFORM.frx')
      .replace('mIxEdCaSeFoRm', 'MIXEDCASEFORM');
    const edit = new WorkspaceEdit();
    edit.replace(
      sourceUri,
      new Range(
        sourceDocument.positionAt(0),
        sourceDocument.positionAt(sourceDocument.getText().length)),
      renamedSource);
    assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

    let entries = await waitForEntries(
      outsideRoot,
      ['MIXEDCASEFORM.frm', 'MIXEDCASEFORM.frx'],
      ['MixedCaseForm.frm', 'mixedcaseform.frx']);
    assert.ok(entries.includes('MIXEDCASEFORM.frm'));
    assert.ok(entries.includes('MIXEDCASEFORM.frx'));
    assert.ok(!entries.includes('MixedCaseForm.frm'));
    assert.ok(!entries.includes('mixedcaseform.frx'));
    assert.match(sourceDocument.getText(), /Begin VB\.UserForm MIXEDCASEFORM/);
    assert.match(sourceDocument.getText(), /OleObjectBlob = "MIXEDCASEFORM\.frx":0000/);
    assert.match(sourceDocument.getText(), /Attribute VB_Name = "MIXEDCASEFORM"/);
    assert.deepEqual(
      Array.from(await workspace.fs.readFile(renamedSidecarUri)),
      [0x04, 0x05, 0x06, 0x07]
    );

    await window.showTextDocument(sourceDocument);
    await commands.executeCommand('undo');
    entries = await waitForEntries(
      outsideRoot,
      ['MixedCaseForm.frm', 'mixedcaseform.frx'],
      ['MIXEDCASEFORM.frm', 'MIXEDCASEFORM.frx']);
    assert.ok(entries.includes('MixedCaseForm.frm'));
    assert.ok(entries.includes('mixedcaseform.frx'));
    assert.ok(!entries.includes('MIXEDCASEFORM.frm'));
    assert.ok(!entries.includes('MIXEDCASEFORM.frx'));
    assert.match(sourceDocument.getText(), /Begin VB\.UserForm mIxEdCaSeFoRm/);
    assert.match(sourceDocument.getText(), /OleObjectBlob = "mixedcaseform\.frx":0000/);
    assert.match(sourceDocument.getText(), /Attribute VB_Name = "mIxEdCaSeFoRm"/);
    assert.deepEqual(
      Array.from(await workspace.fs.readFile(sidecarUri)),
      [0x04, 0x05, 0x06, 0x07]
    );
    assert.deepEqual(failures, []);
  } finally {
    adapter.dispose();
  }
}

async function verifyProductionMismatchedOldFormAndSidecarCasing(
  outsideRoot: string
): Promise<void> {
  const sourcePath = path.join(outsideRoot, 'MixedCaseForm.frm');
  const sourceUri = Uri.file(sourcePath);
  const sourceDocument = await workspace.openTextDocument(sourceUri);
  await window.showTextDocument(sourceDocument);

  const edit = await requestRename(
    sourceUri,
    new Position(4, 'Attribute VB_Name = "'.length),
    'MIXEDCASEFORM'
  );
  assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);

  const entries = await waitForEntries(
    outsideRoot,
    ['MIXEDCASEFORM.frm', 'MIXEDCASEFORM.frx'],
    ['MixedCaseForm.frm', 'mixedcaseform.frx']);
  assert.ok(entries.includes('MIXEDCASEFORM.frm'));
  assert.ok(entries.includes('MIXEDCASEFORM.frx'));
  assert.ok(!entries.includes('MixedCaseForm.frm'));
  assert.ok(!entries.includes('mixedcaseform.frx'));
  assert.match(sourceDocument.getText(), /Begin VB\.UserForm MIXEDCASEFORM/);
  assert.match(sourceDocument.getText(), /OleObjectBlob = "MIXEDCASEFORM\.frx":0000/);
  assert.match(sourceDocument.getText(), /Attribute VB_Name = "MIXEDCASEFORM"/);
  assert.deepEqual(
    Array.from(await workspace.fs.readFile(Uri.file(path.join(
      outsideRoot,
      'MIXEDCASEFORM.frx'
    )))),
    [0x04, 0x05, 0x06, 0x07]
  );
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

async function waitForEntries(
  directoryPath: string,
  expectedNames: readonly string[],
  rejectedNames: readonly string[]
): Promise<string[]> {
  const deadline = Date.now() + 5_000;
  let entries: string[] = [];
  while (Date.now() < deadline) {
    entries = await readEntryNames(directoryPath);
    if (expectedNames.every(name => entries.includes(name))
        && rejectedNames.every(name => !entries.includes(name))) {
      return entries;
    }
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  return entries;
}
