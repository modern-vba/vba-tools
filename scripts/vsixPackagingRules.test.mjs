import test from 'node:test';
import assert from 'node:assert/strict';
import { createWriteStream, promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import yazl from 'yazl';

import {
  assertBundledLanguageServerVersion,
  assertBundledDebugAdapterCapabilities,
  assertBundledCliCapabilities,
  assertCliPublishSettings,
  assertExtensionDebugPackage,
  assertExtensionProjectManifestSchemaPackage,
  assertExtensionWorkspaceTrustPackage,
  assertLanguageServerPublishSettings,
  assertMarketplacePackageMetadata,
  assertPackagedMarkdownLinks,
  assertPackagedVsixMetadata,
  assertVsixContents,
  distributionManifestPath,
  inspectVsixPackage,
  readDistributionManifest,
  readRequiredVbaDebugAdapterContract,
  readRequiredVbaDevContract,
  requiredBundledCliPath,
  requiredBundledDebugAdapterPath,
  requiredBundledLanguageServerPath,
  requiredVbaDebugAdapterContractPath,
  requiredVbaDevContractPath,
  verifyVsixPackaging
} from './vsixPackagingRules.mjs';

const marketplaceIconPath = 'assets/icon.png';
const marketplaceDocumentPaths = [
  'readme.md',
  'changelog.md',
  'LICENSE.txt',
  'SUPPORT.md',
  'schemas/project-manifest.schema.json'
];
const standaloneDebugAdapterPaths = [
  requiredBundledDebugAdapterPath,
  requiredVbaDebugAdapterContractPath
];

test('extension package declares the complete free Marketplace listing metadata', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.doesNotThrow(() => assertMarketplacePackageMetadata(packageJson));
  for (const invalidPackage of [
    { ...packageJson, publisher: 'other' },
    { ...packageJson, icon: 'other.png' },
    { ...packageJson, license: 'ISC' },
    { ...packageJson, homepage: 'https://example.com' },
    { ...packageJson, bugs: { url: 'https://example.com/issues' } },
    { ...packageJson, pricing: 'Trial' },
    { ...packageJson, keywords: packageJson.keywords.filter((keyword) => keyword !== 'debugging') },
    { ...packageJson, galleryBanner: { color: '#ffffff', theme: 'light' } }
  ]) {
    assert.throws(() => assertMarketplacePackageMetadata(invalidPackage), /Marketplace/i);
  }
});

test('support policy routes public support and private security reports with actionable diagnostics', async () => {
  const support = await fs.readFile(new URL('../SUPPORT.md', import.meta.url), 'utf8');

  assert.match(support, /github\.com\/modern-vba\/vba-tools\/issues/i);
  assert.match(support, /github\.com\/modern-vba\/vba-tools\/security\/advisories\/new/i);
  assert.match(support, /do not.*public issue/i);
  for (const diagnostic of ['VBA Tools', 'vba-dev', 'VS Code', 'Windows', 'Excel', 'logs']) {
    assert.match(support, new RegExp(diagnostic, 'i'));
  }
  assert.match(support, /Windows x64/i);
  assert.match(support, /win32-x64/i);
  assert.match(support, /editor-only.*do not require Excel/is);
  assert.match(support, /workbook.*desktop Excel.*trusted.*VBA project object model/is);
  assert.match(support, /no.*response-time.*service-level/is);
});

test('packaged Markdown links resolve only against files present in the VSIX', () => {
  const packagedFiles = new Map([
    ['README.md', '[Support](SUPPORT.md)\n![Icon](assets/icon.png)\n[Section](#usage)\n[Issues](https://github.com/modern-vba/vba-tools/issues)\n'],
    ['SUPPORT.md', '[README](README.md)\n'],
    ['assets/icon.png', null]
  ]);

  assert.doesNotThrow(() => assertPackagedMarkdownLinks(packagedFiles));
  packagedFiles.delete('SUPPORT.md');
  assert.throws(
    () => assertPackagedMarkdownLinks(packagedFiles),
    /README\.md.*SUPPORT\.md.*not packaged/i
  );
});

