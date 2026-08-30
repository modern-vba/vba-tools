import type { CommandCancellationToken } from './devtoolCommand';

export interface ReferenceQuickPickDisposable {
  dispose(): void;
}

export interface ReferenceQuickPickItem {
  label: string;
  description?: string | undefined;
  canonicalName: string;
}

export interface ReferenceQuickPick {
  title: string | undefined;
  busy: boolean;
  enabled: boolean;
  canSelectMany: boolean;
  matchOnDescription: boolean;
  items: readonly ReferenceQuickPickItem[];
  selectedItems: readonly ReferenceQuickPickItem[];
  onDidAccept(listener: () => void): ReferenceQuickPickDisposable;
  onDidHide(listener: () => void): ReferenceQuickPickDisposable;
  show(): void;
  hide(): void;
  dispose(): void;
}

export interface ReferenceQuickPickCancellationSource {
  readonly token: CommandCancellationToken;
  cancel(): void;
  dispose(): void;
}

export type ReferenceQuickPickResult =
  | { kind: 'accepted'; names: readonly string[] }
  | { kind: 'cancelled' }
  | { kind: 'empty' }
  | { kind: 'failed'; error: unknown };

export interface ShowReferenceQuickPickOptions {
  title: string;
  createQuickPick(): ReferenceQuickPick;
  createCancellationSource(): ReferenceQuickPickCancellationSource;
  discover(token: CommandCancellationToken): Promise<readonly ReferenceQuickPickItem[]>;
}

export function showReferenceQuickPick(
  options: ShowReferenceQuickPickOptions
): Promise<ReferenceQuickPickResult> {
  const quickPick = options.createQuickPick();
  const cancellation = options.createCancellationSource();
  quickPick.title = options.title;
  quickPick.busy = true;
  quickPick.enabled = false;
  quickPick.canSelectMany = true;
  quickPick.matchOnDescription = true;

  return new Promise((resolve) => {
    let settled = false;
    let closing = false;
    let hidden = false;
    let discoverySettled = false;
    let inventory: readonly ReferenceQuickPickItem[] = [];
    const subscriptions: ReferenceQuickPickDisposable[] = [];
    const settle = (result: ReferenceQuickPickResult): void => {
      if (settled) {
        return;
      }
      settled = true;
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      quickPick.dispose();
      resolve(result);
    };
    const closeWith = (result: ReferenceQuickPickResult): void => {
      closing = true;
      quickPick.hide();
      settle(result);
    };

    subscriptions.push(
      quickPick.onDidAccept(() => {
        if (!discoverySettled || hidden || settled || quickPick.selectedItems.length === 0) {
          return;
        }

        const selected = new Set(quickPick.selectedItems);
        closeWith({
          kind: 'accepted',
          names: inventory
            .filter((item) => selected.has(item))
            .map((item) => item.canonicalName)
        });
      }),
      quickPick.onDidHide(() => {
        if (closing || settled || hidden) {
          return;
        }

        hidden = true;
        cancellation.cancel();
        if (discoverySettled) {
          settle({ kind: 'cancelled' });
        }
      })
    );
    quickPick.show();

    void Promise.resolve()
      .then(() => options.discover(cancellation.token))
      .then((items) => {
        discoverySettled = true;
        if (hidden || settled) {
          return;
        }

        inventory = [...items];
        if (inventory.length === 0) {
          closeWith({ kind: 'empty' });
          return;
        }

        quickPick.items = inventory;
        quickPick.busy = false;
        quickPick.enabled = true;
      }, (error: unknown) => {
        discoverySettled = true;
        if (!hidden && !settled) {
          closeWith({ kind: 'failed', error });
        }
      })
      .finally(() => {
        cancellation.dispose();
        if (hidden && !settled) {
          settle({ kind: 'cancelled' });
        }
      });
  });
}
