import {
  VbaDevCommandRuntimeOptions,
  runVbaDevProjectCommand
} from './devtoolRuntime';
import { reportCancellationDeliveryFailureAfterTrustedSuccess } from './cancellationDeliveryNotice';

export type WorkbookBackedProjectToolCommand = 'build' | 'test' | 'publish';

export interface WorkbookBackedProjectCommandOptions extends VbaDevCommandRuntimeOptions {
  toolCommandName: WorkbookBackedProjectToolCommand;
  title: string;
  showWarningMessage: (
    message: string,
    ...items: string[]
  ) => Thenable<string | undefined> | PromiseLike<string | undefined>;
}

export interface WorkbookBackedProjectCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
  cancellationRequested: boolean;
  cancellationRequestDelivered: boolean | undefined;
  cancellationRequestError: string | undefined;
}

export async function runWorkbookBackedProjectCommand(
  options: WorkbookBackedProjectCommandOptions
): Promise<WorkbookBackedProjectCommandResult | undefined> {
  const result = await runVbaDevProjectCommand(
    options,
    [options.toolCommandName],
    [],
    'document'
  );
  if (!result) {
    return undefined;
  }

  const commandTitle = options.title.replace('VBA Tools: ', '');
  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage(`${commandTitle} failed. See the VBA Tools output for details.`);
  } else if (!result.cancelled && result.exitCode === 0) {
    reportCancellationDeliveryFailureAfterTrustedSuccess(
      options,
      result,
      `${commandTitle} completed.`
    );
  }

  return {
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled,
    cancellationRequested: result.cancellationRequested,
    cancellationRequestDelivered: result.cancellationRequestDelivered,
    cancellationRequestError: result.cancellationRequestError
  };
}