test('extension changelog provides the curated initial 0.1.0 release summary', async () => {
  const changelog = await fs.readFile(new URL('../CHANGELOG.md', import.meta.url), 'utf8');

  assert.match(changelog, /^# Changelog/m);
  assert.match(changelog, /^## \[0\.1\.0\] - Unreleased/m);
  assert.match(changelog, /^### Added/m);
  assert.match(changelog, /language server/i);
  assert.match(changelog, /workbook/i);
  assert.match(changelog, /debug/i);
  assert.match(changelog, /Windows x64/i);
});

test('distribution manifest requires every Marketplace-facing document and icon', () => {
  const manifest = readDistributionManifest();

  for (const requiredPath of [
    'readme.md',
    'changelog.md',
    'LICENSE.txt',
    'SUPPORT.md',
    'schemas/project-manifest.schema.json',
    marketplaceIconPath
  ]) {
    assert.ok(manifest.vsix.requiredFiles.includes(requiredPath), requiredPath);
  }
});

test('extension package associates only the canonical ProjectManifest basename with its bundled schema', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.doesNotThrow(() => assertExtensionProjectManifestSchemaPackage(packageJson));
  for (const invalidValidation of [
    [],
    [{ fileMatch: '**/vba-project*.json', url: './schemas/project-manifest.schema.json' }],
    [{ fileMatch: '**/vba-project.failed-*.json', url: './schemas/project-manifest.schema.json' }],
    [{ fileMatch: '**/vba-project.json', url: './schemas/other.schema.json' }],
    [
      { fileMatch: '**/vba-project.json', url: './schemas/project-manifest.schema.json' },
      { fileMatch: '**/vba-project.failed-*.json', url: './schemas/project-manifest.schema.json' }
    ]
  ]) {
    const invalidPackage = structuredClone(packageJson);
    invalidPackage.contributes.jsonValidation = invalidValidation;
    assert.throws(
      () => assertExtensionProjectManifestSchemaPackage(invalidPackage),
      /canonical.*vba-project\.json.*schema/i
    );
  }
});

test('distribution manifest declares the standalone VBA debug adapter runtime', () => {
  const manifest = readDistributionManifest();

  assert.equal(
    manifest.runtimes.vbaDebugAdapter.executablePath,
    'bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe'
  );
  assert.equal(
    manifest.runtimes.vbaDebugAdapter.contractPath,
    'vba-debug-adapter-contract.json'
  );
  assert.notEqual(
    manifest.runtimes.vbaDebugAdapter.executablePath,
    manifest.runtimes.vbaDev.executablePath
  );
  assert.ok(
    manifest.vsix.excludedSourcePrefixes.includes('tools/vba-debug-adapter/')
  );
});

test('VSIX inspection reads the generated archive metadata documents and file list', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-inspection-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const vsixPath = path.join(root, 'vba-tools-win32-x64-0.1.0.vsix');
  await writeZip(vsixPath, new Map([
    ['extension/package.json', JSON.stringify({
      name: 'vba-tools',
      version: '0.1.0',
      publisher: 'modern-vba'
    })],
    ['extension/readme.md', '[Support](SUPPORT.md)\n'],
    ['extension/SUPPORT.md', '# Support\n'],
    ['extension/assets/icon.png', 'png'],
    ['extension.vsixmanifest', '<Identity Publisher="modern-vba" Version="0.1.0" TargetPlatform="win32-x64" />']
  ]));

  const inspected = await inspectVsixPackage(vsixPath);

  assert.deepEqual([...inspected.files.keys()].sort(), [
    'SUPPORT.md',
    'assets/icon.png',
    'package.json',
    'readme.md'
  ]);
  assert.equal(inspected.packageJson.name, 'vba-tools');
  assert.doesNotThrow(() => assertPackagedVsixMetadata(
    inspected.vsixManifest,
    inspected.packageJson,
    'win32-x64'
  ));
  assert.throws(
    () => assertPackagedVsixMetadata(inspected.vsixManifest, inspected.packageJson, 'linux-x64'),
    /linux-x64/i
  );
  assert.doesNotThrow(() => assertPackagedMarkdownLinks(inspected.files));
});

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

test('extension package metadata declares limited Restricted Mode support', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const unsupportedPackage = structuredClone(packageJson);
  unsupportedPackage.capabilities.untrustedWorkspaces.supported = true;

  assert.doesNotThrow(() => assertExtensionWorkspaceTrustPackage(packageJson));
  assert.throws(
    () => assertExtensionWorkspaceTrustPackage(unsupportedPackage),
    /limited Restricted Mode support/i
  );
});

test('extension package metadata describes the safe Restricted Mode language surface', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  for (const description of ['', 'Managed tooling requires trust.']) {
    const undescribedPackage = structuredClone(packageJson);
    undescribedPackage.capabilities.untrustedWorkspaces.description = description;

    assert.throws(
      () => assertExtensionWorkspaceTrustPackage(undescribedPackage),
      /language assistance.*Restricted Mode/i
    );
  }
});

