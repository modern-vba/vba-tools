import * as path from 'node:path';

import {
  ProcessRunner,
  RequiredVbaDevContract,
  resolveCompatibleVbaDev
} from './devtool';

export interface VbaDevTerminalLike {
  show(preserveFocus?: boolean): void;
}

export interface VbaDevTerminalOptionsLike {
  name: string;
  cwd?: string | undefined;
  env: { [key: string]: string | null | undefined };
}

export interface VbaDevTerminalCommandOptions {
  extensionRoot: string;
  configuredDevToolPath?: string | undefined;
  activeFilePath?: string | undefined;
  workspaceRoots: readonly string[];
  processEnv?: NodeJS.ProcessEnv | undefined;
  capabilitiesProcess?: ProcessRunner | undefined;
  requiredContract?: RequiredVbaDevContract | undefined;
  chooseWorkspaceRoot: (workspaceRoots: readonly string[]) => Promise<string | undefined>;
  createTerminal: (options: VbaDevTerminalOptionsLike) => VbaDevTerminalLike;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
}

export interface VbaDevTerminalCommandResult {
  executablePath: string;
  executableDirectory: string;
  cwd?: string | undefined;
}

interface TerminalCwdSelection {
  cwd?: string | undefined;
  cancelled: boolean;
}

export async function openVbaDevTerminal(
  options: VbaDevTerminalCommandOptions
): Promise<VbaDevTerminalCommandResult | undefined> {
  const cwdSelection = await selectTerminalCwd({
    activeFilePath: options.activeFilePath,
    workspaceRoots: options.workspaceRoots,
    chooseWorkspaceRoot: options.chooseWorkspaceRoot
  });
  if (cwdSelection.cancelled) {
    return undefined;
  }

  let executablePath: string;
  try {
    executablePath = (await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: options.configuredDevToolPath,
      runProcess: options.capabilitiesProcess,
      requiredContract: options.requiredContract
    })).executablePath;
  } catch (error) {
    await options.showErrorMessage(`Could not open vba-dev terminal. ${error instanceof Error ? error.message : String(error)}`);
    return undefined;
  }

  const executableDirectory = path.dirname(executablePath);
  const terminal = options.createTerminal({
    name: 'vba-dev',
    cwd: cwdSelection.cwd,
    env: createVbaDevTerminalEnvironment(executableDirectory, options.processEnv ?? process.env)
  });
  terminal.show();

  return {
    executablePath,
    executableDirectory,
    cwd: cwdSelection.cwd
  };
}

export async function selectTerminalCwd(options: {
  activeFilePath?: string | undefined;
  workspaceRoots: readonly string[];
  chooseWorkspaceRoot: (workspaceRoots: readonly string[]) => Promise<string | undefined>;
}): Promise<TerminalCwdSelection> {
  const activeWorkspaceRoot = options.activeFilePath
    ? findContainingWorkspaceRoot(options.activeFilePath, options.workspaceRoots)
    : undefined;
  if (activeWorkspaceRoot) {
    return { cwd: activeWorkspaceRoot, cancelled: false };
  }

  if (options.workspaceRoots.length === 0) {
    return { cancelled: false };
  }

  if (options.workspaceRoots.length === 1) {
    return { cwd: options.workspaceRoots[0], cancelled: false };
  }

  const selected = await options.chooseWorkspaceRoot(options.workspaceRoots);
  return selected
    ? { cwd: selected, cancelled: false }
    : { cancelled: true };
}

export function createVbaDevTerminalEnvironment(
  executableDirectory: string,
  processEnv: NodeJS.ProcessEnv
): { [key: string]: string } {
  const pathVariableName = getPathEnvironmentVariableName(processEnv);
  const existingPath = processEnv[pathVariableName] ?? processEnv.Path ?? processEnv.PATH ?? '';
  return {
    [pathVariableName]: existingPath.length > 0
      ? `${executableDirectory}${path.delimiter}${existingPath}`
      : executableDirectory
  };
}

function findContainingWorkspaceRoot(
  activeFilePath: string,
  workspaceRoots: readonly string[]
): string | undefined {
  const matchingRoots = workspaceRoots.filter((workspaceRoot) =>
    isPathInsideOrEqual(workspaceRoot, activeFilePath)
  );
  matchingRoots.sort((left, right) => right.length - left.length);
  return matchingRoots[0];
}

function isPathInsideOrEqual(rootPath: string, candidatePath: string): boolean {
  const normalizedRoot = normalizePathForComparison(path.resolve(rootPath));
  const normalizedCandidate = normalizePathForComparison(path.resolve(candidatePath));
  const relative = path.relative(normalizedRoot, normalizedCandidate);
  return relative === '' || (
    relative.length > 0 &&
    !relative.startsWith('..') &&
    !path.isAbsolute(relative)
  );
}

function normalizePathForComparison(value: string): string {
  return process.platform === 'win32' ? value.toLowerCase() : value;
}

function getPathEnvironmentVariableName(processEnv: NodeJS.ProcessEnv): string {
  if (Object.hasOwn(processEnv, 'Path')) {
    return 'Path';
  }

  if (Object.hasOwn(processEnv, 'PATH')) {
    return 'PATH';
  }

  return process.platform === 'win32' ? 'Path' : 'PATH';
}
