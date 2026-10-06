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
  loadRequiredVbaDevContractFile
} from './vbaDevOutputContract';
import { admitVbaDevCapabilities, CapabilityRejection } from './capabilityAdmission';
import {
  classifyAbnormalProcessError,
  formatAbnormalProcessTermination
} from './companionProcessTermination';

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
  signal?: AbortSignal | undefined;
  reportDiagnostic?: ((message: string) => void) | undefined;
  isWorkspaceTrusted?: (() => boolean) | undefined;
}

export interface CompatibleVbaDev {
  executablePath: string;
  capabilities: VbaDevCapabilities;
}

export const configuredVbaDevFallbackMessage =
  'The configured vba-dev executable is unavailable or incompatible. VBA Tools is using its bundled vba-dev for this window.';
export const noCompatibleVbaDevMessage =
  'VBA Tools could not find a compatible vba-dev executable.';
export const abnormalVbaDevResolutionMessage =
  'VBA Tools could not verify vba-dev capabilities because a companion process terminated unexpectedly. See VBA Tools Output.';
export const abnormalConfiguredVbaDevFallbackMessage =
  'The configured vba-dev terminated unexpectedly during capability inspection. VBA Tools is using its bundled vba-dev for this window.';

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
  readonly kind?: 'abnormal-termination' | undefined;
}

export interface VbaDevResolutionLog {
  readonly outcome: 'resolved' | 'failed';
  readonly configuredPath?: string | undefined;
  readonly bundledPath: string;
  readonly effectivePath?: string | undefined;
  readonly source?: 'configured' | 'bundled' | undefined;
  readonly requiredContract: RequiredVbaDevContract;
  readonly failures: readonly VbaDevResolutionFailure[];
  readonly probeDiagnostics?: readonly string[] | undefined;
}

