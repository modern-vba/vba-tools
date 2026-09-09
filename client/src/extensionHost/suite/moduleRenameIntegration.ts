import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import * as path from 'node:path';
import {
  Location,
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
import { useVbaRenameWarningHostForTest } from '../../rename';

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
  await verifyConfirmedLocalRenamePreservesTheConflictingDeclaration(outsideRoot);
  await verifyCancelledAndDismissedLocalRenamesLeaveSourceUnchanged(outsideRoot);
  await verifySourceChangedDuringConfirmationKeepsOnlyTheUserEdit(outsideRoot);
  await verifyDeletedDestinationDuringConfirmationDoesNotReplanTheRetainedRename(outsideRoot);
}

async function verifyConfirmedLocalRenamePreservesTheConflictingDeclaration(outsideRoot: string): Promise<void> {
  const uri = Uri.file(path.join(outsideRoot, 'CollisionConfirmation.bas'));
  const original = [
    'Attribute VB_Name = "CollisionConfirmation"',
    'Option Explicit',
    'Public Sub RenameTarget()',
    '    Dim OldValue As Long',
    '    Dim ExistingValue As Long',
    '    ExistingValue = 42',
    '    OldValue = ExistingValue',
    '    Debug.Print OldValue',
    "    ' OldValue remains in this comment.",
    'End Sub',
    ''
  ].join('\r\n');
  await workspace.fs.writeFile(uri, Buffer.from(original, 'utf8'));
  const document = await workspace.openTextDocument(uri);
  await window.showTextDocument(document);
  const position = new Position(3, 9);
  await waitForRenameReferences(uri, position, [3, 6, 7]);

  let promptCount = 0;
  const warningHost = useVbaRenameWarningHostForTest(async (message, options, ...items) => {
    promptCount += 1;
    assert.match(message, /OldValue/);
    assert.match(message, /ExistingValue/);
    assert.equal(options.modal, true);
    assert.match(options.detail, /CollisionConfirmation\.bas:5:/);
    assert.match(options.detail, /manual/i);
    return items.find(item => item.title === 'Continue once');
  });
  try {
    const edit = await commands.executeCommand<WorkspaceEdit | undefined>(
      'vscode.executeDocumentRenameProvider', uri, position, 'ExistingValue');
    assert.ok(edit, 'Confirmed Rename must return the complete edit.');
    assert.equal(promptCount, 1);
    assert.equal(await workspace.applyEdit(edit, { isRefactoring: true }), true);
    assert.equal(document.getText(), [
      'Attribute VB_Name = "CollisionConfirmation"',
      'Option Explicit',
      'Public Sub RenameTarget()',
      '    Dim ExistingValue As Long',
      '    Dim ExistingValue As Long',
      '    ExistingValue = 42',
      '    ExistingValue = ExistingValue',
      '    Debug.Print ExistingValue',
      "    ' OldValue remains in this comment.",
      'End Sub',
      ''
    ].join('\r\n'));
    assert.deepEqual(Buffer.from(await workspace.fs.readFile(uri)), Buffer.from(original, 'utf8'));
  } finally {
    warningHost.dispose();
    await window.showTextDocument(document);
    await commands.executeCommand('workbench.action.files.revert');
  }
}

