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

export interface ReferenceCommandOptions {
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

export type ReferenceToolCommand = 'add' | 'list' | 'remove';

export interface ReferenceCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  referenceList?: ReferenceList | undefined;
}

export interface ReferenceList {
  document: string;
  references: readonly ReferenceListItem[];
}

export interface ReferenceListItem {
  name: string;
}

export async function runReferenceAddCommand(
  options: ReferenceCommandOptions,
  referenceName: string
): Promise<ReferenceCommandResult | undefined> {
  return runReferenceMutatingCommand(options, 'add', referenceName);
}

export async function runReferenceRemoveCommand(
  options: ReferenceCommandOptions,
  referenceName: string
): Promise<ReferenceCommandResult | undefined> {
  return runReferenceMutatingCommand(options, 'remove', referenceName);
}

export async function runReferenceListCommand(
  options: ReferenceCommandOptions
): Promise<ReferenceCommandResult | undefined> {
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

  return runReferenceListForProject(options, project, devtool.executablePath);
}

export function parseReferenceList(stdout: string): ReferenceList {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Reference list returned invalid JSON: ${String(error)}`);
  }

  if (!isReferenceList(parsed)) {
    throw new Error('Reference list JSON did not include document and references.');
  }

  return parsed;
}

export function appendFormattedReferenceList(
  outputChannel: VbaToolsOutputChannel,
  list: ReferenceList
): void {
  outputChannel.appendLine(`References for ${list.document}:`);
  if (list.references.length === 0) {
    outputChannel.appendLine('  (none)');
    return;
  }

  for (const reference of list.references) {
    outputChannel.appendLine(`  ${reference.name}`);
  }
}

async function runReferenceMutatingCommand(
  options: ReferenceCommandOptions,
  commandName: 'add' | 'remove',
  referenceName: string
): Promise<ReferenceCommandResult | undefined> {
  const normalizedReferenceName = referenceName.trim();
  if (normalizedReferenceName.length === 0) {
    await options.showErrorMessage('Reference name is required.');
    return undefined;
  }

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
    args: ['reference', commandName, normalizedReferenceName, '--project', project.projectRoot],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevToolDiagnosticOutput(result.stdout, result.stderr)
  );

  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage('Reference command failed. See the VBA Tools output for details.');
    return {
      projectRoot: project.projectRoot,
      exitCode: result.exitCode,
      cancelled: result.cancelled
    };
  }

  return runReferenceListForProject(options, project, devtool.executablePath);
}

async function runReferenceListForProject(
  options: ReferenceCommandOptions,
  project: WorkbookBackedProjectCandidate,
  executablePath: string
): Promise<ReferenceCommandResult> {
  const result = await runVbaDevToolCommand({
    executablePath,
    args: ['reference', 'list', '--project', project.projectRoot, '--format', 'json'],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevToolDiagnosticOutput(result.stdout, result.stderr)
  );

  let referenceList: ReferenceList | undefined;
  if (!result.cancelled && result.exitCode === 0) {
    try {
      referenceList = parseReferenceList(result.stdout);
      appendFormattedReferenceList(options.outputChannel, referenceList);
    } catch (error) {
      await options.showErrorMessage(`${String(error)} See the VBA Tools output for details.`);
    }
  } else if (!result.cancelled) {
    await options.showErrorMessage('Reference list failed. See the VBA Tools output for details.');
  }

  return {
    projectRoot: project.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    referenceList
  };
}

function isReferenceList(value: unknown): value is ReferenceList {
  if (!isRecord(value) || typeof value.document !== 'string' || !Array.isArray(value.references)) {
    return false;
  }

  return value.references.every((reference) => (
    isRecord(reference) &&
    typeof reference.name === 'string'
  ));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
