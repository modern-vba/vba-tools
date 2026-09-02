import { spawn } from 'node:child_process';
import { promises as fs, readFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import yauzl from 'yauzl';

export const distributionManifestPath = 'distribution-manifest.json';

const defaultDistributionManifest = readDistributionManifest();

const requiredExtensionCommandIds = [
  'vbaTools.doctor',
  'vbaTools.openVbaDevTerminal',
  'vbaTools.newExcel',
  'vbaTools.build',
  'vbaTools.test',
  'vbaTools.publish',
  'vbaTools.userFormEvents.refresh',
  'vbaTools.export',
  'vbaTools.commonModules.add',
  'vbaTools.commonModules.list',
  'vbaTools.commonModules.update',
  'vbaTools.references.list',
  'vbaTools.references.add',
  'vbaTools.references.remove'
];
const removedExtensionCommandIds = [
  'vbaTools.hostEvents.refresh',
  'vbaTools.hostClasses.refresh'
];

export const requiredBundledCliPath = defaultDistributionManifest.runtimes.vbaDev.executablePath;
export const requiredBundledDebugAdapterPath =
  defaultDistributionManifest.runtimes.vbaDebugAdapter.executablePath;
export const requiredBundledLanguageServerPath = defaultDistributionManifest.runtimes.vbaLanguageServer.executablePath;
export const requiredVbaDevContractPath = defaultDistributionManifest.runtimes.vbaDev.contractPath;
export const requiredVbaDebugAdapterContractPath =
  defaultDistributionManifest.runtimes.vbaDebugAdapter.contractPath;
export const bundledLanguageServerVersionPrefix = defaultDistributionManifest.runtimes.vbaLanguageServer.versionOutputPrefix;
const activeWindowsCodePageFeatureName = 'sourceSnapshot.activeWindowsCodePage';

export async function verifyVsixPackaging(options = {}) {
  const root = options.root ?? process.cwd();
  const runCommand = options.runCommand ?? runCommandWithSpawn;
  const inspectPackage = options.inspectPackage ?? inspectVsixPackage;
  const manifest = readDistributionManifest(root);
  const bundledCliPath = path.join(root, manifest.runtimes.vbaDev.executablePath);
  const bundledDebugAdapterPath = path.join(
    root,
    manifest.runtimes.vbaDebugAdapter.executablePath
  );
  const bundledLanguageServerPath = path.join(root, manifest.runtimes.vbaLanguageServer.executablePath);
  const requiredContract = readRequiredVbaDevContract(root, manifest);
  const requiredDebugAdapterContract = readRequiredVbaDebugAdapterContract(root, manifest);
  const extensionPackageJson = JSON.parse(
    await fs.readFile(path.join(root, 'package.json'), 'utf8')
  );
  assertMarketplacePackageMetadata(extensionPackageJson);
  assertExtensionDebugPackage(extensionPackageJson);
  assertExtensionProjectManifestSchemaPackage(extensionPackageJson);
  assertExtensionWorkspaceTrustPackage(extensionPackageJson);

  await fs.access(bundledCliPath);
  await fs.access(bundledDebugAdapterPath);
  await fs.access(bundledLanguageServerPath);
  assertRuntimePublishSettings(
    await fs.readFile(path.join(root, manifest.runtimes.vbaDev.projectPath), 'utf8'),
    manifest.runtimes.vbaDev);
  assertRuntimePublishSettings(
    await fs.readFile(path.join(root, manifest.runtimes.vbaDebugAdapter.projectPath), 'utf8'),
    manifest.runtimes.vbaDebugAdapter);
  assertRuntimePublishSettings(
    await fs.readFile(path.join(root, manifest.runtimes.vbaLanguageServer.projectPath), 'utf8'),
    manifest.runtimes.vbaLanguageServer);

  const targetPlatform = 'win32-x64';
  const temporaryDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-verify-'));
  try {
    const vsixPath = path.join(
      temporaryDirectory,
      `vba-tools-${targetPlatform}-${extensionPackageJson.version}.vsix`
    );
    await runCommand(process.execPath, [
      path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce'),
      'package',
      '--no-dependencies',
      '--target',
      targetPlatform,
      '--out',
      vsixPath
    ], root);
    const packaged = await inspectPackage(vsixPath);
    assertVsixContents([...packaged.files.keys()], manifest);
    assertMarketplacePackageMetadata(packaged.packageJson);
    assertExtensionDebugPackage(packaged.packageJson);
    assertExtensionProjectManifestSchemaPackage(packaged.packageJson);
    assertExtensionWorkspaceTrustPackage(packaged.packageJson);
    assertPackagedVsixMetadata(packaged.vsixManifest, packaged.packageJson, targetPlatform);
    assertPackagedMarkdownLinks(packaged.files);
  } finally {
    await fs.rm(temporaryDirectory, { recursive: true, force: true });
  }

  const capabilitiesResult = await runCommand(
    bundledCliPath,
    manifest.runtimes.vbaDev.smokeCommand,
    root);
  assertBundledCliCapabilities(
    capabilitiesResult.stdout,
    requiredContract);
  const debugAdapterCapabilitiesResult = await runCommand(
    bundledDebugAdapterPath,
    manifest.runtimes.vbaDebugAdapter.smokeCommand,
    root);
  assertBundledDebugAdapterCapabilities(
    debugAdapterCapabilitiesResult.stdout,
    requiredDebugAdapterContract);
  await runCommand(
    bundledDebugAdapterPath,
    [
      '--stdio',
      '--vba-dev',
      bundledCliPath,
      '--session',
      '0123456789abcdef0123456789abcdef'
    ],
    root);
  const languageServerVersionResult = await runCommand(
    bundledLanguageServerPath,
    manifest.runtimes.vbaLanguageServer.smokeCommand,
    root);
  assertBundledLanguageServerVersion(
    languageServerVersionResult.stdout,
    manifest.runtimes.vbaLanguageServer.versionOutputPrefix);
}

export function readDistributionManifest(root = process.cwd()) {
  const manifestPath = path.join(root, distributionManifestPath);
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    throw new Error(`Distribution manifest must be readable from ${distributionManifestPath}: ${String(error)}`);
  }

  if (!isDistributionManifest(parsed)) {
    throw new Error(`Distribution manifest must include runtime executable paths and VSIX rules in ${distributionManifestPath}.`);
  }

  return parsed;
}

