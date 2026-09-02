import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import * as path from 'node:path';

export interface RestrictedModeExtensionHostTestOptions {
  readonly vscodeExecutablePath: string;
  readonly extensionDevelopmentPath: string;
  readonly extensionTestsPath: string;
  readonly userDataPath: string;
  readonly workspacePath: string;
  readonly extensionTestsEnvironment: Readonly<Record<string, string | undefined>>;
}

export async function runRestrictedModeExtensionHostTests(
  options: RestrictedModeExtensionHostTestOptions
): Promise<void> {
  await prepareRestrictedModeProfile(options.userDataPath);
  const args = createRestrictedModeExtensionHostLaunchArgs(options);
  await new Promise<void>((resolve, reject) => {
    const child = spawn(options.vscodeExecutablePath, args, {
      env: {
        ...process.env,
        ...options.extensionTestsEnvironment
      },
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    });
    child.stdout.pipe(process.stdout);
    child.stderr.pipe(process.stderr);
    child.once('error', reject);
    child.once('exit', (code, signal) => {
      if (code === 0) {
        resolve();
        return;
      }
      reject(new Error(
        signal === null
          ? `Restricted Mode Extension Host tests failed with exit code ${String(code)}.`
          : `Restricted Mode Extension Host tests ended with signal ${signal}.`
      ));
    });
  });
}

export function createRestrictedModeExtensionHostLaunchArgs(
  options: Omit<RestrictedModeExtensionHostTestOptions, 'vscodeExecutablePath' |
    'extensionTestsEnvironment'>
): string[] {
  return [
    '--no-sandbox',
    '--disable-gpu-sandbox',
    '--disable-updates',
    '--disable-extensions',
    '--skip-welcome',
    '--skip-release-notes',
    `--user-data-dir=${options.userDataPath}`,
    `--extensions-dir=${path.join(options.userDataPath, 'extensions')}`,
    `--extensionTestsPath=${options.extensionTestsPath}`,
    `--extensionDevelopmentPath=${options.extensionDevelopmentPath}`,
    options.workspacePath
  ];
}

async function prepareRestrictedModeProfile(userDataPath: string): Promise<void> {
  const settingsDirectory = path.join(userDataPath, 'User');
  await mkdir(settingsDirectory, { recursive: true });
  await writeFile(
    path.join(settingsDirectory, 'settings.json'),
    JSON.stringify({
      'security.workspace.trust.enabled': true,
      'security.workspace.trust.startupPrompt': 'never',
      'security.workspace.trust.banner': 'never',
      'security.workspace.trust.untrustedFiles': 'open',
      'workbench.startupEditor': 'none'
    }, undefined, 2),
    'utf8'
  );
}
