import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export const IntrinsicHostEventCatalogSnapshotMethod =
  'vba/intrinsicHostEventCatalog';

export interface IntrinsicHostEventCatalogCancellationDisposable {
  dispose(): void;
}

export interface IntrinsicHostEventCatalogCancellationToken {
  readonly isCancellationRequested: boolean;
  onCancellationRequested(
    listener: () => void
  ): IntrinsicHostEventCatalogCancellationDisposable;
}

export interface IntrinsicHostEventCatalogInvocation {
  readonly trigger: 'activation' | 'explicitRefresh';
  readonly args: readonly string[];
  readonly cancellationToken: IntrinsicHostEventCatalogCancellationToken;
}

export interface IntrinsicHostEventCatalogRunResult {
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  readonly cancelled: boolean;
}

export type IntrinsicHostEventTypeReference =
  | { readonly kind: 'intrinsic'; readonly name: string }
  | {
      readonly kind: 'typeLib';
      readonly name: string;
      readonly libraryGuid: string;
      readonly majorVersion: number;
      readonly minorVersion: number;
      readonly lcid: number;
    }
  | { readonly kind: 'unresolved'; readonly displayName: string };

export interface IntrinsicHostEventParameter {
  readonly name: string;
  readonly type: IntrinsicHostEventTypeReference;
  readonly passing: 'byVal' | 'byRef';
  readonly arrayShape: 'scalar' | 'array';
  readonly optional: boolean;
  readonly paramArray: boolean;
}

export interface IntrinsicHostEvent {
  readonly identity: {
    readonly sourceName: string;
    readonly name: string;
  };
  readonly signature: {
    readonly parameters: readonly IntrinsicHostEventParameter[];
    readonly documentation?: string;
  };
  readonly authoringAvailable: boolean;
  readonly existingHandlerRecognizable: boolean;
}

export interface IntrinsicHostEventBaseTypeProvenance {
  readonly name: string;
  readonly libraryGuid: string;
  readonly majorVersion: number;
  readonly minorVersion: number;
  readonly lcid: number;
}

export interface IntrinsicHostEventCatalog {
  readonly sourceKind: 'userForm';
  readonly intrinsicEventSourceName: 'UserForm';
  readonly events: readonly IntrinsicHostEvent[];
  readonly baseTypeProvenance?: IntrinsicHostEventBaseTypeProvenance;
}

export interface IntrinsicHostEventCatalogSnapshot {
  readonly schemaVersion: '1.0';
  readonly revision: number;
  readonly catalog: IntrinsicHostEventCatalog | null;
}

export interface IntrinsicHostEventCatalogLifecycleTransition {
  readonly kind:
    | 'started'
    | 'committed'
    | 'unavailable'
    | 'cancelled'
    | 'pendingReplay'
    | 'replayed'
    | 'notificationFailed';
  readonly trigger?: IntrinsicHostEventCatalogInvocation['trigger'];
  readonly revision: number;
  readonly eventCount?: number;
  readonly message?: string;
  readonly catalogRetained?: boolean;
  readonly catalogAvailable?: boolean;
}

export type IntrinsicHostEventCatalogRefreshOutcome =
  | { readonly status: 'succeeded'; readonly revision: number }
  | { readonly status: 'cancelled' }
  | {
      readonly status: 'failed';
      readonly reason:
        | 'executionFailed'
        | 'commandFailed'
        | 'invalidResult'
        | 'notificationFailed';
      readonly exitCode?: number;
    };

export interface IntrinsicHostEventCatalogRefreshHandle {
  readonly completion: Promise<IntrinsicHostEventCatalogRefreshOutcome>;
  cancel(): void;
}

export interface IntrinsicHostEventCatalogLifecycleOptions {
  readonly runHostEventList: (
    invocation: IntrinsicHostEventCatalogInvocation
  ) => Promise<IntrinsicHostEventCatalogRunResult>;
  readonly sendNotification: (
    method: string,
    parameters: unknown
  ) => Promise<void>;
  readonly isNotificationTargetAvailable?: () => boolean;
  readonly onTransition?: (
    transition: IntrinsicHostEventCatalogLifecycleTransition
  ) => void;
}

