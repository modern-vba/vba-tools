import {
  CompanionExecutableResolution,
  CompanionExecutableResolver,
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevSessionResolver,
  loadRequiredVbaDevContract,
  resolveCompatibleVbaDev
} from './devtool';
import {
  CompatibleVbaDebugAdapter,
  RequiredVbaDebugAdapterContract,
  VbaDebugAdapterResolver,
  loadRequiredVbaDebugAdapterContract,
  resolveCompatibleVbaDebugAdapter
} from './debugAdapter';
import { CommandCancellationToken } from './devtoolCommand';

export class SnapshotProviderCancellationError extends Error {}

export interface SnapshotProviderOptions {
  readonly extensionRoot: string;
  readonly configuredDevToolPath?: string | undefined;
  readonly configuredDebugAdapterPath?: string | undefined;
  readonly vbaDevResolver?: CompanionExecutableResolver | undefined;
  readonly vbaDebugAdapterResolver?: VbaDebugAdapterResolver | undefined;
  readonly capabilitiesProcess?: ProcessRunner | undefined;
  readonly requiredContract?: RequiredVbaDevContract | undefined;
  readonly requiredDebugAdapterContract?: RequiredVbaDebugAdapterContract | undefined;
  readonly cancellationToken?: CommandCancellationToken | undefined;
}

export interface SnapshotProviders {
  readonly vbaDev: CompanionExecutableResolution;
  readonly adapter: CompatibleVbaDebugAdapter;
}

/** Resolves the compatible snapshot pair before any source capture or artifacts. */
export async function resolveSnapshotProviders(
  options: SnapshotProviderOptions,
  pinned?: SnapshotProviders
): Promise<SnapshotProviders> {
  const cancellation = new AbortController();
  const cancel = () => cancellation.abort();
  const subscription = options.cancellationToken?.onCancellationRequested(cancel);
  if (options.cancellationToken?.isCancellationRequested) { cancel(); }
  const token: CommandCancellationToken = {
    get isCancellationRequested() { return cancellation.signal.aborted; },
    onCancellationRequested: listener => {
      cancellation.signal.addEventListener('abort', listener);
      return { dispose: () => cancellation.signal.removeEventListener('abort', listener) };
    }
  };
  let abortListener: (() => void) | undefined;
  const cancelled = new Promise<never>((_resolve, reject) => {
    abortListener = () => reject(new SnapshotProviderCancellationError('Snapshot provider inspection was cancelled.'));
    cancellation.signal.addEventListener('abort', abortListener);
    if (cancellation.signal.aborted) { abortListener(); }
  });
  try {
    // Cancelling a snapshot abandons its wait for a shared resolver, not that
    // resolver's process. Only this invocation's inspections receive the signal.
    return await Promise.race([cancelled, inspectSnapshotProviders({
      ...options,
      capabilitiesProcess: options.capabilitiesProcess === undefined ? undefined
        : (file, args) => options.capabilitiesProcess!(file, args, cancellation.signal)
    }, pinned, token, cancellation.signal)]);
  } catch (error) {
    if (token.isCancellationRequested) {
      throw new SnapshotProviderCancellationError('Snapshot provider inspection was cancelled.');
    }
    throw error;
  } finally {
    subscription?.dispose();
    if (abortListener !== undefined) { cancellation.signal.removeEventListener('abort', abortListener); }
  }
}

async function inspectSnapshotProviders(
  options: SnapshotProviderOptions,
  pinned: SnapshotProviders | undefined,
  cancellationToken: CommandCancellationToken,
  signal: AbortSignal
): Promise<SnapshotProviders> {
  const checkCancellation = () => {
    if (cancellationToken.isCancellationRequested || options.cancellationToken?.isCancellationRequested) {
      throw new SnapshotProviderCancellationError('Snapshot provider inspection was cancelled.');
    }
  };
  checkCancellation();
  const requiredContract = options.requiredContract ?? loadRequiredVbaDevContract(options.extensionRoot);
  const requiredAdapter = options.requiredDebugAdapterContract ?? loadRequiredVbaDebugAdapterContract(options.extensionRoot);
  validateSnapshotVersions(requiredContract, requiredAdapter);
  options = { ...options, requiredContract, requiredDebugAdapterContract: requiredAdapter };
  if (pinned !== undefined) {
    const inspected = await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: pinned.vbaDev.executablePath,
      requiredContract: options.requiredContract,
      runProcess: options.capabilitiesProcess,
      signal
    });
    checkCancellation();
    const adapter = await resolveCompatibleVbaDebugAdapter({
      extensionRoot: options.extensionRoot,
      configuredPath: pinned.adapter.executablePath,
      requiredContract: options.requiredDebugAdapterContract,
      runProcess: options.capabilitiesProcess,
      cancellationToken
    });
    checkCancellation();
    validateSnapshotVersions(inspected.capabilities, adapter.capabilities);
    return Object.freeze({ vbaDev: { ...pinned.vbaDev, ...inspected }, adapter });
  }
  let vbaDev = await (options.vbaDevResolver ?? new VbaDevSessionResolver({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract,
    signal
  })).resolve();
  checkCancellation();
  if (options.vbaDevResolver !== undefined) {
    const inspected = await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: vbaDev.executablePath,
      requiredContract: options.requiredContract,
      runProcess: options.capabilitiesProcess,
      signal
    });
    checkCancellation();
    vbaDev = { ...vbaDev, ...inspected };
  }
  const adapter = await (options.vbaDebugAdapterResolver?.resolve()
    ?? resolveCompatibleVbaDebugAdapter({
      extensionRoot: options.extensionRoot,
      configuredPath: options.configuredDebugAdapterPath,
      runProcess: options.capabilitiesProcess,
      requiredContract: options.requiredDebugAdapterContract,
      cancellationToken
    }));
  checkCancellation();
  validateSnapshotVersions(vbaDev.capabilities, adapter.capabilities);
  return Object.freeze({ vbaDev, adapter });
}

function validateSnapshotVersions(
  cli: Pick<RequiredVbaDevContract, 'contractVersion' | 'featureVersions'>,
  adapter: RequiredVbaDebugAdapterContract
): void {
  if (cli.contractVersion !== '1.0' || adapter.contractVersion !== '1.0'
      || adapter.protocolVersion !== '2.0'
      || adapter.requiredVbaDevFeatureVersions['build.sourceSnapshot'] !== '2.0'
      || cli.featureVersions?.['build.sourceSnapshot'] !== '2.0'
      || cli.featureVersions?.['test.sourceSnapshot'] !== '2.0'
      || cli.featureVersions?.['sourceSnapshot.activeWindowsCodePage'] !== '1.0') {
    throw new Error('Snapshot schema 2 requires the matching extension, CLI feature and adapter protocol matrix.');
  }
}

export function snapshotActiveWindowsCodePage(providers: SnapshotProviders): number {
  const codePage = providers.vbaDev.capabilities.activeWindowsCodePage;
  if (!Number.isSafeInteger(codePage) || codePage! <= 0) {
    throw new Error('The snapshot CLI did not report a valid active Windows code page.');
  }
  return codePage!;
}