export function readRequiredVbaDevContract(root = process.cwd(), distributionManifest = readDistributionManifest(root)) {
  const contractPath = path.join(root, distributionManifest.runtimes.vbaDev.contractPath);
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(contractPath, 'utf8'));
  } catch (error) {
    throw new Error(`Required vba-dev contract must be readable from ${distributionManifest.runtimes.vbaDev.contractPath}: ${String(error)}`);
  }

  if (!isRequiredVbaDevContract(parsed)) {
    throw new Error(`Required vba-dev contract must include contractVersion and commandSchemaVersions in ${distributionManifest.runtimes.vbaDev.contractPath}.`);
  }

  return parsed;
}

export function readRequiredVbaDebugAdapterContract(
  root = process.cwd(),
  distributionManifest = readDistributionManifest(root)
) {
  const contractPath = path.join(
    root,
    distributionManifest.runtimes.vbaDebugAdapter.contractPath
  );
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(contractPath, 'utf8'));
  } catch (error) {
    throw new Error(
      `Required vba-debug-adapter contract must be readable from ${distributionManifest.runtimes.vbaDebugAdapter.contractPath}: ${String(error)}`
    );
  }

  if (!isRequiredVbaDebugAdapterContract(parsed)) {
    throw new Error(
      `Required vba-debug-adapter contract must declare its adapter and vba-dev compatibility surface in ${distributionManifest.runtimes.vbaDebugAdapter.contractPath}.`
    );
  }

  return parsed;
}

