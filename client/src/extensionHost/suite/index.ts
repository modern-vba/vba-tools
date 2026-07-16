import { runBlockIfSkeletonIntegrationTests } from './blockIfSkeleton';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';

export async function run(): Promise<void> {
  await runGuardedEnterFeasibilityTests();
  await runBlockIfSkeletonIntegrationTests();
}
