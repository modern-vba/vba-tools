import { runBlockSkeletonIntegrationTests } from './blockSkeletonIntegration';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';
import { runTestExplorerNavigationIntegrationTests } from './testExplorerNavigationIntegration';
import { runDebugConfigurationIntegrationTests } from './debugConfigurationIntegration';
import { runModuleRenameIntegrationTests } from './moduleRenameIntegration';

export async function run(): Promise<void> {
  await runDebugConfigurationIntegrationTests();
  await runTestExplorerNavigationIntegrationTests();
  await runGuardedEnterFeasibilityTests();
  await runBlockSkeletonIntegrationTests();
  await runModuleRenameIntegrationTests();
}
