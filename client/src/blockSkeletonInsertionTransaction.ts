export interface BlockSkeletonInsertionPlanRequest<Plan> {
  readonly response: Promise<Plan | null>;
  readonly cancellation?: Promise<void>;
  isCancellationRequested?(): boolean;
  cancel(): void;
}

export interface BlockSkeletonInsertionTransactionOptions<NativeInsertion, Plan> {
  readonly nativeInsertion: NativeInsertion;
  beginPlanRequest(): BlockSkeletonInsertionPlanRequest<Plan>;
  isCurrent(nativeInsertion: NativeInsertion, plan: Plan): boolean;
  applyPlan(nativeInsertion: NativeInsertion, plan: Plan): Promise<boolean>;
  readonly timeoutMilliseconds: number;
  readonly cancellation?: Promise<void>;
}

export type BlockSkeletonInsertionTransactionResult =
  | 'applied'
  | 'declined'
  | 'failed'
  | 'cancelled'
  | 'timed-out'
  | 'stale'
  | 'apply-failed';

type PlanOutcome<Plan> =
  | { readonly kind: 'response'; readonly plan: Plan | null }
  | { readonly kind: 'failed' }
  | { readonly kind: 'cancelled' }
  | { readonly kind: 'timed-out' };

export async function runBlockSkeletonInsertionTransaction<NativeInsertion, Plan>(
  options: BlockSkeletonInsertionTransactionOptions<NativeInsertion, Plan>
): Promise<BlockSkeletonInsertionTransactionResult> {
  let request: BlockSkeletonInsertionPlanRequest<Plan>;
  try {
    request = options.beginPlanRequest();
  } catch {
    return 'failed';
  }

  let requestCancelled = false;
  const cancelRequest = (): void => {
    if (requestCancelled) {
      return;
    }

    requestCancelled = true;
    try {
      request.cancel();
    } catch {
      // Native Enter is already visible; cancellation is best effort.
    }
  };
  let timeoutHandle: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<PlanOutcome<Plan>>((resolve) => {
    timeoutHandle = setTimeout(
      () => resolve({ kind: 'timed-out' }),
      options.timeoutMilliseconds
    );
  });
  const response = request.response.then<PlanOutcome<Plan>, PlanOutcome<Plan>>(
    (plan) => ({ kind: 'response', plan }),
    () => ({ kind: 'failed' })
  );
  const outcomes: Array<Promise<PlanOutcome<Plan>>> = [response, timeout];
  let cancellationRequested = false;
  for (const cancellation of [options.cancellation, request.cancellation]) {
    if (cancellation === undefined) {
      continue;
    }

    outcomes.push(cancellation.then<PlanOutcome<Plan>, PlanOutcome<Plan>>(
      () => {
        cancellationRequested = true;
        return { kind: 'cancelled' };
      },
      () => {
        cancellationRequested = true;
        return { kind: 'cancelled' };
      }
    ));
  }
  const outcomePromise = Promise.race(outcomes);
  void outcomePromise.then((outcome) => {
    if (outcome.kind === 'timed-out' || outcome.kind === 'cancelled') {
      cancelRequest();
    }
  });

  const outcome = await outcomePromise;
  if (timeoutHandle !== undefined) {
    clearTimeout(timeoutHandle);
  }
  if (outcome.kind !== 'response') {
    return outcome.kind;
  }

  await Promise.resolve();
  let cancellationProbeFailed = false;
  let cancellationProbeResult = false;
  try {
    cancellationProbeResult = request.isCancellationRequested?.() === true;
  } catch {
    cancellationProbeFailed = true;
  }
  if (
    cancellationRequested
    || cancellationProbeResult
    || cancellationProbeFailed
  ) {
    cancelRequest();
    return 'cancelled';
  }
  if (outcome.plan === null) {
    return 'declined';
  }

  let current: boolean;
  try {
    current = options.isCurrent(options.nativeInsertion, outcome.plan);
  } catch {
    current = false;
  }
  if (!current) {
    cancelRequest();
    return 'stale';
  }

  try {
    return await options.applyPlan(options.nativeInsertion, outcome.plan)
      ? 'applied'
      : 'apply-failed';
  } catch {
    return 'apply-failed';
  }
}
