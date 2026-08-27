import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import type { CompletionItem, FileSystemWatcher, SignatureHelp } from 'vscode';
import type { FormattingMiddleware } from 'vscode-languageclient/node';
import type { ClientCapabilities } from 'vscode-languageclient/node';

import {
  createVbaLanguageClientOptions,
  createVbaSignatureHelpClientCapabilitiesFeature,
  createVbaLanguageServerReferenceCatalogCacheRoot,
  createVbaLanguageServerOptions,
  referenceCatalogCacheRootEnvironmentVariable,
  resolveVbaLanguageServerPath
} from './languageServer';

test('VbaLanguageServer client advertises explicit null active-parameter support', () => {
  const capabilities: ClientCapabilities = {};

  createVbaSignatureHelpClientCapabilitiesFeature()
    .fillClientCapabilities(capabilities);

  const signatureHelp = capabilities.textDocument?.signatureHelp;
  assert.equal(signatureHelp?.contextSupport, true);
  assert.equal(
    signatureHelp?.signatureInformation?.activeParameterSupport,
    true
  );
  assert.equal(
    (signatureHelp?.signatureInformation as
      | { noActiveParameterSupport?: boolean }
      | undefined)?.noActiveParameterSupport,
    true
  );
});

test('VbaLanguageServer client preserves explicit no-active parameter through VS Code', async () => {
  const options = createVbaLanguageClientOptions(
    {} as FileSystemWatcher,
    {} as FileSystemWatcher
  );
  const provideSignatureHelp = options.middleware?.provideSignatureHelp;
  assert.ok(provideSignatureHelp);
  const converted = {
    signatures: [
      {
        label: 'Sub Work(Value As Long)',
        parameters: [{ label: 'Value As Long' }],
        activeParameter: null
      }
    ],
    activeSignature: 0,
    activeParameter: 0
  } as unknown as SignatureHelp;

  const result = await provideSignatureHelp(
    {} as never,
    {} as never,
    {} as never,
    {} as never,
    () => converted
  );

  assert.equal(result?.signatures[0]?.activeParameter, 1);
});

test('VbaLanguageServer client retriggers completion only for neutral continuation items', async () => {
  const options = createVbaLanguageClientOptions(
    {} as FileSystemWatcher,
    {} as FileSystemWatcher
  );
  const provideCompletionItem = options.middleware?.provideCompletionItem;
  assert.ok(provideCompletionItem);
  const continuation = {
    label: 'UserForm_',
    data: { retriggerCompletion: true }
  } as unknown as CompletionItem;
  const ordinary = {
    label: 'Value'
  } as unknown as CompletionItem;
  const preconfigured = {
    label: 'Configured',
    data: { retriggerCompletion: true },
    command: {
      title: 'Keep existing behavior',
      command: 'extension.keepExisting'
    }
  } as unknown as CompletionItem;

  const result = await provideCompletionItem(
    {} as never,
    {} as never,
    {} as never,
    {} as never,
    () => [continuation, ordinary, preconfigured]
  );
  assert.ok(Array.isArray(result));
  assert.deepEqual(result[0]?.command, {
    title: 'Continue contract completion',
    command: 'editor.action.triggerSuggest'
  });
  assert.equal(result[1]?.command, undefined);
  assert.deepEqual(result[2]?.command, {
    title: 'Keep existing behavior',
    command: 'extension.keepExisting'
  });
});

test('VbaLanguageServer client preserves completion-list metadata and ignores malformed continuation data', async () => {
  const options = createVbaLanguageClientOptions(
    {} as FileSystemWatcher,
    {} as FileSystemWatcher
  );
  const provideCompletionItem = options.middleware?.provideCompletionItem;
  assert.ok(provideCompletionItem);
  const continuation = {
    label: 'UserForm_',
    data: { retriggerCompletion: true }
  } as unknown as CompletionItem;
  const malformed = {
    label: 'Malformed',
    data: { retriggerCompletion: 'true' }
  } as unknown as CompletionItem;

  const result = await provideCompletionItem(
    {} as never,
    {} as never,
    {} as never,
    {} as never,
    () => ({
      isIncomplete: true,
      items: [continuation, malformed]
    })
  );
  if (result === null || result === undefined || Array.isArray(result)) {
    assert.fail('Expected a CompletionList result.');
  }
  assert.equal(result.isIncomplete, true);
  assert.deepEqual(result.items[0]?.command, {
    title: 'Continue contract completion',
    command: 'editor.action.triggerSuggest'
  });
  assert.equal(result.items[1]?.command, undefined);
});

