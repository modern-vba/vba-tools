import {
  VbaDevCommandRuntimeOptions,
  resolveVbaDevProjectCommandContext,
  runResolvedVbaDevProjectCommand
} from './devtoolRuntime';
import {
  CompatibleVbaDebugAdapter,
  RequiredVbaDebugAdapterContract,
  VbaDebugAdapterResolver,
  resolveCompatibleVbaDebugAdapter
} from './debugAdapter';
import { ProcessRunner } from './devtool';
import {
  parseVbaDebugAdapterDoctorReport,
  renderVbaDebugAdapterDoctorReport
} from './debugAdapterDoctorOutput';
import {
  parseVbaDevDoctorReport,
  renderVbaDevDoctorReport
} from './vbaDevDoctorOutput';
import {
  CancellationDisposable,
  CommandCancellationToken,
  StartVbaDevProcess,
  runCompanionCommand
} from './devtoolCommand';

export const FirstRunDoctorPromptState = {
  Prompted: 'vbaTools.doctor.firstRunPrompted',
  Suppress: 'vbaTools.doctor.suppressFirstRunPrompt'
} as const;

export interface WorkspaceState {
  get<T>(key: string): T | undefined;
  update(key: string, value: unknown): Thenable<void> | Promise<void>;
}

export interface DoctorCommandOptions extends VbaDevCommandRuntimeOptions {
  vbaDebugAdapterResolver?: VbaDebugAdapterResolver | undefined;
  configuredDebugAdapterPath?: string | undefined;
  requiredDebugAdapterContract?: RequiredVbaDebugAdapterContract | undefined;
  debugAdapterCapabilitiesProcess?: ProcessRunner | undefined;
  startDebugAdapterProcess?: StartVbaDevProcess | undefined;
}

export interface DoctorCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
}

export interface FirstRunDoctorPromptOptions {
  workspaceState: WorkspaceState;
  showInformationMessage: (
    message: string,
    ...items: string[]
  ) => Thenable<string | undefined> | Promise<string | undefined>;
  runDoctor: () => Promise<void>;
}

