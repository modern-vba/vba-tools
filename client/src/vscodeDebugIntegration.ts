import * as path from 'node:path';

import {
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevCompatibilityError,
  resolveCompatibleVbaDev
} from './devtool';
import {
  VbaDebugConfiguration,
  VbaDebugConfigurationHost,
  VbaDebugSelectionError,
  normalizeVbaDebugConfiguration,
  provideDynamicVbaDebugConfigurations,
  resolveVbaDebugConfiguration
} from './vscodeDebugConfiguration';

export interface VbaDebugConfigurationProviderLike {
  provideDebugConfigurations(): readonly VbaDebugConfiguration[];
  resolveDebugConfiguration(
    configuration: VbaDebugConfiguration
  ): VbaDebugConfiguration | undefined;
  resolveDebugConfigurationWithSubstitutedVariables(
    configuration: VbaDebugConfiguration,
    workspaceFolderPath?: string
  ): Promise<VbaDebugConfiguration | undefined>;
}

export interface VbaDebugConfigurationResolver {
  provideDynamicDebugConfigurations(): readonly VbaDebugConfiguration[];
  resolveDebugConfiguration(
    configuration: VbaDebugConfiguration
  ): Promise<VbaDebugConfiguration>;
}

export type VbaDebugConfigurationObserver = (
  configuration: VbaDebugConfiguration
) => void;

let debugConfigurationObserver: VbaDebugConfigurationObserver | undefined;

export function createVbaDebugConfigurationProvider(
  integration: VbaDebugConfigurationResolver,
  reportError: (message: string) => unknown
): VbaDebugConfigurationProviderLike {
  return {
    provideDebugConfigurations: () => integration.provideDynamicDebugConfigurations(),
    resolveDebugConfiguration: (configuration) => {
      try {
        return normalizeVbaDebugConfiguration(configuration);
      } catch (error) {
        reportError(error instanceof Error ? error.message : String(error));
        return undefined;
      }
    },
    resolveDebugConfigurationWithSubstitutedVariables: async (
      configuration,
      workspaceFolderPath
    ) => {
      try {
        const resolvedConfiguration = await integration.resolveDebugConfiguration(
          resolveWorkspaceRelativeProject(configuration, workspaceFolderPath)
        );
        if (debugConfigurationObserver !== undefined) {
          debugConfigurationObserver(resolvedConfiguration);
          return undefined;
        }

        return resolvedConfiguration;
      } catch (error) {
        reportError(error instanceof Error ? error.message : String(error));
        return undefined;
      }
    }
  };
}

function resolveWorkspaceRelativeProject(
  configuration: VbaDebugConfiguration,
  workspaceFolderPath: string | undefined
): VbaDebugConfiguration {
  const project = configuration.project;
  if (typeof project !== 'string' || path.isAbsolute(project)) {
    return configuration;
  }

  if (workspaceFolderPath === undefined) {
    throw new VbaDebugSelectionError(
      'A relative VBA debug project selector requires a workspace folder; '
      + 'use an absolute path or ${workspaceFolder}.'
    );
  }

  return {
    ...configuration,
    project: path.resolve(workspaceFolderPath, project)
  };
}

export function useVbaDebugConfigurationObserverForTest(
  observer: VbaDebugConfigurationObserver
): { dispose(): void } {
  const previousObserver = debugConfigurationObserver;
  debugConfigurationObserver = observer;
  return {
    dispose: () => {
      debugConfigurationObserver = previousObserver;
    }
  };
}

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
  debugConfigurationHost?: VbaDebugConfigurationHost | undefined;
}

export class VscodeDebugIntegration {
  private activeSessionId: string | undefined;

  public constructor(private readonly options: VscodeDebugIntegrationOptions) {}

  public resolveDebugConfiguration(
    configuration: VbaDebugConfiguration
  ): Promise<VbaDebugConfiguration> {
    if (!this.options.debugConfigurationHost) {
      throw new Error('VBA debug configuration resolution is not available in this host.');
    }

    return resolveVbaDebugConfiguration(
      this.options.debugConfigurationHost,
      configuration
    );
  }

  public provideDynamicDebugConfigurations(): readonly VbaDebugConfiguration[] {
    return this.options.debugConfigurationHost
      ? provideDynamicVbaDebugConfigurations(this.options.debugConfigurationHost)
      : [];
  }

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
