import {
  createConnection,
  CompletionItem,
  CompletionItemKind,
  Definition,
  Hover,
  InitializeParams,
  InitializeResult,
  InsertTextFormat,
  Location,
  MarkupKind,
  ParameterInformation,
  Position,
  ProposedFeatures,
  Range,
  SignatureHelp,
  SignatureInformation,
  TextEdit,
  TextDocumentSyncKind,
  TextDocuments,
  WorkspaceEdit
} from 'vscode-languageserver/node';
import { TextDocument } from 'vscode-languageserver-textdocument';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import { createDefaultHostCatalogManager } from './hostCatalogService';
import { HostApplicationConfigurationProvider } from './hostApplicationSettings';
import {
  buildVbaProject,
  CompletionEntryKind,
  getCompletions,
  getDefinition,
  getDocumentFormattingEdits,
  getHover,
  getRenameEdits,
  getRenameTarget,
  getSemanticTokens,
  getSignatureHelp,
  RenameEdit,
  SourceRange,
  VBA_SEMANTIC_TOKEN_TYPES,
  VbaSemanticToken,
  VbaProjectFile
} from './vbaProject';

const connection = createConnection(ProposedFeatures.all);
const documents: TextDocuments<TextDocument> = new TextDocuments(TextDocument);
const hostCatalogManager = createDefaultHostCatalogManager();
const hostApplicationConfigurationProvider = new HostApplicationConfigurationProvider((scopeUri) =>
  connection.workspace.getConfiguration({ scopeUri, section: 'vbaLanguageServer' })
);

void hostCatalogManager.refreshFromExcelComAsync();

connection.onInitialize((_params: InitializeParams): InitializeResult => {
  return {
    capabilities: {
      textDocumentSync: TextDocumentSyncKind.Incremental,
      completionProvider: {
        triggerCharacters: ['.', ' ']
      },
      definitionProvider: true,
      hoverProvider: true,
      renameProvider: {
        prepareProvider: true
      },
      documentFormattingProvider: true,
      signatureHelpProvider: {
        triggerCharacters: ['(', ',']
      },
      semanticTokensProvider: {
        legend: {
          tokenTypes: [...VBA_SEMANTIC_TOKEN_TYPES],
          tokenModifiers: []
        },
        full: true
      }
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

connection.onCompletion(async (params): Promise<CompletionItem[]> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return [];
  }

  const project = await buildProjectForDocument(document);
  return getCompletions(project, {
    uri: document.uri,
    position: params.position
  }).map((item) => ({
    label: item.label,
    kind: toLspCompletionItemKind(item.kind),
    detail: item.detail,
    insertText: item.insertText,
    insertTextFormat: item.insertTextFormat === 'snippet' ? InsertTextFormat.Snippet : undefined
  }));
});

connection.languages.semanticTokens.on(async (params) => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return { data: [] };
  }

  const project = await buildProjectForDocument(document);
  return encodeSemanticTokens(getSemanticTokens(project, document.uri));
});

connection.onDefinition(async (params): Promise<Definition | undefined> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return undefined;
  }

  const project = await buildProjectForDocument(document);
  const definition = getDefinition(project, {
    uri: document.uri,
    position: params.position
  });
  if (definition === undefined) {
    return undefined;
  }

  return Location.create(definition.uri, {
    start: toLspPosition(definition.range.start),
    end: toLspPosition(definition.range.end)
  });
});

connection.onPrepareRename(async (params): Promise<Range | undefined> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return undefined;
  }

  const project = await buildProjectForDocument(document);
  const target = getRenameTarget(project, {
    uri: document.uri,
    position: params.position
  });
  return target === undefined ? undefined : toLspRange(target.range);
});

connection.onRenameRequest(async (params): Promise<WorkspaceEdit> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return {};
  }

  const project = await buildProjectForDocument(document);
  const edits = getRenameEdits(
    project,
    {
      uri: document.uri,
      position: params.position
    },
    params.newName
  );

  return toWorkspaceEdit(edits);
});

connection.onDocumentFormatting(async (params): Promise<TextEdit[]> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return [];
  }

  const project = await buildProjectForDocument(document);
  return getDocumentFormattingEdits(project, document.uri, {
    tabSize: params.options.tabSize,
    insertSpaces: params.options.insertSpaces
  }).map((edit) => TextEdit.replace(toLspRange(edit.range), edit.text));
});

