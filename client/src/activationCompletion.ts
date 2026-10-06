export interface ExtensionActivationCompletionOptions<Result> {
  promptForFirstRunDoctor: () => Promise<void>;
  reportFirstRunDoctorPromptError: (error: unknown) => void;
  createResult: () => Result;
}

export function completeExtensionActivation<Result>(
  options: ExtensionActivationCompletionOptions<Result>
): Result {
  void options.promptForFirstRunDoctor().catch((error: unknown) => {
    options.reportFirstRunDoctorPromptError(error);
  });
  return options.createResult();
}
