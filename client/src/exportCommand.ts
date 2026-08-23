import * as path from 'node:path';

import { CommandCancellationToken } from './devtoolCommand';
import {
  VbaDevCommandRunResult,
  VbaDevCommandRuntimeOptions,
  VbaDevProjectCommandRunResult,
  runVbaDevCommandInvocation,
  runVbaDevProjectCommandInvocation
} from './devtoolRuntime';
import { discoverWorkbookBackedProject } from './projectDiscovery';
import { parseProjectManifest } from './projectManifest';

const ExportAction = 'Export';

export interface ExportCommandOptions extends VbaDevCommandRuntimeOptions {
  readTextFile: (filePath: string) => Promise<string>;
  showWarningMessage: (
    message: string,
    options: { readonly modal: true; readonly detail: string },
    ...items: string[]
  ) => PromiseLike<string | undefined>;
  runWithProgress: <T>(
    task: (token: CommandCancellationToken) => Promise<T>
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

    const result = await options.runWithProgress((cancellationToken) =>
      runVbaDevCommandInvocation(
        { ...options, cancellationToken },
        args
      )
    );

    if (result && !result.cancelled && result.exitCode !== 0) {
      await options.showErrorMessage('Export failed. See the VBA Tools output for details.');
    }

    return result;
  }

  const project = await discoverWorkbookBackedProject(options);
  if (!project) {
    await options.showErrorMessage('VBA Tools could not find a workbook-backed vba-project.json.');
    return undefined;
  }

  let manifestText: string;
  try {
    manifestText = await options.readTextFile(project.manifestPath);
  } catch (error) {
    await options.showErrorMessage(
      `VBA Tools could not read ${project.manifestPath}: ${toErrorMessage(error)}`
    );
    return undefined;
  }

  const manifest = parseProjectManifest(manifestText);
  if (!manifest) {
    await options.showErrorMessage(
      `VBA Tools could not resolve the primary document from ${project.manifestPath}.`
    );
    return undefined;
  }

  const document = manifest.documents.find(
    (candidate) => candidate.name.toLowerCase() === manifest.primaryDocument.toLowerCase()
  );
  if (!document) {
    await options.showErrorMessage(
      `VBA Tools could not resolve the primary document from ${project.manifestPath}.`
    );
    return undefined;
  }

  const destinationPath = path.resolve(project.projectRoot, document.sourcePath);
  if (!await obtainCleanupConsent(options, destinationPath)) {
    return undefined;
  }

  const result = await options.runWithProgress((cancellationToken) =>
    runVbaDevProjectCommandInvocation(
      { ...options, cancellationToken },
      {
        projectRoot: project.projectRoot,
        argsBeforeProject: ['export'],
        argsAfterProject: [
          '--document', document.name,
          '--to', destinationPath
        ]
      }
    )
  );

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

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
