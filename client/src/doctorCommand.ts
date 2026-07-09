import {
  ProcessRunner,
  RequiredVbaDevToolContract,
  resolveCompatibleVbaDevTool
} from './devtool';
import {
  CommandCancellationToken,
  StartVbaDevToolProcess,
  VbaToolsOutputChannel,
  runVbaDevToolCommand
} from './devtoolCommand';
import {
  WorkbookBackedProjectCandidate,
  discoverWorkbookBackedProject
} from './projectDiscovery';
import {
  VbaDevToolDiagnosticReporterLike,
  combineVbaDevToolDiagnosticOutput,
  projectDiagnosticScope
} from './toolDiagnostics';

export const FirstRunDoctorPromptState = {
  Prompted: 'vbaTools.doctor.firstRunPrompted',
  Suppress: 'vbaTools.doctor.suppressFirstRunPrompt'
} as const;

export interface WorkspaceState {
  get<T>(key: string): T | undefined;
  update(key: string, value: unknown): Thenable<void> | Promise<void>;
}

export interface DoctorCommandOptions {
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
  startProcess?: StartVbaDevToolProcess | undefined;
  outputChannel: VbaToolsOutputChannel;
  diagnosticReporter?: VbaDevToolDiagnosticReporterLike | undefined;
  showErrorMessage: (message: string) => Thenable<unknown> | Promise<unknown>;
  cancellationToken?: CommandCancellationToken | undefined;
  requiredContract?: RequiredVbaDevToolContract | undefined;
}

export interface DoctorCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
}

export interface FirstRunDoctorPromptOptions {
  workspaceState: WorkspaceState;
  showInformationMessage: (
    message: string,
    ...items: string[]
  ) => Thenable<string | undefined> | Promise<string | undefined>;
  runDoctor: () => Promise<void>;
}

export async function runDoctorCommand(options: DoctorCommandOptions): Promise<DoctorCommandResult | undefined> {
  const project = await discoverWorkbookBackedProject(options);
  if (!project) {
    await options.showErrorMessage('VBA Tools could not find a workbook-backed project.json.');
    return undefined;
  }

  const devtool = await resolveCompatibleVbaDevTool({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract
  });

  const result = await runVbaDevToolCommand({
    executablePath: devtool.executablePath,
    args: ['doctor', '--project', project.projectRoot],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevToolDiagnosticOutput(result.stdout, result.stderr)
  );

  if (!result.cancelled && hasBlockingDoctorFinding(result.exitCode, result.stdout, result.stderr)) {
    await options.showErrorMessage('VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.');
  }

  return {
    projectRoot: project.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled
  };
}

export async function promptForFirstRunDoctor(options: FirstRunDoctorPromptOptions): Promise<void> {
  if (options.workspaceState.get<boolean>(FirstRunDoctorPromptState.Suppress)) {
    return;
  }

  if (options.workspaceState.get<boolean>(FirstRunDoctorPromptState.Prompted)) {
    return;
  }

  const answer = await options.showInformationMessage(
    'VBA Tools detected a workbook-backed project. Run Doctor?',
    'Run Doctor',
    "Don't Ask Again"
  );
  await options.workspaceState.update(FirstRunDoctorPromptState.Prompted, true);

  if (answer === "Don't Ask Again") {
    await options.workspaceState.update(FirstRunDoctorPromptState.Suppress, true);
    return;
  }

  if (answer === 'Run Doctor') {
    await options.runDoctor();
  }
}

function hasBlockingDoctorFinding(exitCode: number, stdout: string, stderr: string): boolean {
  return exitCode !== 0 || stdout.includes('[FAIL]') || stderr.trim().length > 0;
}
