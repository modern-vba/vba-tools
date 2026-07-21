import * as path from 'node:path';
import { execFile } from 'node:child_process';
import {
  loadDistributionManifest,
  resolveBundledRuntimePath
} from './distributionManifest';
import {
  RequiredVbaDevContract,
  VbaDevCapabilities,
  VbaDevOutputContractError,
  loadRequiredVbaDevContractFile,
  parseVbaDevCapabilities,
  validateVbaDevCapabilities
} from './vbaDevOutputContract';

export type {
  RequiredVbaDevContract,
  VbaDevCapabilities
} from './vbaDevOutputContract';

export interface VbaDevPathResolutionOptions {
  extensionRoot: string;
  configuredPath?: string | undefined;
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

export class VbaDevCompatibilityError extends VbaDevOutputContractError {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevCompatibilityError';
  }
}

export function resolveVbaDevPath(options: VbaDevPathResolutionOptions): string {
  if (options.configuredPath && options.configuredPath.trim().length > 0) {
    if (!path.isAbsolute(options.configuredPath)) {
      throw new VbaDevCompatibilityError(
        `The configured VbaDev path '${options.configuredPath}' must be an absolute path.`
      );
    }

    return options.configuredPath;
  }

  return resolveBundledRuntimePath(options.extensionRoot, 'vbaDev');
}

export function loadRequiredVbaDevContract(extensionRoot: string): RequiredVbaDevContract {
  try {
    const manifest = loadDistributionManifest(extensionRoot);
    return loadRequiredVbaDevContractFile(
      path.join(extensionRoot, manifest.runtimes.vbaDev.contractPath ?? requiredVbaDevContractFileName)
    );
  } catch (error) {
    if (error instanceof VbaDevCompatibilityError) {
      throw error;
    }

    throw new VbaDevCompatibilityError(error instanceof Error ? error.message : String(error));
  }
}

export async function resolveCompatibleVbaDev(
  options: CompatibleVbaDevResolutionOptions
): Promise<CompatibleVbaDev> {
  const executablePath = resolveVbaDevPath(options);
  const requiredContract = options.requiredContract ?? loadRequiredVbaDevContract(options.extensionRoot);
  const runProcess = options.runProcess ?? runProcessWithExecFile;
  const result = await runProcess(executablePath, ['capabilities', '--format', 'json']);
  let capabilities: VbaDevCapabilities;
  try {
    capabilities = parseVbaDevCapabilities(executablePath, result.stdout);
    validateVbaDevCapabilities(executablePath, capabilities, requiredContract);
  } catch (error) {
    throw new VbaDevCompatibilityError(error instanceof Error ? error.message : String(error));
  }

  return {
    executablePath,
    capabilities
  };
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
