import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';
import {
  createExtensionHostRuntimeSelection
} from './configuration';

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
  const runtime = createExtensionHostRuntimeSelection(process.env);

  await runTests({
    extensionDevelopmentPath,
    extensionTestsPath,
    vscodeExecutablePath: runtime.vscodeExecutablePath,
    version: runtime.version,
    launchArgs: [
      '--disable-extensions',
      '--skip-welcome',
      '--skip-release-notes'
    ],
    extensionTestsEnv: {
      VBA_TOOLS_EXTENSION_HOST_TEST: '1'
    }
  });
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
