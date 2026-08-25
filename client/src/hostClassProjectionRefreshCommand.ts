import {
  HostClassCancellationDisposable,
  HostClassExplicitRefreshHandle,
  HostClassProjectionContext
} from './hostClassProjectionLifecycle';

export const HostClassShowOutputAction = 'Show Output';

export interface HostClassRefreshDocument {
  readonly context: HostClassProjectionContext;
}

export interface HostClassRefreshProgressCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(
    listener: () => void
  ): HostClassCancellationDisposable;
}

export interface HostClassProjectionRefreshCommandOptions {
  readonly getActiveDocuments: () => readonly HostClassRefreshDocument[];
  readonly chooseDocument: (
    documents: readonly HostClassRefreshDocument[]
  ) => Promise<HostClassRefreshDocument | undefined>;
  readonly refreshDocument: (
    context: HostClassProjectionContext
  ) => HostClassExplicitRefreshHandle;
  readonly runWithCancellableProgress: (
    title: string,
    task: (token: HostClassRefreshProgressCancellationToken) => Promise<void>
  ) => Promise<void>;
  readonly showWarningMessage: (
    message: string,
    action: typeof HostClassShowOutputAction
  ) => Promise<string | undefined>;
  readonly showErrorMessage: (
    message: string,
    action: typeof HostClassShowOutputAction
  ) => Promise<string | undefined>;
  readonly showOutput: () => void;
}

export async function runHostClassProjectionRefreshCommand(
  options: HostClassProjectionRefreshCommandOptions
): Promise<void> {
  const documents = options.getActiveDocuments();
  const selected = await options.chooseDocument(documents);
  if (selected === undefined) {
    return;
  }

  await options.runWithCancellableProgress(
    `VBA Tools: Refresh Host Events — ${selected.context.document}`,
    async (token) => {
      const current = options.getActiveDocuments().find((document) =>
        contextsEqual(document.context, selected.context)
      );
      if (current === undefined) {
        return;
      }

      const refresh = options.refreshDocument(current.context);
      const cancellation = token.onCancellationRequested(() => refresh.cancel());
      if (token.isCancellationRequested) {
        refresh.cancel();
      }

      try {
        const outcome = await refresh.completion;
        if (outcome.status !== 'succeeded') {
          if (outcome.status === 'failed') {
            const action = await options.showErrorMessage(
              'Host Events refresh failed. See VBA Tools Output for details.',
              HostClassShowOutputAction
            );
            if (action === HostClassShowOutputAction) {
              options.showOutput();
            }
          }
          return;
        }

        if (outcome.associationFailureCount > 0) {
          const action = await options.showWarningMessage(
            `Host Events refreshed, but ${outcome.associationFailureCount} source module(s) could not be associated.`,
            HostClassShowOutputAction
          );
          if (action === HostClassShowOutputAction) {
            options.showOutput();
          }
        }
      } finally {
        cancellation.dispose();
      }
    }
  );
}

function contextsEqual(
  left: HostClassProjectionContext,
  right: HostClassProjectionContext
): boolean {
  return left.project === right.project &&
    left.document === right.document &&
    left.sourceTemplate === right.sourceTemplate;
}
