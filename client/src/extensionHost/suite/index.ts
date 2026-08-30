import { runBlockSkeletonIntegrationTests } from './blockSkeletonIntegration';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';
import { runTestExplorerNavigationIntegrationTests } from './testExplorerNavigationIntegration';
import { runDebugConfigurationIntegrationTests } from './debugConfigurationIntegration';
import { runModuleRenameIntegrationTests } from './moduleRenameIntegration';
import { runCommandPaletteTargetIntegrationTests } from './commandPaletteTargetIntegration';
import { runCommonModulesCommandIntegrationTests } from './commonModulesCommandIntegration';
import { runProjectManifestMutationIntegrationTests } from './projectManifestMutationIntegration';
import { runReferenceQuickPickIntegrationTests } from './referenceQuickPickIntegration';

export async function run(): Promise<void> {
  await runReferenceQuickPickIntegrationTests();
  await runCommonModulesCommandIntegrationTests();
  await runCommandPaletteTargetIntegrationTests();
  await runDebugConfigurationIntegrationTests();
  await runTestExplorerNavigationIntegrationTests();
  await runGuardedEnterFeasibilityTests();
  await runBlockSkeletonIntegrationTests();
  await runModuleRenameIntegrationTests();
  await runProjectManifestMutationIntegrationTests();
}