class CancellationSource {
  private readonly listeners = new Set<() => void>();
  private requested = false;
  public readonly token: IntrinsicHostEventCatalogCancellationToken;

  public constructor() {
    const source = this;
    this.token = {
      get isCancellationRequested() {
        return source.requested;
      },
      onCancellationRequested: (listener) => {
        if (source.requested) {
          listener();
          return { dispose: () => undefined };
        }
        source.listeners.add(listener);
        return { dispose: () => source.listeners.delete(listener) };
      }
    };
  }

  public cancel(): void {
    if (this.requested) {
      return;
    }
    this.requested = true;
    for (const listener of [...this.listeners]) {
      listener();
    }
    this.listeners.clear();
  }
}

export class IntrinsicHostEventCatalogLifecycle {
  private activationStarted = false;
  private shuttingDown = false;
  private revision = 0;
  private currentSnapshot: IntrinsicHostEventCatalogSnapshot | undefined;
  private pendingSnapshot: IntrinsicHostEventCatalogSnapshot | undefined;
  private hasHealthyCatalog = false;
  private work: Promise<void> = Promise.resolve();
  private readonly cancellationSources = new Set<CancellationSource>();

  public constructor(
    private readonly options: IntrinsicHostEventCatalogLifecycleOptions
  ) {
  }

  public activate(): void {
    if (this.activationStarted || this.shuttingDown) {
      return;
    }
    this.activationStarted = true;
    const source = new CancellationSource();
    this.enqueue(async () => {
      await this.acquire('activation', source);
    });
  }

  public refresh(): IntrinsicHostEventCatalogRefreshHandle {
    const source = new CancellationSource();
    let started = false;
    let settled = false;
    let resolveCompletion!: (
      outcome: IntrinsicHostEventCatalogRefreshOutcome
    ) => void;
    const completion = new Promise<IntrinsicHostEventCatalogRefreshOutcome>(
      (resolve) => {
        resolveCompletion = resolve;
      }
    );
    const settle = (outcome: IntrinsicHostEventCatalogRefreshOutcome): void => {
      if (settled) {
        return;
      }
      settled = true;
      resolveCompletion(outcome);
    };
    this.enqueue(async () => {
      if (this.shuttingDown || source.token.isCancellationRequested) {
        settle({ status: 'cancelled' });
        return;
      }
      started = true;
      settle(await this.acquire('explicitRefresh', source));
    });
    return {
      completion,
      cancel: () => {
        source.cancel();
        if (!started) {
          settle({ status: 'cancelled' });
        }
      }
    };
  }

  public async replayCurrentSnapshot(): Promise<void> {
    const pendingSnapshot = this.pendingSnapshot;
    const snapshot = pendingSnapshot ?? this.currentSnapshot;
    if (snapshot === undefined || this.shuttingDown) {
      return;
    }
    try {
      await this.options.sendNotification(
        IntrinsicHostEventCatalogSnapshotMethod,
        snapshot
      );
      if (this.pendingSnapshot === snapshot) {
        this.currentSnapshot = snapshot;
        this.pendingSnapshot = undefined;
        this.hasHealthyCatalog = snapshot.catalog !== null;
      }
      this.observe({
        kind: 'replayed',
        revision: snapshot.revision,
        eventCount: snapshot.catalog?.events.length,
        catalogAvailable: snapshot.catalog !== null
      });
    } catch (error) {
      this.observe({
        kind: 'notificationFailed',
        revision: snapshot.revision,
        message: errorMessage(error)
      });
    }
  }

  public shutdown(): void {
    this.shuttingDown = true;
    for (const source of this.cancellationSources) {
      source.cancel();
    }
  }

  public async flush(): Promise<void> {
    await this.work;
  }

  private enqueue(operation: () => Promise<void>): void {
    this.work = this.work.then(operation, operation);
  }

