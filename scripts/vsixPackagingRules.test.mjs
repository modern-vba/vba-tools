import test from 'node:test';
import assert from 'node:assert/strict';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  assertBundledLanguageServerVersion,
  assertBundledCliCapabilities,
  assertCliPublishSettings,
  assertExtensionDebugPackage,
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

const marketplaceIconPath = 'assets/icon.png';

test('extension package metadata activates the packaged VBA debug entry point dynamically', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.doesNotThrow(() => assertExtensionDebugPackage(packageJson));

  for (const incompatiblePackage of [
    { ...packageJson, main: './client/out/other.js' },
    {
      ...packageJson,
      activationEvents: packageJson.activationEvents.filter(
        (event) => event !== 'onDebugDynamicConfigurations'
      )
    },
    {
      ...packageJson,
      activationEvents: packageJson.activationEvents.filter(
        (event) => event !== 'onDebugResolve:vba'
      )
    }
  ]) {
    assert.throws(
      () => assertExtensionDebugPackage(incompatiblePackage),
      /packaged VBA debug entry point.*dynamic configuration/i
    );
  }
});

test('extension package metadata exposes the complete VBA launch selector schema', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const missingProcedureSelector = structuredClone(packageJson);
  delete missingProcedureSelector.contributes.debuggers[0]
    .configurationAttributes.launch.properties.procedure;

  assert.doesNotThrow(() => assertExtensionDebugPackage(packageJson));
  assert.throws(
    () => assertExtensionDebugPackage(missingProcedureSelector),
    /VBA launch selector schema/i
  );
});

test('extension package metadata keeps module and procedure as an atomic launch selector pair', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const independentProcedureSelector = structuredClone(packageJson);
  independentProcedureSelector.contributes.debuggers[0]
    .configurationAttributes.launch.dependencies = {};

  assert.throws(
    () => assertExtensionDebugPackage(independentProcedureSelector),
    /module and procedure.*together/i
  );
});

test('extension package metadata does not advertise unsupported VBA attach', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const packageWithAttach = structuredClone(packageJson);
  packageWithAttach.contributes.debuggers[0].configurationAttributes.attach = {
    properties: {}
  };

  assert.throws(
    () => assertExtensionDebugPackage(packageWithAttach),
    /does not support attach/i
  );
});

test('extension package metadata includes the complete user command surface', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const missingDoctorCommand = structuredClone(packageJson);
  missingDoctorCommand.contributes.commands = missingDoctorCommand.contributes.commands.filter(
    (command) => command.command !== 'vbaTools.doctor'
  );

  assert.throws(
    () => assertExtensionDebugPackage(missingDoctorCommand),
    /required extension command.*vbaTools\.doctor/i
  );
});

test('VSIX content rules require the bundled CLI artifact and exclude source tree files', () => {
  assert.doesNotThrow(() => assertVsixContents([
    'README.md',
    'package.json',
    distributionManifestPath,
    marketplaceIconPath,
    requiredBundledCliPath,
    requiredBundledLanguageServerPath,
    requiredVbaDevContractPath,
    'client/out/extension.js'
  ]));

  for (const requiredExtensionFile of [
    'package.json',
    'client/out/extension.js'
  ]) {
    assert.throws(
      () => assertVsixContents([
        'README.md',
        'package.json',
        distributionManifestPath,
        marketplaceIconPath,
        requiredBundledCliPath,
        requiredBundledLanguageServerPath,
        requiredVbaDevContractPath,
        'client/out/extension.js'
      ].filter((file) => file !== requiredExtensionFile)),
      new RegExp(requiredExtensionFile.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    );
  }

  assert.throws(
    () => assertVsixContents([
      'README.md',
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
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
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
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
      'package.json',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      'client/out/extension.js'
    ]),
    /bin\/vba-language-server\/win-x64\/vba-language-server\.exe/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
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
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
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
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
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
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'server/out/server.js'
    ]),
    /server\/out\/server\.js/
  );

  assert.throws(
    () => assertVsixContents([
      'README.md',
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'client/out/extensionHost/runTests.js'
    ]),
    /client\/out\/extensionHost/
  );

  for (const excludedFile of [
    'client/out/example.test.js',
    'client/out/example.js.map',
    'client/out/testRunner.js',
    '.tmp/old-smoke/output.bas',
    'temp/old-smoke/output.xlsm'
  ]) {
    assert.throws(
      () => assertVsixContents([
        'README.md',
        'package.json',
        'client/out/extension.js',
        distributionManifestPath,
        marketplaceIconPath,
        requiredBundledCliPath,
        requiredBundledLanguageServerPath,
        requiredVbaDevContractPath,
        excludedFile
      ]),
      new RegExp(excludedFile.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    );
  }
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
  const debugAdapter = {
    protocolVersion: contract.debugAdapterProtocolVersion,
    transport: 'stdio',
    command: 'debug-adapter'
  };

  assert.doesNotThrow(() => assertBundledCliCapabilities(JSON.stringify({
    toolVersion: '0.1.0',
    contractVersion: contract.contractVersion,
    commands,
    debugAdapter
  })));

  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      toolVersion: '0.1.0',
      contractVersion: contract.contractVersion,
      commands
    })),
    /debug adapter/
  );

  for (const incompatibleDebugAdapter of [
    { ...debugAdapter, protocolVersion: '0.9' },
    { ...debugAdapter, transport: 'socket' },
    { ...debugAdapter, command: 'other-adapter' }
  ]) {
    assert.throws(
      () => assertBundledCliCapabilities(JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: contract.contractVersion,
        commands,
        debugAdapter: incompatibleDebugAdapter
      })),
      /debug adapter/
    );
  }

  delete commands.doctor;
  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      toolVersion: '0.1.0',
      contractVersion: contract.contractVersion,
      commands,
      debugAdapter
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

