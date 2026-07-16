import test from 'node:test';
import assert from 'node:assert/strict';

import {
  runBlockSkeletonInsertionTransaction
} from './blockSkeletonInsertionTransaction';

test('a timed-out post-native request is cancelled and its late plan is ignored', async () => {
  let resolvePlan: ((plan: string) => void) | undefined;
  const response = new Promise<string>((resolve) => {
    resolvePlan = resolve;
  });
  let cancellations = 0;
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => ({
      response,
      cancel: () => {
        cancellations += 1;
      }
    }),
    isCurrent: () => true,
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 10
  });

  assert.equal(result, 'timed-out');
  assert.equal(cancellations, 1);
  assert.equal(applications, 0);

  resolvePlan?.('late plan');
  await new Promise<void>((resolve) => setTimeout(resolve, 0));
  assert.equal(applications, 0);
});

test('explicit cancellation leaves the existing native insertion unchanged', async () => {
  let resolveCancellation: (() => void) | undefined;
  const cancellation = new Promise<void>((resolve) => {
    resolveCancellation = resolve;
  });
  let cancellations = 0;
  let applications = 0;
  const transaction = runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => ({
      response: new Promise<string>(() => undefined),
      cancel: () => {
        cancellations += 1;
      }
    }),
    isCurrent: () => true,
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 1_000,
    cancellation
  });

  resolveCancellation?.();

  assert.equal(await transaction, 'cancelled');
  assert.equal(cancellations, 1);
  assert.equal(applications, 0);
});

test('the recorded native insertion is revalidated and passed to plan application', async () => {
  const calls: string[] = [];

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native receipt',
    beginPlanRequest: () => ({
      response: Promise.resolve('plan'),
      cancel: () => undefined
    }),
    isCurrent: (nativeInsertion, plan) => {
      calls.push(`validate ${nativeInsertion} ${plan}`);
      return true;
    },
    applyPlan: async (nativeInsertion, plan) => {
      calls.push(`apply ${nativeInsertion} ${plan}`);
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'applied');
  assert.deepEqual(calls, [
    'validate native receipt plan',
    'apply native receipt plan'
  ]);
});

test('a refused non-EOF plan leaves the native insertion as the declined fallback', async () => {
  let cancellations = 0;
  let validations = 0;
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native receipt before following source',
    beginPlanRequest: () => ({
      response: Promise.resolve(null),
      cancel: () => {
        cancellations += 1;
      }
    }),
    isCurrent: () => {
      validations += 1;
      return true;
    },
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'declined');
  assert.equal(cancellations, 0);
  assert.equal(validations, 0);
  assert.equal(applications, 0);
});

test('an accepted non-EOF plan reaches application with the recorded native insertion', async () => {
  const nativeInsertion = { range: 'native line break before following source' };
  const plan = {
    textBeforeCursor: '\n  ',
    textAfterCursor: '\nEnd Sub'
  };
  let validations = 0;
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion,
    beginPlanRequest: () => ({
      response: Promise.resolve(plan),
      cancel: () => undefined
    }),
    isCurrent: (candidateInsertion, candidatePlan) => {
      validations += 1;
      assert.equal(candidateInsertion, nativeInsertion);
      assert.equal(candidatePlan, plan);
      return true;
    },
    applyPlan: async (candidateInsertion, candidatePlan) => {
      applications += 1;
      assert.equal(candidateInsertion, nativeInsertion);
      assert.equal(candidatePlan, plan);
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'applied');
  assert.equal(validations, 1);
  assert.equal(applications, 1);
});

test('cancellation settled with a plan response still prevents application', async () => {
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => ({
      response: Promise.resolve('plan'),
      cancellation: Promise.resolve(),
      cancel: () => undefined
    }),
    isCurrent: () => true,
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'cancelled');
  assert.equal(applications, 0);
});

test('a failing cancellation probe fails closed without rejecting the command', async () => {
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => ({
      response: Promise.resolve('plan'),
      isCancellationRequested: () => {
        throw new Error('probe failed');
      },
      cancel: () => undefined
    }),
    isCurrent: () => true,
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'cancelled');
  assert.equal(applications, 0);
});

test('a synchronous request setup failure leaves the native insertion in place', async () => {
  let applications = 0;

  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => {
      throw new Error('request setup failed');
    },
    isCurrent: () => true,
    applyPlan: async () => {
      applications += 1;
      return true;
    },
    timeoutMilliseconds: 100
  });

  assert.equal(result, 'failed');
  assert.equal(applications, 0);
});

test('a cancellation callback failure cannot reject a timed-out transaction', async () => {
  const result = await runBlockSkeletonInsertionTransaction({
    nativeInsertion: 'native',
    beginPlanRequest: () => ({
      response: new Promise<string>(() => undefined),
      cancel: () => {
        throw new Error('cancellation failed');
      }
    }),
    isCurrent: () => true,
    applyPlan: async () => true,
    timeoutMilliseconds: 10
  });

  assert.equal(result, 'timed-out');
});

test('a declined or throwing plan application leaves the native insertion as fallback', async () => {
  for (const applyPlan of [
    async () => false,
    async (): Promise<boolean> => {
      throw new Error('application failed');
    }
  ]) {
    const result = await runBlockSkeletonInsertionTransaction({
      nativeInsertion: 'native',
      beginPlanRequest: () => ({
        response: Promise.resolve('plan'),
        cancel: () => undefined
      }),
      isCurrent: () => true,
      applyPlan,
      timeoutMilliseconds: 100
    });

    assert.equal(result, 'apply-failed');
  }
});
