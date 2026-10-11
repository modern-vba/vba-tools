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
import { runExtensionHostIntegrationSuites } from '../integrationSuites';

export async function run(): Promise<void> {
  const profile = process.env.VBA_TOOLS_EXTENSION_HOST_TEST_PROFILE;
  if (profile !== 'ordinary' && profile !== 'windows-excel') {
    throw new Error('Run the Extension Host suite through its explicit test runner profile.');
  }
  const completed = await runExtensionHostIntegrationSuites(profile, [
    { name: 'code-only indentation', requiresExcel: false, run: runCodeOnlyIndentationIntegrationTests },
    { name: 'reference quick pick', requiresExcel: false, run: runReferenceQuickPickIntegrationTests },
    { name: 'CommonModules commands', requiresExcel: false, run: runCommonModulesCommandIntegrationTests },
    { name: 'Command Palette targets', requiresExcel: false, run: runCommandPaletteTargetIntegrationTests },
    { name: 'Build Problems', requiresExcel: true, run: runBuildProblemsIntegrationTests },
    { name: 'Publish Problems', requiresExcel: true, run: runPublishProblemsIntegrationTests },
    { name: 'snapshot Build Problems', requiresExcel: true, run: runSnapshotBuildProblemsIntegrationTests },
    { name: 'Test Build Problems', requiresExcel: true, run: runTestBuildProblemsIntegrationTests },
    { name: 'debug configuration', requiresExcel: false, run: runDebugConfigurationIntegrationTests },
    { name: 'Test Explorer navigation', requiresExcel: false, run: runTestExplorerNavigationIntegrationTests },
    { name: 'guarded Enter', requiresExcel: false, run: runGuardedEnterFeasibilityTests },
    { name: 'block skeleton', requiresExcel: false, run: runBlockSkeletonIntegrationTests },
    { name: 'module rename', requiresExcel: false, run: runModuleRenameIntegrationTests },
    { name: 'manifest mutation', requiresExcel: false, run: runProjectManifestMutationIntegrationTests }
  ]);
  console.log(`Extension Host ${profile}: ${completed.length} suites passed: ${completed.join(', ')}`);
}
