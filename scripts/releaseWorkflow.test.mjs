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
  const debugAdapterExecutable = Buffer.from('independent vba-debug-adapter executable');
  const languageServerExecutable = Buffer.from('independent vba-language-server executable');
  const contract = JSON.parse(await fs.readFile('vba-dev-contract.json', 'utf8'));
  const debugAdapterContract = JSON.parse(
    await fs.readFile('vba-debug-adapter-contract.json', 'utf8')
  );
  const commandCapabilities = Object.fromEntries(
    Object.entries(contract.commandSchemaVersions)
      .map(([name, outputSchemaVersion]) => [name, { outputSchemaVersion }])
  );
  await writeTestVsix(vsixPath, {
    contract,
    debugAdapterContract,
    executable,
    debugAdapterExecutable,
    languageServerExecutable
  });
  await writeZip(cliArchivePath, new Map([
    ['vba-dev.exe', executable],
    ['vba-dev.pdb', 'symbols'],
    ['README.md', '# vba-dev\n'],
    ['LICENSE', 'MIT\n'],
    ['vba-dev-contract.json', JSON.stringify(contract)]
  ]));
  await writeReleaseChecksums(directory, [vsixPath, cliArchivePath]);
  const probes = [];
  const runCommand = async (file, args) => {
    probes.push(args);
    if (path.basename(file) === 'vba-debug-adapter.exe') {
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          ...debugAdapterContract
        }),
        stderr: ''
      };
    }
    if (path.basename(file) === 'vba-language-server.exe') {
      return { stdout: 'vba-language-server 0.1.0\n', stderr: '' };
    }
    if (args[0] === '--version') {
      return { stdout: 'vba-dev 0.1.0\n', stderr: '' };
    }
    if (args[0] === '--help') {
      return {
        stdout: 'vba-dev [command] [options]\n  build  Build a project\n  capabilities  Show capabilities\n  --help\n',
        stderr: ''
      };
    }
    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '1.0',
        commands: commandCapabilities,
        featureVersions: contract.featureVersions,
        activeWindowsCodePage: 932
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
    ['capabilities', '--format', 'json'],
    ['capabilities', '--format', 'json'],
    ['--version']
  ]);

  const missingCodePageProbe = async (_file, args) => {
    if (args[0] === '--version') {
      return { stdout: 'vba-dev 0.1.0\n', stderr: '' };
    }
    if (args[0] === '--help') {
      return { stdout: 'Usage:\n  vba-dev [command] [options]\n  build  Build a project\n  capabilities  Show capabilities\n  --help\n', stderr: '' };
    }
    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: '1.0',
        commands: commandCapabilities,
        featureVersions: contract.featureVersions
      }),
      stderr: ''
    };
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: missingCodePageProbe
  }), /active Windows code page/i);

  const incompatibleAdapterProbe = async (file, args, cwd) => {
    if (path.basename(file) === 'vba-debug-adapter.exe') {
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          ...debugAdapterContract,
          protocolVersion: '9.9'
        }),
        stderr: ''
      };
    }
    return runCommand(file, args, cwd);
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: incompatibleAdapterProbe
  }), /debug-adapter capabilities/i);

  const incompatibleLanguageServerProbe = async (file, args, cwd) => {
    if (path.basename(file) === 'vba-language-server.exe') {
      return { stdout: 'unknown-server 0.1.0\n', stderr: '' };
    }
    return runCommand(file, args, cwd);
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: incompatibleLanguageServerProbe
  }), /language-server.*version/i);

  const driftedDebugAdapterVersionProbe = async (file, args, cwd) => {
    if (path.basename(file) === 'vba-debug-adapter.exe') {
      return {
        stdout: JSON.stringify({
          toolVersion: '9.9.9',
          ...debugAdapterContract
        }),
        stderr: ''
      };
    }
    return runCommand(file, args, cwd);
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: driftedDebugAdapterVersionProbe
  }), /vba-debug-adapter.*0\.1\.0/i);

  const driftedLanguageServerVersionProbe = async (file, args, cwd) => {
    if (path.basename(file) === 'vba-language-server.exe') {
      return { stdout: 'vba-language-server 9.9.9\n', stderr: '' };
    }
    return runCommand(file, args, cwd);
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: driftedLanguageServerVersionProbe
  }), /vba-language-server.*0\.1\.0/i);

  const driftedDebugAdapterContract = {
    ...debugAdapterContract,
    protocolVersion: '9.9'
  };
  await writeTestVsix(vsixPath, {
    contract,
    debugAdapterContract: driftedDebugAdapterContract,
    executable,
    debugAdapterExecutable,
    languageServerExecutable
  });
  await writeReleaseChecksums(directory, [vsixPath, cliArchivePath]);
  const selfConsistentDriftProbe = async (file, args, cwd) => {
    if (path.basename(file) === 'vba-debug-adapter.exe') {
      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          ...driftedDebugAdapterContract
        }),
        stderr: ''
      };
    }
    return runCommand(file, args, cwd);
  };
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand: selfConsistentDriftProbe
  }), /reviewed vba-debug-adapter contract/i);

  await writeTestVsix(vsixPath, {
    contract,
    debugAdapterContract,
    executable,
    debugAdapterExecutable,
    languageServerExecutable,
    omittedEntry: 'extension/schemas/project-manifest.schema.json'
  });
  await writeReleaseChecksums(directory, [vsixPath, cliArchivePath]);
  await assert.rejects(() => validateStagedReleaseAssets({
    directory,
    extensionVersion: '0.1.0',
    channel: 'pre-release',
    vbaDevVersion: '0.1.0',
    runCommand
  }), /must include.*project-manifest/i);

  await writeTestVsix(vsixPath, {
    contract,
    debugAdapterContract,
    executable,
    debugAdapterExecutable,
    languageServerExecutable
  });

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

  const checkoutSteps = Object.values(workflow.jobs)
    .flatMap((job) => job.steps)
    .filter((step) => step.uses?.startsWith('actions/checkout@'));
  assert.ok(checkoutSteps.length >= 4);
  assert.ok(checkoutSteps.every((step) => step.with?.['persist-credentials'] === false));
  assert.ok(Object.values(workflow.jobs).every((job) => job['runs-on'] === 'windows-2025'));

  const validateTagStep = workflow.jobs.validate.steps.find(
    (step) => step.name === 'Resolve and validate the annotated tag'
  );
  assert.match(validateTagStep.run, /EVENT_REF/);
  assert.match(validateTagStep.run, /refs\/tags\/\$env:DISPATCH_TAG/);

  const buildAttestations = workflow.jobs.build.steps.filter(
    (step) => step.uses?.startsWith('actions/attest-build-provenance@')
  );
  const publishAttestations = workflow.jobs.publish.steps.filter(
    (step) => step.uses?.startsWith('actions/attest-build-provenance@')
  );
  assert.equal(buildAttestations.length, 0);
  assert.equal(publishAttestations.length, 2);
  assert.ok(publishAttestations.every(
    (step) => step.if === "github.event_name == 'push'"
  ));

  const publishSteps = workflow.jobs.publish.steps;
  const currentRunDownloadIndex = publishSteps.findIndex(
    (step) => step.name === 'Download the immutable current-run release set'
  );
  const draftBindingIndex = publishSteps.findIndex(
    (step) => step.name === 'Bind the initial draft to the current-run artifact'
  );
  const provenanceIndex = publishSteps.findIndex(
    (step) => step.name === 'Verify build provenance before inspecting executables'
  );
  const draftValidationIndex = publishSteps.findIndex(
    (step) => step.name === 'Fail-closed validate the existing draft'
  );
  const authenticationIndex = publishSteps.findIndex(
    (step) => step.name === 'Authenticate with the publishing managed identity'
  );
  assert.ok(currentRunDownloadIndex >= 0);
  assert.equal(publishSteps[currentRunDownloadIndex].if, "github.event_name == 'push'");
  assert.ok(draftBindingIndex > currentRunDownloadIndex);
  assert.equal(publishSteps[draftBindingIndex].if, "github.event_name == 'push'");
  assert.match(publishSteps[draftBindingIndex].run, /Get-FileHash/);
  assert.match(publishSteps[draftBindingIndex].run, /Actions artifact and draft release bytes differ/);
  assert.ok(publishAttestations.every(
    (step) => publishSteps.indexOf(step) > draftBindingIndex &&
      publishSteps.indexOf(step) < provenanceIndex
  ));
  assert.ok(provenanceIndex >= 0);
  assert.ok(provenanceIndex < draftValidationIndex);
  assert.ok(draftValidationIndex < authenticationIndex);
  const provenanceSource = publishSteps[provenanceIndex].run;
  assert.match(provenanceSource, /--signer-workflow modern-vba\/vba-tools\/\.github\/workflows\/release\.yml/);
  assert.match(provenanceSource, /--source-ref "refs\/tags\/\$env:TAG_NAME"/);
  assert.match(provenanceSource, /--source-digest \$env:TARGET_COMMIT/);
  assert.match(provenanceSource, /--deny-self-hosted-runners/);

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
  assert.match(source, /Get-Content -Raw -LiteralPath 'CHANGELOG\.md'/);
  assert.match(source, /VbaDebugAdapterReleaseVersion/);
  assert.match(source, /vba-debug-adapter-contract\.json/);
  assert.match(source, /VbaLanguageServer\.Cli\/Program\.cs/);
  assert.match(source, /## Bundled Tools/);
  assert.match(source, /modern-vba\.vba-tools@\$env:EXTENSION_VERSION/);
  assert.match(source, /if \(\$env:CHANNEL -eq 'pre-release'\)[\s\S]+--pre-release/);
  assert.doesNotMatch(
    source,
    /code --install-extension modern-vba\.vba-tools --pre-release/
  );
  assert.doesNotMatch(source, /VSCE_PAT|client-secret|secrets\.|ubuntu-/i);

  const releaseGuide = await fs.readFile(path.resolve('docs/release.md'), 'utf8');
  assert.match(
    releaseGuide,
    /gh workflow run release\.yml --ref vba-tools-vX\.Y\.Z -f release_tag=vba-tools-vX\.Y\.Z/
  );
  assert.doesNotMatch(releaseGuide, /gh workflow run release\.yml --ref main/);
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

async function writeTestVsix(filePath, {
  contract,
  debugAdapterContract,
  executable,
  debugAdapterExecutable,
  languageServerExecutable,
  omittedEntry
}) {
  const packageJson = JSON.stringify({
    name: 'vba-tools',
    publisher: 'modern-vba',
    version: '0.1.0'
  });
  const distributionManifest = await fs.readFile('distribution-manifest.json', 'utf8');
  const entries = new Map([
    ['extension/readme.md', '# VBA Tools\n'],
    ['extension/changelog.md', '# Changelog\n'],
    ['extension/LICENSE.txt', 'MIT\n'],
    ['extension/SUPPORT.md', '# Support\n'],
    ['extension/schemas/project-manifest.schema.json', '{}\n'],
    ['extension/distribution-manifest.json', distributionManifest],
    ['extension/assets/icon.png', Buffer.from('icon')],
    ['extension/package.json', packageJson],
    ['extension/client/out/extension.js', 'export {};\n'],
    ['extension/bin/vba-dev/win-x64/vba-dev.exe', executable],
    ['extension/vba-dev-contract.json', JSON.stringify(contract)],
    ['extension/bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe', debugAdapterExecutable],
    ['extension/vba-debug-adapter-contract.json', JSON.stringify(debugAdapterContract)],
    ['extension/bin/vba-language-server/win-x64/vba-language-server.exe', languageServerExecutable],
    ['extension.vsixmanifest', '<PackageManifest><Metadata><Identity Publisher="modern-vba" Version="0.1.0" TargetPlatform="win32-x64" /><Properties><Property Id="Microsoft.VisualStudio.Code.PreRelease" Value="true" /></Properties></Metadata></PackageManifest>']
  ]);
  if (omittedEntry) {
    entries.delete(omittedEntry);
  }
  await writeZip(filePath, entries);
}
