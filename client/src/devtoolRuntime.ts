import {
  CompatibleVbaDev,
  CompanionExecutableResolver,
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevCapabilities,
  isReportedVbaDevResolutionFailure,
  resolveCompatibleVbaDev
} from './devtool';
import {
  CommandCancellationToken,
  StartVbaDevProcess,
  VbaToolsOutputChannel,
  runVbaDevCommand
} from './devtoolCommand';
import { WorkbookBackedProjectCandidate } from './projectDiscovery';
import {
  CommandPaletteDocumentTarget,
  CommandPaletteTarget,
  CommandPaletteTargetScope
} from './commandPaletteTarget';
import {
  VbaDevDiagnosticReporterLike,
  combineVbaDevDiagnosticOutput,
  projectDiagnosticScope
} from './toolDiagnostics';

export const VbaDevCooperativeCancellationGraceMilliseconds = 10_000;

export interface VbaDevInvocationRuntimeOptions {
  extensionRoot: string;
  configuredDevToolPath?: string | undefined;
  vbaDevResolver?: CompanionExecutableResolver | undefined;
  capabilitiesProcess?: ProcessRunner | undefined;
  startProcess?: StartVbaDevProcess | undefined;
  outputChannel: VbaToolsOutputChannel;
  revealOutput?: boolean | undefined;
  diagnosticReporter?: VbaDevDiagnosticReporterLike | undefined;
  cancellationToken?: CommandCancellationToken | undefined;
  forceKillAfterCancellationMilliseconds?: number | undefined;
  reportCancellationProgress?: ((message: string) => void) | undefined;
  requiredContract?: RequiredVbaDevContract | undefined;
}

export interface VbaDevCommandRuntimeOptions extends VbaDevInvocationRuntimeOptions {
  activeFilePath?: string | undefined;
  workspaceRoots: readonly string[];
  fileExists: (filePath: string) => Promise<boolean>;
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  chooseProject: (
    candidates: readonly WorkbookBackedProjectCandidate[]
  ) => Promise<WorkbookBackedProjectCandidate | undefined>;
  resolveCommandPaletteTarget: (
    scope: CommandPaletteTargetScope
  ) => Promise<CommandPaletteTarget | undefined>;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
}

export interface VbaDevProjectCommandContext {
  target: CommandPaletteTarget;
  project: WorkbookBackedProjectCandidate;
  document?: CommandPaletteDocumentTarget | undefined;
  targetReported?: boolean | undefined;
  executablePath: string;
  capabilities: VbaDevCapabilities;
}

export interface VbaDevProjectCommandInvocation {
  projectRoot: string;
  documentName?: string | undefined;
  argsBeforeProject: readonly string[];
  argsAfterProject?: readonly string[] | undefined;
  refreshDiagnostics?: boolean | undefined;
  reportTarget?: boolean | undefined;
}

export interface VbaDevProjectCommandRunResult {
  projectRoot: string;
  executablePath: string;
  stdout: string;
  stderr: string;
  exitCode: number;
  cancelled: boolean;
  cancellationRequested: boolean;
  cancellationRequestDelivered: boolean | undefined;
  cancellationRequestError: string | undefined;
}

export interface VbaDevCommandRunResult {
  executablePath: string;
  stdout: string;
  stderr: string;
  exitCode: number;
  cancelled: boolean;
  cancellationRequested: boolean;
  cancellationRequestDelivered: boolean | undefined;
  cancellationRequestError: string | undefined;
}

export async function resolveVbaDevProjectCommandContext(
  options: VbaDevCommandRuntimeOptions,
  targetScope: CommandPaletteTargetScope = 'project'
): Promise<VbaDevProjectCommandContext | undefined> {
  const target = await options.resolveCommandPaletteTarget(targetScope);
  if (target === undefined) {
    return undefined;
  }

  reportCommandPaletteTargetSelection(options, target);

  const devtool = await resolveInvocationVbaDev(options);
  if (devtool === undefined) {
    return undefined;
  }

  return {
    target,
    project: target.project,
    document: target.document,
    targetReported: true,
    executablePath: devtool.executablePath,
    capabilities: devtool.capabilities
  };
}

