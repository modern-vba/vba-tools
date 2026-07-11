import { VbaToolsOutputChannel } from './devtoolCommand';
import {
  VbaDevCommandRuntimeOptions,
  VbaDevProjectCommandContext,
  resolveVbaDevProjectCommandContext,
  runResolvedVbaDevProjectCommand
} from './devtoolRuntime';
import {
  CommonModuleListItem,
  CommonModulesList,
  parseCommonModulesListOutput
} from './vbaDevOutputContract';

export interface CommonModulesCommandOptions extends VbaDevCommandRuntimeOptions {}

export type CommonModulesToolCommand = 'add' | 'list' | 'update';

export interface CommonModulesCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  commonModulesList?: CommonModulesList | undefined;
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
  const context = await resolveVbaDevProjectCommandContext(options);
  if (!context) {
    return undefined;
  }

  return runCommonModulesListForProject(options, context);
}

export function parseCommonModulesList(stdout: string): CommonModulesList {
  return parseCommonModulesListOutput(stdout);
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
  context: VbaDevProjectCommandContext
): Promise<CommonModulesCommandResult> {
  const result = await runResolvedVbaDevProjectCommand(
    options,
    context,
    ['common-module', 'list'],
    ['--format', 'json']
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
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    commonModulesList
  };
}

async function runCommonModulesMutatingCommand(
  options: CommonModulesCommandOptions,
  toolArgs: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options);
  if (!context) {
    return undefined;
  }

  const result = await runResolvedVbaDevProjectCommand(options, context, toolArgs);

  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage('CommonModules command failed. See the VBA Tools output for details.');
    return {
      projectRoot: result.projectRoot,
      exitCode: result.exitCode,
      cancelled: result.cancelled
    };
  }

  return runCommonModulesListForProject(options, context);
}