test('extension package metadata restricts every managed executable override', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  for (const executableSetting of [
    'vbaTools.devtool.path',
    'vbaTools.debugAdapter.path'
  ]) {
    const unrestrictedPackage = structuredClone(packageJson);
    unrestrictedPackage.capabilities.untrustedWorkspaces.restrictedConfigurations =
      unrestrictedPackage.capabilities.untrustedWorkspaces.restrictedConfigurations.filter(
        (setting) => setting !== executableSetting
      );

    assert.throws(
      () => assertExtensionWorkspaceTrustPackage(unrestrictedPackage),
      /restricted executable configurations/i
    );
  }
});

test('extension package keeps Create Excel VBA Project discoverable in Restricted Mode', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const missingActivation = structuredClone(packageJson);
  missingActivation.activationEvents = missingActivation.activationEvents.filter(
    (event) => event !== 'onCommand:vbaTools.newExcel'
  );
  const wrongTitle = structuredClone(packageJson);
  wrongTitle.contributes.commands.find(
    (command) => command.command === 'vbaTools.newExcel'
  ).title = 'Create project';
  const disabledCommand = structuredClone(packageJson);
  disabledCommand.contributes.commands.find(
    (command) => command.command === 'vbaTools.newExcel'
  ).enablement = 'isWorkspaceTrusted';
  const hiddenCommand = structuredClone(packageJson);
  hiddenCommand.contributes.menus = {
    commandPalette: [{ command: 'vbaTools.newExcel', when: 'isWorkspaceTrusted' }]
  };

  assert.doesNotThrow(() => assertExtensionWorkspaceTrustPackage(packageJson));
  for (const undiscoverablePackage of [
    missingActivation,
    wrongTitle,
    disabledCommand,
    hiddenCommand
  ]) {
    assert.throws(
      () => assertExtensionWorkspaceTrustPackage(undiscoverablePackage),
      /discoverable.*Restricted Mode/i
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

  for (const commandId of [
    'vbaTools.doctor',
    'vbaTools.newExcel',
    'vbaTools.userFormEvents.refresh'
  ]) {
    const missingCommand = structuredClone(packageJson);
    missingCommand.contributes.commands = missingCommand.contributes.commands.filter(
      (command) => command.command !== commandId
    );

    assert.throws(
      () => assertExtensionDebugPackage(missingCommand),
      new RegExp(`required extension command.*${commandId.replaceAll('.', '\\.')}`, 'i')
    );
  }
});

test('extension package metadata rejects both removed Host Event refresh IDs', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  for (const removedCommandId of [
    'vbaTools.hostEvents.refresh',
    'vbaTools.hostClasses.refresh'
  ]) {
    const legacyPackage = structuredClone(packageJson);
    legacyPackage.contributes.commands.push({
      command: removedCommandId,
      title: 'Removed command'
    });
    legacyPackage.activationEvents.push(`onCommand:${removedCommandId}`);

    assert.throws(
      () => assertExtensionDebugPackage(legacyPackage),
      new RegExp(`removed extension command.*${removedCommandId.replaceAll('.', '\\.')}`, 'i')
    );
  }
});

test('VSIX content rules exclude product-neutral syntax and integration-test sources', () => {
  for (const source of [
    'tools/vba-syntax/src/VbaTools.Syntax/VbaSyntaxTree.cs',
    'tools/vba-integration-tests/tests/VbaTools.Integration.Tests/PrebuiltTools.cs'
  ]) {
    assert.throws(() => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
      'client/out/extension.js',
      source
    ]), /tools\/vba-(syntax|integration-tests)/);
  }
});