export async function runVbaDevProjectCommand(
  options: VbaDevCommandRuntimeOptions,
  argsBeforeProject: readonly string[],
  argsAfterProject: readonly string[] = [],
  targetScope: CommandPaletteTargetScope = 'project'
): Promise<VbaDevProjectCommandRunResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options, targetScope);
  if (!context) {
    return undefined;
  }

  return runResolvedVbaDevProjectCommand(options, context, argsBeforeProject, argsAfterProject);
}

export async function runResolvedVbaDevProjectCommand(
  options: VbaDevCommandRuntimeOptions,
  context: VbaDevProjectCommandContext,
  argsBeforeProject: readonly string[],
  argsAfterProject: readonly string[] = [],
  refreshDiagnostics = true
): Promise<VbaDevProjectCommandRunResult> {
  return runResolvedVbaDevProjectCommandInvocation(
    options,
    context.executablePath,
    {
      projectRoot: context.project.projectRoot,
      documentName: context.document?.name,
      argsBeforeProject,
      argsAfterProject,
      refreshDiagnostics,
      reportTarget: context.targetReported !== true
    },
    context.capabilities
  );
}

export async function runVbaDevProjectCommandInvocation(
  options: VbaDevInvocationRuntimeOptions,
  invocation: VbaDevProjectCommandInvocation
): Promise<VbaDevProjectCommandRunResult | undefined> {
  const devtool = await resolveInvocationVbaDev(options);
  if (devtool === undefined) {
    return undefined;
  }

  return runResolvedVbaDevProjectCommandInvocation(
    options,
    devtool.executablePath,
    invocation,
    devtool.capabilities
  );
}

export async function runVbaDevCommandInvocation(
  options: VbaDevInvocationRuntimeOptions,
  args: readonly string[]
): Promise<VbaDevCommandRunResult | undefined> {
  const devtool = await resolveInvocationVbaDev(options);
  if (devtool === undefined) {
    return undefined;
  }

  return runResolvedVbaDevCommandInvocation(options, devtool, args);
}

export async function runResolvedVbaDevCommandInvocation(
  options: VbaDevInvocationRuntimeOptions,
  resolution: CompatibleVbaDev,
  args: readonly string[]
): Promise<VbaDevCommandRunResult> {
  const result = await runVbaDevCommand({
    executablePath: resolution.executablePath,
    args: withStdinCancellationTransport(args, resolution.capabilities),
    outputChannel: options.outputChannel,
    revealOutput: options.revealOutput,
    reportCancellationProgress: options.reportCancellationProgress,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess,
    cancellationTransport: supportsStdinCancellation(resolution.capabilities)
      ? 'stdin-v1'
      : undefined,
    forceKillAfterCancellationMilliseconds: forceKillDelayForManagedCommand(
      args,
      resolution.capabilities,
      options.forceKillAfterCancellationMilliseconds
    )
  });

  return {
    executablePath: resolution.executablePath,
    stdout: result.stdout,
    stderr: result.stderr,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    cancellationRequested: result.cancellationRequested,
    cancellationRequestDelivered: result.cancellationRequestDelivered,
    cancellationRequestError: result.cancellationRequestError
  };
}

