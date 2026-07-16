import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  EndOfLine,
  ConfigurationTarget,
  Position,
  Selection,
  TextDocument,
  Uri,
  commands,
  extensions,
  languages,
  window,
  workspace
} from 'vscode';
import {
  BlockSkeletonInsertionPlan,
  BlockSkeletonInsertionRequest,
  getBlockSkeletonInsertionPlanProvider,
  useBlockSkeletonInsertionPlanProviderForTest
} from '../../blockSkeletonInsertion';

const afterNativeEnterCommand =
  'vbaTools.blockSkeletonInsertion.afterNativeEnter';

export async function runGuardedEnterFeasibilityTests(): Promise<void> {
  await runTest(
    'guarded Enter activates the VBA extension and delegates once to native Enter',
    async () => {
      const extension = extensions.getExtension('modern-vba.vba-tools');
      assert.ok(extension, 'The VBA Tools extension must be installed in the Extension Host.');
      let requests = 0;
      let provider: { dispose(): void } | undefined;

      const document = await workspace.openTextDocument({
        language: 'vba',
        content: '    Public Sub Main()'
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await waitFor(() => extension.isActive);
      provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        requests += 1;
        return {
          response: Promise.resolve(null),
          cancel: () => undefined
        };
      });
      await delay(50);
      const selectionEvents: string[] = [];
      const selectionSubscription = window.onDidChangeTextEditorSelection((event) => {
        if (event.textEditor.document === document) {
          selectionEvents.push(formatPosition(event.selections[0]?.active));
        }
      });

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        await waitFor(
          () => (
            document.getText() === `    Public Sub Main()${lineEnding}    `
            && (window.activeTextEditor?.selection.active.isEqual(new Position(1, 4)) ?? false)
          ),
          1_000,
          () => [
            `captured editor: ${formatPosition(editor.selection.active)}`,
            `active editor: ${formatPosition(window.activeTextEditor?.selection.active)}`,
            `selection events: ${selectionEvents.join(', ') || '(none)'}`
          ].join('; ')
        );
        assert.equal(extension.isActive, true);
        assert.ok(
          (await commands.getCommands()).includes(afterNativeEnterCommand),
          `${afterNativeEnterCommand} must be registered after activation.`
        );
        assert.equal(requests, 1);
        assert.deepEqual(window.activeTextEditor?.selection.active, new Position(1, 4));
      } finally {
        selectionSubscription.dispose();
        provider?.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a synthetic plan places the body cursor and one Undo restores the pre-Enter state',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const initialDocumentVersion = document.version;
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      const literalTerminator = "End Sub '$1 }";
      let request: {
        documentVersion: number;
        position: { line: number; character: number };
      } | undefined;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response: Promise.resolve({
            documentVersion: candidate.documentVersion,
            position: candidate.position,
            textBeforeCursor: `${lineEnding}    `,
            textAfterCursor: `${lineEnding}${literalTerminator}`
          }),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        const expectedText = [
          'Public Sub Main()',
          '    ',
          literalTerminator
        ].join(lineEnding);
        await waitFor(
          () => (
            document.getText() === expectedText
            && editor.selection.active.isEqual(new Position(1, 4))
          )
        );
        assert.equal(document.getText(), expectedText);
        assert.deepEqual(editor.selection.active, new Position(1, 4));
        assert.ok(request);
        assert.equal(request.documentVersion, initialDocumentVersion + 1);
        assert.deepEqual(request.position, {
          line: 0,
          character: originalText.length
        });

        await commands.executeCommand('undo');
        assert.equal(document.getText(), originalText);
        assert.deepEqual(editor.selection.active, end);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'the production language server inserts a Sub skeleton at EOF',
    async () => {
      const originalText = 'Public Sub Main()';
      const documentUri = Uri.file(join(
        tmpdir(),
        `vba-tools-block-skeleton-${randomUUID()}.bas`
      ));
      let fileCreated = false;
      let openedDocument: TextDocument | undefined;
      let observer: { dispose(): void } | undefined;
      try {
        await workspace.fs.writeFile(documentUri, Buffer.from(originalText, 'utf8'));
        fileCreated = true;
        const document = await workspace.openTextDocument(documentUri);
        openedDocument = document;
        assert.equal(document.languageId, 'vba');
        const editor = await window.showTextDocument(document);
        editor.options = {
          insertSpaces: true,
          tabSize: 4,
          indentSize: 2
        };
        const end = document.lineAt(0).range.end;
        editor.selection = new Selection(end, end);
        await commands.executeCommand('workbench.action.focusActiveEditorGroup');
        await waitFor(
          () => languages.getDiagnostics(document.uri).some(
            (diagnostic) => diagnostic.code === 'syntax.missingBlockTerminator'
          ),
          5_000,
          () => `diagnostics=${JSON.stringify(languages.getDiagnostics(document.uri))}`
        );
        const productionProvider = getBlockSkeletonInsertionPlanProvider();
        const warmup = productionProvider({
          documentUri: document.uri.toString(),
          documentVersion: document.version,
          position: {
            line: end.line,
            character: end.character
          },
          options: {
            insertSpaces: true,
            indentSize: 2,
            tabSize: 4
          }
        });
        await warmup.response;
        let capturedRequest: BlockSkeletonInsertionRequest | undefined;
        let capturedResponse: BlockSkeletonInsertionPlan | null | undefined;
        let capturedError: unknown;
        let cancellationCount = 0;
        observer = useBlockSkeletonInsertionPlanProviderForTest((request) => {
          capturedRequest = request;
          const pending = productionProvider(request);
          return {
            response: pending.response.then(
              (response) => {
                capturedResponse = response;
                return response;
              },
              (error) => {
                capturedError = error;
                throw error;
              }
            ),
            cancel: () => {
              cancellationCount++;
              pending.cancel();
            }
          };
        });

        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        const expectedText = [
          originalText,
          '  ',
          'End Sub'
        ].join(lineEnding);
        await waitFor(
          () => document.getText() === expectedText
            && editor.selection.active.isEqual(new Position(1, 2)),
          2_000,
          () => [
            `text=${JSON.stringify(document.getText())}`,
            `cursor=${formatPosition(editor.selection.active)}`,
            `request=${JSON.stringify(capturedRequest)}`,
            `response=${JSON.stringify(capturedResponse)}`,
            `error=${String(capturedError)}`,
            `cancellations=${cancellationCount}`
          ].join('; ')
        );
        assert.equal(capturedError, undefined);
        assert.equal(cancellationCount, 0);

        await commands.executeCommand('undo');
        await waitFor(() => document.getText() === originalText);
        assert.equal(document.getText(), originalText);
      } finally {
        observer?.dispose();
        try {
          if (
            openedDocument !== undefined
            && window.activeTextEditor?.document !== openedDocument
          ) {
            await window.showTextDocument(openedDocument);
          }
          if (openedDocument?.isDirty) {
            await commands.executeCommand('workbench.action.files.revert');
          }
          if (window.activeTextEditor?.document === openedDocument) {
            await commands.executeCommand('workbench.action.closeActiveEditor');
          }
        } finally {
          if (fileCreated) {
            await workspace.fs.delete(documentUri, { useTrash: false });
          }
        }
      }
    }
  );

  await runTest(
    'rapid text after Enter remains after the native fallback newline',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let resolveRequestStarted: (() => void) | undefined;
      const requestStarted = new Promise<void>((resolve) => {
        resolveRequestStarted = resolve;
      });
      let resolvePlan: ((plan: null) => void) | undefined;
      const pendingPlan = new Promise<null>((resolve) => {
        resolvePlan = resolve;
      });
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        resolveRequestStarted?.();
        return {
          response: pendingPlan,
          cancel: () => undefined
        };
      });
      let enter: Thenable<unknown> | undefined;

      try {
        enter = executeGuardedEnter();
        await requestStarted;
        await commands.executeCommand('type', { text: 'x' });

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        assert.equal(document.getText(), `${originalText}${lineEnding}x`);
        assert.deepEqual(editor.selection.active, new Position(1, 1));

        resolvePlan?.(null);
        await enter;
        await delay(50);
        assert.equal(document.getText(), `${originalText}${lineEnding}x`);
      } finally {
        resolvePlan?.(null);
        await enter?.then(undefined, () => undefined);
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a disabled resource setting bypasses planning and keeps native indentation',
    async () => {
      const originalText = '    Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const configuration = workspace.getConfiguration(
        'vbaLanguageServer.blockSkeletonInsertion',
        document.uri
      );
      await configuration.update('enabled', false, ConfigurationTarget.Global);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((request) => {
        requests += 1;
        return {
          response: Promise.resolve({
            documentVersion: request.documentVersion,
            position: request.position,
            textBeforeCursor: '\n        ',
            textAfterCursor: '\n    End Sub'
          }),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        const expectedText = `${originalText}${lineEnding}    `;
        await waitFor(() => document.getText() === expectedText);
        assert.equal(requests, 0);
        assert.deepEqual(editor.selection.active, new Position(1, 4));
      } finally {
        provider.dispose();
        await configuration.update('enabled', undefined, ConfigurationTarget.Global);
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'accepted-plan application racing with text never loses or reorders the text',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: {
        documentVersion: number;
        position: { line: number; character: number };
      } | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();
        await waitFor(() => request !== undefined);
        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}    `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await commands.executeCommand('type', { text: 'x' });
        await delay(150);

        const text = document.getText();
        assert.ok(
          text === `${originalText}${lineEnding}x`
          || text === [originalText, '    x', 'End Sub'].join(lineEnding),
          `Unexpected race result: ${JSON.stringify(text)}`
        );
        assert.equal(countOccurrences(text, 'x'), 1);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a cancelled transaction keeps native Enter and ignores a late accepted plan',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let resolveCancellation: (() => void) | undefined;
      const cancellation = new Promise<void>((resolve) => {
        resolveCancellation = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((request) => ({
        response,
        cancellation,
        cancel: () => {
          cancellations += 1;
        }
      }));

      try {
        await executeGuardedEnter();
        await waitFor(() => document.getText() === `${originalText}${lineEnding}`);

        resolveCancellation?.();
        await delay(10);
        assert.equal(cancellations, 1);
        resolvePlan?.({
          documentVersion: 1,
          position: { line: 0, character: originalText.length },
          textBeforeCursor: `${lineEnding}    `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await delay(50);

        assert.equal(document.getText(), `${originalText}${lineEnding}`);
        assert.equal(document.getText().includes('End Sub'), false);
      } finally {
        resolveCancellation?.();
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'changing effective editor indentation invalidates a pending plan',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      editor.options = {
        insertSpaces: true,
        tabSize: 4,
        indentSize: 2
      };
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: BlockSkeletonInsertionRequest | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => {
            cancellations += 1;
          }
        };
      });
      let resolveOptionsChanged: (() => void) | undefined;
      const optionsChanged = new Promise<void>((resolve) => {
        resolveOptionsChanged = resolve;
      });
      const optionsSubscription = window.onDidChangeTextEditorOptions((event) => {
        if (event.textEditor === editor) {
          resolveOptionsChanged?.();
        }
      });

      try {
        await executeGuardedEnter();
        await waitFor(() => request !== undefined);
        editor.options = {
          insertSpaces: true,
          tabSize: 4,
          indentSize: 3
        };
        await optionsChanged;

        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}  `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await waitFor(() => cancellations === 1);

        assert.equal(document.getText(), `${originalText}${lineEnding}`);
        assert.equal(document.getText().includes('End Sub'), false);
      } finally {
        optionsSubscription.dispose();
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a timed-out transaction keeps one native Enter and ignores a late accepted plan',
    async () => {
      const originalText = '    Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: { documentVersion: number; position: { line: number; character: number } } | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => {
            cancellations += 1;
          }
        };
      });

      try {
        await executeGuardedEnter();
        const expectedText = `${originalText}${lineEnding}    `;
        await waitFor(() => document.getText() === expectedText);
        await waitFor(() => cancellations === 1, 500);

        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}        `,
          textAfterCursor: `${lineEnding}    End Sub`
        });
        await delay(50);

        assert.equal(document.getText(), expectedText);
        assert.equal(cancellations, 1);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a stale accepted plan cannot replace native Enter after the document changes',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: { documentVersion: number; position: { line: number; character: number } } | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => {
            cancellations += 1;
          }
        };
      });

      try {
        await executeGuardedEnter();
        await waitFor(() => document.getText() === `${originalText}${lineEnding}`);
        await commands.executeCommand('type', { text: 'x' });

        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}    `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await waitFor(() => cancellations === 1);

        assert.equal(document.getText(), `${originalText}${lineEnding}x`);
        assert.equal(document.getText().includes('End Sub'), false);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a failed plan request leaves exactly one native indented newline',
    async () => {
      const originalText = '    Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => ({
        response: Promise.reject(new Error('synthetic request failure')),
        cancel: () => undefined
      }));

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        const expectedText = `${originalText}${lineEnding}    `;
        await waitFor(() => document.getText() === expectedText);
        assert.equal(countOccurrences(document.getText(), lineEnding), 1);
        assert.deepEqual(editor.selection.active, new Position(1, 4));
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a non-VBA editor bypasses planning and retains native Enter',
    async () => {
      const originalText = 'plain text';
      const document = await workspace.openTextDocument({
        language: 'plaintext',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        requests += 1;
        return {
          response: Promise.resolve(null),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        await waitFor(() => document.getText() === `${originalText}${lineEnding}`);
        assert.equal(requests, 0);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a selection bypasses planning and is replaced by native Enter',
    async () => {
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: 'abc'
      });
      const editor = await window.showTextDocument(document);
      editor.selection = new Selection(new Position(0, 1), new Position(0, 2));
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        requests += 1;
        return {
          response: Promise.resolve(null),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        await waitFor(() => document.getText() === `a${lineEnding}c`);
        assert.equal(requests, 0);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'multiple cursors bypass planning and retain native Enter at every cursor',
    async () => {
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: 'one\ntwo'
      });
      const editor = await window.showTextDocument(document);
      editor.selections = [
        new Selection(new Position(0, 3), new Position(0, 3)),
        new Selection(new Position(1, 3), new Position(1, 3))
      ];
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        requests += 1;
        return {
          response: Promise.resolve(null),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        await waitFor(() => document.lineCount === 4);
        assert.equal(requests, 0);
        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        assert.equal(countOccurrences(document.getText(), lineEnding), 3);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a cursor before the physical line end bypasses planning and retains native Enter',
    async () => {
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: 'abc'
      });
      const editor = await window.showTextDocument(document);
      editor.selection = new Selection(new Position(0, 1), new Position(0, 1));
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest(() => {
        requests += 1;
        return {
          response: Promise.resolve(null),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();

        const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
        await waitFor(() => document.getText() === `a${lineEnding}bc`);
        assert.equal(requests, 0);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'a read-only VBA editor cannot be changed by the guarded Enter command',
    async () => {
      const scheme = `guarded-enter-readonly-${Date.now()}`;
      const content = 'Public Sub Main()';
      const contentProvider = workspace.registerTextDocumentContentProvider(scheme, {
        provideTextDocumentContent: () => content
      });
      const openedDocument = await workspace.openTextDocument(Uri.parse(`${scheme}:/Main.bas`));
      const document = await languages.setTextDocumentLanguage(openedDocument, 'vba');
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      let requests = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((request) => {
        requests += 1;
        return {
          response: Promise.resolve({
            documentVersion: request.documentVersion,
            position: request.position,
            textBeforeCursor: '\n    ',
            textAfterCursor: '\nEnd Sub'
          }),
          cancel: () => undefined
        };
      });

      try {
        await executeGuardedEnter();
        await delay(50);

        assert.equal(document.getText(), content);
        assert.equal(requests, 0);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
        contentProvider.dispose();
      }
    }
  );

  await runTest(
    'moving the cursor away and back permanently invalidates a pending plan',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: { documentVersion: number; position: { line: number; character: number } } | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => {
            cancellations += 1;
          }
        };
      });

      try {
        await executeGuardedEnter();
        await waitFor(() => request !== undefined);
        const nativeCursor = new Position(1, 0);
        editor.selection = new Selection(end, end);
        await delay(0);
        editor.selection = new Selection(nativeCursor, nativeCursor);

        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}    `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await waitFor(() => cancellations === 1);

        assert.equal(document.getText(), `${originalText}${lineEnding}`);
        assert.equal(document.getText().includes('End Sub'), false);
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeActiveEditor');
      }
    }
  );

  await runTest(
    'switching editors away and back permanently invalidates a pending plan',
    async () => {
      const originalText = 'Public Sub Main()';
      const document = await workspace.openTextDocument({
        language: 'vba',
        content: originalText
      });
      const editor = await window.showTextDocument(document);
      const end = document.lineAt(0).range.end;
      editor.selection = new Selection(end, end);
      await commands.executeCommand('workbench.action.focusActiveEditorGroup');
      await delay(50);
      const lineEnding = document.eol === EndOfLine.CRLF ? '\r\n' : '\n';
      let request: { documentVersion: number; position: { line: number; character: number } } | undefined;
      let resolvePlan: ((plan: BlockSkeletonInsertionPlan) => void) | undefined;
      const response = new Promise<BlockSkeletonInsertionPlan>((resolve) => {
        resolvePlan = resolve;
      });
      let cancellations = 0;
      const provider = useBlockSkeletonInsertionPlanProviderForTest((candidate) => {
        request = candidate;
        return {
          response,
          cancel: () => {
            cancellations += 1;
          }
        };
      });

      try {
        await executeGuardedEnter();
        await waitFor(() => document.getText() === `${originalText}${lineEnding}`);
        const otherDocument = await workspace.openTextDocument({
          language: 'plaintext',
          content: 'other editor'
        });
        await window.showTextDocument(otherDocument);
        await window.showTextDocument(document);

        assert.ok(request);
        resolvePlan?.({
          documentVersion: request.documentVersion,
          position: request.position,
          textBeforeCursor: `${lineEnding}    `,
          textAfterCursor: `${lineEnding}End Sub`
        });
        await waitFor(() => cancellations === 1);

        assert.equal(document.getText(), `${originalText}${lineEnding}`);
        assert.equal(otherDocument.getText(), 'other editor');
      } finally {
        provider.dispose();
        await commands.executeCommand('workbench.action.closeAllEditors');
      }
    }
  );
}

async function waitFor(
  condition: () => boolean,
  timeoutMilliseconds = 1_000,
  describeState: () => string = () => ''
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (!condition()) {
    if (Date.now() >= deadline) {
      throw new Error(
        `Condition was not met within ${timeoutMilliseconds} ms. ${describeState()}`.trim()
      );
    }

    await delay(10);
  }
}

function executeGuardedEnter(): Thenable<unknown> {
  return commands.executeCommand('runCommands', {
    commands: [
      'lineBreakInsert',
      afterNativeEnterCommand
    ]
  });
}

function formatPosition(position: Position | undefined): string {
  return position === undefined ? '(none)' : `(${position.line}, ${position.character})`;
}

function countOccurrences(text: string, value: string): number {
  return text.split(value).length - 1;
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
