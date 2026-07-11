import * as path from 'node:path';
import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';

export interface VbaDevPathResolutionOptions {
  extensionRoot: string;
  configuredPath?: string | undefined;
}

export interface VbaDevCommandCapability {
  outputSchemaVersion: string;
}

export interface VbaDevCapabilities {
  toolVersion: string;
  contractVersion: string;
  commands: Record<string, VbaDevCommandCapability>;
}

export interface RequiredVbaDevContract {
  contractVersion: string;
  commandSchemaVersions: Record<string, string>;
}

export interface ProcessResult {
  stdout: string;
  stderr: string;
}

export type ProcessRunner = (file: string, args: readonly string[]) => Promise<ProcessResult>;

export interface CompatibleVbaDevResolutionOptions extends VbaDevPathResolutionOptions {
  requiredContract?: RequiredVbaDevContract | undefined;
  runProcess?: ProcessRunner | undefined;
}

export interface CompatibleVbaDev {
  executablePath: string;
  capabilities: VbaDevCapabilities;
}

export const requiredVbaDevContractFileName = 'vba-dev-contract.json';

export class VbaDevCompatibilityError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevCompatibilityError';
  }
}

export function resolveVbaDevPath(options: VbaDevPathResolutionOptions): string {
  if (options.configuredPath && options.configuredPath.trim().length > 0) {
    return options.configuredPath;
  }

  return path.join(options.extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
}

export function loadRequiredVbaDevContract(extensionRoot: string): RequiredVbaDevContract {
  const contractPath = path.join(extensionRoot, requiredVbaDevContractFileName);
  let parsed: unknown;
  try {
    parsed = JSON.parse(readFileSync(contractPath, 'utf8')) as unknown;
  } catch (error) {
    throw new VbaDevCompatibilityError(
      `VbaDev required contract could not be read from '${contractPath}': ${String(error)}`
    );
  }

  if (!isRequiredVbaDevContract(parsed)) {
    throw new VbaDevCompatibilityError(
      `VbaDev required contract at '${contractPath}' must include contractVersion and commandSchemaVersions.`
    );
  }

  return parsed;
}

export async function resolveCompatibleVbaDev(
  options: CompatibleVbaDevResolutionOptions
): Promise<CompatibleVbaDev> {
  const executablePath = resolveVbaDevPath(options);
  const requiredContract = options.requiredContract ?? loadRequiredVbaDevContract(options.extensionRoot);
  const runProcess = options.runProcess ?? runProcessWithExecFile;
  const result = await runProcess(executablePath, ['capabilities', '--format', 'json']);
  const capabilities = parseCapabilities(executablePath, result.stdout);

  validateCapabilities(executablePath, capabilities, requiredContract);

  return {
    executablePath,
    capabilities
  };
}

function parseCapabilities(executablePath: string, stdout: string): VbaDevCapabilities {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new VbaDevCompatibilityError(
      `VbaDev at '${executablePath}' returned invalid capabilities JSON: ${String(error)}`
    );
  }

  if (!isCapabilities(parsed)) {
    throw new VbaDevCompatibilityError(
      `VbaDev at '${executablePath}' returned capabilities JSON without toolVersion, contractVersion, and commands.`
    );
  }

  return parsed;
}

function validateCapabilities(
  executablePath: string,
  capabilities: VbaDevCapabilities,
  requiredContract: RequiredVbaDevContract
): void {
  if (capabilities.contractVersion !== requiredContract.contractVersion) {
    throw new VbaDevCompatibilityError(
      `VbaDev at '${executablePath}' reports contractVersion ${capabilities.contractVersion}, but this extension requires ${requiredContract.contractVersion}.`
    );
  }

  for (const [commandName, requiredSchemaVersion] of Object.entries(requiredContract.commandSchemaVersions)) {
    const command = capabilities.commands[commandName];
    if (!command) {
      throw new VbaDevCompatibilityError(
        `VbaDev at '${executablePath}' does not report required command '${commandName}'.`
      );
    }

    if (command.outputSchemaVersion !== requiredSchemaVersion) {
      throw new VbaDevCompatibilityError(
        `VbaDev at '${executablePath}' reports ${commandName} outputSchemaVersion ${command.outputSchemaVersion}, but this extension requires ${requiredSchemaVersion}.`
      );
    }
  }
}

function isCapabilities(value: unknown): value is VbaDevCapabilities {
  if (!isRecord(value)) {
    return false;
  }

  return (
    typeof value.toolVersion === 'string' &&
    typeof value.contractVersion === 'string' &&
    isCommandCapabilities(value.commands)
  );
}

function isCommandCapabilities(value: unknown): value is Record<string, VbaDevCommandCapability> {
  if (!isRecord(value)) {
    return false;
  }

  return Object.values(value).every((command) => (
    isRecord(command) &&
    typeof command.outputSchemaVersion === 'string'
  ));
}

function isRequiredVbaDevContract(value: unknown): value is RequiredVbaDevContract {
  if (!isRecord(value) || typeof value.contractVersion !== 'string' || !isRecord(value.commandSchemaVersions)) {
    return false;
  }

  return Object.values(value.commandSchemaVersions).every((schemaVersion) => typeof schemaVersion === 'string');
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
