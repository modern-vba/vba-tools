import { Disposable, TextDocument, Uri, window, workspace } from 'vscode';
import { CodeIndentation, CodeIndentationOptions, CodeIndentationRequest, CodeIndentationResult } from './codeIndentation';

export interface CodeIndentationController extends Disposable {
  ensure(document: TextDocument): Promise<boolean | CodeIndentationOptions>;
  refresh(): void;
}

export function registerCodeIndentation(
  detect: (request: CodeIndentationRequest) => Promise<CodeIndentationResult | null>,
  isAvailable: () => boolean
): CodeIndentationController {
  const indentation = new CodeIndentation({
    getConfiguration: document => {
      const configured = workspace.getConfiguration('editor', {
        uri: Uri.parse(document.uri.toString()),
        languageId: document.languageId
      });
      const indentSize = configured.get<number | string>('indentSize', 'tabSize');
      return {
        detectIndentation: configured.get<boolean>('detectIndentation', true),
        tabSize: configured.get<number>('tabSize', 4),
        insertSpaces: configured.get<boolean>('insertSpaces', true),
        ...(typeof indentSize === 'number' ? { indentSize } : {})
      };
    },
    detect
  });
  const ensure = async (document: TextDocument): Promise<boolean | CodeIndentationOptions> => {
    if (document.languageId !== 'vba') {
      return true;
    }
    if (document.isClosed) {
      return false;
    }
    const editor = window.visibleTextEditors.find(candidate => candidate.document === document);
    if (editor !== undefined) {
      indentation.observeEditor(editor);
    }
    if (!isAvailable()) {
      return false;
    }
    try {
      return await indentation.resolve(document, editor) ?? false;
    } catch {
      // A failed detection must not let formatting use a header-derived width.
      return false;
    }
  };
  const refresh = (): void => {
    for (const editor of window.visibleTextEditors) {
      void ensure(editor.document);
    }
  };
  const subscriptions = [
    window.onDidChangeVisibleTextEditors(refresh),
    window.onDidChangeTextEditorOptions(event => indentation.observeOptions(event.textEditor)),
    workspace.onDidCloseTextDocument(document => indentation.close(document)),
    workspace.onDidChangeConfiguration(event => {
      for (const editor of window.visibleTextEditors) {
        const scope = { uri: editor.document.uri, languageId: editor.document.languageId };
        if (['detectIndentation', 'tabSize', 'indentSize', 'insertSpaces'].some(
          key => event.affectsConfiguration(`editor.${key}`, scope)
        )) {
          void ensure(editor.document);
        }
      }
    })
  ];
  refresh();
  return {
    ensure,
    refresh,
    dispose: () => {
      indentation.dispose();
      subscriptions.forEach(subscription => subscription.dispose());
    }
  };
}
