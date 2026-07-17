import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import { runTests } from '@vscode/test-electron';
import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection
} from './configuration';

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..', '..');
  const extensionTestsPath = path.resolve(__dirname, 'suite', 'index.js');
  const runtime = createExtensionHostRuntimeSelection(process.env);
  const userDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-extension-host-'
  ));

  try {
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      vscodeExecutablePath: runtime.vscodeExecutablePath,
      version: runtime.version,
      launchArgs: createExtensionHostLaunchArgs(userDataPath),
      extensionTestsEnv: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1'
      }
    });
  } finally {
    await rm(userDataPath, { recursive: true, force: true });
  }
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
