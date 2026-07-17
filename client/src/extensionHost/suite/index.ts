import { runBlockSkeletonIntegrationTests } from './blockSkeletonIntegration';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';

export async function run(): Promise<void> {
  await runGuardedEnterFeasibilityTests();
  await runBlockSkeletonIntegrationTests();
}
