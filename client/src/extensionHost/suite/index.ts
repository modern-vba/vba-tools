import { runBlockSkeletonIntegrationTests } from './blockSkeletonIntegration';
import { runGuardedEnterFeasibilityTests } from './guardedEnterFeasibility';
import { runTestExplorerNavigationIntegrationTests } from './testExplorerNavigationIntegration';
import { runDebugConfigurationIntegrationTests } from './debugConfigurationIntegration';
import { runModuleRenameIntegrationTests } from './moduleRenameIntegration';
import { runCommandPaletteTargetIntegrationTests } from './commandPaletteTargetIntegration';
import { runBuildProblemsIntegrationTests, runPublishProblemsIntegrationTests } from './buildProblemsIntegration';
import { runSnapshotBuildProblemsIntegrationTests } from './snapshotBuildProblemsIntegration';
import { runTestBuildProblemsIntegrationTests } from './testBuildProblemsIntegration';
import { runCommonModulesCommandIntegrationTests } from './commonModulesCommandIntegration';
import { runProjectManifestMutationIntegrationTests } from './projectManifestMutationIntegration';
import { runReferenceQuickPickIntegrationTests } from './referenceQuickPickIntegration';
import { runCodeOnlyIndentationIntegrationTests } from './codeOnlyIndentationIntegration';

export async function run(): Promise<void> {
  await runCodeOnlyIndentationIntegrationTests();
  await runReferenceQuickPickIntegrationTests();
  await runCommonModulesCommandIntegrationTests();
  await runCommandPaletteTargetIntegrationTests();
  await runBuildProblemsIntegrationTests();
  await runPublishProblemsIntegrationTests();
  await runSnapshotBuildProblemsIntegrationTests();
  await runTestBuildProblemsIntegrationTests();
  await runDebugConfigurationIntegrationTests();
  await runTestExplorerNavigationIntegrationTests();
  await runGuardedEnterFeasibilityTests();
  await runBlockSkeletonIntegrationTests();
  await runModuleRenameIntegrationTests();
  await runProjectManifestMutationIntegrationTests();
}
