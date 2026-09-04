import { execFile } from 'node:child_process';
import {
  mkdir,
  mkdtemp,
  readFile,
  rm,
  stat,
  writeFile
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import { promisify } from 'node:util';
import { runTests } from '@vscode/test-electron';
import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection
} from './configuration';

const execFileAsync = promisify(execFile);
const ActiveSourceEnvironment = 'VBA_TOOLS_COMMON_MODULES_ACTIVE_SOURCE';
const ResultPathEnvironment = 'VBA_TOOLS_SEMANTIC_READINESS_RESULT';

async function main(): Promise<void> {
  if (process.platform !== 'win32') {
    throw new Error('The semantic-readiness Release measurement is Windows-only.');
  }

  const extensionDevelopmentPath = path.resolve(__dirname, '..', '..', '..');
  const activeSourcePath = requiredAbsolutePath(ActiveSourceEnvironment);
  const activeSourceStat = await stat(activeSourcePath);
  if (!activeSourceStat.isFile()) {
    throw new Error(`${ActiveSourceEnvironment} is not a file: ${activeSourcePath}`);
  }
  if (!['.bas', '.cls', '.frm'].includes(path.extname(activeSourcePath).toLowerCase())) {
    throw new Error(
      `${ActiveSourceEnvironment} must identify a .bas, .cls, or .frm source.`
    );
  }

  const resultPath = path.resolve(
    process.env[ResultPathEnvironment]
      ?? path.join(
        extensionDevelopmentPath,
        'test-results',
        'semantic-readiness',
        'windows-release.json'
      )
  );
  const runtime = createExtensionHostRuntimeSelection(process.env);
  const userDataPath = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-semantic-readiness-user-data-'
  ));
  const timingRoot = await mkdtemp(path.join(
    tmpdir(),
    'vba-tools-semantic-readiness-timing-'
  ));
  const timingDirectory = path.join(timingRoot, 'language-server');
  const extensionTestsPath = path.resolve(
    __dirname,
    'suite',
    'semanticReadinessPerformanceIndex.js'
  );
  const repository = await captureRepositoryRevision(activeSourcePath);

  try {
    await prepareMeasurementProfile(userDataPath);
    const launchArgs = createExtensionHostLaunchArgs(userDataPath);
    launchArgs.push(activeSourcePath);
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      vscodeExecutablePath: runtime.vscodeExecutablePath,
      version: runtime.version,
      launchArgs,
      extensionTestsEnv: {
        VBA_TOOLS_EXTENSION_HOST_TEST: '1',
        VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE: 'controlled-trusted',
        VBA_TOOLS_COMPANION_RESOLUTION_TEST: '1',
        VBA_TOOLS_COMMON_MODULES_ACTIVE_SOURCE: activeSourcePath,
        VBA_TOOLS_COMMON_MODULES_CORPUS_REVISION: repository.commit,
        VBA_TOOLS_COMMON_MODULES_CORPUS_DIRTY: String(repository.dirty),
        VBA_TOOLS_SEMANTIC_READINESS_TIMING_DIRECTORY: timingDirectory,
        VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY: timingDirectory,
        VBA_TOOLS_SEMANTIC_READINESS_RESULT: resultPath
      }
    });
    const report = await readFile(resultPath, 'utf8');
    process.stdout.write(report);
  } finally {
    await rm(userDataPath, { recursive: true, force: true });
    await rm(timingRoot, { recursive: true, force: true });
  }
}

async function prepareMeasurementProfile(userDataPath: string): Promise<void> {
  const settingsDirectory = path.join(userDataPath, 'User');
  await mkdir(settingsDirectory, { recursive: true });
  await writeFile(
    path.join(settingsDirectory, 'settings.json'),
    `${JSON.stringify({
      'files.associations': {
        '*.bas': 'plaintext',
        '*.cls': 'plaintext',
        '*.frm': 'plaintext'
      },
      'security.workspace.trust.enabled': false,
      'workbench.startupEditor': 'none'
    }, undefined, 2)}\n`,
    'utf8'
  );
}

async function captureRepositoryRevision(activeSourcePath: string): Promise<{
  readonly commit: string;
  readonly dirty: boolean;
}> {
  const workingDirectory = path.dirname(activeSourcePath);
  const repositoryRoot = (await execFileAsync(
    'git',
    ['-C', workingDirectory, 'rev-parse', '--show-toplevel'],
    { encoding: 'utf8', windowsHide: true }
  )).stdout.trim();
  const commit = (await execFileAsync(
    'git',
    ['-C', repositoryRoot, 'rev-parse', 'HEAD'],
    { encoding: 'utf8', windowsHide: true }
  )).stdout.trim();
  const status = (await execFileAsync(
    'git',
    ['-C', repositoryRoot, 'status', '--porcelain', '--untracked-files=no'],
    { encoding: 'utf8', windowsHide: true }
  )).stdout;
  return {
    commit,
    dirty: status.trim().length > 0
  };
}

function requiredAbsolutePath(name: string): string {
  const value = process.env[name];
  if (value === undefined || value.trim().length === 0) {
    throw new Error(`${name} must be provided.`);
  }
  if (!path.isAbsolute(value)) {
    throw new Error(`${name} must be an absolute path.`);
  }
  return path.normalize(value);
}

void main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});
