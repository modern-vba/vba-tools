import {
  CompanionExecutableResolution,
  CompanionExecutableResolver,
  ProcessRunner,
  RequiredVbaDevContract,
  VbaDevCapabilities,
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
  readonly purpose?: 'debug' | 'test' | undefined;
  readonly extensionRoot: string;
  readonly configuredDevToolPath?: string | undefined;
  readonly configuredDebugAdapterPath?: string | undefined;
  readonly vbaDevResolver?: CompanionExecutableResolver | undefined;
  readonly vbaDebugAdapterResolver?: VbaDebugAdapterResolver | undefined;
  readonly capabilitiesProcess?: ProcessRunner | undefined;
  readonly requiredContract?: RequiredVbaDevContract | undefined;
  readonly requiredDebugAdapterContract?: RequiredVbaDebugAdapterContract | undefined;
  readonly cancellationToken?: CommandCancellationToken | undefined;
  readonly reportCapabilityDiagnostic?: ((message: string) => void) | undefined;
  readonly isWorkspaceTrusted?: (() => boolean) | undefined;
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
  const declaredContract = options.requiredContract ?? loadRequiredVbaDevContract(options.extensionRoot);
  const requiredAdapter = options.requiredDebugAdapterContract ?? loadRequiredVbaDebugAdapterContract(options.extensionRoot);
  const purpose = options.purpose ?? 'debug';
  validateSnapshotVersions(declaredContract, requiredAdapter, purpose);
  const requiredContract = projectSnapshotContract(declaredContract, purpose);
  options = { ...options, requiredContract, requiredDebugAdapterContract: requiredAdapter };
  if (pinned !== undefined) {
    const inspected = await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: pinned.vbaDev.executablePath,
      requiredContract: options.requiredContract,
      runProcess: options.capabilitiesProcess,
      signal,
      reportDiagnostic: options.reportCapabilityDiagnostic,
      isWorkspaceTrusted: options.isWorkspaceTrusted
    });
    checkCancellation();
    const adapter = await resolveCompatibleVbaDebugAdapter({
      extensionRoot: options.extensionRoot,
      configuredPath: pinned.adapter.executablePath,
      requiredContract: options.requiredDebugAdapterContract,
      runProcess: options.capabilitiesProcess,
      cancellationToken,
      reportDiagnostic: options.reportCapabilityDiagnostic,
      isWorkspaceTrusted: options.isWorkspaceTrusted
    });
    checkCancellation();
    validateSnapshotVersions(inspected.capabilities, adapter.capabilities, options.purpose ?? 'debug');
    return Object.freeze({ vbaDev: { ...pinned.vbaDev, ...inspected }, adapter });
  }
  let vbaDev = await (options.vbaDevResolver ?? new VbaDevSessionResolver({
    extensionRoot: options.extensionRoot,
    configuredPath: options.configuredDevToolPath,
    runProcess: options.capabilitiesProcess,
    requiredContract: options.requiredContract,
    signal,
    reportDiagnostic: options.reportCapabilityDiagnostic,
    isWorkspaceTrusted: options.isWorkspaceTrusted
  })).resolve();
  checkCancellation();
  if (options.vbaDevResolver !== undefined) {
    const inspected = await resolveCompatibleVbaDev({
      extensionRoot: options.extensionRoot,
      configuredPath: vbaDev.executablePath,
      requiredContract: options.requiredContract,
      runProcess: options.capabilitiesProcess,
      signal,
      reportDiagnostic: options.reportCapabilityDiagnostic,
      isWorkspaceTrusted: options.isWorkspaceTrusted
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
      cancellationToken,
      reportDiagnostic: options.reportCapabilityDiagnostic,
      isWorkspaceTrusted: options.isWorkspaceTrusted
    }));
  checkCancellation();
  validateSnapshotVersions(vbaDev.capabilities, adapter.capabilities, options.purpose ?? 'debug');
  return Object.freeze({ vbaDev, adapter });
}

function projectSnapshotContract(
  declared: RequiredVbaDevContract,
  purpose: 'debug' | 'test'
): RequiredVbaDevContract {
  const names = purpose === 'debug'
    ? [
        'build.sourceSnapshot',
        'debug.sourceWorkbookPreparation',
        'invocation.stdinCancellation',
        'invocation.stdinWorkbookConfirmation',
        'sourceSnapshot.activeWindowsCodePage'
      ]
    : [
        'build.sourceSnapshot',
        'build.sourceSnapshotAnalysis',
        'test.sourceSnapshot',
        'test.sourceWorkbook',
        'invocation.stdinCancellation',
        'invocation.stdinWorkbookConfirmation',
        'sourceSnapshot.activeWindowsCodePage'
      ];
  const command = purpose === 'debug' ? 'prepare-debug' : 'test';
  return {
    contractVersion: declared.contractVersion,
    featureVersions: Object.fromEntries(names.map(name => [name, declared.featureVersions?.[name]])) as Record<string, string>,
    commandSchemaVersions: { [command]: declared.commandSchemaVersions[command] }
  };
}

function validateSnapshotVersions(
  cli: RequiredVbaDevContract | VbaDevCapabilities,
  adapter: RequiredVbaDebugAdapterContract,
  purpose: 'debug' | 'test'
): void {
  const debugDependencies = {
    'build.sourceSnapshot': '2.0',
    'debug.sourceWorkbookPreparation': '2.0',
    'invocation.stdinCancellation': '1.0',
    'invocation.stdinWorkbookConfirmation': '1.0',
    'sourceSnapshot.activeWindowsCodePage': '1.0'
  };
  const cliSchema = 'commandSchemaVersions' in cli
    ? cli.commandSchemaVersions['prepare-debug']
    : cli.commands['prepare-debug']?.outputSchemaVersion;
  if (cli.contractVersion !== '1.0' || adapter.contractVersion !== '1.0'
      || adapter.protocolVersion !== '2.0'
      || !adapter.transports.includes('stdio')
      || !adapter.commands.includes('cleanup')
      || adapter.sessionIdFormat !== 'lowercase-hex-32'
      || adapter.featureVersions?.['doctor.stdinCancellation'] !== '1.0'
      || adapter.featureVersions?.['debug.sourceWorkbook'] !== '1.0'
      || Object.keys(adapter.requiredVbaDevFeatureVersions).length !== Object.keys(debugDependencies).length
      || Object.entries(debugDependencies).some(([name, version]) =>
        adapter.requiredVbaDevFeatureVersions[name] !== version)
      || (purpose === 'debug' && (
        cliSchema !== '1.0'
        || Object.entries(debugDependencies).some(([name, version]) =>
          cli.featureVersions?.[name] !== version)
      ))
      || (purpose === 'test' && (
        cli.featureVersions?.['build.sourceSnapshot'] !== '2.0'
        || cli.featureVersions?.['build.sourceSnapshotAnalysis'] !== '1.0'
        || cli.featureVersions?.['test.sourceSnapshot'] !== '2.0'
        || cli.featureVersions?.['test.sourceWorkbook'] !== '1.0'
        || cli.featureVersions?.['invocation.stdinCancellation'] !== '1.0'
        || cli.featureVersions?.['invocation.stdinWorkbookConfirmation'] !== '1.0'
        || cli.featureVersions?.['sourceSnapshot.activeWindowsCodePage'] !== '1.0'
      ))) {
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