  private async acquire(
    trigger: IntrinsicHostEventCatalogInvocation['trigger'],
    cancellationSource: CancellationSource
  ): Promise<IntrinsicHostEventCatalogRefreshOutcome> {
    if (this.shuttingDown || cancellationSource.token.isCancellationRequested) {
      return { status: 'cancelled' };
    }
    this.cancellationSources.add(cancellationSource);
    this.observe({ kind: 'started', trigger, revision: this.revision });
    let result: IntrinsicHostEventCatalogRunResult;
    try {
      result = await this.options.runHostEventList({
        trigger,
        args: ['host-event', 'list', '--format', 'json'],
        cancellationToken: cancellationSource.token
      });
    } catch (error) {
      if (cancellationSource.token.isCancellationRequested || this.shuttingDown) {
        this.observe({ kind: 'cancelled', trigger, revision: this.revision });
        return { status: 'cancelled' };
      }
      return this.fail(trigger, 'executionFailed', errorMessage(error));
    } finally {
      this.cancellationSources.delete(cancellationSource);
    }

    if (result.cancelled || cancellationSource.token.isCancellationRequested || this.shuttingDown) {
      this.observe({ kind: 'cancelled', trigger, revision: this.revision });
      return { status: 'cancelled' };
    }
    if (result.exitCode !== 0) {
      return this.fail(
        trigger,
        'commandFailed',
        result.stderr.trim() || `vba-dev exited with code ${result.exitCode}.`,
        result.exitCode
      );
    }

    let catalog: IntrinsicHostEventCatalog;
    try {
      catalog = parseCatalog(result.stdout);
    } catch (error) {
      return this.fail(trigger, 'invalidResult', errorMessage(error));
    }

    const snapshot: IntrinsicHostEventCatalogSnapshot = {
      schemaVersion: '1.0',
      revision: ++this.revision,
      catalog
    };
    if (trigger === 'activation' &&
      this.options.isNotificationTargetAvailable?.() === false) {
      this.pendingSnapshot = snapshot;
      this.observe({
        kind: 'pendingReplay',
        trigger,
        revision: snapshot.revision,
        eventCount: catalog.events.length,
        catalogAvailable: false
      });
      return { status: 'succeeded', revision: snapshot.revision };
    }
    try {
      await this.options.sendNotification(
        IntrinsicHostEventCatalogSnapshotMethod,
        snapshot
      );
    } catch (error) {
      this.observe({
        kind: 'notificationFailed',
        trigger,
        revision: snapshot.revision,
        eventCount: catalog.events.length,
        message: errorMessage(error)
      });
      if (!this.hasHealthyCatalog) {
        this.pendingSnapshot = snapshot;
      }
      return { status: 'failed', reason: 'notificationFailed' };
    }
    this.currentSnapshot = snapshot;
    this.pendingSnapshot = undefined;
    this.hasHealthyCatalog = true;
    this.observe({
      kind: 'committed',
      trigger,
      revision: snapshot.revision,
      eventCount: catalog.events.length,
      catalogAvailable: true
    });
    return { status: 'succeeded', revision: snapshot.revision };
  }

  private async fail(
    trigger: IntrinsicHostEventCatalogInvocation['trigger'],
    reason: 'executionFailed' | 'commandFailed' | 'invalidResult',
    message: string,
    exitCode?: number
  ): Promise<IntrinsicHostEventCatalogRefreshOutcome> {
    if (!this.hasHealthyCatalog) {
      const snapshot: IntrinsicHostEventCatalogSnapshot = {
        schemaVersion: '1.0',
        revision: ++this.revision,
        catalog: null
      };
      if (this.options.isNotificationTargetAvailable?.() === false) {
        this.pendingSnapshot = snapshot;
      } else {
        try {
          await this.options.sendNotification(
            IntrinsicHostEventCatalogSnapshotMethod,
            snapshot
          );
          this.currentSnapshot = snapshot;
          this.pendingSnapshot = undefined;
        } catch (error) {
          this.pendingSnapshot = snapshot;
          this.observe({
            kind: 'notificationFailed',
            trigger,
            revision: snapshot.revision,
            message: errorMessage(error)
          });
        }
      }
    }
    this.observe({
      kind: 'unavailable',
      trigger,
      revision: this.revision,
      message,
      catalogRetained: this.hasHealthyCatalog
    });
    return { status: 'failed', reason, exitCode };
  }

