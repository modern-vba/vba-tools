import {
  createConnection,
  InitializeParams,
  InitializeResult,
  ProposedFeatures,
  TextDocumentSyncKind,
  TextDocuments
} from 'vscode-languageserver/node';
import { TextDocument } from 'vscode-languageserver-textdocument';

const connection = createConnection(ProposedFeatures.all);
const documents: TextDocuments<TextDocument> = new TextDocuments(TextDocument);

connection.onInitialize((_params: InitializeParams): InitializeResult => {
  return {
    capabilities: {
      textDocumentSync: TextDocumentSyncKind.Incremental
    }
  };
});

connection.onInitialized((): void => {
  connection.console.log('VBA Language Server initialized.');
});

documents.onDidChangeContent((change): void => {
  connection.sendDiagnostics({
    diagnostics: [],
    uri: change.document.uri
  });
});

documents.onDidClose((event): void => {
  connection.sendDiagnostics({
    diagnostics: [],
    uri: event.document.uri
  });
});

documents.listen(connection);
connection.listen();