test('packaging verification checks file contents publish settings and bundled CLI capabilities', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-packaging-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
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
  const extensionPackageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  await fs.writeFile(
    path.join(root, 'package.json'),
    JSON.stringify(extensionPackageJson, null, 2)
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
  const runCommand = async (file, args) => {
    calls.push({ file: path.basename(file), args });
    if (args.includes('ls')) {
      return {
        stdout: `${distributionManifestPath}\n${marketplaceIconPath}\n${requiredBundledCliPath}\n${requiredBundledLanguageServerPath}\n${requiredVbaDevContractPath}\nREADME.md\npackage.json\nclient/out/extension.js\n`,
        stderr: ''
      };
    }

    if (args.includes('--version')) {
      return {
        stdout: 'vba-language-server 0.1.0\n',
        stderr: ''
      };
    }

    if (args[0] === 'debug-adapter') {
      return { stdout: '', stderr: '' };
    }

    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: contract.contractVersion,
        commands,
        debugAdapter: {
          protocolVersion: contract.debugAdapterProtocolVersion,
          transport: 'stdio',
          command: 'debug-adapter'
        }
      }),
      stderr: ''
    };
  };

  await verifyVsixPackaging({ root, runCommand });

  assert.deepEqual(calls.map((call) => call.args.includes('ls') ? call.args.slice(-2) : call.args), [
    ['ls', '--no-dependencies'],
    ['capabilities', '--format', 'json'],
    ['debug-adapter', '--stdio'],
    ['--version']
  ]);

  await fs.writeFile(
    path.join(root, 'package.json'),
    JSON.stringify({ ...extensionPackageJson, main: './client/out/other.js' }, null, 2)
  );
  await assert.rejects(
    () => verifyVsixPackaging({ root, runCommand }),
    /packaged VBA debug entry point/i
  );
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
  assert.equal(
    packageJson.scripts['verify:guarded-enter'],
    'npm run test:extension && npm run test:extension-host && npm run test:packaging'
  );
  assert.match(packageJson.scripts.package, /package:verify/);
  assert.match(packageJson.scripts.test, /test:packaging/);
  assert.match(packageJson.scripts['test:packaging'], /--test-isolation=none/);
  assert.deepEqual(packageJson.repository, {
    type: 'git',
    url: 'https://github.com/modern-vba/vba-tools.git'
  });
});

test('release verification scripts expose every suite and keep Excel integration explicitly opt-in', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const scripts = packageJson.scripts;

  assert.match(scripts['test:syntax-core'], /VbaLanguageServer\.Syntax\.Tests/);
  assert.match(scripts['test:compatibility'], /devtool\.test\.js/);
  assert.match(scripts['test:compatibility'], /vscodeDebugIntegration\.test\.js/);
  assert.match(scripts['test:windows-excel-integration'], /WindowsExcelIntegration/);
  assert.match(
    scripts['test:windows-excel-integration'],
    /VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1/
  );

  for (const requiredSuite of [
    'test:extension',
    'test:extension-host',
    'test:devtool',
    'test:language-server',
    'test:syntax-core',
    'test:packaging',
    'test:compatibility',
    'package:verify'
  ]) {
    assert.match(scripts['verify:release'], new RegExp(`npm run ${requiredSuite}`));
  }
  assert.doesNotMatch(scripts['verify:release'], /windows-excel-integration/);
  assert.equal(
    scripts['verify:release:windows-excel'],
    'npm run verify:release && npm run test:windows-excel-integration'
  );
});

test('language server test script includes CLI and syntax test projects', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.match(packageJson.scripts['test:language-server'], /VbaLanguageServer\.slnx/);
});
