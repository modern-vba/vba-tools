import type {
  BlockSkeletonInsertionPlanRequest,
  BlockSkeletonInsertionTransactionResult
} from './blockSkeletonInsertionTransaction';
import type { CancellationToken } from 'vscode';

export interface BlockSkeletonInsertionPosition {
  readonly line: number;
  readonly character: number;
}

export interface BlockSkeletonInsertionRequest {
  readonly documentUri: string;
  readonly documentVersion: number;
  readonly position: BlockSkeletonInsertionPosition;
  readonly options: BlockSkeletonInsertionOptions;
}

export interface BlockSkeletonInsertionOptions {
  readonly insertSpaces: boolean;
  readonly tabSize: number;
  readonly indentSize?: number;
}

export interface BlockSkeletonInsertionEditorOptions {
  readonly insertSpaces?: boolean | string;
  readonly tabSize?: number | string;
  readonly indentSize?: number | string;
}

export interface BlockSkeletonInsertionConfiguredOptions {
  readonly insertSpaces?: boolean;
  readonly tabSize?: number;
  readonly indentSize?: number | string;
}

export interface BlockSkeletonInsertionPlan {
  readonly documentVersion: number;
  readonly position: BlockSkeletonInsertionPosition;
  readonly textBeforeCursor: string;
  readonly textAfterCursor: string;
}

export function formatBlockSkeletonInsertionFallbackTrace(
  result: BlockSkeletonInsertionTransactionResult,
  traceLevel: string
): string | undefined {
  return traceLevel === 'verbose' && result !== 'applied'
    ? `Block skeleton insertion used native Enter fallback: ${result}.`
    : undefined;
}

export type BlockSkeletonInsertionPlanProvider = (
  request: BlockSkeletonInsertionRequest
) => BlockSkeletonInsertionPlanRequest<BlockSkeletonInsertionPlan>;

export interface BlockSkeletonInsertionLanguageClient {
  sendRequest(
    method: string,
    parameters: BlockSkeletonInsertionRequest,
    token: CancellationToken
  ): Promise<BlockSkeletonInsertionPlan | null>;
}

export interface BlockSkeletonInsertionCancellationSource {
  readonly token: CancellationToken;
  cancel(): void;
  dispose(): void;
}

let planProvider: BlockSkeletonInsertionPlanProvider = () => ({
  response: Promise.resolve(null),
  cancel: () => undefined
});

export function getBlockSkeletonInsertionPlanProvider(): BlockSkeletonInsertionPlanProvider {
  return planProvider;
}

export function resolveBlockSkeletonInsertionOptions(
  editorOptions: BlockSkeletonInsertionEditorOptions,
  configuredOptions: BlockSkeletonInsertionConfiguredOptions = {}
): BlockSkeletonInsertionOptions | undefined {
  const insertSpaces = typeof editorOptions.insertSpaces === 'boolean'
    ? editorOptions.insertSpaces
    : configuredOptions.insertSpaces;
  const tabSize = isPositiveInteger(editorOptions.tabSize)
    ? editorOptions.tabSize
    : configuredOptions.tabSize;
  if (typeof insertSpaces !== 'boolean' || !isPositiveInteger(tabSize)) {
    return undefined;
  }

  if (
    typeof editorOptions.indentSize === 'number'
    && !isPositiveInteger(editorOptions.indentSize)
  ) {
    return undefined;
  }

  const indentSize = isPositiveInteger(editorOptions.indentSize)
    ? editorOptions.indentSize
    : editorOptions.indentSize === 'tabSize'
      ? undefined
      : isPositiveInteger(configuredOptions.indentSize)
        ? configuredOptions.indentSize
        : undefined;

  return {
    insertSpaces,
    tabSize,
    ...(indentSize === undefined
      ? {}
      : { indentSize })
  };
}

export function createLanguageClientBlockSkeletonInsertionPlanProvider(
  languageClient: BlockSkeletonInsertionLanguageClient,
  createCancellationSource: () => BlockSkeletonInsertionCancellationSource
): BlockSkeletonInsertionPlanProvider {
  return (request) => {
    const cancellationSource = createCancellationSource();
    let cancelled = false;
    let disposed = false;
    const dispose = (): void => {
      if (!disposed) {
        disposed = true;
        cancellationSource.dispose();
      }
    };
    let response: Promise<BlockSkeletonInsertionPlan | null>;
    try {
      response = languageClient.sendRequest(
        'vba/blockSkeletonInsertion',
        request,
        cancellationSource.token
      );
    } catch (error) {
      dispose();
      throw error;
    }

    return {
      response: response.finally(dispose),
      cancel: () => {
        if (!cancelled) {
          cancelled = true;
          cancellationSource.cancel();
        }

        dispose();
      }
    };
  };
}

export function useBlockSkeletonInsertionPlanProvider(
  provider: BlockSkeletonInsertionPlanProvider
): { dispose(): void } {
  const previousProvider = planProvider;
  planProvider = provider;
  return {
    dispose: () => {
      planProvider = previousProvider;
    }
  };
}

export function useBlockSkeletonInsertionPlanProviderForTest(
  provider: BlockSkeletonInsertionPlanProvider
): { dispose(): void } {
  return useBlockSkeletonInsertionPlanProvider(provider);
}

function isPositiveInteger(value: unknown): value is number {
  return typeof value === 'number'
    && Number.isInteger(value)
    && value > 0;
}
