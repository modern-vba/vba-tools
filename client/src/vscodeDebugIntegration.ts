import {
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevCompatibilityError,
  resolveCompatibleVbaDev
} from './devtool';

export interface VbaDebugSessionLike {
  id: string;
  workspaceRoot?: string | undefined;
}

export interface VbaDebugAdapterExecutableSpec {
  command: string;
  args: readonly string[];
  options?: {
    cwd?: string | undefined;
  } | undefined;
}

export interface VscodeDebugIntegrationOptions {
  extensionRoot: string;
  getConfiguredDevToolPath: () => string | undefined;
  capabilitiesProcess?: ProcessRunner | undefined;
  requiredContract?: RequiredVbaDevContract | undefined;
}

export class VscodeDebugIntegration {
  private activeSessionId: string | undefined;

  public constructor(private readonly options: VscodeDebugIntegrationOptions) {}

  public async createDebugAdapterExecutable(
    session: VbaDebugSessionLike
  ): Promise<VbaDebugAdapterExecutableSpec> {
    this.reserveSession(session.id);
    try {
      const devtool = await resolveCompatibleVbaDev({
        extensionRoot: this.options.extensionRoot,
        configuredPath: this.options.getConfiguredDevToolPath(),
        runProcess: this.options.capabilitiesProcess,
        requiredContract: this.options.requiredContract
      });
      const debugAdapter = devtool.capabilities.debugAdapter;
      if (!debugAdapter) {
        throw new VbaDevCompatibilityError(
          `VbaDev at '${devtool.executablePath}' does not report the required debug adapter capability.`
        );
      }

      return {
        command: devtool.executablePath,
        args: [debugAdapter.command, '--stdio'],
        options: session.workspaceRoot === undefined
          ? undefined
          : { cwd: session.workspaceRoot }
      };
    } catch (error) {
      this.releaseSession(session.id);
      throw error;
    }
  }

  public releaseSession(sessionId: string): void {
    if (this.activeSessionId === sessionId) {
      this.activeSessionId = undefined;
    }
  }

  private reserveSession(sessionId: string): void {
    if (this.activeSessionId !== undefined) {
      throw new Error('A VBA debug session is already running in this VS Code window.');
    }

    this.activeSessionId = sessionId;
  }
}