  private observe(transition: IntrinsicHostEventCatalogLifecycleTransition): void {
    this.options.onTransition?.(transition);
  }
}

function parseCatalog(stdout: string): IntrinsicHostEventCatalog {
  let parsed: unknown;
  try {
    parsed = JSON.parse(stdout);
  } catch {
    throw new Error('Host Event catalog output was not valid JSON.');
  }
  const value = objectValue(parsed, 'Host Event catalog');
  onlyKeys(value, [
    'schemaVersion',
    'sourceKind',
    'intrinsicEventSourceName',
    'events',
    'baseTypeProvenance'
  ], 'Host Event catalog');
  exactString(value.schemaVersion, '1.0', 'schemaVersion');
  exactString(value.sourceKind, 'userForm', 'sourceKind');
  exactString(value.intrinsicEventSourceName, 'UserForm', 'intrinsicEventSourceName');
  if (!Array.isArray(value.events)) {
    throw new Error('events must be an array.');
  }
  if (value.events.length === 0) {
    throw new Error('events must contain at least one intrinsic UserForm Event.');
  }
  const names = new Set<string>();
  const events = value.events.map((entry, index) => {
    const inspectedEvent = parseEvent(entry, index);
    const key = ordinalIgnoreCaseKey(inspectedEvent.identity.name);
    if (names.has(key)) {
      throw new Error(`events contains duplicate identity '${inspectedEvent.identity.name}'.`);
    }
    names.add(key);
    return inspectedEvent;
  });
  const baseTypeProvenance = value.baseTypeProvenance === undefined
    ? undefined
    : parseProvenance(value.baseTypeProvenance, 'baseTypeProvenance');
  return {
    sourceKind: 'userForm',
    intrinsicEventSourceName: 'UserForm',
    events,
    ...(baseTypeProvenance === undefined ? {} : { baseTypeProvenance })
  };
}

function parseEvent(value: unknown, index: number): IntrinsicHostEvent {
  const context = `events[${index}]`;
  const entry = objectValue(value, context);
  onlyKeys(entry, [
    'identity',
    'signature',
    'authoringAvailable',
    'existingHandlerRecognizable'
  ], context);
  const identity = objectValue(entry.identity, `${context}.identity`);
  onlyKeys(identity, ['sourceName', 'name'], `${context}.identity`);
  exactString(identity.sourceName, 'UserForm', `${context}.identity.sourceName`);
  const name = nonEmptyString(identity.name, `${context}.identity.name`);
  const signature = objectValue(entry.signature, `${context}.signature`);
  onlyKeys(signature, ['parameters', 'documentation'], `${context}.signature`);
  if (!Array.isArray(signature.parameters)) {
    throw new Error(`${context}.signature.parameters must be an array.`);
  }
  const parameters = signature.parameters.map((parameter, parameterIndex) =>
    parseParameter(parameter, `${context}.signature.parameters[${parameterIndex}]`)
  );
  const documentation = signature.documentation === undefined
    ? undefined
    : stringValue(signature.documentation, `${context}.signature.documentation`);
  return {
    identity: { sourceName: 'UserForm', name },
    signature: {
      parameters,
      ...(documentation === undefined ? {} : { documentation })
    },
    authoringAvailable: booleanValue(
      entry.authoringAvailable,
      `${context}.authoringAvailable`
    ),
    existingHandlerRecognizable: booleanValue(
      entry.existingHandlerRecognizable,
      `${context}.existingHandlerRecognizable`
    )
  };
}

function parseParameter(value: unknown, context: string): IntrinsicHostEventParameter {
  const parameter = objectValue(value, context);
  onlyKeys(parameter, [
    'name', 'type', 'passing', 'arrayShape', 'optional', 'paramArray'
  ], context);
  const passing = enumString(parameter.passing, ['byVal', 'byRef'], `${context}.passing`);
  const arrayShape = enumString(
    parameter.arrayShape,
    ['scalar', 'array'],
    `${context}.arrayShape`
  );
  return {
    name: nonEmptyString(parameter.name, `${context}.name`),
    type: parseType(parameter.type, `${context}.type`),
    passing,
    arrayShape,
    optional: booleanValue(parameter.optional, `${context}.optional`),
    paramArray: booleanValue(parameter.paramArray, `${context}.paramArray`)
  };
}

