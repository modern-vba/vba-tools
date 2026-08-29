import * as path from 'node:path';

import type { CompanionExecutableResolution } from './devtool';
import type { VbaDevCommandRunResult } from './devtoolRuntime';
import {
  projectCreationPathValidationReasons,
  validateExcelWorkbookPath,
  validateProjectName
} from './projectCreationPathValidation';
import { parseNewExcelProjectReceipt } from './newExcelProjectReceipt';
import {
  isReusableVbaDevEnvironmentDoctorReport,
  parseVbaDevDoctorReport
} from './vbaDevDoctorOutput';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export interface NewExcelProjectResource {
  readonly scheme: string;
  readonly fsPath: string;
}

export interface NewExcelProjectNameInputOptions {
  readonly title: string;
  readonly prompt: string;
  readonly placeHolder: string;
  readonly value?: string | undefined;
  readonly valueSelection?: readonly [number, number] | undefined;
  readonly validateInput: (candidate: string) => string | undefined;
}

export interface NewExcelProjectFolderOptions {
  readonly title: string;
  readonly openLabel: string;
  readonly defaultUri?: NewExcelProjectResource | undefined;
}

export interface NewExcelProjectCommandOptions {
  readonly resolveCompanionExecutable: () => Promise<CompanionExecutableResolution>;
  readonly runCommand: (
    resolution: CompanionExecutableResolution,
    args: readonly string[]
  ) => Promise<VbaDevCommandRunResult>;
  readonly showProjectNameInput: (
    options: NewExcelProjectNameInputOptions
  ) => Promise<string | undefined>;
  readonly showParentFolder: (
    options: NewExcelProjectFolderOptions
  ) => Promise<NewExcelProjectResource | undefined>;
  readonly getWorkspaceFolders: () => readonly NewExcelProjectResource[];
  readonly getActiveResource: () => NewExcelProjectResource | undefined;
  readonly showInformationMessage: (
    message: string,
    ...actions: readonly string[]
  ) => Promise<string | undefined>;
  readonly showWarningMessage: (
    message: string,
    ...actions: readonly string[]
  ) => Promise<string | undefined>;
  readonly showErrorMessage: (
    message: string,
    options: { readonly modal: boolean } | undefined,
    ...actions: readonly string[]
  ) => Promise<string | undefined>;
  readonly showOutput: () => void;
  readonly appendOutput: (text: string) => void;
  readonly openSetupInstructions: () => Promise<void>;
  readonly openSettings: () => Promise<void>;
  readonly openManifest: (manifestPath: string) => Promise<void>;
  readonly openFolderInNewWindow: (projectRoot: string) => Promise<void>;
}

export class NewExcelProjectCommand {
  private activeFlow: symbol | undefined;
  private passingPreflightResolution: CompanionExecutableResolution | undefined;

  public constructor(private readonly options: NewExcelProjectCommandOptions) {}

  public invalidatePreflight(): void {
    this.passingPreflightResolution = undefined;
  }

  public async run(): Promise<void> {
    if (this.activeFlow !== undefined) {
      await this.options.showInformationMessage(
        'Excel VBA project creation is already in progress in this window.'
      );
      return;
    }

    const flow = Symbol('guided Excel project creation');
    this.activeFlow = flow;
    try {
      await this.runExclusive(flow);
    } finally {
      this.releaseSingleFlight(flow);
    }
  }

