import type {
  BlockSkeletonInsertionPlanRequest
} from './blockSkeletonInsertionTransaction';

export interface BlockSkeletonInsertionPosition {
  readonly line: number;
  readonly character: number;
}

export interface BlockSkeletonInsertionRequest {
  readonly documentUri: string;
  readonly documentVersion: number;
  readonly position: BlockSkeletonInsertionPosition;
}

export interface BlockSkeletonInsertionPlan {
  readonly documentVersion: number;
  readonly position: BlockSkeletonInsertionPosition;
  readonly textBeforeCursor: string;
  readonly textAfterCursor: string;
}

export type BlockSkeletonInsertionPlanProvider = (
  request: BlockSkeletonInsertionRequest
) => BlockSkeletonInsertionPlanRequest<BlockSkeletonInsertionPlan>;

let planProvider: BlockSkeletonInsertionPlanProvider = () => ({
  response: Promise.resolve(null),
  cancel: () => undefined
});

export function getBlockSkeletonInsertionPlanProvider(): BlockSkeletonInsertionPlanProvider {
  return planProvider;
}

export function useBlockSkeletonInsertionPlanProviderForTest(
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
