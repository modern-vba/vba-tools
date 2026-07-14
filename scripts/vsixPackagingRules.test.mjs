import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  assertBundledLanguageServerVersion,
  assertBundledCliCapabilities,
  assertCliPublishSettings,
  assertLanguageServerPublishSettings,
  assertVsixContents,
  distributionManifestPath,
  readDistributionManifest,
  readRequiredVbaDevContract,
  requiredBundledCliPath,
  requiredBundledLanguageServerPath,
  requiredVbaDevContractPath,
  verifyVsixPackaging
} from './vsixPackagingRules.mjs';

test('VSIX content rules require the bundled CLI artifact and exclude source tree files', () => {
  assert.doesNotThrow(() => assertVsixContents([
    'README.md',
    distributionManifestPath,
    requiredBundledCliPath,
    requiredBundledLanguageServerPath,
    requiredVbaDevContractPath,
    'client/out/extension.js'
  ]));

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      'tools/vba-dev/src/VbaDev.Cli/Program.cs',
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath
    ]),
    /tools\/vba-dev/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      'tools/vba-language-server/src/VbaLanguageServer.Cli/Program.cs',
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath
    ]),
    /tools\/vba-language-server/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      requiredBundledCliPath,
      'client/out/extension.js'
    ]),
    /bin\/vba-language-server\/win-x64\/vba-language-server\.exe/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'bin/vba-language-server/win-x64/vba-language-server.dll'
    ]),
    /self-contained single executable/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'bin/vba-language-server/win-x64/vba-language-server.runtimeconfig.json'
    ]),
    /runtimeconfig/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'bin/vba-language-server/win-x64/vba-language-server.pdb'
    ]),
    /vba-language-server\.pdb/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      distributionManifestPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'server/out/server.js'
    ]),
    /server\/out\/server\.js/
  );
});

test('CLI publish settings require a Windows x64 self-contained single-file executable', () => {
  assert.doesNotThrow(() => assertCliPublishSettings(`
<Project>
  <PropertyGroup>
    <AssemblyName>vba-dev</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
`));

  assert.throws(
    () => assertCliPublishSettings(`
<Project>
  <PropertyGroup>
    <AssemblyName>vba-dev</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
`),
    /SelfContained/
  );
});

test('language server publish settings require a Windows x64 self-contained single-file executable', () => {
  assert.doesNotThrow(() => assertLanguageServerPublishSettings(`
<Project>
  <PropertyGroup>
    <AssemblyName>vba-language-server</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
`));

  assert.throws(
    () => assertLanguageServerPublishSettings(`
<Project>
  <PropertyGroup>
    <AssemblyName>vba-language-server</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
  </PropertyGroup>
</Project>
`),
    /PublishSingleFile/
  );
});

test('bundled CLI capabilities must satisfy the packaged extension contract surface', () => {
  const contract = readRequiredVbaDevContract();
  const commands = Object.fromEntries(
    Object.entries(contract.commandSchemaVersions)
      .map(([commandName, schemaVersion]) => [commandName, { outputSchemaVersion: schemaVersion }])
  );

  assert.doesNotThrow(() => assertBundledCliCapabilities(JSON.stringify({
    toolVersion: '0.1.0',
    contractVersion: contract.contractVersion,
    commands
  })));

  delete commands.doctor;
  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      toolVersion: '0.1.0',
      contractVersion: contract.contractVersion,
      commands
    })),
    /doctor/
  );
});

test('bundled language server smoke must prove the C# executable runs directly', () => {
  assert.doesNotThrow(() => assertBundledLanguageServerVersion('vba-language-server 0.1.0\n'));
  assert.throws(
    () => assertBundledLanguageServerVersion('typescript-language-server 0.1.0\n'),
    /vba-language-server/
  );
});

