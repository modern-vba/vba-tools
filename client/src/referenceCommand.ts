import {
  CommandCancellationToken,
  VbaToolsOutputChannel
} from './devtoolCommand';
import {
  VbaDevCommandRuntimeOptions,
  VbaDevProjectCommandContext,
  resolveVbaDevProjectCommandContext,
  runResolvedVbaDevProjectCommand
} from './devtoolRuntime';
import {
  ReferenceList,
  parseReferenceListOutput
} from './vbaDevOutputContract';
import { ProjectManifestMutationCommandCoordinator } from './projectManifestMutation';
import {
  ReferenceMutationOperation,
  TrustedReferenceMutationOutput,
  parseAvailableReferenceInventoryOutput,
  parseReferenceMutationOutput,
  parseReferenceSelectionInventoryOutput
} from './referenceOutputContract';
import {
  ReferenceQuickPickItem,
  ReferenceQuickPickResult
} from './referenceQuickPick';

export interface ReferenceCommandOptions extends VbaDevCommandRuntimeOptions {
  projectManifestMutationCoordinator: ProjectManifestMutationCommandCoordinator;
}

export interface ReferenceQuickPickDiscoveryRequest {
  readonly title: string;
  discover(token: CommandCancellationToken): Promise<readonly ReferenceQuickPickItem[]>;
}

export interface ReferenceQuickPickWorkflowOptions extends ReferenceCommandOptions {
  selectReferences(
    request: ReferenceQuickPickDiscoveryRequest
  ): Promise<ReferenceQuickPickResult>;
  runMutationWithProgress(
    title: string,
    task: (token: CommandCancellationToken) => Promise<void>
  ): Promise<void>;
  showInformationMessage(message: string): PromiseLike<unknown> | Promise<unknown>;
  showWarningMessage(
    message: string,
    action: string
  ): PromiseLike<string | undefined> | Promise<string | undefined>;
  showReferenceErrorMessage(
    message: string,
    action: string
  ): PromiseLike<string | undefined> | Promise<string | undefined>;
  showOutput(): void;
}

export interface ReferenceCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  referenceList?: ReferenceList | undefined;
}

export class ReferenceDiscoveryCancelledError extends Error {
  public constructor() {
    super('Reference discovery was cancelled.');
    this.name = 'ReferenceDiscoveryCancelledError';
  }
}

export async function runReferenceQuickPickWorkflow(
  options: ReferenceQuickPickWorkflowOptions,
  operation: 'add' | 'remove'
): Promise<void> {
  const target = await options.resolveCommandPaletteTarget('document');
  const document = target?.document;
  if (target === undefined || document === undefined) {
    return;
  }

  const projectName = target.project.projectName;
  const documentName = document.name;
  const commandLabel = operation === 'add' ? 'Add' : 'Remove';
  let context: VbaDevProjectCommandContext | undefined;
  const selection = await options.selectReferences({
    title: `VBA Tools: ${commandLabel} Reference — ${projectName} / ${documentName}`,
    discover: async (token) => {
      if (token.isCancellationRequested) {
        throw new ReferenceDiscoveryCancelledError();
      }

      context = await resolveVbaDevProjectCommandContext(
        {
          ...options,
          cancellationToken: token,
          resolveCommandPaletteTarget: async () => target
        },
        'document'
      );
      if (token.isCancellationRequested) {
        throw new ReferenceDiscoveryCancelledError();
      }
      if (context === undefined || context.document === undefined) {
        throw new Error('Reference discovery could not resolve the companion command context.');
      }

      return discoverReferenceQuickPickItems(
        options,
        context,
        operation,
        token
      );
    }
  });

  if (selection.kind === 'cancelled') {
    return;
  }
  if (selection.kind === 'failed') {
    if (selection.error instanceof ReferenceDiscoveryCancelledError) {
      return;
    }
    await offerShowOutput(
      options,
      'error',
      `References for ${documentName} could not be loaded. See VBA Tools Output for details.`
    );
    return;
  }
  if (selection.kind === 'empty') {
    await options.showInformationMessage(operation === 'add'
      ? `No resolved references are available to add to ${documentName}.`
      : `${documentName} has no configured references to remove.`);
    return;
  }
  const mutationContext = context as VbaDevProjectCommandContext | undefined;
  if (mutationContext === undefined || mutationContext.document === undefined) {
    await offerShowOutput(
      options,
      'error',
      `References for ${documentName} could not be loaded. See VBA Tools Output for details.`
    );
    return;
  }

  const progressTitle = operation === 'add'
    ? `VBA Tools: Adding references — ${projectName} / ${documentName}`
    : `VBA Tools: Removing references — ${projectName} / ${documentName}`;
  await options.runMutationWithProgress(progressTitle, async (token) => {
    await runReferenceMutationCommand(
      { ...options, cancellationToken: token },
      mutationContext,
      operation,
      selection.names
    );
  });
}