export async function inspectVsixPackage(vsixPath) {
  const entries = await readZipEntries(vsixPath);
  const manifestEntry = entries.get('extension.vsixmanifest');
  const packageEntry = entries.get('extension/package.json');
  if (!manifestEntry || !packageEntry) {
    throw new Error('Generated VSIX must contain extension.vsixmanifest and extension/package.json.');
  }

  const files = new Map();
  for (const [entryPath, contents] of entries) {
    if (!entryPath.startsWith('extension/') || entryPath.endsWith('/')) {
      continue;
    }
    const packagePath = entryPath.slice('extension/'.length);
    files.set(
      packagePath,
      packagePath.toLowerCase().endsWith('.md') || packagePath === 'package.json'
        ? contents.toString('utf8')
        : null
    );
  }

  return {
    files,
    packageJson: JSON.parse(packageEntry.toString('utf8')),
    vsixManifest: manifestEntry.toString('utf8')
  };
}

export function assertPackagedVsixMetadata(vsixManifest, packageJson, targetPlatform) {
  const identity = /<Identity\b[^>]*>/i.exec(vsixManifest)?.[0] ?? '';
  const requiredAttributes = {
    Publisher: packageJson?.publisher,
    Version: packageJson?.version,
    TargetPlatform: targetPlatform
  };
  if (
    packageJson?.name !== 'vba-tools' ||
    !Object.entries(requiredAttributes).every(([name, value]) => (
      typeof value === 'string' &&
      new RegExp(`\\b${name}="${escapeRegExp(value)}"`, 'i').test(identity)
    ))
  ) {
    throw new Error(
      `Generated VSIX metadata must identify modern-vba.vba-tools version ${packageJson?.version ?? '<missing>'} for ${targetPlatform}.`
    );
  }
}

export function assertExtensionDebugPackage(packageJson) {
  if (
    !isRecord(packageJson) ||
    packageJson.main !== './client/out/extension.js' ||
    !isStringArray(packageJson.activationEvents) ||
    !packageJson.activationEvents.includes('onDebugDynamicConfigurations') ||
    !packageJson.activationEvents.includes('onDebugResolve:vba')
  ) {
    throw new Error(
      'Extension package metadata must activate the packaged VBA debug entry point through dynamic configuration resolution.'
    );
  }

  const debuggers = packageJson.contributes?.debuggers;
  const vbaDebugger = Array.isArray(debuggers)
    ? debuggers.find((candidate) => isRecord(candidate) && candidate.type === 'vba')
    : undefined;
  const launchProperties = vbaDebugger?.configurationAttributes?.launch?.properties;
  if (
    !isRecord(launchProperties) ||
    !['project', 'document', 'module', 'procedure'].every(
      (selector) => isRecord(launchProperties[selector]) && launchProperties[selector].type === 'string'
    )
  ) {
    throw new Error(
      'Extension package metadata must expose the project, document, module, and procedure VBA launch selector schema.'
    );
  }

  const launchDependencies = vbaDebugger.configurationAttributes.launch.dependencies;
  if (
    !isRecord(launchDependencies) ||
    !isStringArray(launchDependencies.module) ||
    launchDependencies.module.length !== 1 ||
    launchDependencies.module[0] !== 'procedure' ||
    !isStringArray(launchDependencies.procedure) ||
    launchDependencies.procedure.length !== 1 ||
    launchDependencies.procedure[0] !== 'module'
  ) {
    throw new Error(
      'Extension package metadata must require the VBA launch selectors module and procedure together.'
    );
  }
  if (Object.hasOwn(vbaDebugger.configurationAttributes, 'attach')) {
    throw new Error('Extension package metadata does not support attach for VBA debugging.');
  }

  const contributedCommands = packageJson.contributes?.commands;
  for (const commandId of requiredExtensionCommandIds) {
    if (
      !Array.isArray(contributedCommands) ||
      !contributedCommands.some(
        (command) => isRecord(command) && command.command === commandId
      )
    ) {
      throw new Error(`Extension package metadata must include required extension command ${commandId}.`);
    }
  }
  for (const commandId of removedExtensionCommandIds) {
    if (
      (Array.isArray(contributedCommands) &&
        contributedCommands.some(
          (command) => isRecord(command) && command.command === commandId
        )) ||
      (Array.isArray(packageJson.activationEvents) &&
        packageJson.activationEvents.includes(`onCommand:${commandId}`)
      )
    ) {
      throw new Error(`Extension package metadata must not include removed extension command ${commandId}.`);
    }
  }
}

