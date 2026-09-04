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

export type ProcessRunner = (
  file: string,
  args: readonly string[],
  signal?: AbortSignal
) => Promise<ProcessResult>;

export interface CompatibleVbaDevResolutionOptions extends VbaDevPathResolutionOptions {
  requiredContract?: RequiredVbaDevContract | undefined;
  runProcess?: ProcessRunner | undefined;
}

export interface CompatibleVbaDev {
  executablePath: string;
  capabilities: VbaDevCapabilities;
}

export const configuredVbaDevFallbackMessage =
  'The configured vba-dev executable is unavailable or incompatible. VBA Tools is using its bundled vba-dev for this window.';
export const noCompatibleVbaDevMessage =
  'VBA Tools could not find a compatible vba-dev executable.';

export const VbaDevResolutionNoticeAction = {
  OpenSettings: 'Open Settings',
  ShowOutput: 'Show Output'
} as const;

export type VbaDevResolutionNoticeAction = typeof VbaDevResolutionNoticeAction[
  keyof typeof VbaDevResolutionNoticeAction
];

export interface VbaDevResolutionNotice {
  readonly severity: 'warning' | 'error';
  readonly message: string;
  readonly actions: readonly VbaDevResolutionNoticeAction[];
}

export interface VbaDevResolutionFailure {
  readonly source: 'configured' | 'bundled';
  readonly executablePath: string;
  readonly message: string;
}

export interface VbaDevResolutionLog {
  readonly outcome: 'resolved' | 'failed';
  readonly configuredPath?: string | undefined;
  readonly bundledPath: string;
  readonly effectivePath?: string | undefined;
  readonly source?: 'configured' | 'bundled' | undefined;
  readonly requiredContract: RequiredVbaDevContract;
  readonly failures: readonly VbaDevResolutionFailure[];
}

export function formatVbaDevResolutionLog(log: VbaDevResolutionLog): readonly string[] {
  const lines = [`vba-dev companion resolution: ${log.outcome}`];
  if (log.configuredPath !== undefined) {
    lines.push(`  Configured candidate: ${log.configuredPath}`);
    for (const failure of log.failures.filter((candidate) => candidate.source === 'configured')) {
      lines.push(`  Configured failure: ${failure.message}`);
    }
  }
  lines.push(`  Bundled candidate: ${log.bundledPath}`);
  for (const failure of log.failures.filter((candidate) => candidate.source === 'bundled')) {
    lines.push(`  Bundled failure: ${failure.message}`);
  }
  if (log.effectivePath !== undefined) {
    lines.push(`  Effective executable: ${log.effectivePath}`);
  }
  lines.push(`  Required contract: ${JSON.stringify(log.requiredContract)}`);
  return lines;
}

export interface CompanionExecutableResolution extends CompatibleVbaDev {
  readonly configuredPath?: string | undefined;
  readonly bundledPath: string;
  readonly source: 'configured' | 'bundled';
  readonly configuredFailure?: string | undefined;
}

export interface CompanionExecutableResolver {
  resolve(): Promise<CompanionExecutableResolution>;
}

export interface CompanionExecutableResolutionSubscription {
  dispose(): void;
}

export interface VbaDevSessionResolverOptions extends CompatibleVbaDevResolutionOptions {
  configuredPathProvider?: (() => string | undefined) | undefined;
  reportNotice?: ((notice: VbaDevResolutionNotice) => void) | undefined;
  reportLog?: ((log: VbaDevResolutionLog) => void) | undefined;
}

export const requiredVbaDevContractFileName = 'vba-dev-contract.json';

export class VbaDevCompatibilityError extends VbaDevOutputContractError {
  public constructor(
    message: string,
    public readonly resolutionNoticeReported = false
  ) {
    super(message);
    this.name = 'VbaDevCompatibilityError';
  }
}

export function isReportedVbaDevResolutionFailure(
  error: unknown
): error is VbaDevCompatibilityError {
  return error instanceof VbaDevCompatibilityError
    && error.resolutionNoticeReported;
}

