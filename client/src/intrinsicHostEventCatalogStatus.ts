import {
  IntrinsicHostEventCatalogLifecycleTransition
} from './intrinsicHostEventCatalogLifecycle';

export interface IntrinsicHostEventCatalogStatusView {
  readonly visible: boolean;
  readonly text: string;
  readonly tooltip: string;
  readonly command: 'vbaTools.userFormEvents.showOutput';
}

export interface IntrinsicHostEventCatalogStatusObserverOptions {
  readonly updateStatus: (view: IntrinsicHostEventCatalogStatusView) => void;
  readonly appendOutput: (line: string) => void;
}

const hiddenView: IntrinsicHostEventCatalogStatusView = {
  visible: false,
  text: '',
  tooltip: '',
  command: 'vbaTools.userFormEvents.showOutput'
};

export class IntrinsicHostEventCatalogStatusObserver {
  private settledView: IntrinsicHostEventCatalogStatusView = hiddenView;

  public constructor(
    private readonly options: IntrinsicHostEventCatalogStatusObserverOptions
  ) {
  }

  public observe(transition: IntrinsicHostEventCatalogLifecycleTransition): void {
    this.options.appendOutput(
      `[user-form-events] ${JSON.stringify(transition)}`
    );
    switch (transition.kind) {
      case 'started':
        this.options.updateStatus({
          visible: true,
          text: '$(sync~spin) VBA UserForm Events',
          tooltip: 'Acquiring the environment UserForm Event catalog...',
          command: 'vbaTools.userFormEvents.showOutput'
        });
        return;
      case 'pendingReplay':
        this.settledView = {
          visible: true,
          text: '$(sync~spin) VBA UserForm Events',
          tooltip: 'Waiting to synchronize the UserForm Event catalog with the language server...',
          command: 'vbaTools.userFormEvents.showOutput'
        };
        this.options.updateStatus(this.settledView);
        return;
      case 'committed':
        this.settledView = hiddenView;
        this.options.updateStatus(this.settledView);
        return;
      case 'replayed':
        this.settledView = transition.catalogAvailable === false
          ? {
              visible: true,
              text: '$(warning) VBA UserForm Events',
              tooltip: 'UserForm Event catalog unavailable.',
              command: 'vbaTools.userFormEvents.showOutput'
            }
          : hiddenView;
        this.options.updateStatus(this.settledView);
        return;
      case 'cancelled':
        this.options.updateStatus(this.settledView);
        return;
      case 'unavailable':
        this.settledView = {
          visible: true,
          text: '$(warning) VBA UserForm Events',
          tooltip: transition.catalogRetained
            ? `UserForm Event catalog refresh failed; the current catalog was retained: ${transition.message ?? 'unknown failure'}`
            : `UserForm Event catalog unavailable: ${transition.message ?? 'unknown failure'}`,
          command: 'vbaTools.userFormEvents.showOutput'
        };
        this.options.updateStatus(this.settledView);
        return;
      case 'notificationFailed':
        this.settledView = {
          visible: true,
          text: '$(warning) VBA UserForm Events',
          tooltip: `UserForm Event catalog could not be synchronized: ${transition.message ?? 'unknown failure'}`,
          command: 'vbaTools.userFormEvents.showOutput'
        };
        this.options.updateStatus(this.settledView);
        return;
    }
  }
}