  private async runExclusive(flow: symbol): Promise<void> {
    const resolution = await this.options.resolveCompanionExecutable();
    if (!await this.ensurePassingPreflight(resolution, flow)) {
      return;
    }

    let projectName: string | undefined;
    let nameInputValue: string | undefined;
    let nameInputSelection: readonly [number, number] | undefined;
    let parent: NewExcelProjectResource | undefined;
    let parentDefault = selectInitialParent(
      this.options.getActiveResource(),
      this.options.getWorkspaceFolders()
    );
    for (;;) {
      if (projectName === undefined) {
        projectName = await this.options.showProjectNameInput({
          title: 'Create Excel VBA Project',
          prompt: 'Enter a project name. The project folder, document, and workbook will use this name.',
          placeHolder: 'MyProject',
          value: nameInputValue,
          valueSelection: nameInputSelection,
          validateInput: validateProjectNameInput
        });
        if (projectName === undefined) {
          return;
        }
      }

      if (parent === undefined) {
        parent = await this.options.showParentFolder({
          title: `Select Parent Folder for "${projectName}"`,
          openLabel: 'Create Here',
          defaultUri: parentDefault
        });
        if (parent === undefined) {
          return;
        }
        if (!isEligibleWindowsFileParent(parent)) {
          const action = await this.options.showErrorMessage(
            'Select a folder on the Windows file system. Remote and virtual workspace folders cannot be used to create an Excel VBA project.',
            { modal: true },
            'Choose Another Parent',
            'Cancel'
          );
          parent = undefined;
          if (action !== 'Choose Another Parent') {
            return;
          }
          continue;
        }
      }

      const candidateProjectRoot = path.win32.join(parent.fsPath, projectName);
      const firstPathFailure = generatedWorkbookPaths(
        candidateProjectRoot,
        projectName
      ).map((candidate) => ({
        candidate,
        result: validateExcelWorkbookPath(candidate)
      })).find(({ result }) => !result.isValid);
      if (
        firstPathFailure?.result.reason ===
        projectCreationPathValidationReasons.excelPathContainsUnsupportedCharacter
      ) {
        this.options.appendOutput(
          'excelPathContainsUnsupportedCharacter: Excel workbook path contains "[" or "]", ' +
          `which Excel does not reliably support: "${firstPathFailure.candidate}".`
        );
        const action = await this.options.showErrorMessage(
          'The selected parent folder cannot be used because its path contains "[" or "]", which Excel does not reliably support.',
          { modal: true },
          'Choose Another Parent',
          'Cancel'
        );
        if (action !== 'Choose Another Parent') {
          return;
        }
        parentDefault = parent;
        parent = undefined;
        continue;
      }
      if (
        firstPathFailure?.result.reason ===
        projectCreationPathValidationReasons.excelPathTooLong
      ) {
        this.options.appendOutput(
          'excelPathTooLong: Excel workbook path exceeds the 218-character limit ' +
          `(${firstPathFailure.candidate.length} UTF-16 code units): ` +
          `"${firstPathFailure.candidate}".`
        );
        const action = await this.options.showErrorMessage(
          "One or more generated workbook paths exceed Excel's 218-character limit.",
          { modal: true },
          'Choose Another Parent',
          'Change Name',
          'Cancel'
        );
        if (action === 'Choose Another Parent') {
          parentDefault = parent;
          parent = undefined;
          continue;
        }
        if (action === 'Change Name') {
          nameInputValue = projectName;
          nameInputSelection = [0, projectName.length];
          projectName = undefined;
          continue;
        }
        return;
      }
      break;
    }

    const projectRoot = path.win32.join(parent.fsPath, projectName);
    const creationResult = await this.options.runCommand(
      resolution,
      ['new', 'excel', '--name', projectName, '--output', projectRoot, '--format', 'json']
    );
    if (creationResult.exitCode === 130) {
      return;
    }
    if (creationResult.exitCode !== 0) {
      this.invalidatePreflight();
      this.releaseSingleFlight(flow);
      const action = await this.options.showErrorMessage(
        `Excel VBA project creation failed for "${projectName}".`,
        undefined,
        'Show Output'
      );
      if (action === 'Show Output') {
        this.options.showOutput();
      }
      return;
    }

    let receipt;
    try {
      receipt = parseNewExcelProjectReceipt(creationResult.stdout, {
        projectName,
        projectRoot
      });
    } catch {
      this.invalidatePreflight();
      this.releaseSingleFlight(flow);
      const action = await this.options.showErrorMessage(
        'Excel VBA project creation may have completed, but its result could not be verified. Inspect the target and VBA Tools Output.',
        undefined,
        'Show Output'
      );
      if (action === 'Show Output') {
        this.options.showOutput();
      }
      return;
    }

    this.releaseSingleFlight(flow);
    const action = isInsideFileWorkspace(
      receipt.manifestPath,
      this.options.getWorkspaceFolders()
    )
      ? 'Open Manifest'
      : 'Open Folder in New Window';
    const cancellationDeliveryFailed =
      creationResult.cancellationRequested &&
      creationResult.cancellationRequestDelivered === false;
    const selection = receipt.warnings.length === 0 && !cancellationDeliveryFailed
      ? await this.options.showInformationMessage(
        `Created Excel VBA project "${projectName}".`,
        action
      )
      : await this.options.showWarningMessage(
        formatCreationWarningMessage(
          projectName,
          receipt.warnings.length,
          cancellationDeliveryFailed
        ),
        action,
        'Show Output'
      );
    if (selection === 'Open Manifest') {
      await this.navigateAfterSuccess(
        'manifest',
        receipt.project,
        receipt.manifestPath
      );
    } else if (selection === 'Open Folder in New Window') {
      await this.navigateAfterSuccess('folder', receipt.project, receipt.project);
    } else if (selection === 'Show Output') {
      this.options.showOutput();
    }
  }

