export const minimumSupportedVscodeVersion = '1.137.0';
const extensionHostTestVscodeVersion = '1.138.0';

export interface ExtensionHostRuntimeSelection {
  readonly version: string | undefined;
  readonly vscodeExecutablePath: string | undefined;
}

export function createExtensionHostLaunchArgs(
  userDataPath: string,
  workspacePath?: string
): string[] {
  const launchArgs = [
    '--disable-extensions',
    '--skip-welcome',
    '--skip-release-notes',
    `--user-data-dir=${userDataPath}`
  ];
  if (workspacePath !== undefined) {
    launchArgs.push(workspacePath);
  }

  return launchArgs;
}

export function createExtensionHostRuntimeSelection(
  environment: Readonly<Record<string, string | undefined>>
): ExtensionHostRuntimeSelection {
  const vscodeExecutablePath = environment.VSCODE_EXECUTABLE_PATH;
  return vscodeExecutablePath === undefined
    ? {
        version: extensionHostTestVscodeVersion,
        vscodeExecutablePath: undefined
      }
    : {
        version: undefined,
        vscodeExecutablePath
      };
}
