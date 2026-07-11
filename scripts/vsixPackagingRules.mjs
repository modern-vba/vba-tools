import { spawn } from 'node:child_process';
import { promises as fs, readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const distributionManifestPath = 'distribution-manifest.json';

const defaultDistributionManifest = readDistributionManifest();

export const requiredBundledCliPath = defaultDistributionManifest.runtimes.vbaDev.executablePath;
export const requiredBundledLanguageServerPath = defaultDistributionManifest.runtimes.vbaLanguageServer.executablePath;
export const requiredVbaDevContractPath = defaultDistributionManifest.runtimes.vbaDev.contractPath;
export const bundledLanguageServerVersionPrefix = defaultDistributionManifest.runtimes.vbaLanguageServer.versionOutputPrefix;

export async function verifyVsixPackaging(options = {}) {
  const root = options.root ?? process.cwd();
  const runCommand = options.runCommand ?? runCommandWithSpawn;
  const manifest = readDistributionManifest(root);
  const bundledCliPath = path.join(root, manifest.runtimes.vbaDev.executablePath);
  const bundledLanguageServerPath = path.join(root, manifest.runtimes.vbaLanguageServer.executablePath);
  const requiredContract = readRequiredVbaDevContract(root, manifest);

  await fs.access(bundledCliPath);
  await fs.access(bundledLanguageServerPath);
  assertRuntimePublishSettings(
    await fs.readFile(path.join(root, manifest.runtimes.vbaDev.projectPath), 'utf8'),
    manifest.runtimes.vbaDev);
  assertRuntimePublishSettings(
    await fs.readFile(path.join(root, manifest.runtimes.vbaLanguageServer.projectPath), 'utf8'),
    manifest.runtimes.vbaLanguageServer);

  const fileListResult = await runCommand(process.execPath, [
    path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce'),
    'ls',
    '--no-dependencies'
  ], root);
  assertVsixContents(parseVsceFileList(fileListResult.stdout), manifest);

  const capabilitiesResult = await runCommand(
    bundledCliPath,
    manifest.runtimes.vbaDev.smokeCommand,
    root);
  assertBundledCliCapabilities(capabilitiesResult.stdout, requiredContract);
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

export function parseVsceFileList(stdout) {
  return stdout
    .split(/\r?\n/)
    .map((line) => line.trim().replaceAll('\\', '/').replace(/^\.\//, ''))
    .filter((line) => line.length > 0);
}

export function assertVsixContents(files, distributionManifest = defaultDistributionManifest) {
  const normalized = files.map((file) => file.replaceAll('\\', '/').replace(/^\.\//, ''));
  const requiredPaths = [
    distributionManifest.runtimes.vbaDev.executablePath,
    distributionManifest.runtimes.vbaLanguageServer.executablePath,
    distributionManifest.runtimes.vbaDev.contractPath,
    ...distributionManifest.vsix.requiredFiles
  ];
  for (const requiredPath of requiredPaths) {
    if (!normalized.includes(requiredPath)) {
      throw new Error(`VSIX file list must include ${requiredPath}.`);
    }
  }
  assertBundledRuntimeShape(normalized, distributionManifest.runtimes.vbaDev, distributionManifest);
  assertBundledRuntimeShape(normalized, distributionManifest.runtimes.vbaLanguageServer, distributionManifest);

  const sourceFiles = normalized.filter((file) => distributionManifest.vsix.excludedSourcePrefixes.some((prefix) => (
    file === prefix.replace(/\/$/, '') || file.startsWith(prefix)
  )));
  if (sourceFiles.length > 0) {
    throw new Error(`VSIX file list must exclude tool source files: ${sourceFiles.join(', ')}`);
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
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Bundled vba-dev capabilities output must be JSON: ${String(error)}`);
  }

  if (!isRecord(parsed) || parsed.contractVersion !== contract.contractVersion || !isRecord(parsed.commands)) {
    throw new Error(`Bundled vba-dev capabilities must report contractVersion ${contract.contractVersion} and commands.`);
  }

  for (const [commandName, schemaVersion] of Object.entries(contract.commandSchemaVersions)) {
    const command = parsed.commands[commandName];
    if (!isRecord(command) || command.outputSchemaVersion !== schemaVersion) {
      throw new Error(`Bundled vba-dev capabilities must report ${commandName} outputSchemaVersion ${schemaVersion}.`);
    }
  }
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

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function isDistributionManifest(value) {
  return isRecord(value) &&
    value.manifestVersion === 1 &&
    isRecord(value.runtimes) &&
    isRuntime(value.runtimes.vbaDev) &&
    isRuntime(value.runtimes.vbaLanguageServer) &&
    isRecord(value.vsix) &&
    isStringArray(value.vsix.requiredFiles) &&
    isStringArray(value.vsix.excludedSourcePrefixes) &&
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
    isRecord(value.commandSchemaVersions) &&
    Object.values(value.commandSchemaVersions).every((schemaVersion) => typeof schemaVersion === 'string');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await verifyVsixPackaging();
  console.log('VSIX packaging verification passed.');
}