export async function runDoctorCommand(options: DoctorCommandOptions): Promise<DoctorCommandResult | undefined> {
  const context = await resolveVbaDevProjectCommandContext(options);
  if (!context) {
    return undefined;
  }
  options.outputChannel.appendLine('Project automation');
  const resolution = await options.vbaDevResolver?.resolve();
  if (
    resolution?.source === 'bundled'
    && resolution.configuredPath !== undefined
  ) {
    options.outputChannel.appendLine('vba-dev executable fallback:');
    options.outputChannel.appendLine(`  Configured: ${resolution.configuredPath}`);
    options.outputChannel.appendLine(`  Effective: ${resolution.executablePath}`);
  }

  const result = await runResolvedVbaDevProjectCommand(
    options,
    context,
    ['doctor', '--format', 'json'],
    [],
    false
  );
  let projectBlocking = false;
  if (!result.cancelled || result.stdout.trim().length > 0) {
    try {
      const doctorCapability = context.capabilities.commands.doctor;
      if (doctorCapability === undefined) {
        throw new Error('vba-dev does not advertise the Doctor output schema.');
      }
      const report = parseVbaDevDoctorReport(
        result.stdout,
        doctorCapability.outputSchemaVersion,
        context.capabilities.toolVersion,
        result.exitCode,
        {
          scope: 'project',
          project: result.projectRoot
        }
      );
      const renderedOutput = `${renderVbaDevDoctorReport(report).join('\n')}\n`;
      options.outputChannel.append(renderedOutput);
      options.diagnosticReporter?.refresh(
        `project:${result.projectRoot}`,
        renderedOutput
      );
      projectBlocking = result.exitCode !== 130 &&
        (!report.complete || report.status === 'fail' || report.status === 'unverified');
    } catch (error) {
      projectBlocking = true;
      options.outputChannel.appendLine(
        `Doctor command infrastructure failure: ${getErrorMessage(error)}`
      );
    }
  }
  let adapterBlocking = false;
  let adapterCancelled = false;
  const adapterResolver = options.vbaDebugAdapterResolver ?? {
    resolve: () => resolveCompatibleVbaDebugAdapter({
      extensionRoot: options.extensionRoot,
      configuredPath: options.configuredDebugAdapterPath,
      requiredContract: options.requiredDebugAdapterContract,
      runProcess: options.debugAdapterCapabilitiesProcess,
      cancellationToken: options.cancellationToken
    })
  };

  if (!result.cancelled) {
    options.outputChannel.appendLine('VBE debugging');
    let adapterProcessClosed = false;
    try {
      const adapter = await resolveAdapterWithCancellation(
        adapterResolver,
        options.cancellationToken
      );
      if (options.cancellationToken?.isCancellationRequested) {
        adapterCancelled = true;
        options.outputChannel.appendLine('VBE debugging command cancelled.');
      } else {
        const adapterResult = await runCompanionCommand({
          executablePath: adapter.executablePath,
          args: [
            'doctor',
            '--format',
            'json',
            '--cancellation-transport',
            'stdin-v1'
          ],
          outputChannel: options.outputChannel,
          displayName: 'VBE debugging',
          cancellationTransport: 'stdin-v1',
          cancellationToken: options.cancellationToken,
          startProcess: options.startDebugAdapterProcess
        });
        adapterProcessClosed = true;
        const report = parseVbaDebugAdapterDoctorReport(
          adapterResult.stdout,
          adapter.capabilities.commandSchemaVersions.doctor,
          adapter.capabilities.toolVersion,
          adapterResult.exitCode,
          adapterResult.cancelled && adapterResult.cancellationRequestDelivered === true
        );
        for (const line of renderVbaDebugAdapterDoctorReport(report)) {
          options.outputChannel.appendLine(line);
        }
        if (adapterResult.cancellationRequestDelivered === false) {
          throw new Error('VBE debugging cancellation request could not be delivered.');
        }
        adapterCancelled = adapterResult.cancelled && !report.complete;
        adapterBlocking = adapterCancelled
          ? report.checks.some((check) =>
            (
              check.id === 'vbe.breakpointCleanup' ||
              check.id === 'excel.processClose' ||
              check.id === 'workspace.deletion'
            ) && (
              check.status === 'fail' || check.status === 'unverified'
            )
          )
          : report.status === 'fail' || report.status === 'unverified';
      }
    } catch (error) {
      if (
        options.cancellationToken?.isCancellationRequested &&
        !adapterProcessClosed
      ) {
        adapterCancelled = true;
        options.outputChannel.appendLine('VBE debugging command cancelled.');
      } else {
        adapterBlocking = true;
        options.outputChannel.appendLine(
          `Doctor command infrastructure failure: ${getErrorMessage(error)}`
        );
      }
    }
  }

  if (projectBlocking || adapterBlocking) {
    await options.showErrorMessage('VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.');
  }

  return {
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled || adapterCancelled
  };
}

export async function promptForFirstRunDoctor(options: FirstRunDoctorPromptOptions): Promise<void> {
  if (options.workspaceState.get<boolean>(FirstRunDoctorPromptState.Suppress)) {
    return;
  }

  if (options.workspaceState.get<boolean>(FirstRunDoctorPromptState.Prompted)) {
    return;
  }

  const answer = await options.showInformationMessage(
    'VBA Tools detected a workbook-backed project. Run Doctor?',
    'Run Doctor',
    "Don't Ask Again"
  );
  await options.workspaceState.update(FirstRunDoctorPromptState.Prompted, true);

  if (answer === "Don't Ask Again") {
    await options.workspaceState.update(FirstRunDoctorPromptState.Suppress, true);
    return;
  }

  if (answer === 'Run Doctor') {
    await options.runDoctor();
  }
}

function resolveAdapterWithCancellation(
  resolver: VbaDebugAdapterResolver,
  cancellationToken?: CommandCancellationToken | undefined
): Promise<CompatibleVbaDebugAdapter> {
  if (cancellationToken === undefined) {
    return resolver.resolve();
  }

  return new Promise((resolve, reject) => {
    let settled = false;
    let cancellationSubscription: CancellationDisposable | undefined;
    const settle = (complete: () => void): void => {
      if (settled) {
        return;
      }
      settled = true;
      cancellationSubscription?.dispose();
      complete();
    };
    const cancel = (): void => {
      settle(() => reject(new Error('VBE debugging command cancelled.')));
    };

    cancellationSubscription = cancellationToken.onCancellationRequested(cancel);
    if (settled) {
      cancellationSubscription.dispose();
      return;
    }
    if (cancellationToken.isCancellationRequested) {
      cancel();
      return;
    }

    resolver.resolve().then(
      (adapter) => settle(() => resolve(adapter)),
      (error: unknown) => settle(() => reject(error))
    );
  });
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
