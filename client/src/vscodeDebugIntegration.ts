import * as path from 'node:path';
import { createHash } from 'node:crypto';

import {
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevCompatibilityError,
  resolveCompatibleVbaDev
} from './devtool';
import {
  VbaDebugCancellationError,
  VbaDebugCancellationToken,
  VbaDebugConfiguration,
  VbaDebugConfigurationHost,
  VbaDebugSelectionError,
  normalizeVbaDebugConfiguration,
  provideDynamicVbaDebugConfigurations,
  resolveVbaDebugConfiguration,
  saveDirtyVbaDebugProjectSources
} from './vscodeDebugConfiguration';

export const VbaDebugRestartPreparationProperty = '__vbaRestartPreparation';
export const VbaDebugRestartPreparationProtocolVersion = 1;

export interface VbaDebugConfigurationProviderLike {
  provideDebugConfigurations(): readonly VbaDebugConfiguration[];
  resolveDebugConfiguration(
    configuration: VbaDebugConfiguration
  ): VbaDebugConfiguration | undefined;
  resolveDebugConfigurationWithSubstitutedVariables(
    configuration: VbaDebugConfiguration,
    workspaceFolderPath?: string,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<VbaDebugConfiguration | undefined>;
}

export interface VbaDebugConfigurationResolver {
  provideDynamicDebugConfigurations(): readonly VbaDebugConfiguration[];
  resolveDebugConfiguration(
    configuration: VbaDebugConfiguration,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<VbaDebugConfiguration>;
  prepareDebugConfigurationForRestart?(
    configuration: VbaDebugConfiguration,
    workspaceFolderPath?: string
  ): VbaDebugConfiguration;
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
      workspaceFolderPath,
      cancellationToken
    ) => {
      try {
        const resolvedConfiguration = await integration.resolveDebugConfiguration(
          resolveWorkspaceRelativeProject(configuration, workspaceFolderPath),
          cancellationToken
        );
        const preparedConfiguration = integration.prepareDebugConfigurationForRestart?.(
          resolvedConfiguration,
          workspaceFolderPath
        ) ?? resolvedConfiguration;
        if (debugConfigurationObserver !== undefined) {
          debugConfigurationObserver(preparedConfiguration);
          return undefined;
        }

        return preparedConfiguration;
      } catch (error) {
        if (error instanceof VbaDebugCancellationError) {
          return undefined;
        }

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
  configuration?: VbaDebugConfiguration | undefined;
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

export function handleVbaDebugLifecycleRequest(
  integration: VscodeDebugIntegration,
  configuration: VbaDebugConfiguration,
  message: unknown,
  notifyAdapter: (
    command: string,
    argumentsValue: Record<string, unknown>
  ) => PromiseLike<unknown> | unknown
): Promise<void> | undefined {
  if (typeof message !== 'object' || message === null) {
    return undefined;
  }

  const request = message as {
    seq?: unknown;
    type?: unknown;
    command?: unknown;
    arguments?: unknown;
  };
  if (request.type !== 'request') {
    return undefined;
  }

  if (request.command === 'disconnect' || request.command === 'terminate') {
    integration.cancelRestartPreparation(configuration);
    return undefined;
  }

  if (request.command !== 'restart' || !Number.isInteger(request.seq)) {
    return undefined;
  }

  const requestArguments = request.arguments;
  const freshConfiguration = typeof requestArguments === 'object'
    && requestArguments !== null
    && typeof (requestArguments as { arguments?: unknown }).arguments === 'object'
    && (requestArguments as { arguments?: unknown }).arguments !== null
    ? (requestArguments as { arguments: VbaDebugConfiguration }).arguments
    : undefined;
  const restartConfiguration = freshConfiguration ?? configuration;
  const preparationId = integration.restartPreparationId(restartConfiguration);
  if (preparationId === undefined) {
    return undefined;
  }

  const restartRequestSequence = request.seq as number;
  return integration.runRestartPreparation(restartConfiguration).then(
    async () => {
      await notifyAdapter('vba/restartPrepared', {
        restartRequestSequence,
        preparationId,
        success: true,
        message: undefined
      });
    },
    async (error: unknown) => {
      await notifyAdapter('vba/restartPrepared', {
        restartRequestSequence,
        preparationId,
        success: false,
        message: error instanceof VbaDebugCancellationError
          ? 'VBA debug restart preparation was cancelled.'
          : error instanceof Error
            ? error.message
            : String(error)
      });
    }
  );
}

export async function stopVbaDebugSessionAfterLifecycleFailure(
  error: unknown,
  reportError: (message: string) => void,
  stopDebugging: () => PromiseLike<unknown> | unknown,
  disconnectAdapter: () => PromiseLike<unknown> | unknown
): Promise<void> {
  const detail = error instanceof Error ? error.message : String(error);
  reportError(
    `VBA debug restart preparation could not notify the debug adapter. ` +
    `Debugging will stop: ${detail}`
  );
  try {
    await stopDebugging();
    return;
  } catch (stopError) {
    const stopDetail = stopError instanceof Error ? stopError.message : String(stopError);
    reportError(
      `VS Code could not stop the VBA debug session. ` +
      `Forcing a direct adapter disconnect before retrying: ${stopDetail}`
    );
  }

  try {
    await disconnectAdapter();
  } catch (disconnectError) {
    const disconnectDetail = disconnectError instanceof Error
      ? disconnectError.message
      : String(disconnectError);
    reportError(
      `The direct VBA debug adapter disconnect also failed: ${disconnectDetail}`
    );
  }

  try {
    await stopDebugging();
  } catch (retryError) {
    const retryDetail = retryError instanceof Error ? retryError.message : String(retryError);
    reportError(
      `VS Code could not confirm VBA debug session termination after the fallback: ` +
      retryDetail
    );
  }
}

export interface VbaDebugTerminatedSessionLike {
  id: string;
  type: string;
  configuration: VbaDebugConfiguration;
}

export interface VbaDebugSessionTerminationIntegration {
  releaseSession(sessionId: string): void;
}

export function handleVbaDebugSessionTermination(
  integration: VbaDebugSessionTerminationIntegration,
  session: VbaDebugTerminatedSessionLike
): void {
  if (session.type !== 'vba') {
    return;
  }

  integration.releaseSession(session.id);
}

export class VscodeDebugIntegration {
  private activeSessionId: string | undefined;
  private readonly restartPreparations = new Map<string, VbaDebugRestartPreparationState>();
  private readonly restartPreparationIdsBySession = new Map<string, Set<string>>();

  public constructor(private readonly options: VscodeDebugIntegrationOptions) {}

  public resolveDebugConfiguration(
    configuration: VbaDebugConfiguration,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<VbaDebugConfiguration> {
    if (!this.options.debugConfigurationHost) {
      throw new Error('VBA debug configuration resolution is not available in this host.');
    }

    return resolveVbaDebugConfiguration(
      this.options.debugConfigurationHost,
      configuration,
      cancellationToken
    );
  }

  public provideDynamicDebugConfigurations(): readonly VbaDebugConfiguration[] {
    return this.options.debugConfigurationHost
      ? provideDynamicVbaDebugConfigurations(this.options.debugConfigurationHost)
      : [];
  }

  public prepareDebugConfigurationForRestart(
    configuration: VbaDebugConfiguration,
    _workspaceFolderPath?: string
  ): VbaDebugConfiguration {
    if (!this.options.debugConfigurationHost) {
      throw new Error('VBA debug configuration resolution is not available in this host.');
    }

    const projectRoot = configuration.project;
    if (typeof projectRoot !== 'string' || projectRoot.trim().length === 0) {
      throw new Error('A resolved VBA debug configuration requires a project for restart.');
    }

    const canonicalProjectRoot = canonicalVbaDebugProjectRoot(projectRoot);
    const id = createHash('sha256')
      .update(canonicalProjectRoot)
      .digest('hex')
      .slice(0, 12);
    const existing = this.restartPreparations.get(id);
    if (existing === undefined) {
      this.restartPreparations.set(id, {
        id,
        projectRoot: path.resolve(projectRoot)
      });
    }

    return {
      ...configuration,
      [VbaDebugRestartPreparationProperty]: {
        protocolVersion: VbaDebugRestartPreparationProtocolVersion,
        id
      }
    };
  }

  public async runRestartPreparation(configuration: VbaDebugConfiguration): Promise<void> {
    const preparationId = this.restartPreparationId(configuration);
    const preparation = preparationId === undefined
      ? undefined
      : this.restartPreparations.get(preparationId);
    if (preparation === undefined) {
      throw new Error('VBA debug restart preparation is unavailable.');
    }
    const projectRoot = configuration.project;
    if (
      typeof projectRoot !== 'string'
      || canonicalVbaDebugProjectRoot(projectRoot)
        !== canonicalVbaDebugProjectRoot(preparation.projectRoot)
    ) {
      throw new Error(
        `VBA debug restart preparation '${preparation.id}' does not match its project.`
      );
    }
    if (!this.options.debugConfigurationHost) {
      throw new Error('VBA debug configuration resolution is not available in this host.');
    }
    if (preparation.cancellation !== undefined) {
      throw new Error('VBA debug restart preparation is already running.');
    }

    if (this.activeSessionId !== undefined) {
      const preparationIds = this.restartPreparationIdsBySession.get(this.activeSessionId)
        ?? new Set<string>();
      preparationIds.add(preparation.id);
      this.restartPreparationIdsBySession.set(this.activeSessionId, preparationIds);
    }

    const cancellation = new VbaDebugCancellationController();
    preparation.cancellation = cancellation;
    try {
      await saveDirtyVbaDebugProjectSources(
        this.options.debugConfigurationHost,
        preparation.projectRoot,
        cancellation.token
      );
    } finally {
      if (preparation.cancellation === cancellation) {
        preparation.cancellation = undefined;
      }
      cancellation.dispose();
    }
  }

  public cancelRestartPreparation(configuration: VbaDebugConfiguration): void {
    const preparationId = this.restartPreparationId(configuration);
    if (preparationId !== undefined) {
      this.restartPreparations.get(preparationId)?.cancellation?.cancel();
    }
    for (const preparation of this.restartPreparations.values()) {
      preparation.cancellation?.cancel();
    }
  }

  public async createDebugAdapterExecutable(
    session: VbaDebugSessionLike
  ): Promise<VbaDebugAdapterExecutableSpec> {
    this.reserveSession(session.id);
    try {
      this.bindRestartPreparation(session);
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
    for (const preparationId of this.restartPreparationIdsBySession.get(sessionId) ?? []) {
      this.restartPreparations.get(preparationId)?.cancellation?.cancel();
      this.restartPreparations.delete(preparationId);
    }
    this.restartPreparationIdsBySession.delete(sessionId);
    if (this.activeSessionId === sessionId) {
      this.activeSessionId = undefined;
    }
  }

  public restartPreparationId(
    configuration: VbaDebugConfiguration
  ): string | undefined {
    const value = configuration[VbaDebugRestartPreparationProperty];
    if (typeof value !== 'object' || value === null) {
      return undefined;
    }

    const preparation = value as { protocolVersion?: unknown; id?: unknown };
    return preparation.protocolVersion === VbaDebugRestartPreparationProtocolVersion
      && typeof preparation.id === 'string'
      ? preparation.id
      : undefined;
  }

  private reserveSession(sessionId: string): void {
    if (this.activeSessionId !== undefined) {
      throw new Error('A VBA debug session is already running in this VS Code window.');
    }

    this.activeSessionId = sessionId;
  }

  private bindRestartPreparation(session: VbaDebugSessionLike): void {
    if (session.configuration === undefined) {
      return;
    }

    const preparationId = this.restartPreparationId(session.configuration);
    if (preparationId === undefined) {
      return;
    }
    if (!this.restartPreparations.has(preparationId)) {
      throw new Error(`VBA debug restart preparation '${preparationId}' is unavailable.`);
    }

    const preparationIds = this.restartPreparationIdsBySession.get(session.id)
      ?? new Set<string>();
    preparationIds.add(preparationId);
    this.restartPreparationIdsBySession.set(session.id, preparationIds);
  }
}

interface VbaDebugRestartPreparationState {
  readonly id: string;
  readonly projectRoot: string;
  cancellation?: VbaDebugCancellationController | undefined;
}

function canonicalVbaDebugProjectRoot(projectRoot: string): string {
  return path.normalize(path.resolve(projectRoot)).toLowerCase();
}

class VbaDebugCancellationController {
  private cancellationRequested = false;
  private readonly listeners = new Set<() => void>();
  public readonly token: VbaDebugCancellationToken;

  public constructor() {
    const controller = this;
    this.token = {
      get isCancellationRequested() {
        return controller.cancellationRequested;
      },
      onCancellationRequested: (listener) => {
        if (this.cancellationRequested) {
          listener();
          return { dispose: () => undefined };
        }

        this.listeners.add(listener);
        return {
          dispose: () => this.listeners.delete(listener)
        };
      }
    };
  }

  public cancel(): void {
    if (this.cancellationRequested) {
      return;
    }

    this.cancellationRequested = true;
    for (const listener of [...this.listeners]) {
      listener();
    }
    this.listeners.clear();
  }

  public dispose(): void {
    this.listeners.clear();
  }
}
