import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';

import { ProcessResult, ProcessRunner } from './devtool';
import {
  loadDistributionManifest,
  resolveBundledRuntimePath
} from './distributionManifest';
import type { CommandCancellationToken } from './devtoolCommand';

import { admitVbaDebugAdapterCapabilities, CapabilityRejection } from './capabilityAdmission';
import {
  classifyAbnormalProcessError,
  formatAbnormalProcessTermination
} from './companionProcessTermination';
import type { RequiredVbaDebugAdapterContract, VbaDebugAdapterCapabilities } from './capabilityAdmission';
export type { RequiredVbaDebugAdapterContract, VbaDebugAdapterCapabilities } from './capabilityAdmission';

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
  readonly reportDiagnostic?: ((message: string) => void) | undefined;
  readonly isWorkspaceTrusted?: (() => boolean) | undefined;
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
  let priorAbnormalDiagnostic: string | undefined;
  let exhaustedAbnormalProbeError: VbaDebugAdapterCompatibilityError | undefined;

  try {
    let result: ProcessResult;
    let firstAbnormalExit: ReturnType<typeof classifyAbnormalProcessError>;
    for (let attempt = 1; ; attempt += 1) {
      try {
        if (attempt > 1) ensureWorkspaceTrusted(options.isWorkspaceTrusted);
        result = await runProcess(executablePath, ['capabilities', '--format', 'json']);
        break;
      } catch (error) {
        const abnormalExit = classifyAbnormalProcessError(error);
        if (options.cancellationToken?.isCancellationRequested) {
          if (abnormalExit?.kind === 'exit') {
            reportDiagnostic(options, formatAbnormalProcessTermination(
              'vba-debug-adapter', 'capabilities', executablePath, attempt, 2,
              abnormalExit, 'not-retried'
            ));
          }
          throw new VbaDebugAdapterCompatibilityError('vba-debug-adapter capabilities command was cancelled.');
        }
        if (abnormalExit === undefined) throw error;
        const diagnostic = formatAbnormalProcessTermination(
          'vba-debug-adapter', 'capabilities', executablePath, attempt, 2,
          abnormalExit, attempt === 1 ? 'retry-pending' : 'failed'
        );
        reportDiagnostic(options, diagnostic);
        if (attempt === 2) {
          exhaustedAbnormalProbeError = new VbaDebugAdapterCompatibilityError(diagnostic);
          throw exhaustedAbnormalProbeError;
        }
        firstAbnormalExit = abnormalExit;
        priorAbnormalDiagnostic = diagnostic;
        await new Promise<void>((resolve) => setTimeout(resolve, 50));
        if (options.cancellationToken?.isCancellationRequested) {
          throw new VbaDebugAdapterCompatibilityError('vba-debug-adapter capabilities command was cancelled.');
        }
        ensureWorkspaceTrusted(options.isWorkspaceTrusted);
      }
    }
    const admitted = admitVbaDebugAdapterCapabilities(result.stdout, requiredContract);
    if (!admitted.accepted) {
      if (firstAbnormalExit !== undefined) {
        reportDiagnostic(options, formatAbnormalProcessTermination(
          'vba-debug-adapter', 'capabilities', executablePath, 2, 2,
          firstAbnormalExit, 'failed'
        ));
      }
      throw new VbaDebugAdapterCompatibilityError(describeCapabilityRejection(executablePath, admitted.rejection));
    }
    if (firstAbnormalExit !== undefined) {
      reportDiagnostic(options, formatAbnormalProcessTermination(
        'vba-debug-adapter', 'capabilities', executablePath, 2, 2,
        firstAbnormalExit, 'recovered'
      ));
    }
    return Object.freeze({ executablePath, capabilities: admitted.facts });
  } catch (error) {
    if (priorAbnormalDiagnostic !== undefined
        && !options.cancellationToken?.isCancellationRequested
        && error !== exhaustedAbnormalProbeError) {
      throw new VbaDebugAdapterCompatibilityError(
        `${priorAbnormalDiagnostic} The retry failed: ${errorMessage(error)}`
      );
    }
    if (error instanceof VbaDebugAdapterCompatibilityError) {
      throw error;
    }
    throw new VbaDebugAdapterCompatibilityError(
      `vba-debug-adapter at '${executablePath}' is unavailable or incompatible: ${errorMessage(error)}`
    );
  }
}

function ensureWorkspaceTrusted(isWorkspaceTrusted: (() => boolean) | undefined): void {
  if (isWorkspaceTrusted?.() === false) {
    throw new VbaDebugAdapterCompatibilityError(
      'vba-debug-adapter capability inspection stopped because Workspace Trust was lost (Restricted Mode).'
    );
  }
}

function reportDiagnostic(options: CompatibleVbaDebugAdapterResolutionOptions, diagnostic: string): void {
  try {
    options.reportDiagnostic?.(diagnostic);
  } catch {
    // Output reporting must not change capability admission or process selection.
  }
}

function describeCapabilityRejection(executablePath: string, rejection: CapabilityRejection): string {
  const prefix = `vba-debug-adapter at '${executablePath}'`;
  const [field, name] = rejection.path;
  switch (rejection.code) {
    case 'InvalidJson': return 'vba-debug-adapter capabilities returned invalid JSON.';
    case 'DuplicateProperty': return `${prefix} reports duplicate capabilities property '${field}'.`;
    case 'InvalidConsumedValue': return `${prefix} reports an invalid capability value at '${rejection.path.join('.') || 'response'}'.`;
    case 'MissingCapability':
      if (field === 'featureVersions' && name !== undefined) {
        return `${prefix} reports incompatible feature ${name}; this extension requires ${rejection.expected}.`;
      }
      if ((field === 'commands' || field === 'transports') && name !== undefined) {
        return `${prefix} reports incompatible ${field}; this extension requires '${name}'.`;
      }
      if (name !== undefined) return `${prefix} reports incompatible command or vba-dev feature versions.`;
      return 'vba-debug-adapter capabilities omitted required contract fields.';
    case 'VersionMismatch':
      if (name === undefined) return `${prefix} reports ${field} ${rejection.actual}, but this extension requires ${rejection.expected}.`;
      if (field === 'featureVersions') return `${prefix} reports incompatible feature ${name}; this extension requires ${rejection.expected}.`;
      return `${prefix} reports incompatible command or vba-dev feature versions.`;
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
