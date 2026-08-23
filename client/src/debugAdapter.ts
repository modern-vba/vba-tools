import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';

import { ProcessResult, ProcessRunner } from './devtool';
import {
  loadDistributionManifest,
  resolveBundledRuntimePath
} from './distributionManifest';
import type { CommandCancellationToken } from './devtoolCommand';

export interface RequiredVbaDebugAdapterContract {
  readonly contractVersion: string;
  readonly protocolVersion: string;
  readonly transports: readonly string[];
  readonly sessionIdFormat: string;
  readonly commands: readonly string[];
  readonly commandSchemaVersions: Readonly<Record<string, string>>;
  readonly featureVersions: Readonly<Record<string, string>>;
  readonly requiredVbaDevFeatureVersions: Readonly<Record<string, string>>;
}

export interface VbaDebugAdapterCapabilities extends RequiredVbaDebugAdapterContract {
  readonly toolVersion: string;
}

export interface CompatibleVbaDebugAdapter {
  readonly executablePath: string;
  readonly capabilities: VbaDebugAdapterCapabilities;
}

export interface VbaDebugAdapterResolver {
  resolve(): Promise<CompatibleVbaDebugAdapter>;
}

export interface CompatibleVbaDebugAdapterResolutionOptions {
  readonly extensionRoot: string;
  readonly configuredPath?: string | undefined;
  readonly requiredContract?: RequiredVbaDebugAdapterContract | undefined;
  readonly runProcess?: ProcessRunner | undefined;
  readonly cancellationToken?: CommandCancellationToken | undefined;
  readonly startCapabilitiesProcess?: StartDebugAdapterProcess | undefined;
}

export interface StartedDebugAdapterProcess {
  kill(): void;
}

export type CompleteDebugAdapterProcess = (
  error: Error | null,
  stdout: string,
  stderr: string
) => void;

export type StartDebugAdapterProcess = (
  file: string,
  args: readonly string[],
  complete: CompleteDebugAdapterProcess
) => StartedDebugAdapterProcess;

export class VbaDebugAdapterCompatibilityError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDebugAdapterCompatibilityError';
  }
}

export function resolveVbaDebugAdapterPath(
  extensionRoot: string,
  configuredPath?: string | undefined
): string {
  if (configuredPath !== undefined && configuredPath.trim().length > 0) {
    if (!path.isAbsolute(configuredPath)) {
      throw new VbaDebugAdapterCompatibilityError(
        `The configured vba-debug-adapter path '${configuredPath}' must be absolute.`
      );
    }
    return configuredPath;
  }

  return path.resolve(resolveBundledRuntimePath(extensionRoot, 'vbaDebugAdapter'));
}

export function loadRequiredVbaDebugAdapterContract(
  extensionRoot: string
): RequiredVbaDebugAdapterContract {
  const manifest = loadDistributionManifest(extensionRoot);
  const contractPath = manifest.runtimes.vbaDebugAdapter.contractPath;
  if (contractPath === undefined) {
    throw new VbaDebugAdapterCompatibilityError(
      'The distribution manifest does not declare the vba-debug-adapter contract.'
    );
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(readFileSync(path.join(extensionRoot, contractPath), 'utf8')) as unknown;
  } catch (error) {
    throw new VbaDebugAdapterCompatibilityError(
      `The required vba-debug-adapter contract could not be read: ${String(error)}`
    );
  }

  if (!isRequiredContract(parsed)) {
    throw new VbaDebugAdapterCompatibilityError(
      'The required vba-debug-adapter contract is invalid.'
    );
  }
  return parsed;
}

export async function resolveCompatibleVbaDebugAdapter(
  options: CompatibleVbaDebugAdapterResolutionOptions
): Promise<CompatibleVbaDebugAdapter> {
  const executablePath = resolveVbaDebugAdapterPath(
    options.extensionRoot,
    options.configuredPath
  );
  const requiredContract = options.requiredContract
    ?? loadRequiredVbaDebugAdapterContract(options.extensionRoot);
  const runProcess = options.runProcess ?? ((file, args) => runDebugAdapterProcess(
    file,
    args,
    options.cancellationToken,
    options.startCapabilitiesProcess
  ));

  try {
    const result = await runProcess(executablePath, ['capabilities', '--format', 'json']);
    const capabilities = parseCapabilities(result.stdout);
    validateCapabilities(capabilities, requiredContract, executablePath);
    return Object.freeze({ executablePath, capabilities });
  } catch (error) {
    if (error instanceof VbaDebugAdapterCompatibilityError) {
      throw error;
    }
    throw new VbaDebugAdapterCompatibilityError(
      `vba-debug-adapter at '${executablePath}' is unavailable or incompatible: ${errorMessage(error)}`
    );
  }
}

function parseCapabilities(stdout: string): VbaDebugAdapterCapabilities {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout) as unknown;
  } catch (error) {
    throw new VbaDebugAdapterCompatibilityError(
      `vba-debug-adapter capabilities returned invalid JSON: ${String(error)}`
    );
  }

  if (!isCapabilities(parsed)) {
    throw new VbaDebugAdapterCompatibilityError(
      'vba-debug-adapter capabilities omitted required contract fields.'
    );
  }
  return parsed;
}