export function assertExtensionProjectManifestSchemaPackage(packageJson) {
  const validation = packageJson?.contributes?.jsonValidation;
  if (
    !Array.isArray(validation) ||
    validation.length !== 1 ||
    !isRecord(validation[0]) ||
    validation[0].fileMatch !== '**/vba-project.json' ||
    validation[0].url !== './schemas/project-manifest.schema.json'
  ) {
    throw new Error(
      'Extension package metadata must associate only the canonical **/vba-project.json basename with the bundled ProjectManifest schema.'
    );
  }
}

export function assertExtensionWorkspaceTrustPackage(packageJson) {
  const untrustedWorkspaces = packageJson?.capabilities?.untrustedWorkspaces;
  if (untrustedWorkspaces?.supported !== 'limited') {
    throw new Error(
      'Extension package metadata must declare limited Restricted Mode support.'
    );
  }
  if (
    typeof untrustedWorkspaces.description !== 'string' ||
    !/language assistance/i.test(untrustedWorkspaces.description)
  ) {
    throw new Error(
      'Extension package metadata must describe language assistance as the safe Restricted Mode surface.'
    );
  }
  const restrictedConfigurations = untrustedWorkspaces.restrictedConfigurations;
  if (
    !isStringArray(restrictedConfigurations) ||
    !['vbaTools.devtool.path', 'vbaTools.debugAdapter.path'].every(
      (setting) => restrictedConfigurations.includes(setting)
    )
  ) {
    throw new Error(
      'Extension package metadata must list both managed executable configurations as restricted executable configurations.'
    );
  }

  const commands = packageJson.contributes?.commands;
  const createCommand = Array.isArray(commands)
    ? commands.find(
        (command) => isRecord(command) && command.command === 'vbaTools.newExcel'
      )
    : undefined;
  const commandPalette = packageJson.contributes?.menus?.commandPalette;
  const hasContextCondition = Array.isArray(commandPalette) && commandPalette.some(
    (entry) => isRecord(entry)
      && entry.command === 'vbaTools.newExcel'
      && Object.hasOwn(entry, 'when')
  );
  if (
    !isStringArray(packageJson.activationEvents) ||
    !packageJson.activationEvents.includes('onCommand:vbaTools.newExcel') ||
    !isRecord(createCommand) ||
    createCommand.title !== 'VBA Tools: Create Excel VBA Project' ||
    Object.hasOwn(createCommand, 'enablement') ||
    hasContextCondition
  ) {
    throw new Error(
      'Extension package metadata must keep Create Excel VBA Project discoverable in Restricted Mode.'
    );
  }
}