connection.onHover(async (params): Promise<Hover | undefined> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return undefined;
  }

  const project = await buildProjectForDocument(document);
  const hover = getHover(project, {
    uri: document.uri,
    position: params.position
  });
  if (hover === undefined) {
    return undefined;
  }

  return {
    contents: {
      kind: MarkupKind.Markdown,
      value: hover.contents
    }
  };
});

connection.onSignatureHelp(async (params): Promise<SignatureHelp | undefined> => {
  const document = documents.get(params.textDocument.uri);
  if (document === undefined) {
    return undefined;
  }

  const project = await buildProjectForDocument(document);
  const signatureHelp = getSignatureHelp(project, {
    uri: document.uri,
    position: params.position
  });
  if (signatureHelp === undefined) {
    return undefined;
  }

  return {
    activeParameter: signatureHelp.activeParameter,
    activeSignature: 0,
    signatures: [
      SignatureInformation.create(
        signatureHelp.label,
        signatureHelp.documentation,
        ...signatureHelp.parameters.map((parameter) =>
          ParameterInformation.create(parameter.label, parameter.documentation)
        )
      )
    ]
  };
});

documents.listen(connection);
connection.listen();

async function buildProjectForDocument(document: TextDocument): Promise<ReturnType<typeof buildVbaProject>> {
  const document_path = fileURLToPath(document.uri);
  const folder_path = path.dirname(document_path);
  const files = new Map<string, VbaProjectFile>();

  for (const file_name of fs.readdirSync(folder_path)) {
    if (!/\.(bas|cls|frm)$/i.test(file_name)) {
      continue;
    }

    const file_path = path.join(folder_path, file_name);
    const uri = pathToFileURL(file_path).toString();
    files.set(uri.toLowerCase(), {
      uri,
      text: fs.readFileSync(file_path, 'utf8')
    });
  }

  for (const open_document of documents.all()) {
    if (!open_document.uri.startsWith('file://')) {
      continue;
    }

    const open_path = fileURLToPath(open_document.uri);
    if (path.dirname(open_path).toLowerCase() !== folder_path.toLowerCase()) {
      continue;
    }

    if (!/\.(bas|cls|frm)$/i.test(open_path)) {
      continue;
    }

    files.set(open_document.uri.toLowerCase(), {
      uri: open_document.uri,
      text: open_document.getText()
    });
  }

  const host_application_options = await hostApplicationConfigurationProvider.getOptions(document.uri);
  return buildVbaProject([...files.values()], {
    ...host_application_options,
    hostDefinitions: hostCatalogManager.getDefinitions(host_application_options)
  });
}

function toLspPosition(position: Position): Position {
  return Position.create(position.line, position.character);
}

function toLspRange(range: SourceRange): Range {
  return Range.create(toLspPosition(range.start), toLspPosition(range.end));
}

function toWorkspaceEdit(edits: RenameEdit[]): WorkspaceEdit {
  const changes: NonNullable<WorkspaceEdit['changes']> = {};
  for (const edit of edits) {
    changes[edit.uri] ??= [];
    changes[edit.uri].push(TextEdit.replace(toLspRange(edit.range), edit.newText));
  }

  return { changes };
}

function toLspCompletionItemKind(kind: CompletionEntryKind): CompletionItemKind {
  switch (kind) {
    case 'class':
      return CompletionItemKind.Class;
    case 'enum':
      return CompletionItemKind.Enum;
    case 'enumMember':
      return CompletionItemKind.EnumMember;
    case 'event':
      return CompletionItemKind.Event;
    case 'namespace':
      return CompletionItemKind.Module;
    case 'parameter':
      return CompletionItemKind.Variable;
    case 'property':
      return CompletionItemKind.Property;
    case 'snippet':
      return CompletionItemKind.Snippet;
    case 'type':
      return CompletionItemKind.Struct;
    case 'variable':
      return CompletionItemKind.Variable;
    case 'function':
    default:
      return CompletionItemKind.Function;
  }
}

function encodeSemanticTokens(tokens: VbaSemanticToken[]): { data: number[] } {
  const data: number[] = [];
  let previous_line = 0;
  let previous_character = 0;

  for (const token of tokens) {
    const token_type = VBA_SEMANTIC_TOKEN_TYPES.indexOf(token.tokenType);
    if (token_type === -1) {
      continue;
    }

    const line_delta = token.range.start.line - previous_line;
    const character_delta = line_delta === 0
      ? token.range.start.character - previous_character
      : token.range.start.character;
    data.push(
      line_delta,
      character_delta,
      token.range.end.character - token.range.start.character,
      token_type,
      0
    );
    previous_line = token.range.start.line;
    previous_character = token.range.start.character;
  }

  return { data };
}
