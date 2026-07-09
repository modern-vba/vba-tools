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
    createVbaLanguageServerOptions({ extensionRoot }),
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
