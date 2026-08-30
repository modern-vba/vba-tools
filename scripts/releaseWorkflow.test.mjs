import test from 'node:test';
import assert from 'node:assert/strict';
import { createWriteStream, promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import yaml from 'js-yaml';
import yazl from 'yazl';

import {
  assertMarketplaceVisibility,
  validateAnnotatedReleaseTag,
  validateDraftReleaseMetadata,
  validateStagedReleaseAssets
} from './releaseWorkflow.mjs';
import { writeReleaseChecksums } from './vbaDevReleasePackage.mjs';

const targetCommit = 'a'.repeat(40);
const validTagObject = `object ${targetCommit}
type commit
tag vba-tools-v0.1.0
tagger Release Maintainer <maintainer@example.com> 1784754000 +0900

VBA Tools 0.1.0

Channel: pre-release
Windows-Excel-Verification-Commit: ${targetCommit}
Windows-Excel-Verification-Result: pass
Clean-Windows-Smoke: pass
`;
const packageJson = { name: 'vba-tools', publisher: 'modern-vba', version: '0.1.0' };
const packageLock = {
  version: '0.1.0',
  packages: { '': { version: '0.1.0' } }
};
const vbaDevProps = `<Project>
  <PropertyGroup>
    <VbaDevReleaseVersion>0.1.0</VbaDevReleaseVersion>
    <Version>$(VbaDevReleaseVersion)</Version>
    <VersionPrefix>$(VbaDevReleaseVersion)</VersionPrefix>
    <PackageVersion>$(VbaDevReleaseVersion)</PackageVersion>
    <InformationalVersion>$(VbaDevReleaseVersion)</InformationalVersion>
  </PropertyGroup>
</Project>
`;

test('annotated release tag binds reviewed versions channel and Windows evidence to one commit', () => {
  assert.deepEqual(validateAnnotatedReleaseTag({
    tagName: 'vba-tools-v0.1.0',
    tagType: 'tag',
    tagObject: validTagObject,
    packageJson,
    packageLock,
    vbaDevProps
  }), {
    tagName: 'vba-tools-v0.1.0',
    targetCommit,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    releaseTitle: 'VBA Tools 0.1.0',
    vsixName: 'vba-tools-win32-x64-0.1.0.vsix',
    cliArchiveName: 'vba-dev-win-x64-0.1.0.zip'
  });
});

test('release tag validation rejects lightweight tags unknown evidence and cross-version metadata', () => {
  const valid = {
    tagName: 'vba-tools-v0.1.0',
    tagType: 'tag',
    tagObject: validTagObject,
    packageJson,
    packageLock,
    vbaDevProps
  };
  assert.throws(
    () => validateAnnotatedReleaseTag({ ...valid, tagType: 'commit' }),
    /annotated tag/i
  );
  assert.throws(
    () => validateAnnotatedReleaseTag({
      ...valid,
      tagObject: `${validTagObject}Unexpected-Evidence: pass\n`
    }),
    /unrecognized tag trailer/i
  );
  assert.throws(
    () => validateAnnotatedReleaseTag({
      ...valid,
      packageLock: { ...packageLock, version: '9.9.9' }
    }),
    /extension version metadata/i
  );
  assert.throws(
    () => validateAnnotatedReleaseTag({
      ...valid,
      tagObject: validTagObject.replace(
        'Windows-Excel-Verification-Result: pass',
        'Windows-Excel-Verification-Result: fail'
      )
    }),
    /Windows Excel verification.*pass/i
  );
});

test('Marketplace visibility requires the exact publisher extension version target and channel', () => {
  const metadata = {
    publisher: { publisherName: 'modern-vba' },
    extensionName: 'vba-tools',
    versions: [{
      version: '0.1.0',
      targetPlatform: 'win32-x64',
      properties: [{
        key: 'Microsoft.VisualStudio.Code.PreRelease',
        value: 'true'
      }]
    }]
  };
  assert.deepEqual(assertMarketplaceVisibility(metadata, {
    publisher: 'modern-vba',
    extensionName: 'vba-tools',
    version: '0.1.0',
    targetPlatform: 'win32-x64',
    channel: 'pre-release'
  }), metadata.versions[0]);

  assert.throws(() => assertMarketplaceVisibility({
    ...metadata,
    publisher: { publisherName: 'other' }
  }, {
    publisher: 'modern-vba',
    extensionName: 'vba-tools',
    version: '0.1.0',
    targetPlatform: 'win32-x64',
    channel: 'pre-release'
  }), /publisher.*modern-vba/i);
  assert.throws(() => assertMarketplaceVisibility(metadata, {
    publisher: 'modern-vba',
    extensionName: 'vba-tools',
    version: '0.1.0',
    targetPlatform: 'linux-x64',
    channel: 'pre-release'
  }), /version.*target.*channel/i);
  const stableOnly = structuredClone(metadata);
  stableOnly.versions[0].properties = [];
  assert.throws(() => assertMarketplaceVisibility(stableOnly, {
    publisher: 'modern-vba',
    extensionName: 'vba-tools',
    version: '0.1.0',
    targetPlatform: 'win32-x64',
    channel: 'pre-release'
  }), /version.*target.*channel/i);
});

test('resume accepts only the existing matching draft with the exact uploaded asset set', () => {
  const expected = {
    tagName: 'vba-tools-v0.1.0',
    releaseTitle: 'VBA Tools 0.1.0',
    channel: 'pre-release',
    vsixName: 'vba-tools-win32-x64-0.1.0.vsix',
    cliArchiveName: 'vba-dev-win-x64-0.1.0.zip'
  };
  const release = {
    tag_name: expected.tagName,
    name: expected.releaseTitle,
    draft: true,
    prerelease: true,
    assets: [
      { name: expected.vsixName, state: 'uploaded', size: 100 },
      { name: expected.cliArchiveName, state: 'uploaded', size: 200 },
      { name: 'SHA256SUMS', state: 'uploaded', size: 189 }
    ]
  };
  assert.deepEqual(validateDraftReleaseMetadata(release, expected), {
    assetNames: [
      'SHA256SUMS',
      expected.cliArchiveName,
      expected.vsixName
    ]
  });
  assert.throws(
    () => validateDraftReleaseMetadata({ ...release, draft: false }, expected),
    /existing draft release/i
  );
  assert.throws(
    () => validateDraftReleaseMetadata({ ...release, assets: release.assets.slice(0, 2) }, expected),
    /exact uploaded asset set/i
  );
  assert.throws(
    () => validateDraftReleaseMetadata({
      ...release,
      assets: [...release.assets, { name: 'rebuilt.vsix', state: 'uploaded', size: 1 }]
    }, expected),
    /exact uploaded asset set/i
  );
});

test('staged asset validation binds VSIX and standalone CLI bytes metadata contract and checksums', async (t) => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-staged-assets-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  const vsixPath = path.join(directory, 'vba-tools-win32-x64-0.1.0.vsix');
  const cliArchivePath = path.join(directory, 'vba-dev-win-x64-0.1.0.zip');
  const executable = Buffer.from('exact self-contained vba-dev executable');
  const contract = {
    contractVersion: '1.0',
    commandSchemaVersions: { test: '1.0' },
    debugAdapterProtocolVersion: '1.0'
  };
  await writeZip(vsixPath, new Map([
    ['extension/package.json', JSON.stringify({
      name: 'vba-tools',
      publisher: 'modern-vba',
      version: '0.1.0'
    })],
    ['extension/bin/vba-dev/win-x64/vba-dev.exe', executable],
    ['extension.vsixmanifest', '<PackageManifest><Metadata><Identity Publisher="modern-vba" Version="0.1.0" TargetPlatform="win32-x64" /><Properties><Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="true" /></Properties></Metadata></PackageManifest>']
  ]));
  await writeZip(cliArchivePath, new Map([
    ['vba-dev.exe', executable],
    ['vba-dev.pdb', 'symbols'],
    ['README.md', '# vba-dev\n'],
    ['LICENSE', 'MIT\n'],
    ['vba-dev-contract.json', JSON.stringify(contract)]
  ]));
  await writeReleaseChecksums(directory, [vsixPath, cliArchivePath]);
  const probes = [];
  const runCommand = async (_file, args) => {
    probes.push(args);
    if (args[0] === '--version') {
      return { stdout: 'vba-dev 0.1.0\n', stderr: '' };
    }
    if (args[0] === '--help') {
      return { stdout: 'vba-dev\nUsage:\n', stderr: '' };
    }
    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '1.0',
        commands: { test: { outputSchemaVersion: '1.0' } },
        debugAdapter: { protocolVersion: '1.0' }
      }),
      stderr: ''
    };
  };

  const result = await validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand
  });

  assert.deepEqual(result, { vsixPath, cliArchivePath });
  assert.deepEqual(probes, [
    ['--version'],
    ['--help'],
    ['capabilities', '--format', 'json']
  ]);

  await writeZip(cliArchivePath, new Map([
    ['vba-dev.exe', 'different executable'],
    ['vba-dev.pdb', 'symbols'],
    ['README.md', '# vba-dev\n'],
    ['LICENSE', 'MIT\n'],
    ['vba-dev-contract.json', JSON.stringify(contract)]
  ]));
  await writeReleaseChecksums(directory, [vsixPath, cliArchivePath]);
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand
  }), /exact bundled vba-dev executable/i);
});

