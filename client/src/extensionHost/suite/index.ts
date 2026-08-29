import { runBlockSkeletonIntegrationTests } from './blockSkeletonIntegration';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';
import { runTestExplorerNavigationIntegrationTests } from './testExplorerNavigationIntegration';
import { runDebugConfigurationIntegrationTests } from './debugConfigurationIntegration';
import { runModuleRenameIntegrationTests } from './moduleRenameIntegration';
import { runCommandPaletteTargetIntegrationTests } from './commandPaletteTargetIntegration';
import { runProjectManifestMutationIntegrationTests } from './projectManifestMutationIntegration';

export async function run(): Promise<void> {
  await runCommandPaletteTargetIntegrationTests();
  await runDebugConfigurationIntegrationTests();
  await runTestExplorerNavigationIntegrationTests();
  await runGuardedEnterFeasibilityTests();
  await runBlockSkeletonIntegrationTests();
  await runModuleRenameIntegrationTests();
  await runProjectManifestMutationIntegrationTests();
}
