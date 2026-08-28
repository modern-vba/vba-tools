import type {
  CancellationToken,
  Position,
  ProviderResult,
  TextDocument,
  WorkspaceEdit
} from 'vscode';
import type {
  Position as ProtocolPosition,
  RenameMiddleware,
  RenameParams,
  TextDocumentIdentifier,
  WorkspaceEdit as ProtocolWorkspaceEdit
} from 'vscode-languageclient/node';
import type { CaseOnlyVbaFileRename } from './caseOnlyVbaFileRename';

export interface VbaRenameClient {
  asTextDocumentIdentifier(document: TextDocument): TextDocumentIdentifier;
  asPosition(position: Position): ProtocolPosition;
  sendRenameRequest(
    parameters: RenameParams,
    token: CancellationToken
  ): Promise<ProtocolWorkspaceEdit | null>;
  asWorkspaceEdit(
    edit: ProtocolWorkspaceEdit | null,
    token: CancellationToken
  ): Promise<WorkspaceEdit | undefined>;
  handleFailedRenameRequest(
    error: unknown,
    token: CancellationToken
  ): ProviderResult<WorkspaceEdit>;
}

export interface VbaRenameMiddlewareOptions {
  readonly getLanguageClient: () => VbaRenameClient | undefined;
  readonly captureCaseOnlyFileRenames: (
    renames: readonly CaseOnlyVbaFileRename[]
  ) => void;
}

export function createVbaRenameMiddleware(
  options: VbaRenameMiddlewareOptions
): NonNullable<RenameMiddleware['provideRenameEdits']> {
  return async (document, position, newName, token, next) => {
    const client = options.getLanguageClient();
    if (client === undefined) {
      return next(document, position, newName, token);
    }

    const parameters: RenameParams = {
      textDocument: client.asTextDocumentIdentifier(document),
      position: client.asPosition(position),
      newName
    };
    try {
      const edit = await client.sendRenameRequest(parameters, token);
      if (token.isCancellationRequested) {
        return null;
      }

      options.captureCaseOnlyFileRenames(readRenameFiles(edit));
      return client.asWorkspaceEdit(edit, token);
    } catch (error: unknown) {
      return client.handleFailedRenameRequest(error, token);
    }
  };
}

function readRenameFiles(
  edit: ProtocolWorkspaceEdit | null
): CaseOnlyVbaFileRename[] {
  if (edit?.documentChanges === undefined) {
    return [];
  }

  return edit.documentChanges.flatMap(change =>
    'kind' in change && change.kind === 'rename'
      ? [{ oldUri: change.oldUri, newUri: change.newUri }]
      : []);
}
