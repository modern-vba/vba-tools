import type {
  CancellationToken,
  MessageItem,
  Position,
  ProviderResult,
  TextDocument,
  WorkspaceEdit
} from 'vscode';
import type {
  ClientCapabilities,
  Position as ProtocolPosition,
  Range as ProtocolRange,
  RenameMiddleware,
  RenameParams,
  StaticFeature,
  TextDocumentIdentifier,
  WorkspaceEdit as ProtocolWorkspaceEdit
} from 'vscode-languageclient/node';
import type { CaseOnlyVbaFileRename } from './caseOnlyVbaFileRename';

export interface VbaRenameConfirmation {
  readonly confirmationId: string;
  readonly decision: 'continue' | 'cancel';
}

export function createVbaRenameClientCapabilitiesFeature(): StaticFeature {
  return {
    fillClientCapabilities(capabilities: ClientCapabilities): void {
      capabilities.experimental = {
        ...capabilities.experimental,
        vbaRenameConfirmation: { protocolVersion: 1 }
      };
    },
    initialize(): void {},
    getState: () => ({ kind: 'static' }),
    clear(): void {}
  };
}

interface VbaRenameConfirmationChallenge {
  readonly kind: 'vbaRenameConfirmationRequired';
  readonly protocolVersion: 1;
  readonly confirmationId: string;
  readonly originalName: string;
  readonly newName: string;
  readonly conflicts: readonly {
    readonly collisionKind: string;
    readonly name: string;
    readonly uri?: string;
    readonly range?: ProtocolRange;
    readonly referenceName?: string;
  }[];
  readonly concerns: readonly string[];
  readonly retainedPaths: readonly string[];
}

export interface VbaRenameClient {
  asTextDocumentIdentifier(document: TextDocument): TextDocumentIdentifier;
  asPosition(position: Position): ProtocolPosition;
  sendRenameRequest(
    parameters: RenameParams,
    token: CancellationToken
  ): Promise<ProtocolWorkspaceEdit | null>;
  sendConfirmationRequest?(
    parameters: VbaRenameConfirmation,
    token?: CancellationToken
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
  readonly showWarningMessage?: (
    message: string,
    options: { modal: true; detail: string },
    ...items: MessageItem[]
  ) => PromiseLike<MessageItem | undefined>;
}

let warningHostForTest: VbaRenameMiddlewareOptions['showWarningMessage'];

export function useVbaRenameWarningHostForTest(
  host: NonNullable<VbaRenameMiddlewareOptions['showWarningMessage']>
): { dispose(): void } {
  const previousHost = warningHostForTest;
  warningHostForTest = host;
  return { dispose: () => { warningHostForTest = previousHost; } };
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
      let edit: ProtocolWorkspaceEdit | null;
      try {
        edit = await client.sendRenameRequest(parameters, token);
      } catch (error) {
        const challenge = readConfirmationChallenge(error, newName);
        const showWarningMessage = warningHostForTest ?? options.showWarningMessage;
        if (challenge === undefined || showWarningMessage === undefined
            || client.sendConfirmationRequest === undefined) {
          throw error;
        }

        if (!await confirmRename(
          challenge, showWarningMessage,
          client.sendConfirmationRequest.bind(client), token)) {
          return null;
        }
        edit = await client.sendConfirmationRequest({
          confirmationId: challenge.confirmationId, decision: 'continue'
        }, token);
      }
      if (token.isCancellationRequested) {
        return null;
      }

      const workspaceEdit = await client.asWorkspaceEdit(edit, token);
      if (token.isCancellationRequested) {
        return null;
      }
      if (workspaceEdit !== undefined) {
        options.captureCaseOnlyFileRenames(readRenameFiles(edit));
      }
      return workspaceEdit;
    } catch (error: unknown) {
      return client.handleFailedRenameRequest(error, token);
    }
  };
}

