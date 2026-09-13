import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  ConfigurationTarget,
  Selection,
  TextDocument,
  TextEdit,
  TextEditor,
  Uri,
  WorkspaceEdit,
  commands,
  extensions,
  languages,
  window,
  workspace
} from 'vscode';
import { getBlockSkeletonInsertionPlanProvider } from '../../blockSkeletonInsertion';

const exportedClassText = [
  'VERSION 1.0 CLASS',
  'BEGIN',
  '  MultiUse = -1  \'True',
  'END',
  'Attribute VB_Name = "IToolSettings"',
  'Option Explicit',
  '',
  'Private Sub Class_Initialize()',
  '    Err.Raise Number:=vbObjectError + 1',
  'End Sub',
  ''
].join('\r\n');

export async function run(): Promise<void> {
  await runCodeOnlyIndentationIntegrationTests();
}

export async function runCodeOnlyIndentationIntegrationTests(): Promise<void> {
  const extension = extensions.getExtension('modern-vba.vba-tools');
  assert.ok(extension, 'VBA Tools must be installed in the isolated Extension Host.');
  const initiallyActive = extension.isActive;
  await withVbaEditor(exportedClassText, async (editor) => {
    await extension.activate();
    await waitFor(() => editor.options.indentSize === 4,
      () => JSON.stringify(editor.options));
    assert.equal(editor.options.tabSize, 4);
    assert.equal(editor.document.getText(), exportedClassText);
    assert.equal(editor.document.isDirty, false);
  });
  console.log(`PASS visible class receives read-only detection after ${initiallyActive ? 'warm' : 'cold'} activation`);
  await extension.activate();

  await withVbaEditor(exportedClassText, async (editor) => {
    const document = editor.document;
    // Do not wait for detection before formatting: the first user action must
    // not consume VS Code's initial header-inclusive guess of two spaces.
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(document.getText(), exportedClassText,
      'Immediate Format Document must preserve the header and four-space code.');
    assert.equal(editor.options.tabSize, 4);
    assert.equal(editor.options.indentSize, 4);
    assert.equal(editor.options.insertSpaces, true);

    await editor.edit((edit) => edit.insert(document.lineAt(6).range.end, ' '));
    assert.equal(await document.save(), true);
    assert.equal(document.lineAt(2).text, '  MultiUse = -1  \'True');
    assert.equal(document.lineAt(8).text, '    Err.Raise Number:=vbObjectError + 1');
    assert.equal(editor.options.indentSize, 4);
  });
  console.log('PASS exported class header is excluded from immediate formatting and save indentation');

  await withVbaEditor(exportedClassText, async (editor) => {
    const document = editor.document;
    await waitFor(() => editor.options.indentSize === 4);
    assert.equal(document.getText(), exportedClassText, 'Detection must not edit the source.');
    assert.equal(document.isDirty, false);
    const end = document.lineAt(document.lineCount - 1).range.end;
    editor.selection = new Selection(end, end);
    await commands.executeCommand('workbench.action.focusActiveEditorGroup');
    await commands.executeCommand('tab');
    assert.equal(document.lineAt(10).text, '    ', 'Tab must use the detected code indentation.');

    await editor.edit((edit) => edit.replace(document.lineAt(10).range, 'Public Sub Added()'));
    const headerEnd = document.lineAt(10).range.end;
    editor.selection = new Selection(headerEnd, headerEnd);
    await waitFor(() => languages.getDiagnostics(document.uri).some(
      (diagnostic) => diagnostic.code === 'syntax.missingBlockTerminator'
    ));
    // Match the existing real-server Enter harness: warm its first request
    // before the command's intentional 100 ms fail-closed budget starts.
    await getBlockSkeletonInsertionPlanProvider()({
      documentUri: document.uri.toString(),
      documentVersion: document.version,
      position: { line: headerEnd.line, character: headerEnd.character },
      options: { tabSize: 4, indentSize: 4, insertSpaces: true }
    }).response;
    await commands.executeCommand('runCommands', {
      commands: ['lineBreakInsert', 'vbaTools.blockSkeletonInsertion.afterNativeEnter']
    });
    await waitFor(() => document.lineCount === 13, () => document.getText());
    assert.equal(document.lineAt(11).text, '    ', 'Enter must use the detected code indentation.');
    assert.equal(document.lineAt(12).text, 'End Sub');
    await commands.executeCommand('editor.action.formatDocument');
    const formatted = document.getText();
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(document.getText(), formatted, 'Repeated formatting must remain stable.');
    assert.equal(document.lineAt(2).text, '  MultiUse = -1  \'True');
    assert.equal(editor.options.indentSize, 4);
  });
  console.log('PASS read-only code detection controls native Tab, guarded Enter, and repeated formatting');

  await withVbaEditor(exportedClassText, async (editor) => {
    const document = editor.document;
    await editor.edit((edit) => edit.insert(document.lineAt(6).range.end, ' '));
    assert.equal(await document.save(), true);
    assert.equal(document.lineAt(2).text, '  MultiUse = -1  \'True');
    assert.equal(document.lineAt(8).text, '    Err.Raise Number:=vbObjectError + 1');
    assert.equal(editor.options.tabSize, 4);
    assert.equal(editor.options.indentSize, 4);
    assert.equal(document.isDirty, false);
  });
  console.log('PASS immediate first save uses code indentation after a document version change');

  const twoSpaceCode = exportedClassText.replace('    Err.Raise', '  Err.Raise');
  await withVbaEditor(twoSpaceCode, async (editor) => {
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.options.tabSize, 2);
    assert.equal(editor.options.indentSize, 2);
    assert.equal(editor.document.getText(), twoSpaceCode);
  });
  console.log('PASS actual two-space code retains two-space indentation');

  await withVbaEditor(exportedClassText, async (editor) => {
    await waitFor(() => editor.options.indentSize === 4);
    editor.options = { tabSize: 4, indentSize: 2, insertSpaces: true };
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.document.lineAt(8).text, '  Err.Raise Number:=vbObjectError + 1');
    await editor.edit((edit) => edit.insert(editor.document.lineAt(6).range.end, ' '));
    const otherDocument = await workspace.openTextDocument({ language: 'plaintext', content: '' });
    await window.showTextDocument(otherDocument);
    await commands.executeCommand('workbench.action.closeActiveEditor');
    await window.showTextDocument(editor.document);
    assert.equal(await editor.document.save(), true);
    assert.equal(editor.options.tabSize, 4);
    assert.equal(editor.options.indentSize, 2);
    assert.equal(editor.document.lineAt(2).text, '  MultiUse = -1  \'True');
    assert.equal(editor.document.lineAt(8).text, '  Err.Raise Number:=vbObjectError + 1');
  });
  console.log('PASS manual separate indentation and tab widths survive edits, reactivation, and save');

  await withVbaEditor(exportedClassText, async (editor) => {
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.options.tabSize, 8);
    assert.equal(editor.options.indentSize, 2);
    assert.equal(editor.document.getText(), twoSpaceCode);
  }, {
    'editor.detectIndentation': false,
    'editor.tabSize': 8,
    'editor.indentSize': 2
  });
  console.log('PASS disabled detection uses configured separate widths directly');

  await withVbaEditor(exportedClassText, async (editor) => {
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.options.tabSize, 8);
    assert.equal(editor.options.indentSize, 4);
    assert.equal(editor.document.getText(), exportedClassText);
  }, {
    'editor.tabSize': 8,
    'editor.indentSize': 2
  });
  console.log('PASS detected indentation preserves an independently configured tab display width');

  const emptyClassBody = exportedClassText.replace('    Err.Raise Number:=vbObjectError + 1\r\n', '');
  await withVbaEditor(emptyClassBody, async (editor) => {
    await waitFor(() => editor.options.indentSize === 6);
    assert.equal(editor.options.tabSize, 6);
    assert.equal(editor.document.getText(), emptyClassBody);
    assert.equal(editor.document.isDirty, false);
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.document.getText(), emptyClassBody);
  }, { 'editor.tabSize': 6 });
  console.log('PASS code without indentation evidence uses configured defaults instead of the header guess');

  const tabbedForm = [
    'VERSION 5.00',
    'Begin VB.UserForm Form1',
    '   Caption = "Form1"',
    '   Begin VB.CommandButton CommandButton1',
    '      Caption = "Button"',
    '   End',
    'End',
    'Attribute VB_Name = "Form1"',
    'Option Explicit',
    'Private Sub Probe()',
    '  Attribute Probe.VB_Description = "Sentinel"',
    '\tDebug.Print "Ready"',
    'End Sub',
    ''
  ].join('\r\n');
  await withVbaEditor(tabbedForm, async (editor) => {
    await waitFor(() => editor.options.insertSpaces === false);
    assert.equal(editor.options.insertSpaces, false);
    assert.equal(editor.options.tabSize, 4);
    assert.equal(editor.options.indentSize, 4);
    assert.equal(editor.document.getText(), tabbedForm);
    assert.equal(editor.document.isDirty, false);
    await commands.executeCommand('editor.action.formatDocument');
    assert.equal(editor.document.getText(), tabbedForm.replace(
      '  Attribute Probe.VB_Description', '\tAttribute Probe.VB_Description'
    ));
  }, {}, 'frm');
  console.log('PASS form designer and Attribute indentation do not override tab-indented code');

  const hiddenUnformattedClass = exportedClassText.replace('Option Explicit', 'option explicit');
  await withVbaEditor(hiddenUnformattedClass, async (editor) => {
    const document = editor.document;
    await waitFor(() => editor.options.indentSize === 4);
    const otherDocument = await workspace.openTextDocument({ language: 'plaintext', content: '' });
    await window.showTextDocument(otherDocument);
    try {
      assert.equal(document.isClosed, false);
      assert.equal(window.visibleTextEditors.some(candidate => candidate.document === document), false);
      const edits = await commands.executeCommand<TextEdit[]>(
        'vscode.executeFormatDocumentProvider', document.uri,
        { tabSize: 2, insertSpaces: true }
      );
      assert.ok(Array.isArray(edits), 'The hidden document must have a working formatter.');
      const edit = new WorkspaceEdit();
      edit.set(document.uri, edits);
      assert.equal(await workspace.applyEdit(edit), true);
      assert.equal(document.getText(), exportedClassText);
    } finally {
      if (window.activeTextEditor?.document === otherDocument) {
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  });
  console.log('PASS hidden-document formatting rejects the header-inclusive formatting width');
}

async function withVbaEditor(
  originalText: string,
  body: (editor: TextEditor) => Promise<void>,
  settings: Readonly<Record<string, unknown>> = {},
  fileExtension = 'cls'
): Promise<void> {
  const configuration = workspace.getConfiguration();
  const originalVbaSettings = configuration.inspect<Record<string, unknown>>('[vba]')?.globalValue;
  const documentUri = Uri.file(join(
    tmpdir(),
    `vba-tools-code-indentation-${randomUUID()}.${fileExtension}`
  ));
  let openedDocument: TextDocument | undefined;

  try {
    await configuration.update('[vba]', {
      ...originalVbaSettings,
      'editor.detectIndentation': true,
      'editor.tabSize': 4,
      'editor.indentSize': 'tabSize',
      'editor.insertSpaces': true,
      'editor.defaultFormatter': 'modern-vba.vba-tools',
      'editor.formatOnSave': true,
      ...settings
    }, ConfigurationTarget.Global);
    await workspace.fs.writeFile(documentUri, Buffer.from(originalText, 'utf8'));
    const document = await workspace.openTextDocument(documentUri);
    openedDocument = document;
    assert.equal(document.languageId, 'vba');
    const editor = await window.showTextDocument(document, { preview: false });
    await body(editor);
  } finally {
    if (openedDocument !== undefined) {
      await window.showTextDocument(openedDocument);
      if (openedDocument.isDirty) {
        await commands.executeCommand('workbench.action.files.revert');
      }
      await commands.executeCommand('workbench.action.closeActiveEditor');
    }
    await workspace.fs.delete(documentUri, { useTrash: false });
    await configuration.update('[vba]', originalVbaSettings, ConfigurationTarget.Global);
  }
}

async function waitFor(
  condition: () => boolean,
  describeState: () => string = () => ''
): Promise<void> {
  const deadline = Date.now() + 5_000;
  while (!condition()) {
    assert.ok(Date.now() < deadline,
      `Expected editor state did not settle within five seconds. ${describeState()}`);
    await new Promise<void>((resolve) => setTimeout(resolve, 10));
  }
}
