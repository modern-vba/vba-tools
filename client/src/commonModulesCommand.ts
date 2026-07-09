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

export interface CommonModulesCommandOptions {
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

export type CommonModulesToolCommand = 'add' | 'list' | 'update';

export interface CommonModulesCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  commonModulesList?: CommonModulesList | undefined;
}

export interface CommonModulesList {
  document: string;
  commonModules: readonly CommonModuleListItem[];
}

export interface CommonModuleListItem {
  name: string;
  requested: boolean;
}

export async function runCommonModulesAddCommand(
  options: CommonModulesCommandOptions,
  moduleNames: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
  const normalizedModuleNames = moduleNames
    .map((moduleName) => moduleName.trim())
    .filter((moduleName) => moduleName.length > 0);
  if (normalizedModuleNames.length === 0) {
    return undefined;
  }

  return runCommonModulesMutatingCommand(options, [
    'common-module',
    'add',
    ...normalizedModuleNames
  ]);
}

export async function runCommonModulesUpdateCommand(
  options: CommonModulesCommandOptions
): Promise<CommonModulesCommandResult | undefined> {
  return runCommonModulesMutatingCommand(options, ['common-module', 'update']);
}

export async function runCommonModulesListCommand(
  options: CommonModulesCommandOptions
): Promise<CommonModulesCommandResult | undefined> {
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

  return runCommonModulesListForProject(options, project, devtool.executablePath);
}

export function parseCommonModulesList(stdout: string): CommonModulesList {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`CommonModules list returned invalid JSON: ${String(error)}`);
  }

  if (!isCommonModulesList(parsed)) {
    throw new Error('CommonModules list JSON did not include document and commonModules.');
  }

  return parsed;
}

export function appendFormattedCommonModulesList(
  outputChannel: VbaToolsOutputChannel,
  list: CommonModulesList
): void {
  outputChannel.appendLine(`CommonModules for ${list.document}:`);
  if (list.commonModules.length === 0) {
    outputChannel.appendLine('  (none)');
    return;
  }

  for (const module of list.commonModules) {
    outputChannel.appendLine(`  ${module.name} (${module.requested ? 'requested' : 'dependency'})`);
  }
}

async function runCommonModulesListForProject(
  options: CommonModulesCommandOptions,
  project: WorkbookBackedProjectCandidate,
  executablePath: string
): Promise<CommonModulesCommandResult> {
  const result = await runVbaDevToolCommand({
    executablePath,
    args: ['common-module', 'list', '--project', project.projectRoot, '--format', 'json'],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevToolDiagnosticOutput(result.stdout, result.stderr)
  );

  let commonModulesList: CommonModulesList | undefined;
  if (!result.cancelled && result.exitCode === 0) {
    try {
      commonModulesList = parseCommonModulesList(result.stdout);
      appendFormattedCommonModulesList(options.outputChannel, commonModulesList);
    } catch (error) {
      await options.showErrorMessage(`${String(error)} See the VBA Tools output for details.`);
    }
  } else if (!result.cancelled) {
    await options.showErrorMessage('CommonModules list failed. See the VBA Tools output for details.');
  }

  return {
    projectRoot: project.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    commonModulesList
  };
}

async function runCommonModulesMutatingCommand(
  options: CommonModulesCommandOptions,
  toolArgs: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
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
    args: [...toolArgs, '--project', project.projectRoot],
    outputChannel: options.outputChannel,
    cancellationToken: options.cancellationToken,
    startProcess: options.startProcess
  });
  options.diagnosticReporter?.refresh(
    projectDiagnosticScope(project.projectRoot),
    combineVbaDevToolDiagnosticOutput(result.stdout, result.stderr)
  );

  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage('CommonModules command failed. See the VBA Tools output for details.');
    return {
      projectRoot: project.projectRoot,
      exitCode: result.exitCode,
      cancelled: result.cancelled
    };
  }

  return runCommonModulesListForProject(options, project, devtool.executablePath);
}

function isCommonModulesList(value: unknown): value is CommonModulesList {
  if (!isRecord(value) || typeof value.document !== 'string' || !Array.isArray(value.commonModules)) {
    return false;
  }

  return value.commonModules.every((module) => (
    isRecord(module) &&
    typeof module.name === 'string' &&
    typeof module.requested === 'boolean'
  ));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
