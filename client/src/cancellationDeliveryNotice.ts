import { VbaToolsOutputChannel } from './devtoolCommand';

export const CancellationDeliveryShowOutputAction = 'Show Output';

export interface CancellationDeliveryEvidence {
  cancellationRequested: boolean;
  cancellationRequestDelivered: boolean | undefined;
}

export interface CancellationDeliveryWarningOptions {
  outputChannel: Pick<VbaToolsOutputChannel, 'show'>;
  showWarningMessage: (
    message: string,
    ...items: string[]
  ) => Thenable<string | undefined> | PromiseLike<string | undefined>;
}

export function reportCancellationDeliveryFailureAfterTrustedSuccess(
  options: CancellationDeliveryWarningOptions,
  result: CancellationDeliveryEvidence,
  successMessage: string
): boolean {
  if (
    !result.cancellationRequested ||
    result.cancellationRequestDelivered !== false
  ) {
    return false;
  }

  const selectedAction = options.showWarningMessage(
    `${successMessage} Cancellation request could not be delivered.`,
    CancellationDeliveryShowOutputAction
  );
  void Promise.resolve(selectedAction).then(
    (action) => {
      if (action === CancellationDeliveryShowOutputAction) {
        options.outputChannel.show();
      }
    },
    () => undefined
  );
  return true;
}