async function waitForRenameReferences(uri: Uri, position: Position, expectedLines: readonly number[]): Promise<void> {
  const deadline = Date.now() + 15_000;
  let references: Location[] | undefined;
  while (Date.now() < deadline) {
    references = await commands.executeCommand<Location[]>(
      'vscode.executeReferenceProvider', uri, position);
    const lines = references?.filter(reference => reference.uri.toString() === uri.toString())
      .map(reference => reference.range.start.line).sort((left, right) => left - right);
    if (JSON.stringify(lines) === JSON.stringify(expectedLines)) {
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  assert.fail(`The VBA reference provider did not resolve the original Rename occurrences: ${JSON.stringify(references)}`);
}

async function verifyCancelledAndDismissedLocalRenamesLeaveSourceUnchanged(outsideRoot: string): Promise<void> {
  const uri = Uri.file(path.join(outsideRoot, 'CancelledCollision.bas'));
  const original = [
    'Attribute VB_Name = "CancelledCollision"',
    'Option Explicit',
    'Public Sub RenameTarget()',
    '    Dim OldValue As Long',
    '    Dim ExistingValue As Long',
    '    ExistingValue = 42',
    '    OldValue = ExistingValue',
    '    Debug.Print OldValue',
    'End Sub',
    ''
  ].join('\r\n');
  await workspace.fs.writeFile(uri, Buffer.from(original, 'utf8'));
  const document = await workspace.openTextDocument(uri);
  await window.showTextDocument(document);
  const position = new Position(3, 9);
  await waitForRenameReferences(uri, position, [3, 6, 7]);

  for (const choice of ['Cancel', undefined]) {
    const prompts: Array<{ message: string; modal: boolean; choices: string[] }> = [];
    const warningHost = useVbaRenameWarningHostForTest(async (message, options, ...items) => {
      prompts.push({ message, modal: options.modal, choices: items.map(item => item.title) });
      return items.find(item => item.title === choice);
    });
    try {
      // VS Code maps a provider's no-edit result to a rejected command.
      const edit = await commands.executeCommand<WorkspaceEdit | undefined>(
        'vscode.executeDocumentRenameProvider', uri, position, 'ExistingValue'
      ).then(result => result, () => undefined);
      assert.equal(edit, undefined, `${choice ?? 'Dismissal'} must not return an edit.`);
      assert.equal(prompts.length, 1, 'Every operation must request its own consent.');
      assert.match(prompts[0].message, /OldValue/);
      assert.match(prompts[0].message, /ExistingValue/);
      assert.equal(prompts[0].modal, true);
      assert.deepEqual(prompts[0].choices, ['Cancel', 'Continue once']);
      assert.equal(document.getText(), original);
      assert.equal(document.isDirty, false);
      assert.deepEqual(Buffer.from(await workspace.fs.readFile(uri)), Buffer.from(original, 'utf8'));
    } finally {
      warningHost.dispose();
    }
  }
}

async function verifySourceChangedDuringConfirmationKeepsOnlyTheUserEdit(outsideRoot: string): Promise<void> {
  const uri = Uri.file(path.join(outsideRoot, 'ChangedCollision.bas'));
  const original = [
    'Attribute VB_Name = "ChangedCollision"',
    'Option Explicit',
    'Public Sub RenameTarget()',
    '    Dim OldValue As Long',
    '    Dim ExistingValue As Long',
    '    ExistingValue = 42',
    '    OldValue = ExistingValue',
    '    Debug.Print OldValue',
    'End Sub',
    ''
  ].join('\r\n');
  const insertedText = '    Debug.Print OldValue\r\n';
  const changed = original.replace('End Sub\r\n', `${insertedText}End Sub\r\n`);
  await workspace.fs.writeFile(uri, Buffer.from(original, 'utf8'));
  const document = await workspace.openTextDocument(uri);
  await window.showTextDocument(document);
  const position = new Position(3, 9);
  await waitForRenameReferences(uri, position, [3, 6, 7]);

  let promptCount = 0;
  let userEditApplied = false;
  let changedReferencesResolved = false;
  const warningHost = useVbaRenameWarningHostForTest(async (_message, _options, ...items) => {
    promptCount += 1;
    if (promptCount > 1) {
      return items.find(item => item.title === 'Cancel');
    }
    const userEdit = new WorkspaceEdit();
    userEdit.insert(uri, new Position(8, 0), insertedText);
    userEditApplied = await workspace.applyEdit(userEdit);
    await waitForRenameReferences(uri, position, [3, 6, 7, 8]);
    changedReferencesResolved = true;
    return items.find(item => item.title === 'Continue once');
  });
  try {
    let rejection: unknown;
    const edit = await commands.executeCommand<WorkspaceEdit | undefined>(
      'vscode.executeDocumentRenameProvider', uri, position, 'ExistingValue'
    ).then(result => result, error => { rejection = error; return undefined; });
    assert.equal(edit, undefined, 'Changed confirmation evidence must not return a Rename edit.');
    assert.equal(promptCount, 1, 'A changed source must not trigger another Rename or confirmation.');
    assert.equal(userEditApplied, true);
    assert.equal(changedReferencesResolved, true, 'The server must observe the user edit before confirmation.');
    assert.ok(rejection instanceof Error);
    assert.match(rejection.message, /participating source changed/i);
    assert.equal(document.getText(), changed);
    assert.equal(document.isDirty, true);
    assert.deepEqual(Buffer.from(await workspace.fs.readFile(uri)), Buffer.from(original, 'utf8'));
  } finally {
    warningHost.dispose();
    await window.showTextDocument(document);
    await commands.executeCommand('workbench.action.files.revert');
  }
}

async function verifyDeletedDestinationDuringConfirmationDoesNotReplanTheRetainedRename(outsideRoot: string): Promise<void> {
  const uri = Uri.file(path.join(outsideRoot, 'RetainedSource.bas'));
  const destinationUri = Uri.file(path.join(outsideRoot, 'RetainedDestination.bas'));
  const original = [
    'Attribute VB_Name = "RetainedSource"',
    'Option Explicit',
    'Public Sub ProbeRetainedSource()',
    '    Dim marker As Long',
    '    marker = 1',
    'End Sub',
    ''
  ].join('\r\n');
  await workspace.fs.writeFile(uri, Buffer.from(original, 'utf8'));
  await workspace.fs.writeFile(destinationUri, Buffer.from(
    'Attribute VB_Name = "UnrelatedDestinationIdentity"\r\nOption Explicit\r\n', 'utf8'));
  const document = await workspace.openTextDocument(uri);
  await window.showTextDocument(document);
  await waitForRenameReferences(uri, new Position(3, 9), [3, 4]);

  let promptCount = 0;
  let warningMessage = '';
  let warningDetail = '';
  let destinationDeleted = false;
  const warningHost = useVbaRenameWarningHostForTest(async (message, options, ...items) => {
    promptCount += 1;
    if (promptCount > 1) {
      return items.find(item => item.title === 'Cancel');
    }
    warningMessage = message;
    warningDetail = options.detail;
    await workspace.fs.delete(destinationUri, { recursive: false, useTrash: false });
    destinationDeleted = true;
    return items.find(item => item.title === 'Continue once');
  });
  try {
    let rejection: unknown;
    const edit = await commands.executeCommand<WorkspaceEdit | undefined>(
      'vscode.executeDocumentRenameProvider', uri,
      new Position(0, 'Attribute VB_Name = "'.length), 'RetainedDestination'
    ).then(result => result, error => { rejection = error; return undefined; });
    assert.equal(edit, undefined, 'A changed destination decision must not return a Rename edit.');
    assert.equal(promptCount, 1, 'Removing the destination must require a new user action.');
    assert.match(warningMessage, /RetainedSource/);
    assert.match(warningMessage, /RetainedDestination/);
    assert.match(warningDetail, /RetainedSource\.bas/);
    assert.match(warningDetail, /retained/i);
    assert.equal(destinationDeleted, true);
    assert.ok(rejection instanceof Error);
    assert.match(rejection.message, /destination.*changed/i);
    assert.equal(document.uri.toString(), uri.toString());
    assert.equal(document.getText(), original);
    assert.equal(document.isDirty, false);
    assert.deepEqual(Buffer.from(await workspace.fs.readFile(uri)), Buffer.from(original, 'utf8'));
    const entries = await readEntryNames(outsideRoot);
    assert.ok(entries.includes('RetainedSource.bas'));
    assert.ok(!entries.includes('RetainedDestination.bas'));
  } finally {
    warningHost.dispose();
  }
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
