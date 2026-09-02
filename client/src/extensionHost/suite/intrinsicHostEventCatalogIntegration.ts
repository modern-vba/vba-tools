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
  };
  completeInvocation(index: number, result: HostEventCatalogRunResult): void;
  restartLanguageClient(): Promise<void>;
}

interface VbaToolsExtensionHostTestApi {
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

  const api = await extension.activate();
  const catalog = api.intrinsicHostEventCatalog;

  assert.equal(catalog.actualWorkspaceTrusted, true);
  assert.equal(catalog.effectiveWorkspaceTrusted, true);
  assert.deepEqual(catalog.snapshot().invocations, [{
    trigger: 'activation',
    args: ['host-event', 'list', '--format', 'json']
  }]);
  assert.deepEqual(
    catalog.snapshot().transitions.map((transition) => transition.kind),
    ['started']
  );

  const formUri = Uri.file(path.join(
    fixtureRoot,
    'src',
    'Book01',
    'ProbeForm.frm'
  ));
  const formDocument = await workspace.openTextDocument(formUri);
  const symbols = await withTimeout(commands.executeCommand<
    readonly DocumentSymbol[] | readonly SymbolInformation[] | undefined
  >('vscode.executeDocumentSymbolProvider', formUri));
  assert.ok(symbols !== undefined && symbols.length > 0);
  assert.equal(formDocument.languageId, 'vba');
  assert.equal(catalog.snapshot().invocations.length, 1);
  console.log(
    'PASS trusted 15-document activation starts one nonblocking environment acquisition'
  );

  catalog.completeInvocation(0, {
    exitCode: 1,
    stdout: '',
    stderr: 'synthetic environment discovery failure',
    cancelled: false
  });
  await waitFor(() => catalog.snapshot().transitions.some((transition) =>
    transition.kind === 'unavailable' && transition.trigger === 'activation'
  ));
  await waitFor(() => catalog.snapshot().notifications.length === 1);
  assert.deepEqual(
    catalog.snapshot().notifications.map((notification) => ({
      method: notification.method,
      parameters: notification.parameters
    })),
    [{
      method: 'vba/intrinsicHostEventCatalog',
      parameters: {
        schemaVersion: '1.0',
        revision: 1,
        catalog: null
      }
    }]
  );

  for (const [templatePath, bytes] of templateSnapshots) {
    await writeFile(templatePath, bytes);
  }
  await delay(1_250);
  assert.equal(catalog.snapshot().invocations.length, 1);
  console.log(
    'PASS startup failure and fifteen template changes start no watcher or per-document fallback'
  );

  const refresh = commands.executeCommand('vbaTools.userFormEvents.refresh');
  await waitFor(() => catalog.snapshot().invocations.length === 2);
  assert.deepEqual(catalog.snapshot().invocations[1], {
    trigger: 'explicitRefresh',
    args: ['host-event', 'list', '--format', 'json']
  });
  catalog.completeInvocation(1, successfulCatalogResult);
  await withTimeout(refresh);
  await waitFor(() => catalog.snapshot().notifications.length === 2);
  assert.deepEqual(notificationSummaries(catalog), [
    { revision: 1, available: false },
    { revision: 2, available: true }
  ]);
  await waitForCompletion(formUri, 'UserForm_Initialize');
  console.log(
    'PASS explicit environment refresh needs no document chooser and publishes the catalog'
  );

  await withTimeout(catalog.restartLanguageClient(), 10_000);
  await waitFor(() => catalog.snapshot().notifications.length === 3, 10_000);
  assert.deepEqual(notificationSummaries(catalog), [
    { revision: 1, available: false },
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
  const catalog = api.intrinsicHostEventCatalog;
  assert.equal(catalog.actualWorkspaceTrusted, false);
  assert.equal(catalog.effectiveWorkspaceTrusted, false);
  await delay(250);
  assert.deepEqual(catalog.snapshot(), {
    invocations: [],
    transitions: [],
    notifications: []
  });

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
