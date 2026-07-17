export const minimumSupportedVscodeVersion = '1.125.0';

export interface ExtensionHostRuntimeSelection {
  readonly version: string | undefined;
  readonly vscodeExecutablePath: string | undefined;
}

export function createExtensionHostLaunchArgs(userDataPath: string): string[] {
  return [
    '--disable-extensions',
    '--skip-welcome',
    '--skip-release-notes',
    `--user-data-dir=${userDataPath}`
  ];
}

export function createExtensionHostRuntimeSelection(
  environment: Readonly<Record<string, string | undefined>>
): ExtensionHostRuntimeSelection {
  const vscodeExecutablePath = environment.VSCODE_EXECUTABLE_PATH;
  return vscodeExecutablePath === undefined
    ? {
        version: minimumSupportedVscodeVersion,
        vscodeExecutablePath: undefined
      }
    : {
        version: undefined,
        vscodeExecutablePath
      };
}
