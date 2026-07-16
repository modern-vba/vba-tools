import test from 'node:test';
import assert from 'node:assert/strict';

import {
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