export function assertMarketplacePackageMetadata(packageJson) {
  const expectedHomepage = 'https://github.com/modern-vba/vba-tools';
  const expectedIssues = `${expectedHomepage}/issues`;
  const keywords = packageJson?.keywords;
  const normalizedKeywords = Array.isArray(keywords)
    ? keywords.map((keyword) => typeof keyword === 'string' ? keyword.toLowerCase() : '')
    : [];
  const requiredKeywords = ['vba', 'excel', 'language tooling', 'testing', 'debugging'];
  if (
    packageJson?.publisher !== 'modern-vba' ||
    packageJson?.repository?.url !== 'https://github.com/modern-vba/vba-tools.git' ||
    packageJson?.icon !== 'assets/icon.png' ||
    packageJson?.license !== 'MIT' ||
    packageJson?.homepage !== expectedHomepage ||
    packageJson?.bugs?.url !== expectedIssues ||
    packageJson?.pricing !== 'Free' ||
    packageJson?.galleryBanner?.color?.toLowerCase() !== '#242424' ||
    packageJson?.galleryBanner?.theme !== 'dark' ||
    normalizedKeywords.length > 10 ||
    !requiredKeywords.every((keyword) => normalizedKeywords.includes(keyword))
  ) {
    throw new Error(
      'Marketplace package metadata must retain the modern-vba publisher, repository, and icon; declare the MIT license, repository homepage, GitHub Issues URL, Free pricing, concise VBA/Excel/language tooling/testing/debugging keywords, and dark #242424 gallery banner.'
    );
  }
}