test('VSIX content rules require the bundled CLI artifact and exclude source tree files', () => {
  assert.doesNotThrow(() => assertVsixContents([
    ...marketplaceDocumentPaths,
    'package.json',
    distributionManifestPath,
    marketplaceIconPath,
    requiredBundledCliPath,
    requiredBundledLanguageServerPath,
    requiredVbaDevContractPath,
    ...standaloneDebugAdapterPaths,
    'client/out/extension.js'
  ]));

  for (const requiredExtensionFile of [
    ...marketplaceDocumentPaths,
    'package.json',
    'client/out/extension.js'
  ]) {
    assert.throws(
      () => assertVsixContents([
        ...marketplaceDocumentPaths,
        'package.json',
        distributionManifestPath,
        marketplaceIconPath,
        requiredBundledCliPath,
        requiredBundledLanguageServerPath,
        requiredVbaDevContractPath,
        ...standaloneDebugAdapterPaths,
        'client/out/extension.js'
      ].filter((file) => file !== requiredExtensionFile)),
      new RegExp(requiredExtensionFile.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    );
  }

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      'tools/vba-dev/src/VbaDev.Cli/Program.cs',
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths
    ]),
    /tools\/vba-dev/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      'tools/vba-debug-adapter/src/VbaDebugAdapter.Cli/Program.cs',
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths
    ]),
    /tools\/vba-debug-adapter/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      'tools/vba-language-server/src/VbaLanguageServer.Cli/Program.cs',
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths
    ]),
    /tools\/vba-language-server/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      ...standaloneDebugAdapterPaths,
      'client/out/extension.js'
    ]),
    /bin\/vba-language-server\/win-x64\/vba-language-server\.exe/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
      'bin/vba-language-server/win-x64/vba-language-server.dll'
    ]),
    /self-contained single executable/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
      'bin/vba-language-server/win-x64/vba-language-server.runtimeconfig.json'
    ]),
    /runtimeconfig/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
      'bin/vba-language-server/win-x64/vba-language-server.pdb'
    ]),
    /vba-language-server\.pdb/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
      'server/out/server.js'
    ]),
    /server\/out\/server\.js/
  );

  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      'client/out/extension.js',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      ...standaloneDebugAdapterPaths,
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
        ...marketplaceDocumentPaths,
        'package.json',
        'client/out/extension.js',
        distributionManifestPath,
        marketplaceIconPath,
        requiredBundledCliPath,
        requiredBundledLanguageServerPath,
        requiredVbaDevContractPath,
        ...standaloneDebugAdapterPaths,
        excludedFile
      ]),
      new RegExp(excludedFile.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    );
  }
});

test('VSIX content rules require the standalone VBA debug adapter executable', () => {
  assert.throws(
    () => assertVsixContents([
      ...marketplaceDocumentPaths,
      'package.json',
      distributionManifestPath,
      marketplaceIconPath,
      requiredBundledCliPath,
      requiredBundledLanguageServerPath,
      requiredVbaDevContractPath,
      'vba-debug-adapter-contract.json',
      'client/out/extension.js'
    ]),
    /bin\/vba-debug-adapter\/win-x64\/vba-debug-adapter\.exe/
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
  const compatibleCapabilities = {
    toolVersion: '0.1.0',
    contractVersion: contract.contractVersion,
    featureVersions: contract.featureVersions,
    activeWindowsCodePage: 932,
    commands
  };

  assert.doesNotThrow(() => assertBundledCliCapabilities(JSON.stringify(compatibleCapabilities)));

  const missingActiveCodePageFeature = { ...contract.featureVersions };
  delete missingActiveCodePageFeature['sourceSnapshot.activeWindowsCodePage'];
  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      ...compatibleCapabilities,
      featureVersions: missingActiveCodePageFeature
    })),
    /sourceSnapshot\.activeWindowsCodePage/
  );

  const { activeWindowsCodePage: _omittedCodePage, ...withoutActiveCodePage } = compatibleCapabilities;
  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify(withoutActiveCodePage)),
    /active Windows code page/
  );

  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      ...compatibleCapabilities,
      debugAdapter: {
        protocolVersion: '1.1',
        transport: 'stdio',
        command: 'debug-adapter'
      }
    })),
    /must not report a debug adapter/i
  );

  assert.throws(
    () => assertBundledCliCapabilities(
      JSON.stringify(compatibleCapabilities),
      { ...contract, debugAdapterProtocolVersion: '1.1' }
    ),
    /vba-dev contract must not reference a debug adapter/i
  );

  delete commands.doctor;
  assert.throws(
    () => assertBundledCliCapabilities(JSON.stringify({
      ...compatibleCapabilities,
      commands
    })),
    /doctor/
  );
});

