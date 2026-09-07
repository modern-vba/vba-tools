import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { createServer } from 'node:net';
import * as path from 'node:path';
import { promisify } from 'node:util';
import { runTests } from '@vscode/test-electron';
import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection
} from './configuration';
import { captureBuildInputProvenance } from './sourceProvenance';

const execute = promisify(execFile);

async function main(): Promise<void> {
  if (process.platform !== 'win32') {
    throw new Error('The CommonModules Explorer measurement requires Windows.');
  }
  const extensionRoot = path.resolve(process.env.VBA_TOOLS_PREVIEW_EXTENSION_ROOT
    ?? path.resolve(__dirname, '..', '..', '..'));
  const projectRoot = process.env.VBA_TOOLS_PREVIEW_PROJECT;
  if (projectRoot === undefined || !path.isAbsolute(projectRoot)) {
    throw new Error('VBA_TOOLS_PREVIEW_PROJECT must name the absolute CommonModules project directory.');
  }
  const resultDirectory = path.resolve(
    process.env.VBA_TOOLS_PREVIEW_RESULT_DIRECTORY
      ?? path.join(extensionRoot, 'test-results', 'issue-365', 'preview')
  );
  const profile = await mkdtemp(path.join(tmpdir(), 'vba-preview-profile-'));
  const timingDirectory = path.join(resultDirectory, 'scheduler');
  await mkdir(path.join(profile, 'User'), { recursive: true });
  await mkdir(resultDirectory, { recursive: true });
  await writeFile(path.join(profile, 'User', 'settings.json'), JSON.stringify({
    'security.workspace.trust.enabled': false,
    'workbench.startupEditor': 'none',
    'workbench.editor.enablePreview': true,
    'workbench.editor.enablePreviewFromCodeNavigation': true,
    'workbench.editor.enablePreviewFromQuickOpen': true,
    'editor.semanticHighlighting.enabled': true,
    'editor.semanticTokenColorCustomizations': {
      enabled: true,
      rules: { '*:vba': { foreground: '#01ff87' } }
    },
    'files.encoding': 'shiftjis',
    'files.autoGuessEncoding': false
  }, undefined, 2) + '\n');
  const revision = async (directory: string) => ({
    commit: (await execute('git', ['-C', directory, 'rev-parse', 'HEAD'],
      { windowsHide: true })).stdout.trim(),
    status: (await execute('git', ['-C', directory, 'status', '--porcelain',
      '--untracked-files=no'], { windowsHide: true })).stdout.trim()
  });
  await writeFile(path.join(resultDirectory, 'launch.json'), JSON.stringify({
    capturedAt: new Date().toISOString(),
    extension: await revision(extensionRoot),
    corpus: await revision(projectRoot),
    sourceInputs: await captureBuildInputProvenance(extensionRoot),
    projectRoot, profile, timingDirectory,
    languageServerSha256: createHash('sha256').update(await readFile(path.join(
      extensionRoot, 'bin', 'vba-language-server', 'win-x64', 'vba-language-server.exe'
    ))).digest('hex'),
    command: 'node client/out/extensionHost/previewPerformanceRun.js',
    dotnetSdk: (await execute('dotnet', ['--version'], { windowsHide: true })).stdout.trim(),
    dotnetRuntimes: (await execute('dotnet', ['--list-runtimes'], { windowsHide: true })).stdout.trim(),
    powerScheme: (await execute('powercfg', ['/GETACTIVESCHEME'], { windowsHide: true })).stdout.trim(),
    build: 'Release net10.0 win-x64; publish:language-server before launch',
    competingLoad: process.env.VBA_TOOLS_PREVIEW_LOAD_NOTE ?? 'ambient load uncontrolled; no synthetic load',
    caches: 'fresh VS Code profile/server/reference catalog; OS filesystem cache uncontrolled'
  }, undefined, 2) + '\n');
  const runtime = createExtensionHostRuntimeSelection(process.env);
  const portReservation = createServer();
  await new Promise<void>(resolve => portReservation.listen(0, '127.0.0.1', resolve));
  const address = portReservation.address();
  if (address === null || typeof address === 'string') {
    throw new Error('Could not reserve a local renderer observation port.');
  }
  const rendererPort = address.port;
  await new Promise<void>((resolve, reject) => portReservation.close(error =>
    error === undefined ? resolve() : reject(error)));
  const launchArgs = createExtensionHostLaunchArgs(profile, projectRoot);
  launchArgs.push(`--remote-debugging-port=${rendererPort}`);
  await runTests({
    extensionDevelopmentPath: extensionRoot,
    extensionTestsPath: path.join(extensionRoot, 'client', 'out', 'extensionHost',
      'suite', 'previewPerformanceIndex.js'),
    vscodeExecutablePath: runtime.vscodeExecutablePath,
    version: runtime.version,
    launchArgs,
    extensionTestsEnv: {
      VBA_TOOLS_EXTENSION_HOST_TEST: '1',
      VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted',
      VBA_TOOLS_COMPANION_RESOLUTION_TEST: '1',
      VBA_TOOLS_PREVIEW_PERFORMANCE: '1',
      VBA_TOOLS_PREVIEW_PROJECT: projectRoot,
      VBA_TOOLS_PREVIEW_RESULT_DIRECTORY: resultDirectory,
      VBA_TOOLS_PREVIEW_RENDERER_PORT: String(rendererPort),
      VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY: timingDirectory
    }
  });
  process.stdout.write(await readFile(path.join(resultDirectory, 'report.json'), 'utf8'));
  // Keep the profile and scheduler records as reproducible measurement evidence.
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