export function assertPackagedMarkdownLinks(packagedFiles) {
  const normalizedFiles = new Map(
    [...packagedFiles.entries()].map(([fileName, contents]) => [
      fileName.replaceAll('\\', '/').replace(/^\.\//, ''),
      contents
    ])
  );
  const packagedPathsByCaseFoldedName = new Map(
    [...normalizedFiles.keys()].map((fileName) => [fileName.toLowerCase(), fileName])
  );
  const markdownLinkPattern = /!?\[[^\]]*\]\(([^)\s]+)(?:\s+["'][^)]*["'])?\)/g;
  for (const [markdownPath, contents] of normalizedFiles) {
    if (!markdownPath.toLowerCase().endsWith('.md') || typeof contents !== 'string') {
      continue;
    }

    for (const match of contents.matchAll(markdownLinkPattern)) {
      const rawTarget = match[1].replace(/^<|>$/g, '');
      if (
        rawTarget.startsWith('#') ||
        rawTarget.startsWith('//') ||
        /^[a-z][a-z0-9+.-]*:/i.test(rawTarget)
      ) {
        continue;
      }

      const pathOnlyTarget = decodeURIComponent(rawTarget.split(/[?#]/, 1)[0]);
      const resolvedTarget = path.posix.normalize(
        path.posix.join(path.posix.dirname(markdownPath), pathOnlyTarget)
      );
      if (
        resolvedTarget.startsWith('../') ||
        path.posix.isAbsolute(resolvedTarget) ||
        !normalizedFiles.has(resolvedTarget) &&
        !packagedPathsByCaseFoldedName.has(resolvedTarget.toLowerCase())
      ) {
        throw new Error(
          `Packaged Markdown link from ${markdownPath} to ${rawTarget} is not packaged in the VSIX.`
        );
      }
    }
  }
}

export function assertVsixContents(files, distributionManifest = defaultDistributionManifest) {
  const normalized = files.map((file) => file.replaceAll('\\', '/').replace(/^\.\//, ''));
  const requiredPaths = [
    distributionManifest.runtimes.vbaDev.executablePath,
    distributionManifest.runtimes.vbaDebugAdapter.executablePath,
    distributionManifest.runtimes.vbaLanguageServer.executablePath,
    distributionManifest.runtimes.vbaDev.contractPath,
    distributionManifest.runtimes.vbaDebugAdapter.contractPath,
    ...distributionManifest.vsix.requiredFiles
  ];
  for (const requiredPath of requiredPaths) {
    if (!normalized.includes(requiredPath)) {
      throw new Error(`VSIX file list must include ${requiredPath}.`);
    }
  }
  assertBundledRuntimeShape(normalized, distributionManifest.runtimes.vbaDev, distributionManifest);
  assertBundledRuntimeShape(
    normalized,
    distributionManifest.runtimes.vbaDebugAdapter,
    distributionManifest
  );
  assertBundledRuntimeShape(normalized, distributionManifest.runtimes.vbaLanguageServer, distributionManifest);

  const sourceFiles = normalized.filter((file) => distributionManifest.vsix.excludedSourcePrefixes.some((prefix) => (
    file === prefix.replace(/\/$/, '') || file.startsWith(prefix)
  )));
  const excludedFiles = normalized.filter((file) => (
    distributionManifest.vsix.excludedFiles.includes(file)
    || distributionManifest.vsix.excludedFileSuffixes.some((suffix) => file.endsWith(suffix))
  ));
  const forbiddenFiles = [...new Set([...sourceFiles, ...excludedFiles])];
  if (forbiddenFiles.length > 0) {
    throw new Error(`VSIX file list must exclude development files: ${forbiddenFiles.join(', ')}`);
  }
}

export function assertCliPublishSettings(csprojText, distributionManifest = defaultDistributionManifest) {
  assertRuntimePublishSettings(csprojText, distributionManifest.runtimes.vbaDev);
}

export function assertLanguageServerPublishSettings(csprojText, distributionManifest = defaultDistributionManifest) {
  assertRuntimePublishSettings(csprojText, distributionManifest.runtimes.vbaLanguageServer);
}

export function assertRuntimePublishSettings(csprojText, runtime) {
  assertProjectProperty(csprojText, 'AssemblyName', runtime.assemblyName, `${runtime.label}.csproj`);
  assertProjectProperty(csprojText, 'RuntimeIdentifier', runtime.runtimeIdentifier, `${runtime.label}.csproj`);
  assertProjectProperty(csprojText, 'SelfContained', String(runtime.selfContained).toLowerCase(), `${runtime.label}.csproj`);
  assertProjectProperty(csprojText, 'PublishSingleFile', String(runtime.publishSingleFile).toLowerCase(), `${runtime.label}.csproj`);
}

export function assertBundledCliCapabilities(stdout, requiredContract = undefined) {
  const contract = requiredContract ?? readRequiredVbaDevContract();
  if (
    Object.prototype.hasOwnProperty.call(contract, 'debugAdapterProtocolVersion') ||
    Object.prototype.hasOwnProperty.call(contract, 'debugAdapter')
  ) {
    throw new Error(
      'The vba-dev contract must not reference a debug adapter; the standalone vba-debug-adapter owns that contract.'
    );
  }
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Bundled vba-dev capabilities output must be JSON: ${String(error)}`);
  }

  if (!isRecord(parsed) || parsed.contractVersion !== contract.contractVersion || !isRecord(parsed.commands)) {
    throw new Error(`Bundled vba-dev capabilities must report contractVersion ${contract.contractVersion} and commands.`);
  }

  if (Object.prototype.hasOwnProperty.call(parsed, 'debugAdapter')) {
    throw new Error('Bundled vba-dev capabilities must not report a debug adapter; the standalone vba-debug-adapter owns that contract.');
  }

  for (const [commandName, schemaVersion] of Object.entries(contract.commandSchemaVersions)) {
    const command = parsed.commands[commandName];
    if (!isRecord(command) || command.outputSchemaVersion !== schemaVersion) {
      throw new Error(`Bundled vba-dev capabilities must report ${commandName} outputSchemaVersion ${schemaVersion}.`);
    }
  }

  for (const [featureName, featureVersion] of Object.entries(contract.featureVersions ?? {})) {
    if (parsed.featureVersions?.[featureName] !== featureVersion) {
      throw new Error(`Bundled vba-dev capabilities must report ${featureName} feature version ${featureVersion}.`);
    }
  }

  if (
    contract.featureVersions?.[activeWindowsCodePageFeatureName] !== undefined
    && (
      !Number.isSafeInteger(parsed.activeWindowsCodePage)
      || parsed.activeWindowsCodePage <= 0
    )
  ) {
    throw new Error('Bundled vba-dev capabilities must report a positive active Windows code page.');
  }

  return parsed;
}

export function assertBundledDebugAdapterCapabilities(
  stdout,
  requiredContract = undefined
) {
  const contract = requiredContract ?? readRequiredVbaDebugAdapterContract();
  if (!equalStringRecords(
    contract.requiredVbaDevFeatureVersions,
    { 'build.sourceSnapshot': '1.0' }
  )) {
    throw new Error(
      'Bundled vba-debug-adapter contract must require only build.sourceSnapshot 1.0.'
    );
  }
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Bundled vba-debug-adapter capabilities output must be JSON: ${String(error)}`);
  }

  if (
    !isRecord(parsed) ||
    typeof parsed.toolVersion !== 'string' ||
    parsed.toolVersion.length === 0 ||
    parsed.contractVersion !== contract.contractVersion ||
    parsed.protocolVersion !== contract.protocolVersion ||
    parsed.sessionIdFormat !== contract.sessionIdFormat ||
    !equalStringArrays(parsed.transports, contract.transports) ||
    !equalStringArrays(parsed.commands, contract.commands) ||
    !equalStringRecords(parsed.commandSchemaVersions, contract.commandSchemaVersions)
  ) {
    throw new Error('Bundled vba-debug-adapter capabilities do not satisfy the required adapter contract.');
  }

  for (const [featureName, featureVersion] of Object.entries(contract.featureVersions)) {
    if (parsed.featureVersions?.[featureName] !== featureVersion) {
      throw new Error(
        `Bundled vba-debug-adapter capabilities must report ${featureName} feature version ${featureVersion}.`
      );
    }
  }

  if (!equalStringRecords(
    parsed.requiredVbaDevFeatureVersions,
    contract.requiredVbaDevFeatureVersions
  )) {
    const requiredFeatures = Object.entries(contract.requiredVbaDevFeatureVersions)
      .map(([name, version]) => `${name} ${version}`)
      .join(', ');
    throw new Error(
      `Bundled vba-debug-adapter capabilities must require only ${requiredFeatures}.`
    );
  }

  return parsed;
}

export function assertBundledLanguageServerVersion(
  stdout,
  versionPrefix = defaultDistributionManifest.runtimes.vbaLanguageServer.versionOutputPrefix
) {
  if (!stdout.trim().startsWith(versionPrefix)) {
    throw new Error(`Bundled VbaLanguageServer must run directly and print a ${versionPrefix.trim()} version.`);
  }
}

function assertBundledRuntimeShape(files, runtime, distributionManifest) {
  const directory = path.posix.dirname(runtime.executablePath);
  const forbiddenSidecars = files
    .filter((file) => file.startsWith(`${directory}/`) && file !== runtime.executablePath)
    .filter((file) => distributionManifest.vsix.forbiddenRuntimeSidecarSuffixes.some((suffix) => file.endsWith(suffix)));
  if (forbiddenSidecars.length > 0) {
    throw new Error(`${runtime.label} must be packaged as a self-contained single executable without runtime sidecars: ${forbiddenSidecars.join(', ')}`);
  }
}

function assertProjectProperty(csprojText, propertyName, expectedValue, projectFileName) {
  const pattern = new RegExp(`<${propertyName}>\\s*${escapeRegExp(expectedValue)}\\s*</${propertyName}>`, 'i');
  if (!pattern.test(csprojText)) {
    throw new Error(`${projectFileName} must set ${propertyName} to ${expectedValue}.`);
  }
}

function runCommandWithSpawn(file, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd, windowsHide: true });
    child.stdin?.end();
    let stdout = '';
    let stderr = '';

    child.stdout?.on('data', (chunk) => {
      stdout += chunk.toString('utf8');
    });
    child.stderr?.on('data', (chunk) => {
      stderr += chunk.toString('utf8');
    });
    child.on('error', reject);
    child.on('exit', (exitCode) => {
      if (exitCode !== 0) {
        reject(new Error(`${file} ${args.join(' ')} exited with code ${exitCode}.\n${stderr}`));
        return;
      }

      resolve({ stdout, stderr });
    });
  });
}

function readZipEntries(vsixPath) {
  return new Promise((resolve, reject) => {
    yauzl.open(vsixPath, { lazyEntries: true }, (openError, zipFile) => {
      if (openError) {
        reject(openError);
        return;
      }

      const entries = new Map();
      zipFile.on('error', reject);
      zipFile.on('end', () => resolve(entries));
      zipFile.on('entry', (entry) => {
        if (entry.fileName.endsWith('/')) {
          zipFile.readEntry();
          return;
        }

        zipFile.openReadStream(entry, (streamError, stream) => {
          if (streamError) {
            reject(streamError);
            return;
          }

          const chunks = [];
          stream.on('data', (chunk) => chunks.push(chunk));
          stream.on('error', reject);
          stream.on('end', () => {
            entries.set(entry.fileName.replaceAll('\\', '/'), Buffer.concat(chunks));
            zipFile.readEntry();
          });
        });
      });
      zipFile.readEntry();
    });
  });
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function isDistributionManifest(value) {
  return isRecord(value) &&
    value.manifestVersion === 1 &&
    isRecord(value.runtimes) &&
    isRuntime(value.runtimes.vbaDev) &&
    isRuntime(value.runtimes.vbaDebugAdapter) &&
    isRuntime(value.runtimes.vbaLanguageServer) &&
    isRecord(value.vsix) &&
    isStringArray(value.vsix.requiredFiles) &&
    isStringArray(value.vsix.excludedSourcePrefixes) &&
    isStringArray(value.vsix.excludedFiles) &&
    isStringArray(value.vsix.excludedFileSuffixes) &&
    isStringArray(value.vsix.forbiddenRuntimeSidecarSuffixes);
}

function isRuntime(value) {
  return isRecord(value) &&
    typeof value.label === 'string' &&
    typeof value.executablePath === 'string' &&
    typeof value.projectPath === 'string' &&
    typeof value.assemblyName === 'string' &&
    typeof value.runtimeIdentifier === 'string' &&
    typeof value.selfContained === 'boolean' &&
    typeof value.publishSingleFile === 'boolean' &&
    isStringArray(value.smokeCommand) &&
    (value.contractPath === undefined || typeof value.contractPath === 'string') &&
    (value.versionOutputPrefix === undefined || typeof value.versionOutputPrefix === 'string');
}

function isStringArray(value) {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

function isRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isRequiredVbaDevContract(value) {
  return isRecord(value) &&
    typeof value.contractVersion === 'string' &&
    (value.featureVersions === undefined || isStringRecord(value.featureVersions)) &&
    isRecord(value.commandSchemaVersions) &&
    Object.values(value.commandSchemaVersions).every((schemaVersion) => typeof schemaVersion === 'string');
}

function isRequiredVbaDebugAdapterContract(value) {
  return isRecord(value) &&
    typeof value.contractVersion === 'string' &&
    typeof value.protocolVersion === 'string' &&
    isStringArray(value.transports) &&
    typeof value.sessionIdFormat === 'string' &&
    isStringArray(value.commands) &&
    isStringRecord(value.commandSchemaVersions) &&
    isStringRecord(value.featureVersions) &&
    isStringRecord(value.requiredVbaDevFeatureVersions);
}

function equalStringArrays(actual, expected) {
  return isStringArray(actual) &&
    actual.length === expected.length &&
    actual.every((value, index) => value === expected[index]);
}

function equalStringRecords(actual, expected) {
  if (!isStringRecord(actual)) {
    return false;
  }

  const actualEntries = Object.entries(actual);
  const expectedEntries = Object.entries(expected);
  return actualEntries.length === expectedEntries.length &&
    expectedEntries.every(([name, version]) => actual[name] === version);
}

function isStringRecord(value) {
  return isRecord(value) && Object.values(value).every((item) => typeof item === 'string');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await verifyVsixPackaging();
  console.log('VSIX packaging verification passed.');
}
