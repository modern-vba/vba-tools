import {
  VbaDevCommandRuntimeOptions,
  runVbaDevProjectCommand
} from './devtoolRuntime';

export const FirstRunDoctorPromptState = {
  Prompted: 'vbaTools.doctor.firstRunPrompted',
  Suppress: 'vbaTools.doctor.suppressFirstRunPrompt'
} as const;

export interface WorkspaceState {
  get<T>(key: string): T | undefined;
  update(key: string, value: unknown): Thenable<void> | Promise<void>;
}

export interface DoctorCommandOptions extends VbaDevCommandRuntimeOptions {}

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
  const result = await runVbaDevProjectCommand(options, ['doctor']);
  if (!result) {
    return undefined;
  }

  if (!result.cancelled && hasBlockingDoctorFinding(result.exitCode, result.stdout, result.stderr)) {
    await options.showErrorMessage('VBA Tools: Doctor found blocking issues. See the VBA Tools output for details.');
  }

  return {
    projectRoot: result.projectRoot,
    exitCode: result.exitCode,
    cancelled: result.cancelled
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

function hasBlockingDoctorFinding(exitCode: number, stdout: string, stderr: string): boolean {
  return exitCode !== 0 || stdout.includes('[FAIL]') || stderr.trim().length > 0;
}
