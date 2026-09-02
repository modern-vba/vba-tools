import {
  IntrinsicHostEventCatalogCancellationDisposable,
  IntrinsicHostEventCatalogRefreshHandle
} from './intrinsicHostEventCatalogLifecycle';

export const IntrinsicHostEventCatalogShowOutputAction = 'Show Output';

export interface IntrinsicHostEventCatalogProgressCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(
    listener: () => void
  ): IntrinsicHostEventCatalogCancellationDisposable;
}

export interface IntrinsicHostEventCatalogRefreshCommandOptions {
  readonly refreshCatalog: () => IntrinsicHostEventCatalogRefreshHandle;
  readonly runWithCancellableProgress: (
    title: string,
    task: (
      token: IntrinsicHostEventCatalogProgressCancellationToken
    ) => Promise<void>
  ) => Promise<void>;
  readonly showErrorMessage: (
    message: string,
    action: typeof IntrinsicHostEventCatalogShowOutputAction
  ) => Promise<string | undefined>;
  readonly showOutput: () => void;
}

export async function runIntrinsicHostEventCatalogRefreshCommand(
  options: IntrinsicHostEventCatalogRefreshCommandOptions
): Promise<void> {
  await options.runWithCancellableProgress(
    'VBA Tools: Refresh UserForm Events',
    async (token) => {
      const refresh = options.refreshCatalog();
      const cancellation = token.onCancellationRequested(() => refresh.cancel());
      if (token.isCancellationRequested) {
        refresh.cancel();
      }
      try {
        const outcome = await refresh.completion;
        if (outcome.status !== 'failed') {
          return;
        }
        const action = await options.showErrorMessage(
          'UserForm Events refresh failed. See VBA Tools Output for details.',
          IntrinsicHostEventCatalogShowOutputAction
        );
        if (action === IntrinsicHostEventCatalogShowOutputAction) {
          options.showOutput();
        }
      } finally {
        cancellation.dispose();
      }
    }
  );
}
