import {
  IntrinsicHostEventCatalogCancellationDisposable,
  IntrinsicHostEventCatalogInvocation,
  IntrinsicHostEventCatalogLifecycleTransition,
  IntrinsicHostEventCatalogRunResult
} from './intrinsicHostEventCatalogLifecycle';

const ExtensionHostTestEnvironment = 'VBA_TOOLS_EXTENSION_HOST_TEST';
const CatalogTestModeEnvironment =
  'VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE';

export interface IntrinsicHostEventCatalogExtensionHostTestSnapshot {
  readonly invocations: readonly {
    readonly trigger: IntrinsicHostEventCatalogInvocation['trigger'];
    readonly args: readonly string[];
  }[];
  readonly transitions: readonly IntrinsicHostEventCatalogLifecycleTransition[];
  readonly notifications: readonly {
    readonly method: string;
    readonly parameters: unknown;
  }[];
}

export interface IntrinsicHostEventCatalogExtensionHostTestApi {
  readonly actualWorkspaceTrusted: boolean;
  readonly effectiveWorkspaceTrusted: boolean;
  snapshot(): IntrinsicHostEventCatalogExtensionHostTestSnapshot;
  completeInvocation(
    index: number,
    result: IntrinsicHostEventCatalogRunResult
  ): void;
  restartLanguageClient(): Promise<void>;
}

export interface VbaToolsExtensionHostTestApi {
  readonly intrinsicHostEventCatalog: IntrinsicHostEventCatalogExtensionHostTestApi;
}

interface PendingInvocation {
  readonly settle: (result: IntrinsicHostEventCatalogRunResult) => void;
  settled: boolean;
}

export class IntrinsicHostEventCatalogExtensionHostProbe {
  private readonly invocations: Array<{
    readonly trigger: IntrinsicHostEventCatalogInvocation['trigger'];
    readonly args: readonly string[];
  }> = [];
  private readonly pendingInvocations: PendingInvocation[] = [];
  private readonly transitions: IntrinsicHostEventCatalogLifecycleTransition[] = [];
  private readonly notifications: Array<{
    readonly method: string;
    readonly parameters: unknown;
  }> = [];

  private constructor(
    public readonly actualWorkspaceTrusted: boolean,
    public readonly effectiveWorkspaceTrusted: boolean
  ) {
  }

  public static fromEnvironment(
    environment: Readonly<Record<string, string | undefined>>,
    actualWorkspaceTrusted: boolean,
    extensionHostTestMode: boolean
  ): IntrinsicHostEventCatalogExtensionHostProbe | undefined {
    if (!extensionHostTestMode || environment[ExtensionHostTestEnvironment] !== '1') {
      return undefined;
    }
    const mode = environment[CatalogTestModeEnvironment];
    if (mode !== 'controlled-trusted' && mode !== 'actual-untrusted') {
      return undefined;
    }
    return new IntrinsicHostEventCatalogExtensionHostProbe(
      actualWorkspaceTrusted,
      mode === 'controlled-trusted' ? true : actualWorkspaceTrusted
    );
  }

  public runHostEventList(
    invocation: IntrinsicHostEventCatalogInvocation
  ): Promise<IntrinsicHostEventCatalogRunResult> {
    this.invocations.push({
      trigger: invocation.trigger,
      args: [...invocation.args]
    });
    return new Promise((resolve) => {
      let cancellation: IntrinsicHostEventCatalogCancellationDisposable = {
        dispose: () => undefined
      };
      const pending: PendingInvocation = {
        settled: false,
        settle: (result) => {
          if (pending.settled) {
            return;
          }
          pending.settled = true;
          cancellation.dispose();
          resolve(result);
        }
      };
      cancellation = invocation.cancellationToken.onCancellationRequested(
        () => pending.settle({
          exitCode: 1,
          stdout: '',
          stderr: '',
          cancelled: true
        })
      );
      if (pending.settled) {
        cancellation.dispose();
      }
      this.pendingInvocations.push(pending);
    });
  }

  public observeTransition(
    transition: IntrinsicHostEventCatalogLifecycleTransition
  ): void {
    this.transitions.push({ ...transition });
  }

  public observeNotification(method: string, parameters: unknown): void {
    this.notifications.push({
      method,
      parameters: cloneJsonValue(parameters)
    });
  }

  public createApi(
    restartLanguageClient: () => Promise<void>
  ): IntrinsicHostEventCatalogExtensionHostTestApi {
    return {
      actualWorkspaceTrusted: this.actualWorkspaceTrusted,
      effectiveWorkspaceTrusted: this.effectiveWorkspaceTrusted,
      snapshot: () => ({
        invocations: this.invocations.map((invocation) => ({
          trigger: invocation.trigger,
          args: [...invocation.args]
        })),
        transitions: this.transitions.map((transition) => ({ ...transition })),
        notifications: this.notifications.map((notification) => ({
          method: notification.method,
          parameters: cloneJsonValue(notification.parameters)
        }))
      }),
      completeInvocation: (index, result) => {
        const pending = this.pendingInvocations[index];
        if (pending === undefined) {
          throw new Error(`Host Event catalog invocation ${index} has not started.`);
        }
        if (pending.settled) {
          throw new Error(`Host Event catalog invocation ${index} is already complete.`);
        }
        pending.settle(result);
      },
      restartLanguageClient
    };
  }
}

function cloneJsonValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
