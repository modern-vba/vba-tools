import * as path from 'node:path';

import { CommandCancellationToken } from './devtoolCommand';
import {
  VbaDevCommandRunResult,
  VbaDevCommandRuntimeOptions,
  VbaDevProjectCommandRunResult,
  reportCommandPaletteTargetSelection,
  runVbaDevCommandInvocation,
  runVbaDevProjectCommandInvocation
} from './devtoolRuntime';

const ExportAction = 'Export';

export interface ExportCommandOptions extends VbaDevCommandRuntimeOptions {
  readTextFile: (filePath: string) => Promise<string>;
  showWarningMessage: (
    message: string,
    options: { readonly modal: true; readonly detail: string },
    ...items: string[]
  ) => PromiseLike<string | undefined>;
  runWithProgress: <T>(
    task: (
      token: CommandCancellationToken,
      reportCancellationProgress?: (message: string) => void
    ) => Promise<T>
  ) => PromiseLike<T>;
}

export type ExportCommandRequest =
  | { readonly mode: 'manifest' }
  | {
    readonly mode: 'explicit';
    readonly workingDirectory: string;
    readonly workbookPath: string;
    readonly destinationPath?: string | undefined;
  };

export async function runExportCommand(
  options: ExportCommandOptions,
  request: ExportCommandRequest = { mode: 'manifest' }
): Promise<VbaDevProjectCommandRunResult | VbaDevCommandRunResult | undefined> {
  if (request.mode === 'explicit') {
    const workbookPath = path.resolve(request.workingDirectory, request.workbookPath);
    const args = ['export', '--from', workbookPath];
    if (request.destinationPath !== undefined) {
      const destinationPath = path.resolve(request.workingDirectory, request.destinationPath);
      if (!await obtainCleanupConsent(options, destinationPath)) {
        return undefined;
      }
      args.push('--to', destinationPath);
    }

    const result = await options.runWithProgress((
      cancellationToken,
      reportCancellationProgress
    ) =>
      runVbaDevCommandInvocation(
        { ...options, cancellationToken, reportCancellationProgress },
        args
      )
    );

    if (result && !result.cancelled && result.exitCode !== 0) {
      await options.showErrorMessage('Export failed. See the VBA Tools output for details.');
    }

    return result;
  }

  const selectedTarget = await options.resolveCommandPaletteTarget('document');
  if (selectedTarget?.document === undefined) {
    return undefined;
  }
  const project = selectedTarget.project;
  const document = selectedTarget.document;

  const destinationPath = document.sourceRoot;
  if (!await obtainCleanupConsent(options, destinationPath)) {
    return undefined;
  }

  const result = await options.runWithProgress((
    cancellationToken,
    reportCancellationProgress
  ) => {
    const invocationOptions = {
      ...options,
      cancellationToken,
      reportCancellationProgress
    };
    reportCommandPaletteTargetSelection(invocationOptions, selectedTarget);
    return runVbaDevProjectCommandInvocation(
      invocationOptions,
      {
        projectRoot: project.projectRoot,
        documentName: document.name,
        argsBeforeProject: ['export'],
        argsAfterProject: [
          '--to', destinationPath
        ],
        reportTarget: false
      }
    );
  });

  if (result && !result.cancelled && result.exitCode !== 0) {
    await options.showErrorMessage('Export failed. See the VBA Tools output for details.');
  }

  return result;
}

async function obtainCleanupConsent(
  options: ExportCommandOptions,
  destinationPath: string
): Promise<boolean> {
  const selectedAction = await options.showWarningMessage(
    'Export workbook VBA source?',
    {
      modal: true,
      detail: `${destinationPath}\n\nExisting source may be overwritten. Stale .bas, .cls, .frm, and .frx files will be deleted.`
    },
    ExportAction
  );
  return selectedAction === ExportAction;
}