async function confirmRename(
  challenge: VbaRenameConfirmationChallenge,
  showWarningMessage: NonNullable<VbaRenameMiddlewareOptions['showWarningMessage']>,
  sendConfirmationRequest: NonNullable<VbaRenameClient['sendConfirmationRequest']>,
  token: CancellationToken
): Promise<boolean> {
  let cancelRequest: Promise<void> | undefined;
  const discard = (): Promise<void> => cancelRequest ??= (async () => {
    try {
      await sendConfirmationRequest({ confirmationId: challenge.confirmationId, decision: 'cancel' });
    } catch {
      // Cancellation must not depend on the server accepting this best-effort release.
    }
  })();
  let cancelPrompt!: () => void;
  const cancelled = new Promise<undefined>(resolve => { cancelPrompt = () => resolve(undefined); });
  const cancellation = token.onCancellationRequested(() => {
    void discard();
    cancelPrompt();
  });
  try {
    const cancel: MessageItem = { title: 'Cancel', isCloseAffordance: true };
    const continueOnce: MessageItem = { title: 'Continue once' };
    const selected = token.isCancellationRequested ? undefined : await Promise.race([
      Promise.resolve(showWarningMessage(
        `Rename '${challenge.originalName}' to '${challenge.newName}' despite the collision?`,
        { modal: true, detail: confirmationDetail(challenge) },
        cancel,
        continueOnce)),
      cancelled
    ]);
    if (selected !== continueOnce || token.isCancellationRequested) {
      void discard();
      return false;
    }
    return true;
  } catch (error) {
    void discard();
    throw error;
  } finally {
    cancellation.dispose();
  }
}

function readConfirmationChallenge(
  error: unknown,
  newName: string
): VbaRenameConfirmationChallenge | undefined {
  if (!isRecord(error) || error.code !== -32803 || !isRecord(error.data)) {
    return undefined;
  }
  const data = error.data;
  if (data.kind !== 'vbaRenameConfirmationRequired' || data.protocolVersion !== 1
      || typeof data.confirmationId !== 'string' || data.confirmationId.length === 0
      || typeof data.originalName !== 'string' || data.originalName.length === 0
      || data.newName !== newName
      || !Array.isArray(data.conflicts) || !data.conflicts.every(isConfirmationConflict)
      || !isStringArray(data.concerns) || !isStringArray(data.retainedPaths)) {
    return undefined;
  }
  return data as unknown as VbaRenameConfirmationChallenge;
}

function isConfirmationConflict(value: unknown): boolean {
  return isRecord(value)
    && typeof value.collisionKind === 'string' && typeof value.name === 'string'
    && (value.uri === undefined || typeof value.uri === 'string')
    && (value.referenceName === undefined || typeof value.referenceName === 'string')
    && (value.range === undefined || isRecord(value.range)
      && isProtocolPosition(value.range.start) && isProtocolPosition(value.range.end));
}

function isProtocolPosition(value: unknown): boolean {
  return isRecord(value)
    && Number.isInteger(value.line) && Number(value.line) >= 0
    && Number.isInteger(value.character) && Number(value.character) >= 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every(item => typeof item === 'string');
}

function confirmationDetail(challenge: VbaRenameConfirmationChallenge): string {
  const conflicts = challenge.conflicts.map(conflict => {
    const location = conflict.uri === undefined ? '' : ` at ${conflict.uri}`
      + (conflict.range === undefined ? ''
        : `:${conflict.range.start.line + 1}:${conflict.range.start.character + 1}`);
    const reference = conflict.referenceName === undefined ? ''
      : ` in reference '${conflict.referenceName}'`;
    return `- '${conflict.name}'${reference}${location}`;
  });
  const retainedPaths = challenge.retainedPaths.length === 0 ? [] : [
    `The resulting module name will be '${challenge.newName}'. These original file paths will be retained:`,
    ...challenge.retainedPaths.map(retainedPath => `- ${retainedPath}`)
  ];
  return [
    ...conflicts,
    ...challenge.concerns,
    ...retainedPaths,
    'Existing conflicting declarations will remain. You may need to consolidate duplicate or ambiguous source manually.'
  ].join('\n');
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
