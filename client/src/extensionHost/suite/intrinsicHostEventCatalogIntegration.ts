import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import {
  CompletionItem,
  CompletionList,
  DocumentSymbol,
  Position,
  SymbolInformation,
  Uri,
  commands,
  extensions,
  workspace
} from 'vscode';

interface HostEventCatalogRunResult {
  readonly exitCode: number;
  readonly stdout: string;
  readonly stderr: string;
  readonly cancelled: boolean;
}

interface HostEventCatalogNotification {
  readonly schemaVersion: '1.0';
  readonly revision: number;
  readonly catalog: {
    readonly sourceKind: 'userForm';
    readonly intrinsicEventSourceName: 'UserForm';
    readonly events: readonly unknown[];
  } | null;
}

interface IntrinsicHostEventCatalogTestApi {
  readonly actualWorkspaceTrusted: boolean;
  readonly effectiveWorkspaceTrusted: boolean;
  snapshot(): {
    readonly invocations: readonly {
      readonly trigger: 'activation' | 'explicitRefresh';
      readonly args: readonly string[];
    }[];
    readonly transitions: readonly {
      readonly kind: string;
      readonly trigger?: 'activation' | 'explicitRefresh';
      readonly revision: number;
      readonly catalogRetained?: boolean;
    }[];
    readonly notifications: readonly {
      readonly method: string;
      readonly parameters: unknown;
    }[];
    readonly pendingInvocationCount: number;
  };
  completeInvocation(index: number, result: HostEventCatalogRunResult): void;
  restartLanguageClient(): Promise<void>;
}

interface CompanionExecutableTestApi {
  snapshot(): {
    readonly invocations: readonly {
      readonly file: string;
      readonly args: readonly string[];
    }[];
    readonly pendingInvocationCount: number;
  };
  completeInvocation(index: number, result: {
    readonly stdout: string;
    readonly stderr: string;
  }): void;
}

interface VbaToolsExtensionHostTestApi {
  readonly companionExecutable: CompanionExecutableTestApi;
  readonly intrinsicHostEventCatalog: IntrinsicHostEventCatalogTestApi;
}

export async function runIntrinsicHostEventCatalogIntegrationTests(): Promise<void> {
  const mode = process.env.VBA_TOOLS_INTRINSIC_HOST_EVENT_CATALOG_TEST_MODE;
  if (mode === 'actual-untrusted') {
    await runUntrustedActivationTest();
    return;
  }

  assert.equal(mode, 'controlled-trusted');
  await runTrustedActivationTest();
}

