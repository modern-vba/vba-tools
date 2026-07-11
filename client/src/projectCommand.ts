import {
  VbaDevCommandRuntimeOptions,
  runVbaDevProjectCommand
} from './devtoolRuntime';

export type WorkbookBackedProjectToolCommand = 'build' | 'test' | 'publish' | 'export';

export interface WorkbookBackedProjectCommandOptions extends VbaDevCommandRuntimeOptions {
  toolCommandName: WorkbookBackedProjectToolCommand;
  title: string;
}

export interface WorkbookBackedProjectCommandResult {
  projectRoot: string;
  exitCode: number;
  cancelled: boolean;
}

export async function runWorkbookBackedProjectCommand(
  options: WorkbookBackedProjectCommandOptions
): Promise<WorkbookBackedProjectCommandResult | undefined> {
  const result = await runVbaDevProjectCommand(options, [options.toolCommandName]);
  if (!result) {
    return undefined;
  }

  if (!result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage(`${options.title.replace('VBA Tools: ', '')} failed. See the VBA Tools output for details.`);
  }

  return {
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled
  };
}
