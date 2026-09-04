export interface CompanionExecutableLanguageServerResolution {
  readonly executablePath: string;
  readonly capabilities: {
    readonly commands: Readonly<Record<
      string,
      { readonly outputSchemaVersion: string }
    >>;
  };
}

export interface CompanionExecutableLanguageServerLifecycleOptions {
  readonly isTrusted: () => boolean;
  readonly resolveCompanion: () => Promise<
    CompanionExecutableLanguageServerResolution
  >;
  readonly observeCompanionResolution?: (
    listener: (resolution: CompanionExecutableLanguageServerResolution) => void
  ) => { dispose(): void };
  readonly sendNotification: (
    method: string,
    parameters: unknown
  ) => Promise<void>;
  readonly startUserFormEventCatalog: () => void;
  readonly reportResolutionError?: (error: unknown) => void | PromiseLike<void>;
  readonly reportPublicationError?: (error: unknown) => void | PromiseLike<void>;
}

export const CompanionExecutableSnapshotMethod = 'vba/companionExecutable';

/**
 * Starts managed companion work only after source-language assistance is operational.
 */
export class CompanionExecutableLanguageServerLifecycle {
  private languageClientRunning = false;
  private connectionGeneration = 0;
  private trustedServicesStarted = false;
  private resolution: CompanionExecutableLanguageServerResolution | undefined;
  private publishedGeneration = -1;
  private disposed = false;
  private pending = Promise.resolve();
  private readonly companionResolutionSubscription: { dispose(): void } | undefined;

  public constructor(
    private readonly options: CompanionExecutableLanguageServerLifecycleOptions
  ) {
    this.companionResolutionSubscription = options.observeCompanionResolution?.(
      (resolution) => this.observeCompanionResolution(resolution)
    );
  }

  public observeLanguageClientRunning(isRunning: boolean): void {
    if (this.disposed) {
      return;
    }

    if (this.languageClientRunning === isRunning) {
      return;
    }

    this.languageClientRunning = isRunning;
    this.connectionGeneration += 1;
    if (isRunning) {
      this.schedulePublication();
    }
  }

  public activateTrustedServices(): void {
    if (this.disposed
        || !this.languageClientRunning
        || !this.options.isTrusted()
        || this.trustedServicesStarted) {
      return;
    }

    this.trustedServicesStarted = true;
    this.options.startUserFormEventCatalog();
    this.pending = this.options.resolveCompanion()
      .then((resolution) => {
        this.acceptCompanionResolution(resolution);
      })
      .catch(error => (
        this.disposed || !this.options.isTrusted()
          ? undefined
          : this.observeError(this.options.reportResolutionError, error)
      ));
  }

  public async flush(): Promise<void> {
    let pending = this.pending;
    await pending;
    while (pending !== this.pending) {
      pending = this.pending;
      await pending;
    }
  }

  public dispose(): void {
    this.disposed = true;
    this.languageClientRunning = false;
    this.connectionGeneration += 1;
    this.companionResolutionSubscription?.dispose();
  }

  private observeCompanionResolution(
    resolution: CompanionExecutableLanguageServerResolution
  ): void {
    try {
      this.acceptCompanionResolution(resolution);
    } catch (error) {
      if (!this.disposed && this.options.isTrusted()) {
        this.pending = this.pending.then(() => (
          this.observeError(this.options.reportResolutionError, error)
        ));
      }
    }
  }

  private acceptCompanionResolution(
    resolution: CompanionExecutableLanguageServerResolution
  ): void {
    if (this.disposed || !this.options.isTrusted() || this.resolution === resolution) {
      return;
    }

    const referenceListSchema =
      resolution.capabilities.commands['reference list']?.outputSchemaVersion;
    if (referenceListSchema !== '1.0') {
      throw new Error(
        'The validated companion must provide reference list output schema 1.0.'
      );
    }

    this.resolution = resolution;
    this.schedulePublication();
  }

  private schedulePublication(): void {
    const generation = this.connectionGeneration;
    this.pending = this.pending.then(async () => {
      const resolution = this.resolution;
      if (this.disposed
          || !this.languageClientRunning
          || !this.options.isTrusted()
          || generation !== this.connectionGeneration
          || generation === this.publishedGeneration
          || resolution === undefined) {
        return;
      }

      await this.options.sendNotification(
        CompanionExecutableSnapshotMethod,
        {
          schemaVersion: '1.0',
          executablePath: resolution.executablePath,
          referenceListOutputSchemaVersion:
            resolution.capabilities.commands['reference list']?.outputSchemaVersion
        }
      );
      if (!this.disposed
          && this.languageClientRunning
          && this.options.isTrusted()
          && generation === this.connectionGeneration) {
        this.publishedGeneration = generation;
      }
    }).catch(error => (
      this.disposed
        || !this.languageClientRunning
        || !this.options.isTrusted()
        || generation !== this.connectionGeneration
        ? undefined
        : this.observeError(this.options.reportPublicationError, error)
    ));
  }

  private async observeError(
    reporter: ((error: unknown) => void | PromiseLike<void>) | undefined,
    error: unknown
  ): Promise<void> {
    try {
      await reporter?.(error);
    } catch {
      // Reporting must not turn a background readiness failure into an unhandled rejection.
    }
  }
}