export class VbaDevSessionResolver implements CompanionExecutableResolver {
  private resolved: CompanionExecutableResolution | undefined;
  private inFlight: Promise<CompanionExecutableResolution> | undefined;
  private inFlightCancellation: AbortController | undefined;
  private readonly resolutionListeners = new Set<(
    resolution: CompanionExecutableResolution
  ) => void>();
  private configuredFallbackNoticeReported = false;
  private resolutionGeneration = 0;

  public constructor(private readonly options: VbaDevSessionResolverOptions) {}

  public resolve(): Promise<CompanionExecutableResolution> {
    if (this.resolved !== undefined) {
      return Promise.resolve(this.resolved);
    }
    if (this.inFlight !== undefined) {
      return this.inFlight;
    }

    const generation = this.resolutionGeneration;
    const cancellation = new AbortController();
    const attempt = this.resolveUncached(generation, cancellation.signal);
    this.inFlight = attempt;
    this.inFlightCancellation = cancellation;
    void attempt.then(
      (resolution) => {
        if (this.inFlight === attempt) {
          this.resolved = resolution;
          this.inFlight = undefined;
          this.inFlightCancellation = undefined;
          this.notifyResolutionListeners(resolution);
        }
      },
      () => {
        if (this.inFlight === attempt) {
          this.inFlight = undefined;
          this.inFlightCancellation = undefined;
        }
      }
    );
    return attempt;
  }

  public onDidResolve(
    listener: (resolution: CompanionExecutableResolution) => void
  ): CompanionExecutableResolutionSubscription {
    this.resolutionListeners.add(listener);
    return {
      dispose: () => {
        this.resolutionListeners.delete(listener);
      }
    };
  }

  public invalidate(): void {
    this.resolutionGeneration += 1;
    this.inFlightCancellation?.abort();
    this.resolved = undefined;
    this.inFlight = undefined;
    this.inFlightCancellation = undefined;
  }

  public async readActiveWindowsCodePage(): Promise<number> {
    const resolution = await this.resolve();
    const requiredContract = this.options.requiredContract
      ?? loadRequiredVbaDevContract(this.options.extensionRoot);
    const runProcess = this.options.runProcess ?? runProcessWithExecFile;
    const inspected = await inspectCompatibleVbaDev(
      resolution.executablePath,
      requiredContract,
      runProcess
    );
    const codePage = inspected.capabilities.activeWindowsCodePage;
    if (codePage === undefined) {
      throw new VbaDevCompatibilityError(
        `VbaDev at '${resolution.executablePath}' did not report the active Windows code page.`
      );
    }

    return codePage;
  }

