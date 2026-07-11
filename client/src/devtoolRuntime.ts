import {
  ProcessRunner,
  RequiredVbaDevContract,
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

export interface VbaDevCommandRuntimeOptions {
  extensionRoot: string;
  configuredDevToolPath?: string | undefined;
  activeFilePath?: string | undefined;
  workspaceRoots: readonly string[];
  fileExists: (filePath: string) => Promise<boolean>;
  findProjectManifests: (workspaceRoots: readonly string[]) => Promise<readonly string[]>;
  chooseProject: (
    candidates: readonly WorkbookBackedProjectCandidate[]
  ) => Promise<WorkbookBackedProjectCandidate | undefined>;
  capabilitiesProcess?: ProcessRunner | undefined;
  startProcess?: StartVbaDevProcess | undefined;
  outputChannel: VbaToolsOutputChannel;
  diagnosticReporter?: VbaDevDiagnosticReporterLike | undefined;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
  cancellationToken?: CommandCancellationToken | undefined;
  requiredContract?: RequiredVbaDevContract | undefined;
}

export interface VbaDevProjectCommandContext {
  project: WorkbookBackedProjectCandidate;
  executablePath: string;
}

export interface VbaDevProjectCommandRunResult {
  projectRoot: string;
  executablePath: string;
  stdout: string;
  stderr: string;
  exitCode: number;
  cancelled: boolean;
}

export async function resolveVbaDevProjectCommandContext(
  options: VbaDevCommandRuntimeOptions
): Promise<VbaDevProjectCommandContext | undefined> {
  const project = await discoverWorkbookBackedProject(options);
  if (!project) {
    await options.showErrorMessage('VBA Tools could not find a workbook-backed project.json.');
    return undefined;
  }

  const devtool = await resolveCompatibleVbaDev({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract
  });

  return {
    project,
    executablePath: devtool.executablePath
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
  argsAfterProject: readonly string[] = []
): Promise<VbaDevProjectCommandRunResult> {
  const result = await runVbaDevCommand({
    executablePath: context.executablePath,
    args: [...argsBeforeProject, '--project', context.project.projectRoot, ...argsAfterProject],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });

  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(context.project.projectRoot),
    combineVbaDevDiagnosticOutput(result.stdout, result.stderr)
  );

  return {
    projectRoot: context.project.projectRoot,
    executablePath: context.executablePath,
    stdout: result.stdout,
    stderr: result.stderr,
    exitCode: result.exitCode,
    cancelled: result.cancelled
  };
}
