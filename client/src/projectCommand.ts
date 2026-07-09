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

export type WorkbookBackedProjectToolCommand = 'build' | 'test' | 'publish' | 'export';

export interface WorkbookBackedProjectCommandOptions {
  toolCommandName: WorkbookBackedProjectToolCommand;
  title: string;
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

export interface WorkbookBackedProjectCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
}

export async function runWorkbookBackedProjectCommand(
  options: WorkbookBackedProjectCommandOptions
): Promise<WorkbookBackedProjectCommandResult | undefined> {
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

  const result = await runVbaDevCommand({
    executablePath: devtool.executablePath,
    args: [options.toolCommandName, '--project', project.projectRoot],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevDiagnosticOutput(result.stdout, result.stderr)
  );

  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage(`${options.title.replace('VBA Tools: ', '')} failed. See the VBA Tools output for details.`);
  }

  return {
    projectRoot: project.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled
  };
}
