import {
  Disposable,
  Position,
  SnippetString,
  TextEditor,
  window,
  workspace
} from 'vscode';
import {
  BlockSkeletonInsertionPlan,
  BlockSkeletonInsertionRequest,
  getBlockSkeletonInsertionPlanProvider,
  resolveBlockSkeletonInsertionOptions
} from './blockSkeletonInsertion';
import {
  NativeLineBreakInsertion,
  NativeLineBreakRecorder
} from './nativeLineBreak';
import { isPostNativeRequestVersion } from './nativeLineBreakText';
import {
  BlockSkeletonInsertionTransactionResult,
  runBlockSkeletonInsertionTransaction
} from './blockSkeletonInsertionTransaction';

const requestTimeoutMilliseconds = 100;

export async function runBlockSkeletonInsertionAfterNativeEnter(
  recorder: NativeLineBreakRecorder
): Promise<BlockSkeletonInsertionTransactionResult | undefined> {
  const editor = window.activeTextEditor;
  if (editor === undefined) {
    return;
  }

  const nativeInsertion = recorder.consume(editor);
  if (nativeInsertion === undefined) {
    return;
  }

  const request = captureCandidate(editor, nativeInsertion);
  if (request === undefined) {
    return;
  }

  const invalidation = observeCandidateInvalidation(editor, nativeInsertion);
  try {
    return await runBlockSkeletonInsertionTransaction({
      nativeInsertion,
      beginPlanRequest: () => getBlockSkeletonInsertionPlanProvider()(request),
      isCurrent: (insertion, plan) => isCurrent(
        editor,
        request,
        plan,
        insertion
      ),
      applyPlan: (insertion, plan) => {
        invalidation.dispose();
        return applyPlan(editor, insertion, plan);
      },
      timeoutMilliseconds: requestTimeoutMilliseconds,
      cancellation: invalidation.promise
    });
  } finally {
    invalidation.dispose();
  }
}

async function applyPlan(
  editor: TextEditor,
  nativeInsertion: NativeLineBreakInsertion,
  plan: BlockSkeletonInsertionPlan
): Promise<boolean> {
  const snippet = new SnippetString()
    .appendText(plan.textBeforeCursor)
    .appendTabstop(0)
    .appendText(plan.textAfterCursor);
  return editor.insertSnippet(
    snippet,
    nativeInsertion.range,
    {
      undoStopBefore: false,
      undoStopAfter: true,
      keepWhitespace: true
    }
  );
}

function captureCandidate(
  editor: TextEditor,
  nativeInsertion: NativeLineBreakInsertion
): BlockSkeletonInsertionRequest | undefined {
  if (
    editor.document.languageId !== 'vba'
    || nativeInsertion.editor !== editor
    || editor.document.version !== nativeInsertion.documentVersion
    || editor.selections.length !== 1
    || !editor.selection.isEmpty
    || !editor.selection.active.isEqual(nativeInsertion.cursor)
    || !editor.document.lineAt(nativeInsertion.cursor.line).range.end.isEqual(
      nativeInsertion.cursor
    )
    || !workspace.getConfiguration(
      'vbaLanguageServer.blockSkeletonInsertion',
      editor.document.uri
    ).get<boolean>('enabled', true)
  ) {
    return undefined;
  }

  const editorConfiguration = workspace.getConfiguration(
    'editor',
    editor.document.uri
  );
  const options = resolveBlockSkeletonInsertionOptions(
    editor.options,
    {
      insertSpaces: editorConfiguration.get<boolean>('insertSpaces'),
      tabSize: editorConfiguration.get<number>('tabSize'),
      indentSize: editorConfiguration.get<number | string>('indentSize')
    }
  );
  if (options === undefined) {
    return undefined;
  }

  return {
    documentUri: editor.document.uri.toString(),
    documentVersion: nativeInsertion.documentVersion,
    position: {
      line: nativeInsertion.range.start.line,
      character: nativeInsertion.range.start.character
    },
    options
  };
}

function isCurrent(
  editor: TextEditor,
  request: BlockSkeletonInsertionRequest,
  plan: BlockSkeletonInsertionPlan,
  nativeInsertion: NativeLineBreakInsertion
): boolean {
  const activeEditor = window.activeTextEditor;
  const position = new Position(request.position.line, request.position.character);
  return activeEditor === editor
    && editor.document.uri.toString() === request.documentUri
    && nativeInsertion.editor === editor
    && nativeInsertion.range.start.isEqual(position)
    && isPostNativeRequestVersion(
      nativeInsertion.documentVersion,
      request.documentVersion
    )
    && editor.document.version === nativeInsertion.documentVersion
    && editor.document.getText(nativeInsertion.range) === nativeInsertion.text
    && editor.selection.isEmpty
    && editor.selections.length === 1
    && editor.selection.active.isEqual(nativeInsertion.cursor)
    && plan.documentVersion === request.documentVersion
    && plan.position.line === request.position.line
    && plan.position.character === request.position.character;
}

function observeCandidateInvalidation(
  editor: TextEditor,
  nativeInsertion: NativeLineBreakInsertion
): { readonly promise: Promise<void>; dispose(): void } {
  let resolveInvalidation: (() => void) | undefined;
  const promise = new Promise<void>((resolve) => {
    resolveInvalidation = resolve;
  });
  let invalidated = false;
  let disposed = false;
  const subscriptions: Disposable[] = [];
  const dispose = (): void => {
    if (disposed) {
      return;
    }

    disposed = true;
    for (const subscription of subscriptions) {
      subscription.dispose();
    }
  };
  const invalidate = (): void => {
    if (invalidated || disposed) {
      return;
    }

    invalidated = true;
    resolveInvalidation?.();
    dispose();
  };

  subscriptions.push(
    workspace.onDidChangeTextDocument((event) => {
      if (event.document === editor.document) {
        invalidate();
      }
    }),
    workspace.onDidCloseTextDocument((document) => {
      if (document === editor.document) {
        invalidate();
      }
    }),
    workspace.onDidChangeConfiguration((event) => {
      if (event.affectsConfiguration(
        'vbaLanguageServer.blockSkeletonInsertion.enabled',
        editor.document.uri
      )) {
        invalidate();
      }
    }),
    window.onDidChangeActiveTextEditor((activeEditor) => {
      if (activeEditor !== editor) {
        invalidate();
      }
    }),
    window.onDidChangeTextEditorSelection((event) => {
      if (
        event.textEditor === editor
        && (
          event.selections.length !== 1
          || !event.selections[0].isEmpty
          || !event.selections[0].active.isEqual(nativeInsertion.cursor)
        )
      ) {
        invalidate();
      }
    }),
    window.onDidChangeTextEditorOptions((event) => {
      if (event.textEditor === editor) {
        invalidate();
      }
    })
  );

  return { promise, dispose };
}
