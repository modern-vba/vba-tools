import * as path from 'node:path';
import { execFile } from 'node:child_process';

export interface VbaDevToolPathResolutionOptions {
  extensionRoot: string;
  configuredPath?: string | undefined;
}

export interface VbaDevToolCommandCapability {
  outputSchemaVersion: string;
}

export interface VbaDevToolCapabilities {
  toolVersion: string;
  contractVersion: string;
  commands: Record<string, VbaDevToolCommandCapability>;
}

export interface RequiredVbaDevToolContract {
  contractVersion: string;
  commandSchemaVersions: Record<string, string>;
}

export interface ProcessResult {
  stdout: string;
  stderr: string;
}

export type ProcessRunner = (file: string, args: readonly string[]) => Promise<ProcessResult>;

export interface CompatibleVbaDevToolResolutionOptions extends VbaDevToolPathResolutionOptions {
  requiredContract?: RequiredVbaDevToolContract | undefined;
  runProcess?: ProcessRunner | undefined;
}

export interface CompatibleVbaDevTool {
  executablePath: string;
  capabilities: VbaDevToolCapabilities;
}

export const defaultRequiredVbaDevToolContract: RequiredVbaDevToolContract = {
  contractVersion: '1.0',
  commandSchemaVersions: {
    build: '1.0',
    'common-module add': '1.0',
    'common-module list': '1.0',
    'common-module update': '1.0',
    doctor: '1.0',
    export: '1.0',
    publish: '1.0',
    'reference add': '1.0',
    'reference list': '1.0',
    'reference remove': '1.0',
    test: '1.0'
  }
};

export class VbaDevToolCompatibilityError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevToolCompatibilityError';
  }
}

export function resolveVbaDevToolPath(options: VbaDevToolPathResolutionOptions): string {
  if (options.configuredPath && options.configuredPath.trim().length > 0) {
    return options.configuredPath;
  }

  return path.join(options.extensionRoot, 'bin', 'vba-devtool', 'win-x64', 'vba-devtool.exe');
}

export async function resolveCompatibleVbaDevTool(
  options: CompatibleVbaDevToolResolutionOptions
): Promise<CompatibleVbaDevTool> {
  const executablePath = resolveVbaDevToolPath(options);
  const requiredContract = options.requiredContract ?? defaultRequiredVbaDevToolContract;
  const runProcess = options.runProcess ?? runProcessWithExecFile;
  const result = await runProcess(executablePath, ['capabilities', '--format', 'json']);
  const capabilities = parseCapabilities(executablePath, result.stdout);

  validateCapabilities(executablePath, capabilities, requiredContract);

  return {
    executablePath,
    capabilities
  };
}

function parseCapabilities(executablePath: string, stdout: string): VbaDevToolCapabilities {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new VbaDevToolCompatibilityError(
      `VbaDevTool at '${executablePath}' returned invalid capabilities JSON: ${String(error)}`
    );
  }

  if (!isCapabilities(parsed)) {
    throw new VbaDevToolCompatibilityError(
      `VbaDevTool at '${executablePath}' returned capabilities JSON without toolVersion, contractVersion, and commands.`
    );
  }

  return parsed;
}

function validateCapabilities(
  executablePath: string,
  capabilities: VbaDevToolCapabilities,
  requiredContract: RequiredVbaDevToolContract
): void {
  if (capabilities.contractVersion !== requiredContract.contractVersion) {
    throw new VbaDevToolCompatibilityError(
      `VbaDevTool at '${executablePath}' reports contractVersion ${capabilities.contractVersion}, but this extension requires ${requiredContract.contractVersion}.`
    );
  }

  for (const [commandName, requiredSchemaVersion] of Object.entries(requiredContract.commandSchemaVersions)) {
    const command = capabilities.commands[commandName];
    if (!command) {
      throw new VbaDevToolCompatibilityError(
        `VbaDevTool at '${executablePath}' does not report required command '${commandName}'.`
      );
    }

    if (command.outputSchemaVersion !== requiredSchemaVersion) {
      throw new VbaDevToolCompatibilityError(
        `VbaDevTool at '${executablePath}' reports ${commandName} outputSchemaVersion ${command.outputSchemaVersion}, but this extension requires ${requiredSchemaVersion}.`
      );
    }
  }
}

function isCapabilities(value: unknown): value is VbaDevToolCapabilities {
  if (!isRecord(value)) {
    return false;
  }

  return (
    typeof value.toolVersion === 'string' &&
    typeof value.contractVersion === 'string' &&
    isCommandCapabilities(value.commands)
  );
}

function isCommandCapabilities(value: unknown): value is Record<string, VbaDevToolCommandCapability> {
  if (!isRecord(value)) {
    return false;
  }

  return Object.values(value).every((command) => (
    isRecord(command) &&
    typeof command.outputSchemaVersion === 'string'
  ));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function runProcessWithExecFile(file: string, args: readonly string[]): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    execFile(file, [...args], { windowsHide: true }, (error, stdout, stderr) => {
      if (error) {
        reject(error);
        return;
      }

      resolve({ stdout, stderr });
    });
  });
}