test('bundled debug adapter capabilities require the snapshot build feature contract', () => {
  const contract = readRequiredVbaDebugAdapterContract();
  assert.deepEqual(contract, {
    contractVersion: '1.0',
    protocolVersion: '2.0',
    transports: ['stdio'],
    sessionIdFormat: 'lowercase-hex-32',
    commands: ['cleanup', 'doctor'],
    commandSchemaVersions: { doctor: '1.0' },
    featureVersions: { 'doctor.stdinCancellation': '1.0' },
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '2.0' }
  });
  const compatibleCapabilities = {
    toolVersion: '0.1.0',
    ...contract
  };

  assert.doesNotThrow(() => assertBundledDebugAdapterCapabilities(
    JSON.stringify(compatibleCapabilities),
    contract
  ));
  assert.throws(
    () => assertBundledDebugAdapterCapabilities(JSON.stringify({
      ...compatibleCapabilities,
      featureVersions: {}
    }), contract),
    /doctor\.stdinCancellation/
  );
  assert.throws(
    () => assertBundledDebugAdapterCapabilities(JSON.stringify({
      ...compatibleCapabilities,
      requiredVbaDevFeatureVersions: {}
    }), contract),
    /build\.sourceSnapshot/
  );
  const contractWithExtraFeature = {
    ...contract,
    requiredVbaDevFeatureVersions: {
      ...contract.requiredVbaDevFeatureVersions,
      'test.sourceSnapshot': '2.0'
    }
  };
  assert.throws(
    () => assertBundledDebugAdapterCapabilities(JSON.stringify({
      toolVersion: '0.1.0',
      ...contractWithExtraFeature
    }), contractWithExtraFeature),
    /only build\.sourceSnapshot 2\.0/i
  );
});

test('packaging admits the coordinated ACP-authoritative snapshot v2 providers', () => {
  const cliContract = readRequiredVbaDevContract();
  const adapterContract = readRequiredVbaDebugAdapterContract();
  assert.equal(cliContract.contractVersion, '1.0');
  assert.equal(cliContract.featureVersions['build.sourceSnapshot'], '2.0');
  assert.equal(cliContract.featureVersions['test.sourceSnapshot'], '2.0');
  assert.equal(cliContract.featureVersions['sourceSnapshot.activeWindowsCodePage'], '1.0');
  assert.equal(adapterContract.contractVersion, '1.0');
  assert.equal(adapterContract.protocolVersion, '2.0');
  assert.deepEqual(adapterContract.requiredVbaDevFeatureVersions, { 'build.sourceSnapshot': '2.0' });
  assert.doesNotThrow(() => assertBundledCliCapabilities(JSON.stringify({
    toolVersion: '0.1.0',
    contractVersion: cliContract.contractVersion,
    featureVersions: cliContract.featureVersions,
    activeWindowsCodePage: 932,
    commands: Object.fromEntries(Object.entries(cliContract.commandSchemaVersions)
      .map(([name, version]) => [name, { outputSchemaVersion: version }]))
  })));
  assert.doesNotThrow(() => assertBundledDebugAdapterCapabilities(JSON.stringify({
    toolVersion: '0.1.0',
    ...adapterContract
  })));
});

test('packaging rejects mixed snapshot feature requirements and providers in either direction', () => {
  const contract = readRequiredVbaDevContract();
  const capabilities = {
    toolVersion: '0.1.0',
    contractVersion: contract.contractVersion,
    featureVersions: contract.featureVersions,
    activeWindowsCodePage: 932,
    commands: Object.fromEntries(Object.entries(contract.commandSchemaVersions)
      .map(([name, version]) => [name, { outputSchemaVersion: version }]))
  };
  for (const feature of ['build.sourceSnapshot', 'test.sourceSnapshot']) {
    const oldFeatures = { ...contract.featureVersions, [feature]: '1.0' };
    assert.throws(() => assertBundledCliCapabilities(JSON.stringify({
      ...capabilities,
      featureVersions: oldFeatures
    }), contract), /sourceSnapshot/);
    assert.throws(() => assertBundledCliCapabilities(JSON.stringify(capabilities), {
      ...contract,
      featureVersions: oldFeatures
    }), /sourceSnapshot/);
  }
});

