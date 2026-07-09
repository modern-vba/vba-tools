import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  createVbaLanguageServerOptions,
  resolveVbaLanguageServerPath
} from './languageServer';

test('VbaLanguageServer resolution uses the bundled Windows executable by default', () => {
  const extensionRoot = path.join('C:', 'extensions', 'vba-tools');

  assert.equal(
    resolveVbaLanguageServerPath({ extensionRoot }),
    path.join(extensionRoot, 'bin', 'vba-language-server', 'win-x64', 'vba-language-server.exe')
  );
});

test('VbaLanguageServer launch options use stdio command transport', () => {
  const extensionRoot = path.join('C:', 'extensions', 'vba-tools');
  const executablePath = resolveVbaLanguageServerPath({ extensionRoot });

  assert.deepEqual(
    createVbaLanguageServerOptions({ extensionRoot, platform: 'win32' }),
    {
      run: {
        command: executablePath,
        transport: 0
      },
      debug: {
        command: executablePath,
        transport: 0
      }
    }
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