export async function runResolvedVbaDevProjectCommandInvocation(
  options: VbaDevInvocationRuntimeOptions,
  executablePath: string,
  invocation: VbaDevProjectCommandInvocation,
  capabilities: VbaDevCapabilities
): Promise<VbaDevProjectCommandRunResult> {
  const args = [
    ...invocation.argsBeforeProject,
    '--project',
    invocation.projectRoot,
    ...(invocation.documentName === undefined
      ? []
      : ['--document', invocation.documentName]),
    ...(invocation.argsAfterProject ?? [])
  ];
  if (invocation.reportTarget === true) {
    reportTargetReceipt(
      options,
      invocation.projectRoot,
      invocation.documentName
    );
  }
  const result = await runVbaDevCommand({
    executablePath,
    args: withStdinCancellationTransport(args, capabilities),
    outputChannel: options.outputChannel,
    revealOutput: options.revealOutput,
    reportCancellationProgress: options.reportCancellationProgress,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess,
    cancellationTransport: supportsStdinCancellation(capabilities)
      ? 'stdin-v1'
      : undefined,
    forceKillAfterCancellationMilliseconds: forceKillDelayForManagedCommand(
      args,
      capabilities,
      options.forceKillAfterCancellationMilliseconds
    )
  });

  if (invocation.refreshDiagnostics !== false) {
    options.diagnosticReporter?.refresh(
      projectDiagnosticScope(invocation.projectRoot),
      combineVbaDevDiagnosticOutput(result.stdout, result.stderr)
    );
  }

  return {
    projectRoot: invocation.projectRoot,
    executablePath,
    stdout: result.stdout,
    stderr: result.stderr,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    cancellationRequested: result.cancellationRequested,
    cancellationRequestDelivered: result.cancellationRequestDelivered,
    cancellationRequestError: result.cancellationRequestError
  };
}

export function reportCommandPaletteTargetSelection(
  options: Pick<VbaDevInvocationRuntimeOptions, 'outputChannel' | 'reportCancellationProgress'>,
  target: CommandPaletteTarget
): void {
  reportTargetReceipt(
    options,
    `${target.project.projectName} (${target.project.projectRoot})`,
    target.document?.name
  );
}

function reportTargetReceipt(
  options: Pick<VbaDevInvocationRuntimeOptions, 'outputChannel' | 'reportCancellationProgress'>,
  project: string,
  documentName: string | undefined
): void {
  options.outputChannel.appendLine('Command Palette target:');
  options.outputChannel.appendLine(`  Project: ${project}`);
  if (documentName !== undefined) {
    options.outputChannel.appendLine(`  Document: ${documentName}`);
  }
  options.reportCancellationProgress?.(
    documentName === undefined
      ? `Project: ${project}`
      : `Project: ${project}; Document: ${documentName}`
  );
}

function supportsStdinCancellation(capabilities: VbaDevCapabilities): boolean {
  return capabilities.featureVersions?.['invocation.stdinCancellation'] === '1.0';
}

function forceKillDelayForManagedCommand(
  args: readonly string[],
  capabilities: VbaDevCapabilities,
  override: number | undefined
): number | undefined {
  if (
    !supportsStdinCancellation(capabilities) ||
    isCallerForceKillExemptCommand(args)
  ) {
    return undefined;
  }
  return override ?? VbaDevCooperativeCancellationGraceMilliseconds;
}

function isCallerForceKillExemptCommand(args: readonly string[]): boolean {
  return (args[0] === 'new' && args[1] === 'excel') ||
    (args[0] === 'host-event' && args[1] === 'list') ||
    (args[0] === 'common-module' && (args[1] === 'add' || args[1] === 'update'));
}

function withStdinCancellationTransport(
  args: readonly string[],
  capabilities: VbaDevCapabilities
): readonly string[] {
  if (!supportsStdinCancellation(capabilities)) {
    return args;
  }

  const invocationArgs: string[] = [];
  for (let index = 0; index < args.length; index++) {
    if (
      args[index] === '--cancellation-transport' &&
      args[index + 1] === 'stdin-v1'
    ) {
      index += 1;
      continue;
    }
    invocationArgs.push(args[index]!);
  }

  return [...invocationArgs, '--cancellation-transport', 'stdin-v1'];
}

async function resolveInvocationVbaDev(
  options: VbaDevInvocationRuntimeOptions
): Promise<{
  executablePath: string;
  capabilities: VbaDevCapabilities;
} | undefined> {
  try {
    if (options.vbaDevResolver !== undefined) {
      const resolution = await options.vbaDevResolver.resolve();
      return resolution;
    }

    const devtool = await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: options.configuredDevToolPath,
      runProcess: options.capabilitiesProcess,
      requiredContract: options.requiredContract
    });
    return devtool;
  } catch (error) {
    if (isReportedVbaDevResolutionFailure(error)) {
      return undefined;
    }
    throw error;
  }
}
