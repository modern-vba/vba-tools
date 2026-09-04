export const ProjectCreationRestrictedModeMessage =
  'Excel VBA project creation is unavailable in Restricted Mode because it starts ' +
  'vba-dev and Microsoft Excel. Trust this workspace or run the command from a ' +
  'trusted Empty Window.';

export const ManagedToolingRestrictedModeMessage =
  'VBA Tools cannot run managed VBA tooling in Restricted Mode. Trust this workspace to continue.';

export const WorkspaceTrustAction = {
  ManageWorkspaceTrust: 'Manage Workspace Trust',
  OpenEmptyWindow: 'Open Empty Window'
} as const;

export type ManagedToolingTrustEntryPoint = 'managed-tooling' | 'project-creation';

export const ManagedToolingCommandIds = [
  'vbaTools.doctor',
  'vbaTools.openVbaDevTerminal',
  'vbaTools.newExcel',
  'vbaTools.export',
  'vbaTools.build',
  'vbaTools.test',
  'vbaTools.publish',
  'vbaTools.userFormEvents.refresh',
  'vbaTools.commonModules.add',
  'vbaTools.commonModules.list',
  'vbaTools.commonModules.update',
  'vbaTools.references.list',
  'vbaTools.references.add',
  'vbaTools.references.remove'
] as const;

export type ManagedToolingCommandId = typeof ManagedToolingCommandIds[number];
export type ManagedToolingCommandOperation = (
  request?: unknown
) => PromiseLike<unknown> | unknown;
export type ManagedToolingCommandOperations = Readonly<Record<
  ManagedToolingCommandId,
  ManagedToolingCommandOperation
>>;

export interface ManagedToolingWorkspaceTrustGateOptions {
  isTrusted: () => boolean;
  invalidateManagedToolingState: () => void;
  showWarningMessage: (
    message: string,
    ...actions: string[]
  ) => Thenable<string | undefined> | PromiseLike<string | undefined>;
  executeCommand: (command: string) => Thenable<unknown> | PromiseLike<unknown>;
}

export class ManagedToolingWorkspaceTrustGate {
  public constructor(
    private readonly options: ManagedToolingWorkspaceTrustGateOptions
  ) {}

  public async requireTrusted(entryPoint: ManagedToolingTrustEntryPoint): Promise<boolean> {
    if (this.options.isTrusted()) {
      return true;
    }

    this.options.invalidateManagedToolingState();
    const projectCreation = entryPoint === 'project-creation';
    const selectedAction = await this.options.showWarningMessage(
      projectCreation
        ? ProjectCreationRestrictedModeMessage
        : ManagedToolingRestrictedModeMessage,
      WorkspaceTrustAction.ManageWorkspaceTrust,
      ...(projectCreation ? [WorkspaceTrustAction.OpenEmptyWindow] : [])
    );

    if (selectedAction === WorkspaceTrustAction.ManageWorkspaceTrust) {
      await this.options.executeCommand('workbench.trust.manage');
    } else if (selectedAction === WorkspaceTrustAction.OpenEmptyWindow) {
      await this.options.executeCommand('vscode.newWindow');
    }
    return false;
  }

  public async run<T>(
    entryPoint: ManagedToolingTrustEntryPoint,
    operation: () => PromiseLike<T> | T
  ): Promise<T | undefined> {
    if (!await this.requireTrusted(entryPoint)) {
      return undefined;
    }
    return operation();
  }
}

export function createManagedToolingCommandHandler<
  TArguments extends readonly unknown[],
  TResult
>(
  gate: ManagedToolingWorkspaceTrustGate,
  entryPoint: ManagedToolingTrustEntryPoint,
  operation: (...args: TArguments) => PromiseLike<TResult> | TResult
): (...args: TArguments) => Promise<TResult | undefined> {
  return (...args) => gate.run(entryPoint, () => operation(...args));
}

export function createManagedToolingCommandHandlers(
  gate: ManagedToolingWorkspaceTrustGate,
  operations: Readonly<Record<string, ManagedToolingCommandOperation>>
): ReadonlyArray<{
  readonly commandId: ManagedToolingCommandId;
  readonly handler: ManagedToolingCommandOperation;
}> {
  return ManagedToolingCommandIds.map((commandId) => {
    const operation = operations[commandId];
    if (operation === undefined) {
      throw new Error(`Managed tooling command operation is missing for ${commandId}.`);
    }
    return {
      commandId,
      handler: createManagedToolingCommandHandler(
        gate,
        commandId === 'vbaTools.newExcel' ? 'project-creation' : 'managed-tooling',
        operation
      )
    };
  });
}