async function runTrustedActivationTest(): Promise<void> {
  assert.equal(workspace.isTrusted, true);
  const fixtureRoot = requiredFixtureRoot();
  const templateSnapshots = await readTemplateSnapshots(fixtureRoot);
  const extension = extensions.getExtension<VbaToolsExtensionHostTestApi>(
    'modern-vba.vba-tools'
  );
  assert.ok(extension, 'The VBA Tools development extension must be available.');

  const api = await withTimeout(extension.activate(), 10_000);
  const companion = api.companionExecutable;
  const catalog = api.intrinsicHostEventCatalog;

  assert.equal(catalog.actualWorkspaceTrusted, true);
  assert.equal(catalog.effectiveWorkspaceTrusted, true);
  assert.deepEqual(
    companion.snapshot().invocations.map((invocation) => invocation.args),
    [['capabilities', '--format', 'json']]
  );
  assert.equal(companion.snapshot().pendingInvocationCount, 1);
  assert.deepEqual(catalog.snapshot().invocations, []);

  const formUri = Uri.file(path.join(
    fixtureRoot,
    'src',
    'Book01',
    'ProbeForm.frm'
  ));
  const formDocument = await workspace.openTextDocument(formUri);
  const acceptedVersion = formDocument.version;
  const semanticTokens = await withTimeout(commands.executeCommand<unknown>(
    '_provideDocumentSemanticTokens',
    formUri
  ));
  assert.ok(semanticTokenDataLength(semanticTokens) > 0);
  assert.equal(formDocument.version, acceptedVersion);
  assert.deepEqual(catalog.snapshot().invocations, []);
  console.log(
    'PASS semantic highlighting starts while companion resolution remains blocked'
  );

  companion.completeInvocation(0, await compatibleCapabilitiesResult(
    extension.extensionPath
  ));
  await waitFor(() => catalog.snapshot().invocations.length === 1);
  assert.equal(companion.snapshot().pendingInvocationCount, 0);
  assert.equal(catalog.snapshot().pendingInvocationCount, 1);
  assert.deepEqual(catalog.snapshot().invocations, [{
    trigger: 'activation',
    args: ['host-event', 'list', '--format', 'json']
  }]);
  assert.deepEqual(
    catalog.snapshot().transitions.map((transition) => transition.kind),
    ['started']
  );

  const symbols = await withTimeout(commands.executeCommand<
    readonly DocumentSymbol[] | readonly SymbolInformation[] | undefined
  >('vscode.executeDocumentSymbolProvider', formUri));
  assert.ok(symbols !== undefined && symbols.length > 0);
  assert.equal(formDocument.languageId, 'vba');
  assert.equal(catalog.snapshot().invocations.length, 1);
  console.log(
    'PASS trusted 15-document activation starts one nonblocking environment acquisition'
  );

  catalog.completeInvocation(0, successfulCatalogResult);
  await waitFor(() => catalog.snapshot().transitions.some((transition) =>
    transition.kind === 'committed' && transition.trigger === 'activation'
  ));
  await waitFor(() => catalog.snapshot().notifications.length === 1);
  assert.deepEqual(notificationSummaries(catalog), [
    { revision: 1, available: true }
  ]);
  assert.equal(catalog.snapshot().pendingInvocationCount, 0);
  await waitForCompletion(formUri, 'UserForm_Initialize');

  for (const [templatePath, bytes] of templateSnapshots) {
    await writeFile(templatePath, bytes);
  }
  await delay(1_250);
  assert.equal(catalog.snapshot().invocations.length, 1);
  console.log(
    'PASS startup success and fifteen template changes start no watcher or per-document fallback'
  );

  const refresh = commands.executeCommand('vbaTools.userFormEvents.refresh');
  await waitFor(() => catalog.snapshot().invocations.length === 2);
  assert.deepEqual(catalog.snapshot().invocations[1], {
    trigger: 'explicitRefresh',
    args: ['host-event', 'list', '--format', 'json']
  });
  assert.equal(catalog.snapshot().pendingInvocationCount, 1);
  catalog.completeInvocation(1, successfulCatalogResult);
  await withTimeout(refresh);
  await waitFor(() => catalog.snapshot().notifications.length === 2);
  assert.deepEqual(notificationSummaries(catalog), [
    { revision: 1, available: true },
    { revision: 2, available: true }
  ]);
  assert.equal(catalog.snapshot().pendingInvocationCount, 0);
  await waitForCompletion(formUri, 'UserForm_Initialize');
  console.log(
    'PASS explicit environment refresh needs no document chooser and publishes the catalog'
  );

  await withTimeout(catalog.restartLanguageClient(), 10_000);
  await waitFor(() => catalog.snapshot().notifications.length === 3, 10_000);
  assert.deepEqual(notificationSummaries(catalog), [
    { revision: 1, available: true },
    { revision: 2, available: true },
    { revision: 2, available: true }
  ]);
  assert.equal(catalog.snapshot().invocations.length, 2);
  await waitForCompletion(formUri, 'UserForm_Initialize');
  for (const [templatePath, expectedBytes] of templateSnapshots) {
    assert.deepEqual(await readFile(templatePath), expectedBytes);
  }
  console.log(
    'PASS language-client restart replays the current catalog without another acquisition'
  );
}

async function runUntrustedActivationTest(): Promise<void> {
  assert.equal(workspace.isTrusted, false);
  const extension = extensions.getExtension<VbaToolsExtensionHostTestApi>(
    'modern-vba.vba-tools'
  );
  assert.ok(extension, 'The VBA Tools development extension must be available.');

  const api = await extension.activate();
  const companion = api.companionExecutable;
  const catalog = api.intrinsicHostEventCatalog;
  assert.equal(catalog.actualWorkspaceTrusted, false);
  assert.equal(catalog.effectiveWorkspaceTrusted, false);
  await delay(250);
  assert.deepEqual(catalog.snapshot(), {
    invocations: [],
    transitions: [],
    notifications: [],
    pendingInvocationCount: 0
  });
  assert.deepEqual(companion.snapshot().invocations, []);
  assert.equal(companion.snapshot().pendingInvocationCount, 0);

  const fixtureRoot = requiredFixtureRoot();
  const formUri = Uri.file(path.join(
    fixtureRoot,
    'src',
    'Book01',
    'ProbeForm.frm'
  ));
  const symbols = await withTimeout(commands.executeCommand<
    readonly DocumentSymbol[] | readonly SymbolInformation[] | undefined
  >('vscode.executeDocumentSymbolProvider', formUri));
  assert.ok(symbols !== undefined && symbols.length > 0);
  assert.equal(catalog.snapshot().invocations.length, 0);
  console.log(
    'PASS untrusted activation starts no catalog acquisition while language assistance remains available'
  );
}

async function compatibleCapabilitiesResult(
  extensionPath: string
): Promise<{ readonly stdout: string; readonly stderr: string }> {
  const contract = JSON.parse(await readFile(
    path.join(extensionPath, 'vba-dev-contract.json'),
    'utf8'
  )) as {
    readonly contractVersion: string;
    readonly featureVersions: Readonly<Record<string, string>>;
    readonly commandSchemaVersions: Readonly<Record<string, string>>;
  };
  return {
    stdout: JSON.stringify({
      toolVersion: 'extension-host-test',
      contractVersion: contract.contractVersion,
      featureVersions: contract.featureVersions,
      activeWindowsCodePage: 932,
      commands: Object.fromEntries(Object.entries(
        contract.commandSchemaVersions
      ).map(([command, outputSchemaVersion]) => [
        command,
        { outputSchemaVersion }
      ]))
    }),
    stderr: ''
  };
}