function validateCapabilities(
  actual: VbaDebugAdapterCapabilities,
  required: RequiredVbaDebugAdapterContract,
  executablePath: string
): void {
  for (const [name, actualValue, requiredValue] of [
    ['contractVersion', actual.contractVersion, required.contractVersion],
    ['protocolVersion', actual.protocolVersion, required.protocolVersion],
    ['sessionIdFormat', actual.sessionIdFormat, required.sessionIdFormat]
  ] as const) {
    if (actualValue !== requiredValue) {
      throw new VbaDebugAdapterCompatibilityError(
        `vba-debug-adapter at '${executablePath}' reports ${name} ${actualValue}, ` +
        `but this extension requires ${requiredValue}.`
      );
    }
  }

  for (const [name, actualValues, requiredValues] of [
    ['transports', actual.transports, required.transports],
    ['commands', actual.commands, required.commands]
  ] as const) {
    if (!equalStringArrays(actualValues, requiredValues)) {
      throw new VbaDebugAdapterCompatibilityError(
        `vba-debug-adapter at '${executablePath}' reports incompatible ${name}.`
      );
    }
  }

  for (const [name, requiredVersion] of Object.entries(required.featureVersions)) {
    if (actual.featureVersions?.[name] !== requiredVersion) {
      throw new VbaDebugAdapterCompatibilityError(
        `vba-debug-adapter at '${executablePath}' reports incompatible feature ${name}; ` +
        `this extension requires ${requiredVersion}.`
      );
    }
  }

  if (
    !equalStringRecords(actual.commandSchemaVersions, required.commandSchemaVersions) ||
    !equalStringRecords(
      actual.requiredVbaDevFeatureVersions,
      required.requiredVbaDevFeatureVersions
    )
  ) {
    throw new VbaDebugAdapterCompatibilityError(
      `vba-debug-adapter at '${executablePath}' reports incompatible command or vba-dev feature versions.`
    );
  }
}

function isRequiredContract(value: unknown): value is RequiredVbaDebugAdapterContract {
  return isRecord(value) &&
    typeof value.contractVersion === 'string' &&
    typeof value.protocolVersion === 'string' &&
    isStringArray(value.transports) &&
    typeof value.sessionIdFormat === 'string' &&
    isStringArray(value.commands) &&
    isStringRecord(value.commandSchemaVersions) &&
    isStringRecord(value.featureVersions) &&
    isStringRecord(value.requiredVbaDevFeatureVersions);
}

function isCapabilities(value: unknown): value is VbaDebugAdapterCapabilities {
  return isRequiredContract(value) &&
    typeof (value as unknown as Record<string, unknown>).toolVersion === 'string';
}

function equalStringArrays(
  actual: readonly string[],
  expected: readonly string[]
): boolean {
  return actual.length === expected.length &&
    actual.every((value, index) => value === expected[index]);
}

function equalStringRecords(
  actual: Readonly<Record<string, string>>,
  expected: Readonly<Record<string, string>>
): boolean {
  const actualEntries = Object.entries(actual);
  const expectedEntries = Object.entries(expected);
  return actualEntries.length === expectedEntries.length &&
    expectedEntries.every(([name, version]) => actual[name] === version);
}

function isStringArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function isStringRecord(value: unknown): value is Readonly<Record<string, string>> {
  return isRecord(value) && Object.values(value).every((item) => typeof item === 'string');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function runDebugAdapterProcess(
  file: string,
  args: readonly string[],
  cancellationToken?: CommandCancellationToken | undefined,
  startProcess: StartDebugAdapterProcess = startNodeDebugAdapterProcess
): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    let settled = false;
    let process: StartedDebugAdapterProcess | undefined;
    let cancellationSubscription: { dispose(): void } | undefined;
    const settle = (complete: () => void): void => {
      if (settled) {
        return;
      }
      settled = true;
      cancellationSubscription?.dispose();
      complete();
    };
    const cancel = (): void => {
      settle(() => {
        process?.kill();
        reject(new Error('vba-debug-adapter capabilities command cancelled.'));
      });
    };

    if (cancellationToken?.isCancellationRequested) {
      cancel();
      return;
    }

    process = startProcess(file, args, (error, stdout, stderr) => {
      if (error) {
        settle(() => reject(error));
        return;
      }
      settle(() => resolve({ stdout, stderr }));
    });
    if (settled) {
      return;
    }

    cancellationSubscription = cancellationToken?.onCancellationRequested(cancel);
    if (settled) {
      cancellationSubscription?.dispose();
      return;
    }
    if (cancellationToken?.isCancellationRequested) {
      cancel();
    }
  });
}

function startNodeDebugAdapterProcess(
  file: string,
  args: readonly string[],
  complete: CompleteDebugAdapterProcess
): StartedDebugAdapterProcess {
  const child = execFile(file, [...args], { windowsHide: true }, (error, stdout, stderr) => {
    complete(error, stdout, stderr);
  });
  return {
    kill: () => {
      child.kill();
    }
  };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