test('packaging verification checks file contents publish settings and bundled CLI capabilities', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-packaging-'));
  await fs.writeFile(
    path.join(root, distributionManifestPath),
    JSON.stringify(readDistributionManifest(), null, 2)
  );
  await fs.mkdir(path.join(root, 'bin', 'vba-dev', 'win-x64'), { recursive: true });
  await fs.writeFile(path.join(root, requiredBundledCliPath), '');
  await fs.mkdir(path.join(root, 'bin', 'vba-language-server', 'win-x64'), { recursive: true });
  await fs.writeFile(path.join(root, requiredBundledLanguageServerPath), '');
  await fs.writeFile(
    path.join(root, requiredVbaDevContractPath),
    JSON.stringify(readRequiredVbaDevContract(), null, 2)
  );
  await fs.mkdir(path.join(root, 'tools', 'vba-dev', 'src', 'VbaDev.Cli'), { recursive: true });
  await fs.writeFile(
    path.join(root, 'tools', 'vba-dev', 'src', 'VbaDev.Cli', 'VbaDev.Cli.csproj'),
    `
<Project>
  <PropertyGroup>
    <AssemblyName>vba-dev</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
`
  );
  await fs.mkdir(path.join(root, 'tools', 'vba-language-server', 'src', 'VbaLanguageServer.Cli'), { recursive: true });
  await fs.writeFile(
    path.join(root, 'tools', 'vba-language-server', 'src', 'VbaLanguageServer.Cli', 'VbaLanguageServer.Cli.csproj'),
    `
<Project>
  <PropertyGroup>
    <AssemblyName>vba-language-server</AssemblyName>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
  </PropertyGroup>
</Project>
`
  );

  const contract = readRequiredVbaDevContract();
  const commands = Object.fromEntries(
    Object.entries(contract.commandSchemaVersions)
      .map(([commandName, schemaVersion]) => [commandName, { outputSchemaVersion: schemaVersion }])
  );
  const calls = [];

  await verifyVsixPackaging({
    root,
    runCommand: async (file, args) => {
      calls.push({ file: path.basename(file), args });
      if (args.includes('ls')) {
        return {
          stdout: `${distributionManifestPath}\n${requiredBundledCliPath}\n${requiredBundledLanguageServerPath}\n${requiredVbaDevContractPath}\nREADME.md\n`,
          stderr: ''
        };
      }

      if (args.includes('--version')) {
        return {
          stdout: 'vba-language-server 0.1.0\n',
          stderr: ''
        };
      }

      return {
        stdout: JSON.stringify({
          toolVersion: '0.1.0',
          contractVersion: contract.contractVersion,
          commands
        }),
        stderr: ''
      };
    }
  });

  assert.deepEqual(calls.map((call) => call.args.includes('ls') ? call.args.slice(-2) : call.args), [
    ['ls', '--no-dependencies'],
    ['capabilities', '--format', 'json'],
    ['--version']
  ]);
});

test('package scripts publish the bundled CLI and verify VSIX contents before packaging', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.match(packageJson.scripts['publish:devtool'], /dotnet publish/);
  assert.match(packageJson.scripts['publish:devtool'], /-o bin\/vba-dev\/win-x64/);
  assert.match(packageJson.scripts['publish:language-server'], /dotnet publish/);
  assert.match(packageJson.scripts['publish:language-server'], /-o bin\/vba-language-server\/win-x64/);
  assert.equal(packageJson.scripts['verify:vsix'], 'node scripts/vsixPackagingRules.mjs');
  assert.match(packageJson.scripts['package:verify'], /publish:devtool/);
  assert.match(packageJson.scripts['package:verify'], /publish:language-server/);
  assert.match(packageJson.scripts['package:verify'], /verify:vsix/);
  assert.match(packageJson.scripts.package, /package:verify/);
  assert.match(packageJson.scripts.test, /test:packaging/);
  assert.deepEqual(packageJson.repository, {
    type: 'git',
    url: 'https://github.com/modern-vba/vba-tools.git'
  });
});