  private async resolveUncached(
    generation: number,
    signal: AbortSignal
  ): Promise<CompanionExecutableResolution> {
    const configuredCandidate = this.options.configuredPathProvider?.()
      ?? this.options.configuredPath;
    const configuredPath = configuredCandidate?.trim().length
      ? configuredCandidate
      : undefined;
    const requiredContract = this.options.requiredContract
      ?? loadRequiredVbaDevContract(this.options.extensionRoot);
    const runProcess = this.options.runProcess ?? runProcessWithExecFile;
    const bundledPath = path.resolve(resolveVbaDevPath({
      extensionRoot: this.options.extensionRoot
    }));
    const failures: VbaDevResolutionFailure[] = [];

    if (configuredPath !== undefined) {
      try {
        const configured = await inspectCompatibleVbaDev(
          configuredPath,
          requiredContract,
          runProcess,
          signal
        );
        const resolution = Object.freeze<CompanionExecutableResolution>({
          ...configured,
          configuredPath,
          bundledPath,
          source: 'configured'
        });
        this.reportLogForGeneration(generation, {
          outcome: 'resolved',
          configuredPath,
          bundledPath,
          effectivePath: configured.executablePath,
          source: 'configured',
          requiredContract,
          failures
        });
        return resolution;
      } catch (error) {
        failures.push({
          source: 'configured',
          executablePath: configuredPath,
          message: errorMessage(error)
        });
      }
    }

    try {
      const bundled = await inspectCompatibleVbaDev(
        bundledPath,
        requiredContract,
        runProcess,
        signal
      );
      const configuredFailure = failures.find((failure) => failure.source === 'configured')?.message;
      const resolution = Object.freeze<CompanionExecutableResolution>({
        ...bundled,
        configuredPath,
        bundledPath,
        source: 'bundled',
        configuredFailure
      });
      this.reportLogForGeneration(generation, {
        outcome: 'resolved',
        configuredPath,
        bundledPath,
        effectivePath: bundled.executablePath,
        source: 'bundled',
        requiredContract,
        failures: [...failures]
      });
      if (configuredPath !== undefined && !this.configuredFallbackNoticeReported) {
        this.configuredFallbackNoticeReported = this.reportNoticeForGeneration(generation, {
          severity: 'warning',
          message: configuredVbaDevFallbackMessage,
          actions: [
            VbaDevResolutionNoticeAction.OpenSettings,
            VbaDevResolutionNoticeAction.ShowOutput
          ]
        });
      }
      return resolution;
    } catch (error) {
      failures.push({
        source: 'bundled',
        executablePath: bundledPath,
        message: errorMessage(error)
      });
      this.reportLogForGeneration(generation, {
        outcome: 'failed',
        configuredPath,
        bundledPath,
        requiredContract,
        failures: [...failures]
      });
      const resolutionNoticeReported = this.reportNoticeForGeneration(generation, {
        severity: 'error',
        message: noCompatibleVbaDevMessage,
        actions: [
          VbaDevResolutionNoticeAction.OpenSettings,
          VbaDevResolutionNoticeAction.ShowOutput
        ]
      });
      throw new VbaDevCompatibilityError(
        `${noCompatibleVbaDevMessage} ${failures
          .map((failure) => `${failure.source} '${failure.executablePath}': ${failure.message}`)
          .join(' ')}`,
        resolutionNoticeReported
      );
    }
  }

  private reportLog(log: VbaDevResolutionLog): void {
    try {
      this.options.reportLog?.(log);
    } catch {
      // Reporting must not change executable compatibility or selection.
    }
  }

  private notifyResolutionListeners(
    resolution: CompanionExecutableResolution
  ): void {
    for (const listener of this.resolutionListeners) {
      try {
        listener(resolution);
      } catch {
        // Observers must not change executable compatibility or selection.
      }
    }
  }

  private reportLogForGeneration(generation: number, log: VbaDevResolutionLog): void {
    if (generation === this.resolutionGeneration) {
      this.reportLog(log);
    }
  }

  private reportNotice(notice: VbaDevResolutionNotice): boolean {
    if (this.options.reportNotice === undefined) {
      return false;
    }
    try {
      this.options.reportNotice(notice);
      return true;
    } catch {
      return false;
    }
  }

  private reportNoticeForGeneration(
    generation: number,
    notice: VbaDevResolutionNotice
  ): boolean {
    return generation === this.resolutionGeneration
      ? this.reportNotice(notice)
      : false;
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
  return inspectCompatibleVbaDev(executablePath, requiredContract, runProcess);
}

async function inspectCompatibleVbaDev(
  executablePath: string,
  requiredContract: RequiredVbaDevContract,
  runProcess: ProcessRunner,
  signal?: AbortSignal
): Promise<CompatibleVbaDev> {
  if (!path.isAbsolute(executablePath)) {
    throw new VbaDevCompatibilityError(
      `The configured VbaDev path '${executablePath}' must be an absolute path.`
    );
  }
  const result = await runProcess(
    executablePath,
    ['capabilities', '--format', 'json'],
    signal
  );
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

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function runProcessWithExecFile(
  file: string,
  args: readonly string[],
  signal?: AbortSignal
): Promise<ProcessResult> {
  return new Promise((resolve, reject) => {
    execFile(file, [...args], { windowsHide: true, signal }, (error, stdout, stderr) => {
      if (error) {
        reject(error);
        return;
      }

      resolve({ stdout, stderr });
    });
  });
}