function parseType(value: unknown, context: string): IntrinsicHostEventTypeReference {
  const type = objectValue(value, context);
  const kind = enumString(type.kind, ['intrinsic', 'typeLib', 'unresolved'], `${context}.kind`);
  if (kind === 'intrinsic') {
    onlyKeys(type, ['kind', 'name'], context);
    return { kind, name: nonEmptyString(type.name, `${context}.name`) };
  }
  if (kind === 'unresolved') {
    onlyKeys(type, ['kind', 'displayName'], context);
    return {
      kind,
      displayName: nonEmptyString(type.displayName, `${context}.displayName`)
    };
  }
  onlyKeys(type, [
    'kind', 'name', 'libraryGuid', 'majorVersion', 'minorVersion', 'lcid'
  ], context);
  return {
    kind,
    name: nonEmptyString(type.name, `${context}.name`),
    libraryGuid: guidValue(type.libraryGuid, `${context}.libraryGuid`),
    majorVersion: integerValue(type.majorVersion, `${context}.majorVersion`),
    minorVersion: integerValue(type.minorVersion, `${context}.minorVersion`),
    lcid: integerValue(type.lcid, `${context}.lcid`)
  };
}

function parseProvenance(
  value: unknown,
  context: string
): IntrinsicHostEventBaseTypeProvenance {
  const provenance = objectValue(value, context);
  onlyKeys(provenance, [
    'name', 'libraryGuid', 'majorVersion', 'minorVersion', 'lcid'
  ], context);
  return {
    name: nonEmptyString(provenance.name, `${context}.name`),
    libraryGuid: guidValue(provenance.libraryGuid, `${context}.libraryGuid`),
    majorVersion: integerValue(provenance.majorVersion, `${context}.majorVersion`),
    minorVersion: integerValue(provenance.minorVersion, `${context}.minorVersion`),
    lcid: integerValue(provenance.lcid, `${context}.lcid`)
  };
}

function objectValue(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${context} must be an object.`);
  }
  return value as Record<string, unknown>;
}

function onlyKeys(
  value: Record<string, unknown>,
  allowed: readonly string[],
  context: string
): void {
  const unknownKey = Object.keys(value).find((key) => !allowed.includes(key));
  if (unknownKey !== undefined) {
    throw new Error(`${context} contains unknown property '${unknownKey}'.`);
  }
}

function exactString(value: unknown, expected: string, context: string): void {
  if (value !== expected) {
    throw new Error(`${context} must be exactly '${expected}'.`);
  }
}

function stringValue(value: unknown, context: string): string {
  if (typeof value !== 'string') {
    throw new Error(`${context} must be a string.`);
  }
  return value;
}

function nonEmptyString(value: unknown, context: string): string {
  const result = stringValue(value, context);
  if (result.length === 0 || result.includes('\r') || result.includes('\n')) {
    throw new Error(`${context} must be a nonempty single-line string.`);
  }
  return result;
}

function booleanValue(value: unknown, context: string): boolean {
  if (typeof value !== 'boolean') {
    throw new Error(`${context} must be a boolean.`);
  }
  return value;
}

function integerValue(value: unknown, context: string): number {
  if (!Number.isSafeInteger(value) ||
    (value as number) < 0 ||
    (value as number) > 2_147_483_647) {
    throw new Error(`${context} must be a non-negative 32-bit integer.`);
  }
  return value as number;
}

function guidValue(value: unknown, context: string): string {
  const result = stringValue(value, context);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(result)) {
    throw new Error(`${context} must be a GUID in D format.`);
  }
  return result;
}

function enumString<T extends string>(
  value: unknown,
  allowed: readonly T[],
  context: string
): T {
  if (typeof value !== 'string' || !allowed.includes(value as T)) {
    throw new Error(`${context} is invalid.`);
  }
  return value as T;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
