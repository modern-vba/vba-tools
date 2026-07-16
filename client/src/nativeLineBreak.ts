import {
  Disposable,
  Position,
  Range,
  Selection,
  TextDocument,
  TextEditor,
  workspace
} from 'vscode';
import { isNativeLineBreakText } from './nativeLineBreakText';

export interface NativeLineBreakInsertion {
  readonly editor: TextEditor;
  readonly documentVersion: number;
  readonly range: Range;
  readonly cursor: Position;
  readonly text: string;
}

interface RecordedNativeLineBreak {
  readonly document: TextDocument;
  readonly documentVersion: number;
  readonly range: Range;
  readonly cursor: Position;
  readonly text: string;
}

export class NativeLineBreakRecorder implements Disposable {
  private readonly receipts = new Map<string, RecordedNativeLineBreak>();
  private readonly subscription: Disposable;

  public constructor() {
    this.subscription = Disposable.from(
      workspace.onDidChangeTextDocument((event) => {
        if (
          event.document.languageId !== 'vba'
          || event.contentChanges.length !== 1
        ) {
          return;
        }

        const change = event.contentChanges[0];
        if (!change.range.isEmpty || !isNativeLineBreakText(change.text)) {
          return;
        }

        const cursor = positionAtEndOfInsertedText(change.range.start, change.text);
        this.receipts.set(event.document.uri.toString(), {
          document: event.document,
          documentVersion: event.document.version,
          range: new Range(change.range.start, cursor),
          cursor,
          text: change.text
        });
      }),
      workspace.onDidCloseTextDocument((document) => {
        this.receipts.delete(document.uri.toString());
      })
    );
  }

  public consume(editor: TextEditor): NativeLineBreakInsertion | undefined {
    const key = editor.document.uri.toString();
    const receipt = this.receipts.get(key);
    this.receipts.delete(key);
    if (
      receipt === undefined
      || receipt.document !== editor.document
      || receipt.documentVersion !== editor.document.version
      || !editor.document.lineAt(receipt.range.start.line).range.end.isEqual(
        receipt.range.start
      )
      || editor.document.getText(receipt.range) !== receipt.text
      || editor.selections.length !== 1
      || !editor.selection.isEmpty
    ) {
      return undefined;
    }

    if (editor.selection.active.isEqual(receipt.range.start)) {
      editor.selection = new Selection(receipt.cursor, receipt.cursor);
    } else if (!editor.selection.active.isEqual(receipt.cursor)) {
      return undefined;
    }

    return {
      editor,
      documentVersion: receipt.documentVersion,
      range: receipt.range,
      cursor: receipt.cursor,
      text: receipt.text
    };
  }

  public dispose(): void {
    this.receipts.clear();
    this.subscription.dispose();
  }
}

function positionAtEndOfInsertedText(start: Position, text: string): Position {
  const lines = text.split(/\r\n|\r|\n/);
  return lines.length === 1
    ? start.translate(0, text.length)
    : new Position(start.line + lines.length - 1, lines[lines.length - 1].length);
}