test('VbaLanguageServer client synchronizes source and project manifest file events', () => {
  const sourceFileWatcher = {} as FileSystemWatcher;
  const projectManifestWatcher = {} as FileSystemWatcher;

  const options = createVbaLanguageClientOptions(
    sourceFileWatcher,
    projectManifestWatcher
  );

  assert.deepEqual(options.documentSelector, [
    { language: 'vba', scheme: 'file' },
    { language: 'vba', scheme: 'untitled' }
  ]);
  assert.deepEqual(
    options.synchronize?.fileEvents,
    [sourceFileWatcher, projectManifestWatcher]
  );
});

test('VbaLanguageServer client uses the VBA document formatting middleware', () => {
  const sourceFileWatcher = {} as FileSystemWatcher;
  const projectManifestWatcher = {} as FileSystemWatcher;
  const provideDocumentFormattingEdits = (() => null) as NonNullable<
    FormattingMiddleware['provideDocumentFormattingEdits']
  >;

  const options = createVbaLanguageClientOptions(
    sourceFileWatcher,
    projectManifestWatcher,
    provideDocumentFormattingEdits
  );

  assert.strictEqual(
    options.middleware?.provideDocumentFormattingEdits,
    provideDocumentFormattingEdits
  );
});

test('VbaLanguageServer resolution uses the bundled Windows executable by default', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');

  assert.equal(
    resolveVbaLanguageServerPath({ extensionRoot }),
    path.join(extensionRoot, 'bin', 'vba-language-server', 'win-x64', 'vba-language-server.exe')
  );
});

test('VbaLanguageServer launch options use stdio command transport', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const executablePath = resolveVbaLanguageServerPath({ extensionRoot });
  const vbaDevExecutablePath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const referenceCatalogCacheRoot = path.join(extensionRoot, 'globalStorage', 'reference-catalogs');
  const options = createVbaLanguageServerOptions({
    extensionRoot,
    platform: 'win32',
    vbaDevExecutablePath,
    referenceCatalogCacheRoot
  });

  const launchOptions = options as {
    readonly run: {
      readonly command: string;
      readonly args?: readonly string[];
      readonly transport: number;
      readonly options: { readonly env?: NodeJS.ProcessEnv };
    };
    readonly debug: {
      readonly command: string;
      readonly args?: readonly string[];
      readonly transport: number;
      readonly options: { readonly env?: NodeJS.ProcessEnv };
    };
  };

  assert.equal(launchOptions.run.command, executablePath);
  assert.deepEqual(launchOptions.run.args, ['--vba-dev', vbaDevExecutablePath]);
  assert.equal(launchOptions.run.transport, 0);
  assert.equal(
    launchOptions.run.options.env?.[referenceCatalogCacheRootEnvironmentVariable],
    referenceCatalogCacheRoot
  );
  assert.deepEqual(launchOptions.debug, launchOptions.run);
});

test('VbaLanguageServer reference catalog cache root is derived from VS Code global storage', () => {
  const globalStorageRoot = path.join(
    'C:',
    'Users',
    'alice',
    'AppData',
    'Roaming',
    'Code',
    'User',
    'globalStorage',
    'modern-vba.vba-tools'
  );

  assert.equal(
    createVbaLanguageServerReferenceCatalogCacheRoot(globalStorageRoot),
    path.join(globalStorageRoot, 'reference-catalogs')
  );
});

test('VbaLanguageServer launch omits vba-dev arguments when no compatible executable is available', () => {
  const options = createVbaLanguageServerOptions({
    extensionRoot: path.resolve(__dirname, '..', '..'),
    platform: 'win32'
  }) as {
    readonly run: { readonly args?: readonly string[] };
    readonly debug: { readonly args?: readonly string[] };
  };

  assert.equal(options.run.args, undefined);
  assert.equal(options.debug.args, undefined);
});

test('VbaLanguageServer launch options reject non-Windows platforms with a clear message', () => {
  assert.throws(
    () => createVbaLanguageServerOptions({
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      platform: 'linux'
    }),
    /Windows/
  );
});