test('packaging rejects mixed adapter protocols and required CLI snapshot features', () => {
  const contract = readRequiredVbaDebugAdapterContract();
  const capabilities = { toolVersion: '0.1.0', ...contract };
  const oldProtocol = { ...contract, protocolVersion: '1.1' };
  const oldBuildRequirement = {
    ...contract,
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '1.0' }
  };
  for (const oldContract of [oldProtocol, oldBuildRequirement]) {
    assert.throws(() => assertBundledDebugAdapterCapabilities(JSON.stringify({
      toolVersion: '0.1.0',
      ...oldContract
    }), contract), /adapter|sourceSnapshot/);
    assert.throws(() => assertBundledDebugAdapterCapabilities(
      JSON.stringify(capabilities), oldContract
    ), /adapter|sourceSnapshot/);
  }
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
  await fs.mkdir(path.join(root, 'bin', 'vba-debug-adapter', 'win-x64'), { recursive: true });
  await fs.writeFile(path.join(root, requiredBundledDebugAdapterPath), '');
  await fs.mkdir(path.join(root, 'bin', 'vba-language-server', 'win-x64'), { recursive: true });
  await fs.writeFile(path.join(root, requiredBundledLanguageServerPath), '');
  await fs.writeFile(
    path.join(root, requiredVbaDevContractPath),
    JSON.stringify(readRequiredVbaDevContract(), null, 2)
  );
  await fs.writeFile(
    path.join(root, requiredVbaDebugAdapterContractPath),
    JSON.stringify(readRequiredVbaDebugAdapterContract(), null, 2)
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
  await fs.mkdir(
    path.join(root, 'tools', 'vba-debug-adapter', 'src', 'VbaDebugAdapter.Cli'),
    { recursive: true }
  );
  await fs.writeFile(
    path.join(
      root,
      'tools',
      'vba-debug-adapter',
      'src',
      'VbaDebugAdapter.Cli',
      'VbaDebugAdapter.Cli.csproj'
    ),
    `
<Project>
  <PropertyGroup>
    <AssemblyName>vba-debug-adapter</AssemblyName>
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
  const adapterContract = readRequiredVbaDebugAdapterContract();
  const commands = Object.fromEntries(
    Object.entries(contract.commandSchemaVersions)
      .map(([commandName, schemaVersion]) => [commandName, { outputSchemaVersion: schemaVersion }])
  );
  const calls = [];
  const runCommand = async (file, args) => {
    calls.push({ file: path.basename(file), args });
    if (args.includes('package')) {
      return { stdout: '', stderr: '' };
    }

    if (args.includes('--version')) {
      return {
        stdout: 'vba-language-server 0.1.0\n',
        stderr: ''
      };
    }

    if (
      path.basename(file) === path.basename(requiredBundledDebugAdapterPath) &&
      args[0] === 'capabilities'
    ) {
      return {
        stdout: JSON.stringify({ toolVersion: '0.1.0', ...adapterContract }),
        stderr: ''
      };
    }

    return {
      stdout: JSON.stringify({
        toolVersion: '0.1.0',
        contractVersion: contract.contractVersion,
        featureVersions: contract.featureVersions,
        activeWindowsCodePage: 932,
        commands
      }),
      stderr: ''
    };
  };
  const packagedFiles = new Map([
    ...marketplaceDocumentPaths.map((file) => [
      file,
      file === 'readme.md' ? '[Support](SUPPORT.md)\n' : '# Document\n'
    ]),
    [distributionManifestPath, null],
    ['schemas/project-manifest.schema.json', null],
    [marketplaceIconPath, null],
    [requiredBundledCliPath, null],
    [requiredBundledDebugAdapterPath, null],
    [requiredBundledLanguageServerPath, null],
    [requiredVbaDevContractPath, null],
    [requiredVbaDebugAdapterContractPath, null],
    ['package.json', JSON.stringify(extensionPackageJson)],
    ['client/out/extension.js', null]
  ]);
  let inspectedPackageJson = extensionPackageJson;
  const inspectPackage = async () => ({
    files: packagedFiles,
    packageJson: inspectedPackageJson,
    vsixManifest: `<Identity Publisher="modern-vba" Version="${extensionPackageJson.version}" TargetPlatform="win32-x64" />`
  });

  await verifyVsixPackaging({ root, runCommand, inspectPackage });

  assert.deepEqual(calls.map((call) => call.args.includes('package') ? call.args.slice(1, 5) : call.args), [
    ['package', '--no-dependencies', '--target', 'win32-x64'],
    ['capabilities', '--format', 'json'],
    ['capabilities', '--format', 'json'],
    [
      '--stdio',
      '--vba-dev',
      path.join(root, requiredBundledCliPath),
      '--session',
      '0123456789abcdef0123456789abcdef'
    ],
    ['--version']
  ]);

  inspectedPackageJson = structuredClone(extensionPackageJson);
  delete inspectedPackageJson.capabilities;
  await assert.rejects(
    () => verifyVsixPackaging({ root, runCommand, inspectPackage }),
    /limited Restricted Mode support/i
  );
  inspectedPackageJson = extensionPackageJson;

  await fs.writeFile(
    path.join(root, 'package.json'),
    JSON.stringify({ ...extensionPackageJson, main: './client/out/other.js' }, null, 2)
  );
  await assert.rejects(
    () => verifyVsixPackaging({ root, runCommand, inspectPackage }),
    /packaged VBA debug entry point/i
  );
});

test('package scripts publish the bundled CLI and verify VSIX contents before packaging', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.match(packageJson.scripts['publish:devtool'], /dotnet publish/);
  assert.match(packageJson.scripts['publish:devtool'], /-o bin\/vba-dev\/win-x64/);
  assert.match(packageJson.scripts['publish:debug-adapter'], /dotnet publish/);
  assert.match(
    packageJson.scripts['publish:debug-adapter'],
    /-o bin\/vba-debug-adapter\/win-x64/
  );
  assert.match(packageJson.scripts['publish:language-server'], /dotnet publish/);
  assert.match(packageJson.scripts['publish:language-server'], /-o bin\/vba-language-server\/win-x64/);
  assert.equal(packageJson.scripts['verify:vsix'], 'node scripts/vsixPackagingRules.mjs');
  assert.match(packageJson.scripts['package:verify'], /publish:devtool/);
  assert.match(packageJson.scripts['package:verify'], /publish:debug-adapter/);
  assert.match(packageJson.scripts['package:verify'], /publish:language-server/);
  assert.match(packageJson.scripts['package:verify'], /verify:vsix/);
  assert.match(packageJson.scripts['test:extension-host'], /publish:devtool/);
  assert.match(packageJson.scripts['test:extension-host'], /publish:debug-adapter/);
  assert.match(packageJson.scripts['test:extension-host'], /publish:language-server/);
  assert.equal(
    packageJson.scripts['verify:guarded-enter'],
    'npm run test:extension && npm run test:extension-host && npm run test:packaging'
  );
  assert.match(packageJson.scripts.package, /package:verify/);
  assert.match(packageJson.scripts['test:debug-adapter'], /VbaDebugAdapter\.Tests/);
  assert.match(packageJson.scripts.test, /test:debug-adapter/);
  assert.match(packageJson.scripts.test, /test:packaging/);
  assert.match(packageJson.scripts['test:packaging'], /--test-isolation=none/);
  assert.deepEqual(packageJson.repository, {
    type: 'git',
    url: 'https://github.com/modern-vba/vba-tools.git'
  });
});

test('standalone debug adapter restores from committed lock files', async () => {
  const props = await fs.readFile(
    new URL('../tools/vba-debug-adapter/Directory.Build.props', import.meta.url),
    'utf8'
  );
  assert.match(props, /<RestorePackagesWithLockFile>true<\/RestorePackagesWithLockFile>/);
  assert.match(props, /<RestoreLockedMode>true<\/RestoreLockedMode>/);

  for (const lockPath of [
    '../tools/vba-debug-adapter/src/VbaDebugAdapter.Cli/packages.lock.json',
    '../tools/vba-debug-adapter/tests/VbaDebugAdapter.Tests/packages.lock.json'
  ]) {
    const lock = JSON.parse(await fs.readFile(new URL(lockPath, import.meta.url), 'utf8'));
    assert.equal(lock.version, 1);
  }
});

test('release verification scripts expose every suite and keep Excel integration explicitly opt-in', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const scripts = packageJson.scripts;

  assert.match(scripts['test:syntax-core'], /vba-syntax\/tests\/VbaTools\.Syntax\.Tests/);
  assert.match(scripts['test:project-metadata'], /vba-project-metadata\/tests\/VbaTools\.ProjectMetadata\.Tests/);
  assert.match(scripts.test, /npm run test:project-metadata/);
  assert.match(scripts['verify:architecture'], /dependencyBoundaries\.mjs/);
  assert.match(scripts.test, /npm run verify:architecture/);
  assert.match(scripts['test:cross-product-integration'], /VbaTools\.Integration\.Tests/);
  assert.doesNotMatch(scripts['test:cross-product-integration'], /dotnet build|RUN_EXCEL_INTEGRATION_TESTS=1/);
  assert.match(scripts['test:windows-excel-integration'], /npm run build:devtool/);
  assert.match(scripts['test:windows-excel-integration'], /npm run test:cross-product-integration -- --environment VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1/);
  assert.match(scripts['test:compatibility'], /devtool\.test\.js/);
  assert.match(scripts['test:compatibility'], /debugAdapter\.test\.js/);
  assert.match(scripts['test:compatibility'], /vscodeDebugIntegration\.test\.js/);
  assert.match(scripts['test:windows-excel-integration'], /WindowsExcelIntegration/);
  assert.match(scripts['test:windows-excel-integration'], /VbaDebugAdapter\.Tests/);
  assert.match(
    scripts['test:windows-excel-integration'],
    /VBA_TOOLS_RUN_EXCEL_INTEGRATION_TESTS=1/
  );

  for (const requiredSuite of [
    'verify:architecture',
    'test:extension',
    'test:extension-host',
    'test:devtool',
    'test:debug-adapter',
    'test:language-server',
    'test:syntax-core',
    'test:project-metadata',
    'test:cross-product-integration',
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

test('private-desktop Excel feasibility has an isolated repeatable opt-in command', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );
  const script = packageJson.scripts['test:private-desktop-excel-feasibility'];

  assert.equal(typeof script, 'string');
  assert.match(script, /VbaDev\.Tests\.csproj/);
  assert.match(script, /--filter Category=PrivateDesktopExcelFeasibility/);
  assert.match(
    script,
    /--environment VBA_TOOLS_RUN_PRIVATE_DESKTOP_EXCEL_FEASIBILITY_TESTS=1/
  );
  assert.doesNotMatch(
    script,
    /WindowsExcelIntegration|InitialWorkbookCreation|VbaDebugAdapter\.Tests|VbaLanguageServer/
  );
  assert.doesNotMatch(packageJson.scripts.test, /private-desktop-excel-feasibility/);
  assert.doesNotMatch(packageJson.scripts['verify:release'], /private-desktop-excel-feasibility/);
  assert.doesNotMatch(
    packageJson.scripts['test:windows-excel-integration'],
    /PrivateDesktopExcelFeasibility/
  );
});

test('private-desktop Excel feasibility tests require their dedicated category and environment gate', async () => {
  const attributes = await fs.readFile(
    new URL(
      '../tools/vba-dev/tests/VbaDev.Tests/WindowsExcelIntegrationAttributes.cs',
      import.meta.url
    ),
    'utf8'
  );

  assert.match(
    attributes,
    /class PrivateDesktopExcelFeasibilityFactAttribute\s*:\s*FactAttribute/
  );
  assert.match(
    attributes,
    /const string Category\s*=\s*"PrivateDesktopExcelFeasibility"/
  );
  assert.match(
    attributes,
    /const string OptInEnvironmentVariable\s*=\s*\r?\n?\s*"VBA_TOOLS_RUN_PRIVATE_DESKTOP_EXCEL_FEASIBILITY_TESTS"/
  );
  assert.match(
    attributes,
    /GetEnvironmentVariable\(OptInEnvironmentVariable\)[\s\S]*?"1"[\s\S]*?Skip\s*=/
  );
  assert.match(
    attributes,
    /CollectionDefinition\(Name, DisableParallelization = true\)[\s\S]*?class PrivateDesktopExcelFeasibilityCollection/
  );
});

test('private-desktop Excel feasibility command documents its intentionally visible baseline', async () => {
  const contributing = await fs.readFile(
    new URL('../CONTRIBUTING.md', import.meta.url),
    'utf8'
  );

  assert.match(contributing, /npm run test:private-desktop-excel-feasibility/);
  assert.match(
    contributing,
    /baseline[\s\S]*intentionally[\s\S]*interactive desktop[\s\S]*temporarily visible/i
  );
  assert.match(
    contributing,
    /does not run[\s\S]*test:windows-excel-integration[\s\S]*InitialWorkbookCreation/i
  );
});

test('language server test script includes CLI and syntax test projects', async () => {
  const packageJson = JSON.parse(
    await fs.readFile(new URL('../package.json', import.meta.url), 'utf8')
  );

  assert.match(packageJson.scripts['test:language-server'], /VbaLanguageServer\.slnx/);
});

function writeZip(filePath, entries) {
  return new Promise((resolve, reject) => {
    const zipFile = new yazl.ZipFile();
    for (const [entryPath, contents] of entries) {
      zipFile.addBuffer(Buffer.from(contents), entryPath);
    }
    zipFile.outputStream
      .pipe(createWriteStream(filePath))
      .on('close', resolve)
      .on('error', reject);
    zipFile.end();
  });
}
