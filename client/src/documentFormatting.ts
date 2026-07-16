import type {
  CancellationToken,
  FormattingOptions,
  ProviderResult,
  TextDocument,
  TextEdit,
  TextEditor
} from 'vscode';
import type {
  DocumentFormattingParams,
  FormattingMiddleware,
  FormattingOptions as ProtocolFormattingOptions,
  TextDocumentIdentifier,
  TextEdit as ProtocolTextEdit
} from 'vscode-languageclient/node';

export interface VbaFileFormattingOptions {
  readonly trimTrailingWhitespace?: boolean;
  readonly trimFinalNewlines?: boolean;
  readonly insertFinalNewline?: boolean;
}

export interface VbaDocumentFormattingClient {
  asTextDocumentIdentifier(document: TextDocument): TextDocumentIdentifier;
  asFormattingOptions(
    options: FormattingOptions,
    fileOptions: VbaFileFormattingOptions
  ): ProtocolFormattingOptions;
  sendDocumentFormattingRequest(
    parameters: DocumentFormattingParams,
    token: CancellationToken
  ): Promise<ProtocolTextEdit[] | null>;
  asTextEdits(
    edits: ProtocolTextEdit[] | null,
    token: CancellationToken
  ): Promise<TextEdit[] | undefined>;
  handleFailedDocumentFormattingRequest(
    error: unknown,
    token: CancellationToken
  ): ProviderResult<TextEdit[]>;
}

export interface VbaDocumentFormattingMiddlewareOptions {
  readonly getLanguageClient: () => VbaDocumentFormattingClient | undefined;
  readonly getTextEditors: () => readonly TextEditor[];
  readonly getFileFormattingOptions?: (
    document: TextDocument
  ) => VbaFileFormattingOptions;
}

export function createVbaDocumentFormattingMiddleware(
  middlewareOptions: VbaDocumentFormattingMiddlewareOptions
): NonNullable<FormattingMiddleware['provideDocumentFormattingEdits']> {
  return async (document, options, token, next) => {
    const languageClient = middlewareOptions.getLanguageClient();
    if (languageClient === undefined) {
      return next(document, options, token);
    }

    const protocolOptions = languageClient.asFormattingOptions(
      options,
      middlewareOptions.getFileFormattingOptions?.(document) ?? {}
    );
    const matchingEditor = middlewareOptions.getTextEditors().find(
      (editor) => editor.document.uri.toString() === document.uri.toString()
    );
    const indentSize = matchingEditor?.options.indentSize;
    if (typeof indentSize === 'number'
        && Number.isInteger(indentSize)
        && indentSize > 0) {
      protocolOptions.indentSize = indentSize;
    }

    const parameters: DocumentFormattingParams = {
      textDocument: languageClient.asTextDocumentIdentifier(document),
      options: protocolOptions
    };

    try {
      const edits = await languageClient.sendDocumentFormattingRequest(parameters, token);
      if (token.isCancellationRequested) {
        return null;
      }

      return languageClient.asTextEdits(edits, token);
    } catch (error) {
      return languageClient.handleFailedDocumentFormattingRequest(error, token);
    }
  };
}