  private releaseSingleFlight(flow: symbol): void {
    if (this.activeFlow === flow) {
      this.activeFlow = undefined;
    }
  }

  private async navigateAfterSuccess(
    kind: 'manifest' | 'folder',
    projectRoot: string,
    target: string
  ): Promise<void> {
    for (;;) {
      try {
        if (kind === 'manifest') {
          await this.options.openManifest(target);
        } else {
          await this.options.openFolderInNewWindow(target);
        }
        return;
      } catch (error) {
        this.options.appendOutput(
          `Post-creation navigation failed. Project: "${projectRoot}". ` +
          `Target: "${target}". Error: ${String(error)}`
        );
        const action = await this.options.showErrorMessage(
          kind === 'manifest'
            ? 'Excel VBA project was created, but its manifest could not be opened.'
            : 'Excel VBA project was created, but its folder could not be opened in a new window.',
          undefined,
          'Retry',
          'Show Output'
        );
        if (action === 'Retry') {
          continue;
        }
        if (action === 'Show Output') {
          this.options.showOutput();
        }
        return;
      }
    }
  }

  private async ensurePassingPreflight(
    resolution: CompanionExecutableResolution,
    flow: symbol
  ): Promise<boolean> {
    if (this.passingPreflightResolution === resolution) {
      return true;
    }
    this.invalidatePreflight();

    for (;;) {
      const doctorResult = await this.options.runCommand(
        resolution,
        ['doctor', '--scope', 'environment', '--format', 'json']
      );
      if (doctorResult.exitCode === 130) {
        return false;
      }
      const doctorCapability = resolution.capabilities.commands.doctor;
      if (doctorCapability === undefined) {
        return false;
      }

      let doctorReport;
      try {
        doctorReport = parseVbaDevDoctorReport(
          doctorResult.stdout,
          doctorCapability.outputSchemaVersion,
          resolution.capabilities.toolVersion,
          doctorResult.exitCode,
          { scope: 'environment', project: null }
        );
      } catch {
        const action = await this.options.showErrorMessage(
          'VBA Tools could not verify Excel VBA project prerequisites.',
          undefined,
          'Open Setup Instructions',
          'Retry',
          'Show Output'
        );
        if (await this.handleBlockingPreflightAction(action, flow) === 'retry') {
          this.invalidatePreflight();
          continue;
        }
        return false;
      }
      if (
        doctorResult.cancellationRequested &&
        doctorResult.exitCode === 0 &&
        doctorReport.complete &&
        (doctorReport.status === 'pass' || doctorReport.status === 'warning')
      ) {
        return false;
      }
      if (!isReusableVbaDevEnvironmentDoctorReport(doctorReport)) {
        let action: string | undefined;
        if (doctorReport.complete && doctorReport.status === 'warning') {
          action = await this.options.showWarningMessage(
            'Excel VBA project prerequisites need attention.',
            'Open Setup Instructions',
            'Retry',
            'Show Output'
          );
        } else {
          action = await this.options.showErrorMessage(
            'Excel VBA project prerequisites are not ready.',
            undefined,
            'Open Setup Instructions',
            'Retry',
            'Show Output'
          );
        }
        if (await this.handleBlockingPreflightAction(action, flow) === 'retry') {
          this.invalidatePreflight();
          continue;
        }
        return false;
      }
      this.passingPreflightResolution = resolution;
      return true;
    }
  }

  private async handleBlockingPreflightAction(
    action: string | undefined,
    flow: symbol
  ): Promise<'retry' | 'stop'> {
    if (action === 'Retry') {
      return 'retry';
    }
    this.releaseSingleFlight(flow);
    if (action === 'Show Output') {
      this.options.showOutput();
    } else if (action === 'Open Setup Instructions') {
      await this.openSetupInstructions();
    }
    return 'stop';
  }

  private async openSetupInstructions(): Promise<void> {
    try {
      await this.options.openSetupInstructions();
    } catch {
      let action = await this.options.showErrorMessage(
        'VBA Tools could not open the Excel VBA setup instructions.',
        undefined,
        'Retry',
        'Show Output'
      );
      while (action === 'Retry') {
        try {
          await this.options.openSetupInstructions();
          return;
        } catch {
          action = await this.options.showErrorMessage(
            'VBA Tools could not open the Excel VBA setup instructions.',
            undefined,
            'Retry',
            'Show Output'
          );
        }
      }
      if (action === 'Show Output') {
        this.options.showOutput();
      }
    }
  }

}