test('release workflow pins secretless least-privilege publish and fail-closed resume contracts', async () => {
  const workflowPath = path.resolve('.github/workflows/release.yml');
  const source = await fs.readFile(workflowPath, 'utf8');
  const workflow = yaml.load(source);
  assert.deepEqual(workflow.on.push.tags, ['vba-tools-v*']);
  assert.equal(workflow.on.workflow_dispatch.inputs.release_tag.required, true);
  assert.deepEqual(workflow.concurrency, {
    group: 'vba-tools-release',
    'cancel-in-progress': false
  });

  assert.equal(workflow.jobs.validate['runs-on'], 'windows-2025');
  assert.deepEqual(workflow.jobs.validate.permissions, { contents: 'read' });
  assert.match(workflow.jobs.build.if, /github\.event_name == 'push'/);
  assert.deepEqual(workflow.jobs.build.permissions, { contents: 'read' });
  assert.match(workflow.jobs.stage.if, /github\.event_name == 'push'/);
  assert.deepEqual(workflow.jobs.stage.permissions, { contents: 'write' });
  assert.equal(workflow.jobs.publish.environment, 'marketplace-release');
  assert.deepEqual(workflow.jobs.publish.permissions, {
    attestations: 'write',
    contents: 'write',
    'id-token': 'write'
  });
  assert.match(workflow.jobs.publish.if, /workflow_dispatch/);
  assert.match(workflow.jobs.publish.if, /needs\.build\.result == 'skipped'/);
  assert.match(workflow.jobs.publish.if, /needs\.stage\.result == 'skipped'/);

  const actionReferences = [...source.matchAll(/\buses:\s*([^\s#]+)/g)]
    .map((match) => match[1]);
  assert.ok(actionReferences.length >= 8);
  assert.ok(actionReferences.every((reference) => /^[^@]+@[0-9a-f]{40}$/.test(reference)));
  assert.deepEqual(new Set(actionReferences.map((reference) => reference.split('@')[0])), new Set([
    'actions/attest-build-provenance',
    'actions/checkout',
    'actions/download-artifact',
    'actions/setup-dotnet',
    'actions/setup-node',
    'actions/upload-artifact',
    'azure/login'
  ]));

  assert.equal((source.match(/npm run release:artifacts/g) ?? []).length, 1);
  assert.match(source, /authorized vba-tools release tag creation/);
  assert.match(source, /keep vba-tools release tags immutable/);
  assert.match(source, /immutable-releases/);
  assert.match(source, /vsce publish --azure-credential --pre-release --packagePath/);
  assert.match(source, /vsce verify-pat modern-vba --azure-credential/);
  assert.match(source, /499b84ac-1321-427f-aa17-267ca6975798/);
  assert.match(source, /gh release verify/);
  assert.match(source, /gh attestation verify/);
  assert.match(source, /code --install-extension/);
  assert.doesNotMatch(source, /VSCE_PAT|client-secret|secrets\.|self-hosted|ubuntu-/i);
});

function writeZip(filePath, entries) {
  return new Promise((resolve, reject) => {
    const zipFile = new yazl.ZipFile();
    for (const [entryPath, contents] of entries) {
      zipFile.addBuffer(Buffer.isBuffer(contents) ? contents : Buffer.from(contents), entryPath);
    }
    zipFile.outputStream
      .pipe(createWriteStream(filePath))
      .on('close', resolve)
      .on('error', reject);
    zipFile.end();
  });
}
