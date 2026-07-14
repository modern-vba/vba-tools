import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';

import {
  distributionManifestFileName,
  loadDistributionManifest,
  resolveBundledRuntimePath
} from './distributionManifest';

test('Distribution manifest provides bundled runtime paths for client launchers', () => {
  const extensionRoot = path.resolve(__dirname, '..', '..');
  const manifest = loadDistributionManifest(extensionRoot);

  assert.equal(manifest.runtimes.vbaDev.executablePath, 'bin/vba-dev/win-x64/vba-dev.exe');
  assert.equal(
    resolveBundledRuntimePath(extensionRoot, 'vbaLanguageServer'),
    path.join(extensionRoot, 'bin/vba-language-server/win-x64/vba-language-server.exe')
  );
  assert.ok(manifest.vsix.requiredFiles.includes(distributionManifestFileName));
});