function formatCreationWarningMessage(
  projectName: string,
  cliWarningCount: number,
  cancellationDeliveryFailed: boolean
): string {
  let message = `Created Excel VBA project "${projectName}".`;
  if (cliWarningCount > 0) {
    message += ` ${cliWarningCount} ${cliWarningCount === 1 ? 'warning' : 'warnings'}.`;
  }
  if (cancellationDeliveryFailed) {
    message += ' Cancellation request could not be delivered.';
  }
  return message;
}

function validateProjectNameInput(candidate: string): string | undefined {
  const lexicalResult = validateProjectName(candidate);
  switch (lexicalResult.reason) {
    case projectCreationPathValidationReasons.projectNameEmpty:
      return 'Enter a project name.';
    case projectCreationPathValidationReasons.projectNameIllFormedUnicode:
      return 'Project name contains an invalid Unicode sequence.';
    case projectCreationPathValidationReasons.projectNameDotSegment:
      return 'Project name cannot be "." or "..".';
    case projectCreationPathValidationReasons.projectNameContainsPathSeparator:
      return 'Project name cannot contain "/" or "\\".';
    case projectCreationPathValidationReasons.projectNameContainsWindowsInvalidCharacter:
      return 'Project name contains a character that Windows does not allow in a file or folder name.';
    case projectCreationPathValidationReasons.projectNameContainsUnicodeControlCharacter:
      return 'Project name cannot contain control characters.';
    case projectCreationPathValidationReasons.projectNameHasLeadingOrTrailingWhitespace:
      return 'Project name cannot start or end with whitespace.';
    case projectCreationPathValidationReasons.projectNameEndsWithDot:
      return 'Project name cannot end with a dot.';
    case projectCreationPathValidationReasons.projectNameUsesReservedDeviceName:
      return 'Project name cannot use a reserved Windows device name, even with an extension.';
    default:
      break;
  }

  const excelResult = validateExcelWorkbookPath(candidate);
  return excelResult.reason ===
    projectCreationPathValidationReasons.excelPathContainsUnsupportedCharacter
    ? 'Project name cannot contain "[" or "]" because Excel does not reliably support them in workbook paths.'
    : undefined;
}

function generatedWorkbookPaths(
  projectRoot: string,
  projectName: string
): readonly string[] {
  return [
    path.win32.join(projectRoot, 'src', projectName, `${projectName}.xlsm`),
    path.win32.join(projectRoot, 'bin', `${projectName}.xlsm`),
    path.win32.join(projectRoot, 'publish', `${projectName}.xlsm`)
  ];
}

function isEligibleWindowsFileParent(resource: NewExcelProjectResource): boolean {
  if (resource.scheme !== 'file') {
    return false;
  }
  if (/^\\\\[?.][\\/]/u.test(resource.fsPath)) {
    return false;
  }
  return /^[A-Za-z]:[\\/]/u.test(resource.fsPath) ||
    /^\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$)/u.test(resource.fsPath);
}

function selectInitialParent(
  activeResource: NewExcelProjectResource | undefined,
  workspaceFolders: readonly NewExcelProjectResource[]
): NewExcelProjectResource | undefined {
  if (activeResource?.scheme === 'file') {
    const activePath = path.win32.normalize(activeResource.fsPath);
    const containingWorkspace = workspaceFolders.find((folder) =>
      folder.scheme === 'file' && isSameOrChildWindowsPath(activePath, folder.fsPath));
    if (containingWorkspace !== undefined) {
      return containingWorkspace;
    }
  }
  const localWorkspaces = workspaceFolders.filter((folder) => folder.scheme === 'file');
  return localWorkspaces.length === 1 ? localWorkspaces[0] : undefined;
}

function isInsideFileWorkspace(
  manifestPath: string,
  workspaceFolders: readonly NewExcelProjectResource[]
): boolean {
  return workspaceFolders.some((folder) =>
    folder.scheme === 'file' && isSameOrChildWindowsPath(manifestPath, folder.fsPath));
}

function isSameOrChildWindowsPath(candidate: string, parent: string): boolean {
  const candidateKey = ordinalIgnoreCaseKey(path.win32.resolve(candidate));
  const parentKey = ordinalIgnoreCaseKey(path.win32.resolve(parent));
  if (candidateKey === parentKey) {
    return true;
  }
  const parentPrefix = parentKey.endsWith(path.win32.sep)
    ? parentKey
    : `${parentKey}${path.win32.sep}`;
  return candidateKey.startsWith(parentPrefix);
}