export async function discoverReferenceQuickPickItems(
  options: ReferenceCommandOptions,
  context: VbaDevProjectCommandContext,
  operation: 'add' | 'remove',
  cancellationToken: CommandCancellationToken
): Promise<readonly ReferenceQuickPickItem[]> {
  if (context.document === undefined) {
    throw new Error('Reference discovery requires an exact document target.');
  }

  const mode = operation === 'add' ? 'available' : 'selection';
  await options.projectManifestMutationCoordinator.reportReadOnlyDiskBasis({
    command: operation === 'add' ? 'Reference Available' : 'Reference Selection',
    target: context.target
  });
  const result = await runResolvedVbaDevProjectCommand(
    { ...options, cancellationToken, revealOutput: false },
    context,
    ['reference', 'list', operation === 'add' ? '--available' : '--no-resolve'],
    ['--format', 'json']
  );
  if (result.cancelled) {
    throw new ReferenceDiscoveryCancelledError();
  }
  if (result.exitCode !== 0) {
    throw new Error(`Reference ${mode} inventory exited with code ${result.exitCode}.`);
  }

  if (operation === 'add') {
    const inventory = parseAvailableReferenceInventoryOutput(
      result.stdout,
      context.project.projectRoot,
      context.document.name
    );
    return inventory.resolvedReferences.map((reference) => ({
      label: reference.name,
      description: `TypeLib ${reference.identity.major}.${reference.identity.minor}`,
      canonicalName: reference.name
    }));
  }

  const selection = parseReferenceSelectionInventoryOutput(
    result.stdout,
    context.project.projectRoot,
    context.document.name
  );
  return selection.references.map((reference) => ({
    label: reference.name,
    canonicalName: reference.name
  }));
}

export async function runReferenceMutationCommand(
  options: ReferenceQuickPickWorkflowOptions,
  context: VbaDevProjectCommandContext,
  operation: ReferenceMutationOperation,
  submittedNames: readonly string[]
): Promise<void> {
  if (context.document === undefined) {
    throw new Error('Reference mutation requires an exact document target.');
  }
  if (submittedNames.length === 0) {
    return;
  }

  const coordinated = await options.projectManifestMutationCoordinator.run({
    command: operation === 'add' ? 'Reference Add' : 'Reference Remove',
    target: context.target,
    run: () => runResolvedVbaDevProjectCommand(
      { ...options, revealOutput: false },
      context,
      ['reference', operation, ...submittedNames],
      ['--format', 'json']
    )
  });
  if (coordinated.status === 'rejected') {
    return;
  }
  if (coordinated.manifestOutcome === 'untrusted' || coordinated.coherence === 'untrusted') {
    await offerShowOutput(
      options,
      'warning',
      `The ${operation} result is untrusted; ${context.document.name}'s manifest may already have committed. Do not retry automatically.`
    );
    return;
  }
  if (coordinated.processResult === undefined) {
    await offerShowOutput(
      options,
      'warning',
      `The ${operation} result is untrusted; ${context.document.name}'s manifest may already have committed. Do not retry automatically.`
    );
    return;
  }

  const result = coordinated.processResult;
  if (result.cancelled) {
    return;
  }
  if (result.exitCode !== 0) {
    await offerShowOutput(
      options,
      'error',
      `Reference ${operation} failed for ${context.document.name}. See VBA Tools Output for details.`
    );
    return;
  }

  let trusted: TrustedReferenceMutationOutput;
  try {
    trusted = parseReferenceMutationOutput(
      result.stdout,
      context.project.projectRoot,
      context.document.name,
      operation,
      submittedNames
    );
  } catch {
    await offerShowOutput(
      options,
      'warning',
      `The ${operation} result is untrusted; ${context.document.name}'s manifest may already have committed. Do not retry automatically.`
    );
    return;
  }

  await notifyTrustedMutation(options, trusted);
}

async function notifyTrustedMutation(
  options: ReferenceQuickPickWorkflowOptions,
  result: TrustedReferenceMutationOutput
): Promise<void> {
  const unchanged = result.results.filter((entry) =>
    entry.status === 'alreadyPresent' || entry.status === 'alreadyAbsent').length;
  const summary = result.operation === 'add'
    ? `References for ${result.document}: ` +
      `${result.results.filter((entry) => entry.status === 'added').length} added, ` +
      `${result.results.filter((entry) => entry.status === 'promoted').length} promoted, ` +
      `${unchanged} unchanged.`
    : `References for ${result.document}: ` +
      `${result.results.filter((entry) => entry.status === 'removed').length} removed, ` +
      `${unchanged} unchanged.`;

  if (result.warnings.length === 0) {
    await options.showInformationMessage(summary);
    return;
  }

  const selected = await options.showWarningMessage(
    `${summary} ${result.warnings.length} warning${result.warnings.length === 1 ? '' : 's'}.`,
    'Show Output'
  );
  if (selected === 'Show Output') {
    options.showOutput();
  }
}

async function offerShowOutput(
  options: ReferenceQuickPickWorkflowOptions,
  severity: 'warning' | 'error',
  message: string
): Promise<void> {
  const selected = severity === 'warning'
    ? await options.showWarningMessage(message, 'Show Output')
    : await options.showReferenceErrorMessage(message, 'Show Output');
  if (selected === 'Show Output') {
    options.showOutput();
  }
}

export async function runReferenceListCommand(
  options: ReferenceCommandOptions
): Promise<ReferenceCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options, 'document');
  if (!context) {
    return undefined;
  }

  return runReferenceListForProject(options, context);
}

export function parseReferenceList(stdout: string): ReferenceList {
  return parseReferenceListOutput(stdout);
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

async function runReferenceListForProject(
  options: ReferenceCommandOptions,
  context: VbaDevProjectCommandContext
): Promise<ReferenceCommandResult> {
  await options.projectManifestMutationCoordinator.reportReadOnlyDiskBasis({
    command: 'Reference List',
    target: context.target
  });
  const result = await runResolvedVbaDevProjectCommand(
    options,
    context,
    ['reference', 'list'],
    ['--format', 'json']
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
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    referenceList
  };
}
