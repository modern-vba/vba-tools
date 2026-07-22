import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  assembleReleaseArtifacts,
  assertVsixReleaseChannel,
  createReleaseTag,
  parseReleaseCommandArguments,
  prepareRelease,
  validateReleaseInputs,
  verifyReleaseArtifactSet
} from './releaseManagement.mjs';
import { writeReleaseChecksums } from './vbaDevReleasePackage.mjs';

test('release inputs require canonical independent versions and the matching Marketplace channel', () => {
  assert.deepEqual(
    validateReleaseInputs({
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '2.4.6'
    }),
    {
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '2.4.6',
      extensionTag: 'vba-tools-v0.1.0',
      cliTag: 'vba-dev-v2.4.6'
    }
  );
  assert.doesNotThrow(() => validateReleaseInputs({
    extensionVersion: '2.2.3',
    channel: 'stable',
    vbaDevVersion: '7.8.9'
  }));

  for (const invalid of [
    { extensionVersion: '1.0', channel: 'stable', vbaDevVersion: '1.0.0' },
    { extensionVersion: 'v1.0.0', channel: 'stable', vbaDevVersion: '1.0.0' },
    { extensionVersion: '1.0.0-beta.1', channel: 'stable', vbaDevVersion: '1.0.0' },
    { extensionVersion: '1.0.0', channel: 'stable', vbaDevVersion: '1.0' },
    { extensionVersion: '1.1.0', channel: 'stable', vbaDevVersion: '1.0.0' },
    { extensionVersion: '1.2.0', channel: 'pre-release', vbaDevVersion: '1.0.0' },
    { extensionVersion: '1.1.0', channel: 'preview', vbaDevVersion: '1.0.0' }
  ]) {
    assert.throws(() => validateReleaseInputs(invalid), /release input|SemVer|channel/i);
  }
});

test('repository release commands parse every required decision explicitly and reject unknown input', () => {
  assert.deepEqual(parseReleaseCommandArguments([
    'prepare',
    '--extension-version', '0.1.0',
    '--channel', 'pre-release',
    '--vba-dev-version', '0.1.0'
  ]), {
    command: 'prepare',
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0'
  });
  assert.deepEqual(parseReleaseCommandArguments([
    'tag',
    '--version', '0.1.0',
    '--channel', 'pre-release',
    '--vba-dev-version', '0.1.0',
    '--verified-commit', 'a'.repeat(40),
    '--windows-excel-result', 'pass',
    '--clean-windows-smoke', 'pass'
  ]), {
    command: 'tag',
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    verifiedCommit: 'a'.repeat(40),
    windowsExcelResult: 'pass',
    cleanWindowsSmoke: 'pass'
  });
  assert.deepEqual(parseReleaseCommandArguments([
    'artifacts',
    '--extension-version', '0.1.0',
    '--channel', 'pre-release',
    '--vba-dev-version', '0.1.0',
    '--output', '.tmp/release-candidate'
  ]), {
    command: 'artifacts',
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    outputDirectory: path.resolve('.tmp/release-candidate')
  });

  assert.throws(
    () => parseReleaseCommandArguments(['prepare', '--extension-version', '0.1.0']),
    /required.*channel.*vba-dev-version/i
  );
  assert.throws(
    () => parseReleaseCommandArguments([
      'prepare',
      '--extension-version', '0.1.0',
      '--channel', 'pre-release',
      '--vba-dev-version', '0.1.0',
      '--publish'
    ]),
    /unknown release argument.*publish/i
  );
});