function semanticTokenDataLength(value: unknown): number {
  if (Array.isArray(value)) {
    return value.length;
  }
  if (ArrayBuffer.isView(value)) {
    return value.byteLength;
  }
  if (typeof value === 'object' && value !== null) {
    if ('byteLength' in value
        && typeof (value as { readonly byteLength: unknown }).byteLength === 'number') {
      return (value as { readonly byteLength: number }).byteLength;
    }
    if ('data' in value) {
      return semanticTokenDataLength((value as { readonly data: unknown }).data);
    }
    if ('buffer' in value) {
      return semanticTokenDataLength((value as { readonly buffer: unknown }).buffer);
    }
  }
  return 0;
}

function requiredFixtureRoot(): string {
  const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureRoot, 'The Host Event catalog fixture root must be provided.');
  return fixtureRoot;
}

async function readTemplateSnapshots(
  fixtureRoot: string
): Promise<ReadonlyMap<string, Buffer>> {
  const entries: Array<readonly [string, Buffer]> = [];
  for (let index = 1; index <= 15; index += 1) {
    const document = `Book${String(index).padStart(2, '0')}`;
    const templatePath = path.join(
      fixtureRoot,
      'templates',
      `${document}.xlsm`
    );
    entries.push([templatePath, await readFile(templatePath)]);
  }
  return new Map(entries);
}

function notificationSummaries(
  catalog: IntrinsicHostEventCatalogTestApi
): Array<{ readonly revision: number; readonly available: boolean }> {
  return catalog.snapshot().notifications.map((notification) => {
    assert.equal(notification.method, 'vba/intrinsicHostEventCatalog');
    const parameters = notification.parameters as HostEventCatalogNotification;
    assert.equal(parameters.schemaVersion, '1.0');
    return {
      revision: parameters.revision,
      available: parameters.catalog !== null
    };
  });
}

async function waitForCompletion(
  uri: Uri,
  expectedLabel: string
): Promise<void> {
  await waitFor(async () => {
    const completion = await commands.executeCommand<
      CompletionList | readonly CompletionItem[] | undefined
    >(
      'vscode.executeCompletionItemProvider',
      uri,
      new Position(8, 'Private Sub UserForm_'.length)
    );
    const items: readonly CompletionItem[] = completion === undefined
      ? []
      : 'items' in completion
        ? completion.items
        : completion;
    return items.some((item) => item.label === expectedLabel);
  }, 10_000);
}

async function waitFor(
  condition: () => boolean | Promise<boolean>,
  timeoutMilliseconds = 5_000
): Promise<void> {
  const deadline = Date.now() + timeoutMilliseconds;
  while (!await condition()) {
    if (Date.now() >= deadline) {
      throw new Error(`Condition was not met within ${timeoutMilliseconds} ms.`);
    }
    await delay(20);
  }
}

async function withTimeout<T>(
  operation: Thenable<T> | PromiseLike<T>,
  timeoutMilliseconds = 5_000
): Promise<T> {
  return Promise.race([
    Promise.resolve(operation),
    new Promise<never>((_resolve, reject) => {
      setTimeout(() => reject(new Error(
        `Operation did not complete within ${timeoutMilliseconds} ms.`
      )), timeoutMilliseconds);
    })
  ]);
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}

const successfulCatalogResult: HostEventCatalogRunResult = {
  exitCode: 0,
  stderr: '',
  cancelled: false,
  stdout: JSON.stringify({
    schemaVersion: '1.0',
    sourceKind: 'userForm',
    intrinsicEventSourceName: 'UserForm',
    events: [
      {
        identity: { sourceName: 'UserForm', name: 'Initialize' },
        signature: {
          parameters: [],
          documentation: 'Occurs when the form is initialized.'
        },
        authoringAvailable: true,
        existingHandlerRecognizable: true
      },
      {
        identity: { sourceName: 'UserForm', name: 'QueryClose' },
        signature: {
          parameters: [
            {
              name: 'Cancel',
              type: { kind: 'intrinsic', name: 'Integer' },
              passing: 'byRef',
              arrayShape: 'scalar',
              optional: false,
              paramArray: false
            },
            {
              name: 'CloseMode',
              type: { kind: 'intrinsic', name: 'Integer' },
              passing: 'byVal',
              arrayShape: 'scalar',
              optional: false,
              paramArray: false
            }
          ],
          documentation: 'Occurs before the form closes.'
        },
        authoringAvailable: true,
        existingHandlerRecognizable: true
      }
    ]
  })
};
