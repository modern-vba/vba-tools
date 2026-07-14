import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  createVbaLanguageServerReferenceCatalogCacheRoot,
  createVbaLanguageServerOptions,
  referenceCatalogCacheRootEnvironmentVariable,
  resolveVbaLanguageServerPath
} from './languageServer';

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
  const referenceCatalogCacheRoot = path.join(extensionRoot, 'globalStorage', 'reference-catalogs');
  const options = createVbaLanguageServerOptions({
    extensionRoot,
    platform: 'win32',
    referenceCatalogCacheRoot
  });

  const launchOptions = options as {
    readonly run: {
      readonly command: string;
      readonly transport: number;
      readonly options: { readonly env?: NodeJS.ProcessEnv };
    };
    readonly debug: {
      readonly command: string;
      readonly transport: number;
      readonly options: { readonly env?: NodeJS.ProcessEnv };
    };
  };

  assert.equal(launchOptions.run.command, executablePath);
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

test('VbaLanguageServer launch options reject non-Windows platforms with a clear message', () => {
  assert.throws(
    () => createVbaLanguageServerOptions({
      extensionRoot: path.join('C:', 'extensions', 'vba-tools'),
      platform: 'linux'
    }),
    /Windows/
  );
});
