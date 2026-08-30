import * as path from 'node:path';

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
import { ProjectManifestMutationCommandCoordinator } from './projectManifestMutation';
import {
  CommonModulesMutationOperation,
  TrustedCommonModulesMutationOutput,
  parseCommonModulesMutationOutput
} from './commonModulesOutputContract';

export interface CommonModulesCommandOptions extends VbaDevCommandRuntimeOptions {
  projectManifestMutationCoordinator: ProjectManifestMutationCommandCoordinator;
  showInformationMessage(message: string): PromiseLike<unknown> | Promise<unknown>;
  showWarningMessage(
    message: string,
    action: string
  ): PromiseLike<string | undefined> | Promise<string | undefined>;
  showOutput(): void;
}

export type CommonModulesToolCommand = 'add' | 'list' | 'update';

export interface CommonModulesCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  commonModulesList?: CommonModulesList | undefined;
  commonModulesMutation?: TrustedCommonModulesMutationOutput | undefined;
}

// MS-VBAL WSC, followed by the UI input protocol's line terminators. U+00A0 is
// intentionally absent because it is a valid CP2 identifier character.
const commonModuleNameSeparator =
  /[\u0009\u0019\u0020\u1680\u180E\u2000-\u200A\u202F\u205F\u3000\r\n]+/;

export function parseCommonModuleNamesInput(value: string): readonly string[] {
  return value
    .split(commonModuleNameSeparator)
    .filter((moduleName) => moduleName.length > 0);
}

export async function runCommonModulesAddCommand(
  options: CommonModulesCommandOptions,
  moduleNames: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
  const exactModuleNames = moduleNames
    .filter((moduleName) => moduleName.length > 0);
  if (exactModuleNames.length === 0) {
    return undefined;
  }

  return runCommonModulesMutatingCommand(options, 'add', exactModuleNames);
}

export async function runCommonModulesUpdateCommand(
  options: CommonModulesCommandOptions
): Promise<CommonModulesCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options, 'project');
  if (!context) {
    return undefined;
  }

  return runCommonModulesMutation(options, context, 'update', []);
}

export async function runCommonModulesListCommand(
  options: CommonModulesCommandOptions
): Promise<CommonModulesCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options, 'document');
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
  await options.projectManifestMutationCoordinator.reportReadOnlyDiskBasis({
    command: 'Common Module List',
    target: context.target
  });
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
  operation: 'add',
  moduleNames: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options, 'document');
  if (!context) {
    return undefined;
  }

  return runCommonModulesMutation(options, context, operation, moduleNames);
}

async function runCommonModulesMutation(
  options: CommonModulesCommandOptions,
  context: VbaDevProjectCommandContext,
  operation: CommonModulesMutationOperation,
  submittedModuleNames: readonly string[]
): Promise<CommonModulesCommandResult | undefined> {
  const commandName = operation === 'add' ? 'Common Module Add' : 'Common Module Update';
  const coordinated = await options.projectManifestMutationCoordinator.run({
    command: commandName,
    target: context.target,
    reportPresentation: 'logOnly',
    run: () => runResolvedVbaDevProjectCommand(
      { ...options, revealOutput: false },
      context,
      ['common-module', operation, ...submittedModuleNames],
      ['--format', 'json']
    )
  });
  if (coordinated.status === 'rejected') {
    return undefined;
  }
  if (coordinated.processResult === undefined) {
    throw coordinated.processError ?? new Error(
      'CommonModules mutation completed without a process result.'
    );
  }
  const fallbackResult: CommonModulesCommandResult = {
    projectRoot: context.project.projectRoot,
    exitCode: coordinated.processResult.exitCode,
    cancelled: coordinated.processResult.cancelled
  };
  if (coordinated.manifestOutcome === 'untrusted' || coordinated.coherence === 'untrusted') {
    await warnUntrustedMutation(options, context, operation);
    return fallbackResult;
  }
  const result = coordinated.processResult;

  if (result.cancelled) {
    return {
      projectRoot: result.projectRoot,
      exitCode: result.exitCode,
      cancelled: true
    };
  }

  if (result.exitCode !== 0) {
    await options.showErrorMessage('CommonModules command failed. See the VBA Tools output for details.');
    return {
      projectRoot: result.projectRoot,
      exitCode: result.exitCode,
      cancelled: result.cancelled
    };
  }

  const expectedDocument = operation === 'add'
    ? context.document?.name ?? null
    : null;
  let trusted: TrustedCommonModulesMutationOutput;
  try {
    trusted = parseCommonModulesMutationOutput(
      result.stdout,
      context.project.projectRoot,
      expectedDocument,
      operation,
      submittedModuleNames
    );
  } catch {
    await warnUntrustedMutation(options, context, operation);
    return {
      projectRoot: result.projectRoot,
      exitCode: result.exitCode,
      cancelled: false
    };
  }

  await notifyTrustedMutation(options, trusted, {
    cancellationRequested: result.cancellationRequested,
    cancellationRequestDelivered: result.cancellationRequestDelivered
  });
  return {
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: false,
    commonModulesMutation: trusted
  };
}

async function notifyTrustedMutation(
  options: CommonModulesCommandOptions,
  result: TrustedCommonModulesMutationOutput,
  cancellation: {
    readonly cancellationRequested: boolean;
    readonly cancellationRequestDelivered: boolean | undefined;
  }
): Promise<void> {
  const modules = result.documents.flatMap((document) => document.modules);
  const changed = modules.filter((module) => module.status === 'changed').length;
  const unchanged = modules.length - changed;
  const referencesAdded = result.documents.reduce(
    (count, document) => count + document.referenceChanges.length,
    0
  );
  const subject = result.operation === 'add'
    ? `CommonModules for ${result.document}`
    : `CommonModules update for ${path.basename(result.project)}`;
  const summary = result.operation === 'update' && result.documents.length === 0
    ? `${subject}: no installed targets.`
    : `${subject}: ${changed} changed, ${unchanged} unchanged, ` +
      `${referencesAdded} reference${referencesAdded === 1 ? '' : 's'} added.`;
  const cancellationDeliveryFailed = cancellation.cancellationRequested &&
    cancellation.cancellationRequestDelivered === false;
  if (result.warnings.length === 0 && !cancellationDeliveryFailed) {
    await options.showInformationMessage(summary);
    return;
  }

  let warning = summary;
  if (result.warnings.length > 0) {
    warning += ` ${result.warnings.length} warning${result.warnings.length === 1 ? '' : 's'}.`;
  }
  if (cancellationDeliveryFailed) {
    warning += ' Cancellation request could not be delivered.';
  }
  const selected = await options.showWarningMessage(warning, 'Show Output');
  if (selected === 'Show Output') {
    options.showOutput();
  }
}

async function warnUntrustedMutation(
  options: CommonModulesCommandOptions,
  context: VbaDevProjectCommandContext,
  operation: CommonModulesMutationOperation
): Promise<void> {
  const command = operation === 'add' ? 'Add' : 'Update';
  const subject = operation === 'add'
    ? `${context.document?.name ?? 'the selected document'}'s manifest`
    : 'the project manifest';
  const selected = await options.showWarningMessage(
    `CommonModules ${command} completed with an untrusted result; ${subject} may already have committed. ` +
      'Inspect the manifest and VBA Tools Output before retrying.',
    'Show Output'
  );
  if (selected === 'Show Output') {
    options.showOutput();
  }
}
