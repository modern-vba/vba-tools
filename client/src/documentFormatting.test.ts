import test from 'node:test';
import assert from 'node:assert/strict';
import type {
  CancellationToken,
  FormattingOptions,
  TextDocument,
  TextEdit,
  TextEditor
} from 'vscode';
import type { DocumentFormattingParams } from 'vscode-languageclient/node';

import {
  VbaDocumentFormattingClient,
  createVbaDocumentFormattingMiddleware
} from './documentFormatting';
import { CodeIndentation, CodeIndentationResult } from './codeIndentation';

test('VBA document formatting sends resolved indentSize to the language server', async () => {
  const uri = 'file:///C:/work/Formatter.bas';
  const document = {
    uri: { toString: () => uri }
  } as unknown as TextDocument;
  const editor = {
    document,
    options: { indentSize: 2 }
  } as unknown as TextEditor;
  const unrelatedEditor = {
    document: { uri: { toString: () => 'file:///C:/work/Other.bas' } },
    options: { indentSize: 8 }
  } as unknown as TextEditor;
  const token = {
    isCancellationRequested: false
  } as CancellationToken;
  const expectedEdits = [{} as TextEdit];
  let requestParameters: DocumentFormattingParams | undefined;
  const languageClient: VbaDocumentFormattingClient = {
    asTextDocumentIdentifier: () => ({ uri }),
    asFormattingOptions: (options: FormattingOptions) => ({
      tabSize: options.tabSize,
      insertSpaces: options.insertSpaces
    }),
    asTextEdits: async () => expectedEdits,
    sendDocumentFormattingRequest: async (parameters: DocumentFormattingParams) => {
      requestParameters = parameters;
      return [];
    },
    handleFailedDocumentFormattingRequest: () => null
  };
  const middleware = createVbaDocumentFormattingMiddleware({
    getLanguageClient: () => languageClient,
    getTextEditors: () => [unrelatedEditor, editor]
  });

  const result = await middleware(
    document,
    { tabSize: 4, insertSpaces: true },
    token,
    () => {
      throw new Error('The default formatting path must not discard indentSize.');
    }
  );

  assert.equal(requestParameters?.options.indentSize, 2);
  assert.strictEqual(result, expectedEdits);
});

test('VBA document formatting omits indentSize without a matching numeric editor option', async () => {
  const uri = 'file:///C:/work/Formatter.bas';
  const document = {
    uri: { toString: () => uri }
  } as unknown as TextDocument;
  const editor = {
    document,
    options: { indentSize: 'tabSize' }
  } as unknown as TextEditor;
  const token = {
    isCancellationRequested: false
  } as CancellationToken;
  let requestParameters: DocumentFormattingParams | undefined;
  const languageClient: VbaDocumentFormattingClient = {
    asTextDocumentIdentifier: () => ({ uri }),
    asFormattingOptions: (options: FormattingOptions) => ({
      tabSize: options.tabSize,
      insertSpaces: options.insertSpaces
    }),
    asTextEdits: async () => [],
    sendDocumentFormattingRequest: async (parameters: DocumentFormattingParams) => {
      requestParameters = parameters;
      return [];
    },
    handleFailedDocumentFormattingRequest: () => null
  };
  const middleware = createVbaDocumentFormattingMiddleware({
    getLanguageClient: () => languageClient,
    getTextEditors: () => [editor]
  });

  await middleware(
    document,
    { tabSize: 4, insertSpaces: true },
    token,
    () => {
      throw new Error('The middleware should use the tabSize fallback request path.');
    }
  );

  assert.equal(requestParameters?.options.tabSize, 4);
  assert.equal('indentSize' in (requestParameters?.options ?? {}), false);
});

for (const visible of [true, false]) {
test(`an immediate ${visible ? 'visible' : 'hidden'} format waits for code-only detection`, async () => {
  const uri = 'file:///work/Immediate.cls';
  const document = { uri: { toString: () => uri }, version: 1,
    languageId: 'vba', isClosed: false } as unknown as TextDocument;
  const editor = { document, options: { tabSize: 2, indentSize: 2, insertSpaces: true } } as unknown as TextEditor;
  let complete!: (result: CodeIndentationResult) => void;
  const indentation = new CodeIndentation({
    getConfiguration: () => ({ detectIndentation: true, tabSize: 4, insertSpaces: true }),
    detect: () => new Promise(resolve => { complete = resolve; })
  });
  let sent: DocumentFormattingParams | undefined;
  const client: VbaDocumentFormattingClient = {
    asTextDocumentIdentifier: () => ({ uri }),
    asFormattingOptions: options => ({ ...options }),
    asTextEdits: async () => [],
    sendDocumentFormattingRequest: async parameters => { sent = parameters; return []; },
    handleFailedDocumentFormattingRequest: () => null
  };
  const middleware = createVbaDocumentFormattingMiddleware({
    getLanguageClient: () => client,
    getTextEditors: () => visible ? [editor] : [],
    ensureIndentation: async () => await indentation.resolve(document, visible ? editor : undefined) ?? false
  });
  const pending = middleware(document, { tabSize: 2, insertSpaces: true },
    { isCancellationRequested: false } as CancellationToken, () => null);
  assert.deepEqual([sent], [undefined]);
  complete({ uri, version: 1, tabSize: 4, indentSize: 4, insertSpaces: true });
  await pending;
  assert.deepEqual(sent?.options, { tabSize: 4, insertSpaces: true, indentSize: 4 });
});
}
