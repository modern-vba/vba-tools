import {
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
import {
  WorkbookBackedProjectCandidate,
  discoverWorkbookBackedProject
} from './projectDiscovery';
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
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
}

export interface VbaDevProjectCommandContext {
  project: WorkbookBackedProjectCandidate;
  executablePath: string;
  capabilities: VbaDevCapabilities;
}

export interface VbaDevProjectCommandInvocation {
  projectRoot: string;
  argsBeforeProject: readonly string[];
  argsAfterProject?: readonly string[] | undefined;
  refreshDiagnostics?: boolean | undefined;
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
  options: VbaDevCommandRuntimeOptions
): Promise<VbaDevProjectCommandContext | undefined> {
  const project = await discoverWorkbookBackedProject(options);
  if (!project) {
    await options.showErrorMessage('VBA Tools could not find a workbook-backed vba-project.json.');
    return undefined;
  }

  const devtool = await resolveInvocationVbaDev(options);
  if (devtool === undefined) {
    return undefined;
  }

  return {
    project,
    executablePath: devtool.executablePath,
    capabilities: devtool.capabilities
  };
}

export async function runVbaDevProjectCommand(
  options: VbaDevCommandRuntimeOptions,
  argsBeforeProject: readonly string[],
  argsAfterProject: readonly string[] = []
): Promise<VbaDevProjectCommandRunResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options);
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
      argsBeforeProject,
      argsAfterProject,
      refreshDiagnostics
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

  const result = await runVbaDevCommand({
    executablePath: devtool.executablePath,
    args: withStdinCancellationTransport(args, devtool.capabilities),
    outputChannel: options.outputChannel,
    reportCancellationProgress: options.reportCancellationProgress,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess,
    cancellationTransport: supportsStdinCancellation(devtool.capabilities)
      ? 'stdin-v1'
      : undefined,
    forceKillAfterCancellationMilliseconds: forceKillDelayForManagedCommand(
      args,
      devtool.capabilities,
      options.forceKillAfterCancellationMilliseconds
    )
  });

  return {
    executablePath: devtool.executablePath,
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
    ...(invocation.argsAfterProject ?? [])
  ];
  const result = await runVbaDevCommand({
    executablePath,
    args: withStdinCancellationTransport(args, capabilities),
    outputChannel: options.outputChannel,
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
    isProtectedTransactionCommand(args)
  ) {
    return undefined;
  }
  return override ?? VbaDevCooperativeCancellationGraceMilliseconds;
}

function isProtectedTransactionCommand(args: readonly string[]): boolean {
  return (args[0] === 'new' && args[1] === 'excel') ||
    (args[0] === 'common-module' && (args[1] === 'add' || args[1] === 'update'));
}

function withStdinCancellationTransport(
  args: readonly string[],
  capabilities: VbaDevCapabilities
): readonly string[] {
  return supportsStdinCancellation(capabilities)
    ? [...args, '--cancellation-transport', 'stdin-v1']
    : args;
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