test('repository pins the release toolchain commands dependency updates and curated CLI history', async () => {
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const packageJson = await readJson(repositoryRoot, 'package.json');
  assert.equal(packageJson.packageManager, 'npm@11.18.0');
  assert.equal(packageJson.engines.node, '>=24 <25');
  assert.equal(packageJson.engines.npm, '>=11 <12');
  assert.equal(packageJson.scripts['release:prepare'], 'node scripts/releaseManagement.mjs prepare');
  assert.equal(packageJson.scripts['release:tag'], 'node scripts/releaseManagement.mjs tag');
  assert.equal(packageJson.scripts['release:artifacts'], 'node scripts/releaseManagement.mjs artifacts');
  assert.equal(await fs.readFile(path.join(repositoryRoot, '.node-version'), 'utf8'), '24.17.0\n');
  const globalJson = await readJson(repositoryRoot, 'global.json');
  assert.deepEqual(globalJson.sdk, {
    version: '10.0.300',
    rollForward: 'latestPatch',
    allowPrerelease: false
  });
  const dotnetProjects = [
    'tools/vba-dev/src/VbaDev.App/VbaDev.App.csproj',
    'tools/vba-dev/src/VbaDev.Cli/VbaDev.Cli.csproj',
    'tools/vba-dev/src/VbaDev.Composition/VbaDev.Composition.csproj',
    'tools/vba-dev/src/VbaDev.Domain/VbaDev.Domain.csproj',
    'tools/vba-dev/src/VbaDev.Infrastructure/VbaDev.Infrastructure.csproj',
    'tools/vba-dev/tests/VbaDev.Tests/VbaDev.Tests.csproj',
    'tools/vba-language-server/src/VbaLanguageServer.Cli/VbaLanguageServer.Cli.csproj',
    'tools/vba-language-server/src/VbaLanguageServer.Syntax/VbaLanguageServer.Syntax.csproj',
    'tools/vba-language-server/tests/VbaLanguageServer.Syntax.Tests/VbaLanguageServer.Syntax.Tests.csproj',
    'tools/vba-language-server/tests/VbaLanguageServer.Tests/VbaLanguageServer.Tests.csproj'
  ];
  for (const projectPath of dotnetProjects) {
    const lockPath = path.join(path.dirname(projectPath), 'packages.lock.json');
    const dependencyLock = await readJson(repositoryRoot, lockPath);
    assert.equal(dependencyLock.version, 1, lockPath);
    assert.ok(dependencyLock.dependencies['net10.0'], lockPath);
  }
  for (const propsPath of [
    'tools/vba-dev/Directory.Build.props',
    'tools/vba-language-server/Directory.Build.props'
  ]) {
    const props = await fs.readFile(path.join(repositoryRoot, propsPath), 'utf8');
    assert.match(props, /<RestorePackagesWithLockFile>true<\/RestorePackagesWithLockFile>/);
    assert.match(props, /<RestoreLockedMode>true<\/RestoreLockedMode>/);
  }
  const dependabot = await fs.readFile(
    path.join(repositoryRoot, '.github/dependabot.yml'),
    'utf8'
  );
  assert.match(dependabot, /package-ecosystem: "npm"/);
  assert.match(dependabot, /package-ecosystem: "github-actions"/);
  const cliChangelog = await fs.readFile(
    path.join(repositoryRoot, 'tools/vba-dev/CHANGELOG.md'),
    'utf8'
  );
  assert.match(cliChangelog, /^## \[0\.1\.0\] - Unreleased/m);
  assert.match(cliChangelog, /standalone Windows x64/i);
  assert.match(cliChangelog, /capabilities/i);
  assert.match(cliChangelog, /debug adapter/i);
});

test('release preparation starts clean and updates extension metadata without coupling the CLI version', async (t) => {
  const root = await createReleaseRepository(t);

  const result = await prepareRelease({
    root,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    releaseDate: '2026-07-22'
  });

  assert.deepEqual(result, {
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    cliVersionChanged: false,
    expectedArtifacts: [
      'vba-tools-win32-x64-0.1.0.vsix',
      'vba-dev-win-x64-0.1.0.zip',
      'SHA256SUMS'
    ]
  });
  const packageJson = await readJson(root, 'package.json');
  const packageLock = await readJson(root, 'package-lock.json');
  assert.equal(packageJson.version, '0.1.0');
  assert.equal(packageLock.version, '0.1.0');
  assert.equal(packageLock.packages[''].version, '0.1.0');
  assert.match(
    await fs.readFile(path.join(root, 'tools/vba-dev/Directory.Build.props'), 'utf8'),
    /<VbaDevReleaseVersion>0\.1\.0<\/VbaDevReleaseVersion>/
  );
  assert.match(await fs.readFile(path.join(root, 'CHANGELOG.md'), 'utf8'), /## \[0\.1\.0\] - 2026-07-22/);
  const cliChangelog = await fs.readFile(path.join(root, 'tools/vba-dev/CHANGELOG.md'), 'utf8');
  assert.match(cliChangelog, /## \[0\.1\.0\] - 2026-07-22/);
  assert.equal((cliChangelog.match(/## \[0\.1\.0\]/g) ?? []).length, 1);

  await assert.rejects(
    () => prepareRelease({
      root,
      extensionVersion: '0.1.1',
      channel: 'pre-release',
      vbaDevVersion: '0.1.0',
      releaseDate: '2026-07-23'
    }),
    /clean worktree/i
  );
});

test('release preparation generates a new CLI history only when its independent version changes', async (t) => {
  const root = await createReleaseRepository(t);
  await run('git', ['tag', '-a', 'vba-dev-v0.1.0', '-m', 'vba-dev 0.1.0'], root);
  await write(root, 'cli-change.txt', 'deterministic output\n');
  await run('git', ['add', '.'], root);
  await run('git', ['commit', '-m', 'feat(vba-dev): add deterministic output'], root);

  const result = await prepareRelease({
    root,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.2.0',
    releaseDate: '2026-07-22'
  });

  assert.equal(result.cliVersionChanged, true);
  assert.match(
    await fs.readFile(path.join(root, 'tools/vba-dev/Directory.Build.props'), 'utf8'),
    /<VbaDevReleaseVersion>0\.2\.0<\/VbaDevReleaseVersion>/
  );
  const cliChangelog = await fs.readFile(path.join(root, 'tools/vba-dev/CHANGELOG.md'), 'utf8');
  assert.match(cliChangelog, /## \[0\.2\.0\] - 2026-07-22/);
  assert.match(cliChangelog, /### Added[\s\S]*- Add deterministic output\./);
  assert.match(cliChangelog, /## \[0\.1\.0\] - Unreleased/);
  assert.equal((cliChangelog.match(/Add deterministic output/g) ?? []).length, 1);
});

test('release preparation leaves an already released unchanged CLI changelog section untouched', async (t) => {
  const root = await createReleaseRepository(t);
  const cliChangelogPath = path.join(root, 'tools/vba-dev/CHANGELOG.md');
  const changelog = await fs.readFile(cliChangelogPath, 'utf8');
  await fs.writeFile(
    cliChangelogPath,
    changelog.replace('[0.1.0] - Unreleased', '[0.1.0] - 2026-01-15')
  );
  await commitAll(root, 'docs(vba-dev): release 0.1.0');

  await prepareInitialRelease(root);

  const updated = await fs.readFile(cliChangelogPath, 'utf8');
  assert.match(updated, /## \[0\.1\.0\] - 2026-01-15/);
  assert.doesNotMatch(updated, /## \[0\.1\.0\] - 2026-07-22/);
});

test('release preparation rejects inconsistent metadata and conflicting namespace tags', async (t) => {
  await t.test('extension package and lock disagree', async (subtest) => {
    const root = await createReleaseRepository(subtest);
    const packageLock = await readJson(root, 'package-lock.json');
    packageLock.packages[''].version = '9.9.9';
    await write(root, 'package-lock.json', `${JSON.stringify(packageLock, null, 2)}\n`);
    await commitAll(root, 'test: make lock inconsistent');
    await assert.rejects(() => prepareInitialRelease(root), /inconsistent extension version/i);
  });

  await t.test('CLI version properties are not bound to the canonical source', async (subtest) => {
    const root = await createReleaseRepository(subtest);
    const propsPath = path.join(root, 'tools/vba-dev/Directory.Build.props');
    const props = await fs.readFile(propsPath, 'utf8');
    await fs.writeFile(propsPath, props.replace(
      '<InformationalVersion>$(VbaDevReleaseVersion)</InformationalVersion>',
      '<InformationalVersion>9.9.9</InformationalVersion>'
    ));
    await commitAll(root, 'test: make CLI metadata inconsistent');
    await assert.rejects(() => prepareInitialRelease(root), /inconsistent vba-dev version/i);
  });

  await t.test('extension tag already exists', async (subtest) => {
    const root = await createReleaseRepository(subtest);
    await run('git', ['tag', 'vba-tools-v0.1.0'], root);
    await assert.rejects(() => prepareInitialRelease(root), /tag vba-tools-v0\.1\.0 already exists/i);
  });

  await t.test('new CLI tag already exists', async (subtest) => {
    const root = await createReleaseRepository(subtest);
    await run('git', ['tag', 'vba-dev-v0.2.0'], root);
    await assert.rejects(() => prepareRelease({
      root,
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '0.2.0',
      releaseDate: '2026-07-22'
    }), /tag vba-dev-v0\.2\.0 already exists/i);
  });
});

test('release tag records reviewed commit and Windows evidence in a local annotated tag', async (t) => {
  const root = await createReleaseRepository(t);
  await prepareInitialRelease(root);
  await commitAll(root, 'chore(release): prepare 0.1.0');
  const verifiedCommit = (await run('git', ['rev-parse', 'HEAD'], root)).stdout.trim();
  await run('git', ['update-ref', 'refs/remotes/origin/main', verifiedCommit], root);

  const result = await createReleaseTag({
    root,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    verifiedCommit,
    windowsExcelResult: 'pass',
    cleanWindowsSmoke: 'pass'
  });

  assert.deepEqual(result, {
    tagName: 'vba-tools-v0.1.0',
    targetCommit: verifiedCommit,
    pushed: false
  });
  const tagType = (await run('git', ['cat-file', '-t', 'vba-tools-v0.1.0'], root)).stdout.trim();
  const tagObject = (await run('git', ['cat-file', '-p', 'vba-tools-v0.1.0'], root)).stdout;
  assert.equal(tagType, 'tag');
  assert.match(tagObject, /VBA Tools 0\.1\.0/);
  assert.match(tagObject, /Channel: pre-release/);
  assert.match(tagObject, new RegExp(`Windows-Excel-Verification-Commit: ${verifiedCommit}`));
  assert.match(tagObject, /Windows-Excel-Verification-Result: pass/);
  assert.match(tagObject, /Clean-Windows-Smoke: pass/);
  await assert.rejects(() => createReleaseTag({
    root,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    verifiedCommit,
    windowsExcelResult: 'pass',
    cleanWindowsSmoke: 'pass'
  }), /tag vba-tools-v0\.1\.0 already exists/i);
});

test('initial release tag fails closed on incomplete evidence or an unreviewed commit', async (t) => {
  await t.test('worktree is dirty', async (subtest) => {
    const { root, verifiedCommit } = await createPreparedReleaseCommit(subtest);
    await fs.writeFile(path.join(root, 'dirty.txt'), 'unreviewed\n');
    await assert.rejects(() => createReleaseTag({
      root,
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '0.1.0',
      verifiedCommit,
      windowsExcelResult: 'pass',
      cleanWindowsSmoke: 'pass'
    }), /clean worktree/i);
  });

  await t.test('Windows Excel result is not pass', async (subtest) => {
    const { root, verifiedCommit } = await createPreparedReleaseCommit(subtest);
    await assert.rejects(() => createReleaseTag({
      root,
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '0.1.0',
      verifiedCommit,
      windowsExcelResult: 'not-run',
      cleanWindowsSmoke: 'pass'
    }), /Windows Excel.*pass/i);
  });

  await t.test('verified commit differs from HEAD', async (subtest) => {
    const { root } = await createPreparedReleaseCommit(subtest);
    await assert.rejects(() => createReleaseTag({
      root,
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '0.1.0',
      verifiedCommit: '0000000000000000000000000000000000000000',
      windowsExcelResult: 'pass',
      cleanWindowsSmoke: 'pass'
    }), /verified commit.*HEAD/i);
  });

  await t.test('reviewed CLI metadata is internally inconsistent', async (subtest) => {
    const root = await createReleaseRepository(subtest);
    await prepareInitialRelease(root);
    const propsPath = path.join(root, 'tools/vba-dev/Directory.Build.props');
    const props = await fs.readFile(propsPath, 'utf8');
    await fs.writeFile(propsPath, props.replace(
      '<PackageVersion>$(VbaDevReleaseVersion)</PackageVersion>',
      '<PackageVersion>9.9.9</PackageVersion>'
    ));
    await commitAll(root, 'test: make reviewed CLI metadata inconsistent');
    const verifiedCommit = (await run('git', ['rev-parse', 'HEAD'], root)).stdout.trim();
    await run('git', ['update-ref', 'refs/remotes/origin/main', verifiedCommit], root);
    await assert.rejects(() => createReleaseTag({
      root,
      extensionVersion: '0.1.0',
      channel: 'pre-release',
      vbaDevVersion: '0.1.0',
      verifiedCommit,
      windowsExcelResult: 'pass',
      cleanWindowsSmoke: 'pass'
    }), /reviewed vba-dev metadata/i);
  });
});

test('artifact assembly emits one internally consistent pre-release set in an empty directory', async (t) => {
  const root = await createReleaseRepository(t);
  await prepareInitialRelease(root);
  const outputDirectory = path.join(root, 'release-set');
  const calls = [];
  const runCommand = async (file, args) => {
    calls.push({ file: path.basename(file), args });
    if (args.includes('package')) {
      const outputIndex = args.indexOf('--out');
      await fs.writeFile(args[outputIndex + 1], 'targeted pre-release VSIX');
    }
    return { stdout: '', stderr: '' };
  };
  let archiveCleaned = false;
  const createCliArchive = async ({ outputDirectory: output }) => {
    const archivePath = path.join(output, 'vba-dev-win-x64-0.1.0.zip');
    await fs.writeFile(archivePath, 'exact bundled CLI archive');
    return {
      archivePath,
      version: '0.1.0',
      cleanup: async () => { archiveCleaned = true; }
    };
  };
  const validatedPackages = [];

  const result = await assembleReleaseArtifacts({
    root,
    outputDirectory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand,
    createCliArchive,
    validateVsix: async (options) => { validatedPackages.push(options); }
  });

  assert.equal(archiveCleaned, true);
  assert.deepEqual((await fs.readdir(outputDirectory)).sort(), [
    'SHA256SUMS',
    'vba-dev-win-x64-0.1.0.zip',
    'vba-tools-win32-x64-0.1.0.vsix'
  ]);
  assert.equal(path.basename(result.vsixPath), 'vba-tools-win32-x64-0.1.0.vsix');
  assert.equal(path.basename(result.cliArchivePath), 'vba-dev-win-x64-0.1.0.zip');
  assert.equal(path.basename(result.checksumPath), 'SHA256SUMS');
  assert.equal(calls[0].file, path.basename(process.execPath));
  assert.match(calls[0].args[0].replaceAll('\\', '/'), /npm\/bin\/npm-cli\.js$/);
  assert.deepEqual(calls[0].args.slice(1), ['run', 'verify:release']);
  const packageCall = calls.find(({ args }) => args.includes('package'));
  assert.ok(packageCall.args.includes('--target'));
  assert.ok(packageCall.args.includes('win32-x64'));
  assert.ok(packageCall.args.includes('--pre-release'));
  assert.deepEqual(validatedPackages, [{
    root,
    vsixPath: result.vsixPath,
    extensionVersion: '0.1.0',
    channel: 'pre-release'
  }]);
  const checksumLines = (await fs.readFile(result.checksumPath, 'utf8')).trimEnd().split('\n');
  assert.equal(checksumLines.length, 2);
  assert.match(checksumLines[0], /^[0-9a-f]{64}  vba-dev-win-x64-0\.1\.0\.zip$/);
  assert.match(checksumLines[1], /^[0-9a-f]{64}  vba-tools-win32-x64-0\.1\.0\.vsix$/);
});

test('artifact assembly rejects a non-empty staging directory before running verification', async (t) => {
  const root = await createReleaseRepository(t);
  await prepareInitialRelease(root);
  const outputDirectory = path.join(root, 'release-set');
  await fs.mkdir(outputDirectory);
  await fs.writeFile(path.join(outputDirectory, 'stale.zip'), 'stale');
  let commandRan = false;

  await assert.rejects(() => assembleReleaseArtifacts({
    root,
    outputDirectory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: async () => {
      commandRan = true;
      return { stdout: '', stderr: '' };
    }
  }), /staging directory.*empty/i);
  assert.equal(commandRan, false);
});

test('VSIX release-channel metadata distinguishes pre-release from stable packages', () => {
  const preReleaseManifest = `
    <PackageManifest>
      <Metadata>
        <Properties>
          <Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="true" />
        </Properties>
      </Metadata>
    </PackageManifest>`;
  const stableManifest = '<PackageManifest><Metadata><Properties /></Metadata></PackageManifest>';

  assert.doesNotThrow(() => assertVsixReleaseChannel(preReleaseManifest, 'pre-release'));
  assert.doesNotThrow(() => assertVsixReleaseChannel(stableManifest, 'stable'));
  assert.throws(() => assertVsixReleaseChannel(preReleaseManifest, 'stable'), /stable Marketplace channel/i);
  assert.throws(() => assertVsixReleaseChannel(stableManifest, 'pre-release'), /pre-release Marketplace channel/i);
});

test('release-set verification rejects duplicate checksum entries', async (t) => {
  const outputDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-checksum-set-'));
  t.after(() => fs.rm(outputDirectory, { recursive: true, force: true }));
  const cliArchivePath = path.join(outputDirectory, 'vba-dev-win-x64-0.1.0.zip');
  const vsixPath = path.join(outputDirectory, 'vba-tools-win32-x64-0.1.0.vsix');
  await fs.writeFile(cliArchivePath, 'cli');
  await fs.writeFile(vsixPath, 'vsix');
  const checksumPath = await writeReleaseChecksums(
    outputDirectory,
    [cliArchivePath, vsixPath]
  );
  const checksums = await fs.readFile(checksumPath, 'utf8');
  await fs.writeFile(checksumPath, `${checksums}${checksums.trimEnd().split('\n')[1]}\n`);

  await assert.rejects(() => verifyReleaseArtifactSet({
    outputDirectory,
    extensionVersion: '0.1.0',
    vbaDevVersion: '0.1.0'
  }), /SHA256SUMS must describe exactly/i);
});

async function createReleaseRepository(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-release-management-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  await write(root, 'package.json', `${JSON.stringify({
    name: 'vba-tools',
    version: '0.0.1'
  }, null, 2)}\n`);
  await write(root, 'package-lock.json', `${JSON.stringify({
    name: 'vba-tools',
    version: '0.0.1',
    lockfileVersion: 3,
    packages: { '': { name: 'vba-tools', version: '0.0.1' } }
  }, null, 2)}\n`);
  await write(root, 'tools/vba-dev/Directory.Build.props', `<Project>\n  <PropertyGroup>\n    <VbaDevReleaseVersion>0.1.0</VbaDevReleaseVersion>\n    <Version>$(VbaDevReleaseVersion)</Version>\n    <VersionPrefix>$(VbaDevReleaseVersion)</VersionPrefix>\n    <PackageVersion>$(VbaDevReleaseVersion)</PackageVersion>\n    <InformationalVersion>$(VbaDevReleaseVersion)</InformationalVersion>\n  </PropertyGroup>\n</Project>\n`);
  await write(root, 'CHANGELOG.md', '# Changelog\n\n## [0.1.0] - Unreleased\n\n### Added\n\n- Curated extension summary.\n');
  await write(root, 'tools/vba-dev/CHANGELOG.md', '# Changelog\n\n## [0.1.0] - Unreleased\n\n### Added\n\n- Curated CLI summary.\n');
  await run('git', ['init'], root);
  await run('git', ['config', 'user.name', 'Release Test'], root);
  await run('git', ['config', 'user.email', 'release-test@example.com'], root);
  await run('git', ['add', '.'], root);
  await run('git', ['commit', '-m', 'chore: create release fixture'], root);
  return root;
}

function prepareInitialRelease(root) {
  return prepareRelease({
    root,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    releaseDate: '2026-07-22'
  });
}

async function commitAll(root, message) {
  await run('git', ['add', '.'], root);
  await run('git', ['commit', '-m', message], root);
}

async function createPreparedReleaseCommit(t) {
  const root = await createReleaseRepository(t);
  await prepareInitialRelease(root);
  await commitAll(root, 'chore(release): prepare 0.1.0');
  const verifiedCommit = (await run('git', ['rev-parse', 'HEAD'], root)).stdout.trim();
  await run('git', ['update-ref', 'refs/remotes/origin/main', verifiedCommit], root);
  return { root, verifiedCommit };
}

async function readJson(root, relativePath) {
  return JSON.parse(await fs.readFile(path.join(root, relativePath), 'utf8'));
}

async function write(root, relativePath, contents) {
  const filePath = path.join(root, relativePath);
  await fs.mkdir(path.dirname(filePath), { recursive: true });
  await fs.writeFile(filePath, contents, 'utf8');
}

function run(file, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk.toString('utf8'); });
    child.stderr.on('data', (chunk) => { stderr += chunk.toString('utf8'); });
    child.on('error', reject);
    child.on('exit', (exitCode) => {
      if (exitCode !== 0) {
        reject(new Error(`${file} ${args.join(' ')} exited with ${exitCode}: ${stderr}`));
        return;
      }
      resolve({ stdout, stderr });
    });
  });
}