export function formatVbaDevResolutionLog(log: VbaDevResolutionLog): readonly string[] {
  const lines = [`vba-dev companion resolution: ${log.outcome}`];
  lines.push(...(log.probeDiagnostics ?? []));
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

class VbaDevAbnormalCapabilityProbeError extends VbaDevCompatibilityError {
  public constructor(message: string) {
    super(message);
    this.name = 'VbaDevAbnormalCapabilityProbeError';
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
      runProcess,
      undefined,
      undefined,
      this.options.reportDiagnostic,
      this.options.isWorkspaceTrusted
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
    signal = this.options.signal === undefined ? signal : AbortSignal.any([signal, this.options.signal]);
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
    const probeDiagnostics: string[] = [];
    const reportedProbeDiagnosticIndexes = new Set<number>();
    const reportCurrentProbeDiagnostic = (message: string): void => {
      if (generation === this.resolutionGeneration
          && safelyReportDiagnostic(this.options.reportDiagnostic, message)) {
        reportedProbeDiagnosticIndexes.add(probeDiagnostics.length - 1);
      }
    };
    const unreportedProbeDiagnostics = (): string[] => probeDiagnostics.filter(
      (_diagnostic, index) => !reportedProbeDiagnosticIndexes.has(index)
    );
    const flushInterruptedProbeDiagnostics = (): void => {
      if (generation !== this.resolutionGeneration) return;
      for (let index = 0; index < probeDiagnostics.length; index += 1) {
        if (!reportedProbeDiagnosticIndexes.has(index)
            && safelyReportDiagnostic(this.options.reportDiagnostic, probeDiagnostics[index]!)) {
          reportedProbeDiagnosticIndexes.add(index);
        }
      }
    };

    if (configuredPath !== undefined) {
      const priorProbeDiagnostics = probeDiagnostics.length;
      try {
        const configured = await inspectCompatibleVbaDev(
          configuredPath,
          requiredContract,
          runProcess,
          signal,
          probeDiagnostics,
          reportCurrentProbeDiagnostic,
          this.options.isWorkspaceTrusted
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
          failures,
          ...(unreportedProbeDiagnostics().length > 0
            ? { probeDiagnostics: unreportedProbeDiagnostics() } : {})
        });
        return resolution;
      } catch (error) {
        if (this.options.signal?.aborted) {
          flushInterruptedProbeDiagnostics();
          throw new VbaDevCompatibilityError('VbaDev capabilities command was cancelled.');
        }
        try {
          ensureWorkspaceTrusted(this.options.isWorkspaceTrusted);
        } catch (trustError) {
          flushInterruptedProbeDiagnostics();
          throw trustError;
        }
        failures.push({
          source: 'configured',
          executablePath: configuredPath,
          message: errorMessage(error),
          ...(error instanceof VbaDevAbnormalCapabilityProbeError
            || probeDiagnostics.length > priorProbeDiagnostics
            ? { kind: 'abnormal-termination' as const } : {})
        });
      }
    }

    const priorBundledProbeDiagnostics = probeDiagnostics.length;
    try {
      const bundled = await inspectCompatibleVbaDev(
        bundledPath,
        requiredContract,
        runProcess,
        signal,
        probeDiagnostics,
        reportCurrentProbeDiagnostic,
        this.options.isWorkspaceTrusted
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
        failures: [...failures],
        ...(unreportedProbeDiagnostics().length > 0
          ? { probeDiagnostics: unreportedProbeDiagnostics() } : {})
      });
      if (configuredPath !== undefined && !this.configuredFallbackNoticeReported) {
        this.configuredFallbackNoticeReported = this.reportNoticeForGeneration(generation, {
          severity: 'warning',
          message: failures.some((failure) => failure.kind === 'abnormal-termination')
            ? abnormalConfiguredVbaDevFallbackMessage
            : configuredVbaDevFallbackMessage,
          actions: [
            VbaDevResolutionNoticeAction.OpenSettings,
            VbaDevResolutionNoticeAction.ShowOutput
          ]
        });
      }
      return resolution;
    } catch (error) {
      if (this.options.signal?.aborted) {
        flushInterruptedProbeDiagnostics();
        throw new VbaDevCompatibilityError('VbaDev capabilities command was cancelled.');
      }
      try {
        ensureWorkspaceTrusted(this.options.isWorkspaceTrusted);
      } catch (trustError) {
        flushInterruptedProbeDiagnostics();
        throw trustError;
      }
      failures.push({
        source: 'bundled',
        executablePath: bundledPath,
        message: errorMessage(error),
        ...(error instanceof VbaDevAbnormalCapabilityProbeError
          || probeDiagnostics.length > priorBundledProbeDiagnostics
          ? { kind: 'abnormal-termination' as const } : {})
      });
      this.reportLogForGeneration(generation, {
        outcome: 'failed',
        configuredPath,
        bundledPath,
        requiredContract,
        failures: [...failures],
        ...(unreportedProbeDiagnostics().length > 0
          ? { probeDiagnostics: unreportedProbeDiagnostics() } : {})
      });
      const resolutionNoticeReported = this.reportNoticeForGeneration(generation, {
        severity: 'error',
        message: failures.some((failure) => failure.kind === 'abnormal-termination')
          ? abnormalVbaDevResolutionMessage
          : noCompatibleVbaDevMessage,
        actions: [
          VbaDevResolutionNoticeAction.OpenSettings,
          VbaDevResolutionNoticeAction.ShowOutput
        ]
      });
      const noticeMessage = failures.some((failure) => failure.kind === 'abnormal-termination')
        ? abnormalVbaDevResolutionMessage : noCompatibleVbaDevMessage;
      const failureDetails = failures
        .map((failure) => `${failure.source} '${failure.executablePath}': ${failure.message}`)
        .join(' ');
      throw new VbaDevCompatibilityError(
        [noticeMessage, ...probeDiagnostics, failureDetails].filter(Boolean).join(' '),
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
  return inspectCompatibleVbaDev(
    executablePath,
    requiredContract,
    runProcess,
    options.signal,
    undefined,
    options.reportDiagnostic,
    options.isWorkspaceTrusted
  );
}

async function inspectCompatibleVbaDev(
  executablePath: string,
  requiredContract: RequiredVbaDevContract,
  runProcess: ProcessRunner,
  signal?: AbortSignal,
  probeDiagnostics?: string[],
  reportDiagnostic?: (message: string) => void,
  isWorkspaceTrusted?: () => boolean
): Promise<CompatibleVbaDev> {
  if (!path.isAbsolute(executablePath)) {
    throw new VbaDevCompatibilityError(
      `The configured VbaDev path '${executablePath}' must be an absolute path.`
    );
  }
  let result: ProcessResult;
  let firstAbnormalExit: ReturnType<typeof classifyAbnormalProcessError>;
  for (let attempt = 1; ; attempt += 1) {
    try {
      if (attempt > 1) ensureWorkspaceTrusted(isWorkspaceTrusted);
      result = await runProcess(
        executablePath,
        ['capabilities', '--format', 'json'],
        signal
      );
      break;
    } catch (error) {
      const abnormalExit = classifyAbnormalProcessError(error);
      if (signal?.aborted) {
        if (abnormalExit?.kind === 'exit') {
          const diagnostic = formatAbnormalProcessTermination(
            'vba-dev', 'capabilities', executablePath, attempt, 2,
            abnormalExit, 'not-retried'
          );
          probeDiagnostics?.push(diagnostic);
          safelyReportDiagnostic(reportDiagnostic, diagnostic);
        }
        throw error;
      }
      if (abnormalExit === undefined) throw error;
      const diagnostic = formatAbnormalProcessTermination(
        'vba-dev', 'capabilities', executablePath, attempt, 2,
        abnormalExit, attempt === 1 ? 'retry-pending' : 'failed'
      );
      probeDiagnostics?.push(diagnostic);
      safelyReportDiagnostic(reportDiagnostic, diagnostic);
      if (attempt === 2) throw new VbaDevAbnormalCapabilityProbeError(diagnostic);
      firstAbnormalExit = abnormalExit;
      await new Promise<void>((resolve) => setTimeout(resolve, 50));
      if (signal?.aborted) {
        throw new VbaDevCompatibilityError('VbaDev capabilities command was cancelled.');
      }
      ensureWorkspaceTrusted(isWorkspaceTrusted);
    }
  }
  const admitted = admitVbaDevCapabilities(result.stdout, requiredContract);
  if (!admitted.accepted) {
    if (firstAbnormalExit !== undefined) {
      const diagnostic = formatAbnormalProcessTermination(
        'vba-dev', 'capabilities', executablePath, 2, 2,
        firstAbnormalExit, 'failed'
      );
      probeDiagnostics?.push(diagnostic);
      safelyReportDiagnostic(reportDiagnostic, diagnostic);
    }
    throw new VbaDevCompatibilityError(describeCapabilityRejection(executablePath, admitted.rejection));
  }
  if (firstAbnormalExit !== undefined) {
    const diagnostic = formatAbnormalProcessTermination(
      'vba-dev', 'capabilities', executablePath, 2, 2,
      firstAbnormalExit, 'recovered'
    );
    probeDiagnostics?.push(diagnostic);
    safelyReportDiagnostic(reportDiagnostic, diagnostic);
  }

  return {
    executablePath,
    capabilities: admitted.facts
  };
}

function ensureWorkspaceTrusted(isWorkspaceTrusted: (() => boolean) | undefined): void {
  if (isWorkspaceTrusted?.() === false) {
    throw new VbaDevCompatibilityError(
      'VbaDev capability inspection stopped because Workspace Trust was lost (Restricted Mode).'
    );
  }
}

function safelyReportDiagnostic(
  reportDiagnostic: ((message: string) => void) | undefined,
  diagnostic: string
): boolean {
  if (reportDiagnostic === undefined) return false;
  try {
    reportDiagnostic(diagnostic);
    return true;
  } catch {
    // Diagnostic output must not change capability admission or process selection.
    return false;
  }
}

function describeCapabilityRejection(executablePath: string, rejection: CapabilityRejection): string {
  const prefix = "VbaDev at '" + executablePath + "'";
  const [field, name] = rejection.path;
  switch (rejection.code) {
    case 'InvalidJson': return prefix + ' returned invalid capabilities JSON.';
    case 'DuplicateProperty': return prefix + " returned duplicate capabilities property '" + field + "'.";
    case 'InvalidConsumedValue': return prefix + " returned an invalid capabilities value at '" + (rejection.path.join('.') || 'response') + "'.";
    case 'MissingCapability':
      if (field === 'featureVersions') return prefix + " does not report required feature '" + name + "'.";
      if (field === 'commands' && name !== undefined) return prefix + " does not report required command '" + name + "'.";
      if (field === 'activeWindowsCodePage') return prefix + " does not report the active Windows code page required by feature 'sourceSnapshot.activeWindowsCodePage'.";
      return prefix + ' returned capabilities JSON without toolVersion, contractVersion, and commands.';
    case 'VersionMismatch':
      if (field === 'contractVersion') return prefix + ' reports contractVersion ' + rejection.actual + ', but this extension requires ' + rejection.expected + '.';
      if (field === 'featureVersions') return prefix + ' reports feature ' + name + ' version ' + rejection.actual + ', but this extension requires ' + rejection.expected + '.';
      return prefix + ' reports ' + name + ' outputSchemaVersion ' + rejection.actual + ', but this extension requires ' + rejection.expected + '.';
  }
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
