import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  createStandaloneVbaDevArchive,
  writeReleaseChecksums
} from './vbaDevReleasePackage.mjs';

test('standalone vba-dev archive is versioned complete and probed after clean extraction', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-dev-release-package-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));

  await write(root, 'tools/vba-dev/Directory.Build.props', `
<Project>
  <PropertyGroup>
    <VbaDevReleaseVersion>0.1.0</VbaDevReleaseVersion>
  </PropertyGroup>
</Project>
`);
  await write(root, 'bin/vba-dev/win-x64/vba-dev.exe', 'self-contained executable');
  await write(root, 'bin/vba-dev/win-x64/vba-dev.pdb', 'cli symbols');
  await write(root, 'bin/vba-dev/win-x64/VbaDev.App.pdb', 'app symbols');
  await write(root, 'tools/vba-dev/README.md', '# vba-dev\n');
  await write(root, 'LICENSE', 'MIT\n');
  await write(root, 'vba-dev-contract.json', JSON.stringify({ contractVersion: '1.0' }));

  let archivedFiles = [];
  let archivedSource;
  const archiveDirectory = async (sourceDirectory, archivePath) => {
    archivedSource = sourceDirectory;
    archivedFiles = (await fs.readdir(sourceDirectory)).sort();
    await fs.writeFile(archivePath, 'zip payload');
  };
  const extractArchive = async (_archivePath, destinationDirectory) => {
    await fs.cp(archivedSource, destinationDirectory, { recursive: true });
  };
  const probes = [];
  const runCommand = async (file, args) => {
    probes.push({ file, args });
    if (args[0] === '--version') {
      return { stdout: 'vba-dev 0.1.0\n', stderr: '' };
    }

    return {
      stdout: JSON.stringify({ toolVersion: '0.1.0', contractVersion: '1.0' }),
      stderr: ''
    };
  };

  const result = await createStandaloneVbaDevArchive({
    root,
    outputDirectory: path.join(root, 'release'),
    archiveDirectory,
    extractArchive,
    runCommand
  });
  t.after(result.cleanup);

  assert.equal(path.basename(result.archivePath), 'vba-dev-win-x64-0.1.0.zip');
  assert.deepEqual(archivedFiles, [
    'LICENSE',
    'README.md',
    'VbaDev.App.pdb',
    'vba-dev-contract.json',
    'vba-dev.exe',
    'vba-dev.pdb'
  ]);
  assert.deepEqual(probes.map(({ args }) => args), [
    ['--version'],
    ['capabilities', '--format', 'json']
  ]);
  assert.ok(probes.every(({ file }) => file.startsWith(result.extractionDirectory)));
});

test('package:devtool publishes and verifies the standalone archive through one repository command', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.equal(
    packageJson.scripts['package:devtool'],
    'npm run publish:devtool && node scripts/vbaDevReleasePackage.mjs'
  );
});

test('release checksums include the standalone CLI ZIP beside the VSIX', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-dev-release-checksums-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const vsixPath = path.join(root, 'vba-tools-win32-x64-0.1.0.vsix');
  const cliArchivePath = path.join(root, 'vba-dev-win-x64-0.1.0.zip');
  await fs.writeFile(vsixPath, 'vsix');
  await fs.writeFile(cliArchivePath, 'cli zip');

  const checksumPath = await writeReleaseChecksums(root, [vsixPath, cliArchivePath]);
  const checksumLines = (await fs.readFile(checksumPath, 'utf8')).trimEnd().split('\n');

  assert.equal(path.basename(checksumPath), 'SHA256SUMS');
  assert.equal(checksumLines.length, 2);
  assert.match(checksumLines[0], /^[0-9a-f]{64}  vba-dev-win-x64-0\.1\.0\.zip$/);
  assert.match(checksumLines[1], /^[0-9a-f]{64}  vba-tools-win32-x64-0\.1\.0\.vsix$/);
});

async function write(root, relativePath, contents) {
  const filePath = path.join(root, relativePath);
  await fs.mkdir(path.dirname(filePath), { recursive: true });
  await fs.writeFile(filePath, contents);
}
