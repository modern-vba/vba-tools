import * as path from 'node:path';
import { randomBytes } from 'node:crypto';
import { SnapshotProviderCancellationError, SnapshotProviders, resolveSnapshotProviders, snapshotActiveWindowsCodePage } from './snapshotProviders';

import {
  CompanionExecutableResolver,
  ProcessRunner,
  RequiredVbaDevContract,
  isReportedVbaDevResolutionFailure
} from './devtool';
import {
  RequiredVbaDebugAdapterContract,
  VbaDebugAdapterResolver,
  runDebugAdapterProcess
} from './debugAdapter';
import {
  VbaDebugCancellationError,
  VbaDebugCancellationToken,
  VbaDebugConfiguration,
  VbaDebugConfigurationHost,
  VbaDebugSelectionError,
  normalizeVbaDebugConfiguration,
  provideDynamicVbaDebugConfigurations,
  recaptureBoundVbaDebugConfiguration,
  resolveVbaDebugConfiguration
} from './vscodeDebugConfiguration';

export const VbaDebugRestartPreparationProperty = '__vbaRestartPreparation';
export const VbaDebugRestartPreparationProtocolVersion = 1;
const VbaDebugRestartGenerationMaximum = 0x7fffffff;

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
  reportError: (message: string) => unknown,
  requireTrustedWorkspace?: (() => Promise<boolean>) | undefined
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
      if (requireTrustedWorkspace !== undefined
          && !await requireTrustedWorkspace()) {
        return undefined;
      }

      try {
        const resolvedConfiguration = await integration.resolveDebugConfiguration(
          resolveWorkspaceRelativeProject(configuration, workspaceFolderPath),
          cancellationToken
        );
        const boundConfiguration = integration.prepareDebugConfigurationForRestart === undefined
          ? resolvedConfiguration
          : integration.prepareDebugConfigurationForRestart(
              resolvedConfiguration,
              workspaceFolderPath
            );
        if (debugConfigurationObserver !== undefined) {
          debugConfigurationObserver(boundConfiguration);
          return undefined;
        }

        return boundConfiguration;
      } catch (error) {
        if (error instanceof VbaDebugCancellationError || error instanceof SnapshotProviderCancellationError) {
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
  stop: () => PromiseLike<unknown> | unknown;
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
  getConfiguredDebugAdapterPath?: (() => string | undefined) | undefined;
  vbaDevResolver?: CompanionExecutableResolver | undefined;
  vbaDebugAdapterResolver?: VbaDebugAdapterResolver | undefined;
  createDebugSessionId?: (() => string) | undefined;
  capabilitiesProcess?: ProcessRunner | undefined;
  requiredContract?: RequiredVbaDevContract | undefined;
  requiredDebugAdapterContract?: RequiredVbaDebugAdapterContract | undefined;
  debugConfigurationHost?: VbaDebugConfigurationHost | undefined;
  debugAdapterCleanupProcess?: ProcessRunner | undefined;
  reportDebugAdapterCleanupWarning?: ((message: string) => unknown) | undefined;
  requireTrustedWorkspace?: (() => Promise<boolean>) | undefined;
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

  const restartConfiguration = configuration;
  const preparationId = integration.restartPreparationId(restartConfiguration);
  if (preparationId === undefined) {
    return undefined;
  }
  if (integration.hasRunningRestartPreparation(restartConfiguration)) {
    return undefined;
  }

  const restartRequestSequence = request.seq as number;
  let preparation: VbaDebugRestartPreparationRun;
  try {
    preparation = integration.beginRestartPreparation(
      restartConfiguration,
      restartRequestSequence
    );
  } catch {
    return undefined;
  }
  return preparation.completion.then(
    async (launch) => {
      await notifyAdapter('vba/restartPrepared', {
        sessionId: preparation.adapterSessionId,
        generation: preparation.generation,
        launch,
        restartRequestSequence,
        preparationId: preparation.id,
        success: true,
        message: undefined
      });
    },
    async (error: unknown) => {
      await notifyAdapter('vba/restartPrepared', {
        sessionId: preparation.adapterSessionId,
        generation: preparation.generation,
        restartRequestSequence,
        preparationId: preparation.id,
        success: false,
        message: error instanceof VbaDebugCancellationError
          ? 'VBA debug restart preparation was cancelled.'
          : error instanceof Error
            ? error.message
            : String(error)
      });
    }
  ).catch((error: unknown) => {
    integration.failRestartPreparationNotification(
      restartConfiguration,
      restartRequestSequence
    );
    throw error;
  });
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

export interface VbaDebugRestartPreparationRun {
  readonly id: string;
  readonly generation: number;
  readonly adapterSessionId: string;
  readonly completion: Promise<VbaDebugConfiguration>;
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
  private readonly capturedProviders = new WeakMap<VbaDebugConfiguration, SnapshotProviders>();
  private activeSessionId: string | undefined;
  private activeSessionReservation: symbol | undefined;
  private activeSessionCancellation: VbaDebugCancellationController | undefined;
  private shutdownRequested = false;
  private readonly restartPreparations = new Map<string, VbaDebugRestartPreparationState>();
  private readonly restartPreparationIdsBySession = new Map<string, Set<string>>();
  private readonly ownedAdapterSessions = new Map<string, OwnedVbaDebugAdapterSession>();

  public constructor(private readonly options: VscodeDebugIntegrationOptions) {}

  public async resolveDebugConfiguration(
    configuration: VbaDebugConfiguration,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<VbaDebugConfiguration> {
    if (!this.options.debugConfigurationHost) {
      throw new Error('VBA debug configuration resolution is not available in this host.');
    }

    const providers = await resolveSnapshotProviders({
      ...this.options,
      cancellationToken,
      configuredDevToolPath: this.options.getConfiguredDevToolPath(),
      configuredDebugAdapterPath: this.options.getConfiguredDebugAdapterPath?.()
    });

    const resolved = await resolveVbaDebugConfiguration(
      this.snapshotHost(providers),
      configuration,
      cancellationToken
    );
    this.capturedProviders.set(resolved, providers);
    return resolved;
  }

  private snapshotHost(providers: SnapshotProviders): VbaDebugConfigurationHost {
    const host = this.options.debugConfigurationHost!;
    if (typeof host.captureSourceInventory !== 'function') {
      throw new VbaDebugSelectionError('VBA debug source inventory capture is unavailable in this host.');
    }
    const activeCodePage = snapshotActiveWindowsCodePage(providers);
    return {
      ...host,
      captureSourceInventory: (sourceSetPath, token) => host.captureSourceInventory(sourceSetPath, token, activeCodePage)
    };
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

    const id = randomBytes(16).toString('hex');
    this.restartPreparations.set(id, {
      id,
      projectRoot: path.resolve(projectRoot),
      configuration,
      providers: this.capturedProviders.get(configuration),
      generation: 0
    });

    return {
      ...configuration,
      [VbaDebugRestartPreparationProperty]: {
        protocolVersion: VbaDebugRestartPreparationProtocolVersion,
        id,
        generation: 0
      }
    };
  }

  public captureBoundRestartConfiguration(
    configuration: VbaDebugConfiguration,
    cancellationToken?: VbaDebugCancellationToken
  ): Promise<VbaDebugConfiguration> {
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

    return resolveSnapshotProviders({
      ...this.options,
      cancellationToken,
      configuredDevToolPath: this.options.getConfiguredDevToolPath(),
      configuredDebugAdapterPath: this.options.getConfiguredDebugAdapterPath?.()
    }, preparation.providers).then(providers => recaptureBoundVbaDebugConfiguration(
      this.snapshotHost(providers),
      preparation.configuration,
      cancellationToken
    ));
  }

  public beginRestartPreparation(
    configuration: VbaDebugConfiguration,
    restartRequestSequence?: number
  ): VbaDebugRestartPreparationRun {
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
    if (preparation.adapterSessionId === undefined) {
      throw new Error('VBA debug restart preparation is not bound to an active adapter session.');
    }
    if (preparation.cancellation !== undefined ||
        preparation.restartRequestSequence !== undefined) {
      throw new Error('VBA debug restart preparation is already running.');
    }
    if (preparation.generation >= VbaDebugRestartGenerationMaximum) {
      throw new Error('The VBA debug restart generation is exhausted.');
    }
    preparation.generation += 1;

    if (this.activeSessionId !== undefined) {
      const preparationIds = this.restartPreparationIdsBySession.get(this.activeSessionId)
        ?? new Set<string>();
      preparationIds.add(preparation.id);
      this.restartPreparationIdsBySession.set(this.activeSessionId, preparationIds);
    }

    const cancellation = new VbaDebugCancellationController();
    preparation.cancellation = cancellation;
    preparation.restartRequestSequence = restartRequestSequence;
    const adapterSessionId = preparation.adapterSessionId;
    const completion = (async (): Promise<VbaDebugConfiguration> => {
      try {
        const captured = await this.captureBoundRestartConfiguration(
          configuration,
          cancellation.token
        );
        return {
          ...captured,
          [VbaDebugRestartPreparationProperty]: {
            protocolVersion: VbaDebugRestartPreparationProtocolVersion,
            id: preparation.id,
            generation: preparation.generation
          }
        };
      } finally {
        if (preparation.cancellation === cancellation) {
          preparation.cancellation = undefined;
        }
        cancellation.dispose();
      }
    })();
    return {
      id: preparation.id,
      generation: preparation.generation,
      adapterSessionId,
      completion
    };
  }

  public async runRestartPreparation(configuration: VbaDebugConfiguration): Promise<void> {
    await this.beginRestartPreparation(configuration).completion;
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
  ): Promise<VbaDebugAdapterExecutableSpec | undefined> {
    if (this.options.requireTrustedWorkspace !== undefined
        && !await this.options.requireTrustedWorkspace()) {
      return undefined;
    }

    if (this.shutdownRequested) {
      return undefined;
    }
    const reservation = this.reserveSession(session.id);
    const cancellationToken = this.activeSessionCancellation!.token;
    try {
      const preparationId = session.configuration === undefined
        ? undefined : this.restartPreparationId(session.configuration);
      const providers = (preparationId === undefined
        ? undefined : this.restartPreparations.get(preparationId)?.providers)
        ?? await resolveSnapshotProviders({
          ...this.options,
          cancellationToken,
          configuredDevToolPath: this.options.getConfiguredDevToolPath(),
          configuredDebugAdapterPath: this.options.getConfiguredDebugAdapterPath?.()
        });
      const devtool = providers.vbaDev;
      const standaloneDebugAdapter = providers.adapter;
      if (!this.hasSessionReservation(session.id, reservation)) {
        return undefined;
      }
      const adapterSessionId = this.options.createDebugSessionId?.()
        ?? randomBytes(16).toString('hex');
      if (!/^[0-9a-f]{32}$/.test(adapterSessionId)) {
        throw new Error(
          'The generated VBA debug adapter session ID must be 32 lowercase hexadecimal characters.'
        );
      }
      this.bindRestartPreparation(session, adapterSessionId);
      this.ownedAdapterSessions.set(session.id, {
        executablePath: standaloneDebugAdapter.executablePath,
        adapterSessionId,
        stop: session.stop
      });

      return {
        command: standaloneDebugAdapter.executablePath,
        args: [
          '--stdio',
          '--vba-dev',
          devtool.executablePath,
          '--session',
          adapterSessionId
        ],
        options: session.workspaceRoot === undefined
          ? undefined
          : { cwd: session.workspaceRoot }
      };
    } catch (error) {
      this.releaseSessionReservation(session.id, reservation);
      if (isReportedVbaDevResolutionFailure(error) || error instanceof SnapshotProviderCancellationError) {
        return undefined;
      }
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
      const cancellation = this.activeSessionCancellation;
      this.activeSessionId = undefined;
      this.activeSessionReservation = undefined;
      this.activeSessionCancellation = undefined;
      cancellation?.cancel();
      cancellation?.dispose();
    }
  }

  public async handleAdapterExit(sessionId: string): Promise<void> {
    const ownedSession = this.ownedAdapterSessions.get(sessionId);
    this.releaseSession(sessionId);
    if (ownedSession === undefined) {
      return;
    }

    if (ownedSession.cleanup !== undefined) {
      await ownedSession.cleanup;
      if (this.ownedAdapterSessions.get(sessionId) !== ownedSession) {
        return;
      }
    }

    await this.cleanupOwnedAdapterSession(sessionId, ownedSession);
  }

  private async cleanupOwnedAdapterSession(
    sessionId: string,
    ownedSession: OwnedVbaDebugAdapterSession
  ): Promise<void> {
    const cleanup = this.runOwnedAdapterCleanup(sessionId, ownedSession);
    ownedSession.cleanup = cleanup;
    try {
      await cleanup;
    } finally {
      if (
        this.ownedAdapterSessions.get(sessionId) === ownedSession
        && ownedSession.cleanup === cleanup
      ) {
        ownedSession.cleanup = undefined;
      }
    }
  }

  private async runOwnedAdapterCleanup(
    sessionId: string,
    ownedSession: OwnedVbaDebugAdapterSession
  ): Promise<void> {
    try {
      const cleanupProcess = this.options.debugAdapterCleanupProcess
        ?? runDebugAdapterProcess;
      await cleanupProcess(
        ownedSession.executablePath,
        ['cleanup', '--session', ownedSession.adapterSessionId]
      );
      if (this.ownedAdapterSessions.get(sessionId) === ownedSession) {
        this.ownedAdapterSessions.delete(sessionId);
      }
    } catch (error) {
      this.options.reportDebugAdapterCleanupWarning?.(
        `VBA debug adapter cleanup retained session '${ownedSession.adapterSessionId}': ` +
        (error instanceof Error ? error.message : String(error))
      );
    }
  }

  public async shutdown(): Promise<void> {
    this.shutdownRequested = true;
    for (const preparation of this.restartPreparations.values()) {
      preparation.cancellation?.cancel();
    }
    if (this.activeSessionId !== undefined) {
      this.releaseSession(this.activeSessionId);
    }
    for (const [sessionId, ownedSession] of [...this.ownedAdapterSessions.entries()]) {
      try {
        await ownedSession.stop();
      } catch (error) {
        this.options.reportDebugAdapterCleanupWarning?.(
          `VBA debug session '${ownedSession.adapterSessionId}' could not be stopped: ` +
          (error instanceof Error ? error.message : String(error))
        );
      }
      if (this.ownedAdapterSessions.get(sessionId) !== ownedSession) {
        continue;
      }
      this.releaseSession(sessionId);
      if (ownedSession.cleanup === undefined) {
        await this.cleanupOwnedAdapterSession(sessionId, ownedSession);
      } else {
        await ownedSession.cleanup;
      }
    }
  }

  public restartPreparationId(
    configuration: VbaDebugConfiguration
  ): string | undefined {
    const value = configuration[VbaDebugRestartPreparationProperty];
    if (typeof value !== 'object' || value === null) {
      return undefined;
    }

    const preparation = value as {
      protocolVersion?: unknown;
      id?: unknown;
      generation?: unknown;
    };
    return preparation.protocolVersion === VbaDebugRestartPreparationProtocolVersion
      && typeof preparation.id === 'string'
      && /^[0-9a-f]{32}$/.test(preparation.id)
      && Number.isSafeInteger(preparation.generation)
      && (preparation.generation as number) >= 0
      && (preparation.generation as number) <= VbaDebugRestartGenerationMaximum
      ? preparation.id
      : undefined;
  }

  public hasRunningRestartPreparation(
    configuration: VbaDebugConfiguration
  ): boolean {
    const preparationId = this.restartPreparationId(configuration);
    return preparationId !== undefined
      && (() => {
        const preparation = this.restartPreparations.get(preparationId);
        return preparation?.cancellation !== undefined
          || preparation?.restartRequestSequence !== undefined;
      })();
  }

  public observeDebugAdapterMessage(
    configuration: VbaDebugConfiguration,
    message: unknown
  ): void {
    if (typeof message !== 'object' || message === null) {
      return;
    }
    const response = message as {
      type?: unknown;
      command?: unknown;
      request_seq?: unknown;
    };
    if (
      response.type !== 'response'
      || response.command !== 'restart'
      || !Number.isInteger(response.request_seq)
    ) {
      return;
    }

    this.failRestartPreparationNotification(
      configuration,
      response.request_seq as number
    );
  }

  public failRestartPreparationNotification(
    configuration: VbaDebugConfiguration,
    restartRequestSequence: number
  ): void {
    const preparationId = this.restartPreparationId(configuration);
    const preparation = preparationId === undefined
      ? undefined
      : this.restartPreparations.get(preparationId);
    if (preparation?.restartRequestSequence === restartRequestSequence) {
      preparation.restartRequestSequence = undefined;
    }
  }

  private reserveSession(sessionId: string): symbol {
    if (this.activeSessionId !== undefined) {
      throw new Error('A VBA debug session is already running in this VS Code window.');
    }

    const reservation = Symbol(sessionId);
    this.activeSessionId = sessionId;
    this.activeSessionReservation = reservation;
    this.activeSessionCancellation = new VbaDebugCancellationController();
    return reservation;
  }

  private hasSessionReservation(sessionId: string, reservation: symbol): boolean {
    return !this.shutdownRequested
      && this.activeSessionId === sessionId
      && this.activeSessionReservation === reservation;
  }

  private releaseSessionReservation(sessionId: string, reservation: symbol): void {
    if (
      this.activeSessionId === sessionId
      && this.activeSessionReservation === reservation
    ) {
      this.releaseSession(sessionId);
    }
  }

  private bindRestartPreparation(
    session: VbaDebugSessionLike,
    adapterSessionId: string
  ): void {
    if (session.configuration === undefined) {
      return;
    }

    const preparationId = this.restartPreparationId(session.configuration);
    if (preparationId === undefined) {
      return;
    }
    const preparation = this.restartPreparations.get(preparationId);
    if (preparation === undefined) {
      throw new Error(`VBA debug restart preparation '${preparationId}' is unavailable.`);
    }
    if (preparation.adapterSessionId !== undefined) {
      throw new Error(`VBA debug restart preparation '${preparationId}' is already bound.`);
    }
    preparation.adapterSessionId = adapterSessionId;
    preparation.vscodeSessionId = session.id;

    const preparationIds = this.restartPreparationIdsBySession.get(session.id)
      ?? new Set<string>();
    preparationIds.add(preparationId);
    this.restartPreparationIdsBySession.set(session.id, preparationIds);
  }
}

interface VbaDebugRestartPreparationState {
  readonly providers?: SnapshotProviders | undefined;
  readonly id: string;
  readonly projectRoot: string;
  readonly configuration: VbaDebugConfiguration;
  generation: number;
  adapterSessionId?: string | undefined;
  vscodeSessionId?: string | undefined;
  restartRequestSequence?: number | undefined;
  cancellation?: VbaDebugCancellationController | undefined;
}

interface OwnedVbaDebugAdapterSession {
  readonly executablePath: string;
  readonly adapterSessionId: string;
  readonly stop: () => PromiseLike<unknown> | unknown;
  cleanup?: Promise<void> | undefined;
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
