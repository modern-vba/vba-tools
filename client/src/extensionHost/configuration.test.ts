import test from 'node:test';
import assert from 'node:assert/strict';

import {
  createExtensionHostLaunchArgs,
  createExtensionHostRuntimeSelection,
  minimumSupportedVscodeVersion
} from './configuration';

test('Extension Host tests use the minimum supported VS Code version by default', () => {
  assert.deepEqual(createExtensionHostRuntimeSelection({}), {
    version: minimumSupportedVscodeVersion,
    vscodeExecutablePath: undefined
  });
});

test('Extension Host tests use only an explicit executable override', () => {
  assert.deepEqual(
    createExtensionHostRuntimeSelection({
      VSCODE_EXECUTABLE_PATH: 'C:\\VSCode\\Code.exe'
    }),
    {
      version: undefined,
      vscodeExecutablePath: 'C:\\VSCode\\Code.exe'
    }
  );
});

test('Extension Host tests isolate each run in an explicit user data directory', () => {
  assert.deepEqual(
    createExtensionHostLaunchArgs(
      'C:\\Temp\\vba-tools-extension-host',
      'C:\\Temp\\vba-tools-debug-fixture'
    ),
    [
      '--disable-extensions',
      '--skip-welcome',
      '--skip-release-notes',
      '--user-data-dir=C:\\Temp\\vba-tools-extension-host',
      'C:\\Temp\\vba-tools-debug-fixture'
    ]
  );
});
