import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const requiredBundledCliPath = 'bin/vba-dev/win-x64/vba-dev.exe';
export const requiredBundledLanguageServerPath = 'bin/vba-language-server/win-x64/vba-language-server.exe';
export const bundledLanguageServerVersionPrefix = 'vba-language-server ';

const excludedDevToolSourcePrefix = 'tools/vba-dev/';
const excludedLanguageServerSourcePrefix = 'tools/vba-language-server/';
const excludedClientSourcePrefix = 'client/src/';
const excludedTypeScriptServerFallbackPrefix = 'server/';
const forbiddenBundledRuntimeSuffixes = [
  '.deps.json',
  '.dll',
  '.pdb',
  '.runtimeconfig.json'
];
const cliProjectPath = 'tools/vba-dev/src/VbaDev.Cli/VbaDev.Cli.csproj';
const languageServerProjectPath = 'tools/vba-language-server/src/VbaLanguageServer.Cli/VbaLanguageServer.Cli.csproj';
const requiredCommandSchemaVersions = {
  build: '1.0',
  'common-module add': '1.0',
  'common-module list': '1.0',
  'common-module update': '1.0',
  doctor: '1.0',
  export: '1.0',
  publish: '1.0',
  'reference add': '1.0',
  'reference list': '1.0',
  'reference remove': '1.0',
  test: '1.0'
};

export async function verifyVsixPackaging(options = {}) {
  const root = options.root ?? process.cwd();
  const runCommand = options.runCommand ?? runCommandWithSpawn;
  const bundledCliPath = path.join(root, requiredBundledCliPath);
  const bundledLanguageServerPath = path.join(root, requiredBundledLanguageServerPath);

  await fs.access(bundledCliPath);
  await fs.access(bundledLanguageServerPath);
  assertCliPublishSettings(await fs.readFile(path.join(root, cliProjectPath), 'utf8'));
  assertLanguageServerPublishSettings(await fs.readFile(path.join(root, languageServerProjectPath), 'utf8'));

  const fileListResult = await runCommand(process.execPath, [
    path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce'),
    'ls',
    '--no-dependencies'
  ], root);
  assertVsixContents(parseVsceFileList(fileListResult.stdout));

  const capabilitiesResult = await runCommand(bundledCliPath, ['capabilities', '--format', 'json'], root);
  assertBundledCliCapabilities(capabilitiesResult.stdout);
  const languageServerVersionResult = await runCommand(bundledLanguageServerPath, ['--version'], root);
  assertBundledLanguageServerVersion(languageServerVersionResult.stdout);
}

export function parseVsceFileList(stdout) {
  return stdout
    .split(/\r?\n/)
    .map((line) => line.trim().replaceAll('\\', '/').replace(/^\.\//, ''))
    .filter((line) => line.length > 0);
}

export function assertVsixContents(files) {
  const normalized = files.map((file) => file.replaceAll('\\', '/').replace(/^\.\//, ''));
  for (const requiredPath of [requiredBundledCliPath, requiredBundledLanguageServerPath]) {
    if (!normalized.includes(requiredPath)) {
      throw new Error(`VSIX file list must include ${requiredPath}.`);
    }
  }
  assertBundledRuntimeShape(normalized, requiredBundledCliPath, 'vba-dev');
  assertBundledRuntimeShape(normalized, requiredBundledLanguageServerPath, 'VbaLanguageServer');

  const sourceFiles = normalized.filter((file) => (
    file === 'tools/vba-dev' || file.startsWith(excludedDevToolSourcePrefix)
    || file === 'tools/vba-language-server' || file.startsWith(excludedLanguageServerSourcePrefix)
    || file === 'client/src' || file.startsWith(excludedClientSourcePrefix)
    || file === 'server' || file.startsWith(excludedTypeScriptServerFallbackPrefix)
  ));
  if (sourceFiles.length > 0) {
    throw new Error(`VSIX file list must exclude tool source files: ${sourceFiles.join(', ')}`);
  }
}

export function assertCliPublishSettings(csprojText) {
  assertProjectProperty(csprojText, 'AssemblyName', 'vba-dev', 'VbaDev.Cli.csproj');
  assertProjectProperty(csprojText, 'RuntimeIdentifier', 'win-x64', 'VbaDev.Cli.csproj');
  assertProjectProperty(csprojText, 'SelfContained', 'true', 'VbaDev.Cli.csproj');
  assertProjectProperty(csprojText, 'PublishSingleFile', 'true', 'VbaDev.Cli.csproj');
}

export function assertLanguageServerPublishSettings(csprojText) {
  assertProjectProperty(csprojText, 'AssemblyName', 'vba-language-server', 'VbaLanguageServer.Cli.csproj');
  assertProjectProperty(csprojText, 'RuntimeIdentifier', 'win-x64', 'VbaLanguageServer.Cli.csproj');
  assertProjectProperty(csprojText, 'SelfContained', 'true', 'VbaLanguageServer.Cli.csproj');
  assertProjectProperty(csprojText, 'PublishSingleFile', 'true', 'VbaLanguageServer.Cli.csproj');
}

export function assertBundledCliCapabilities(stdout) {
  let parsed;
  try {
    parsed = JSON.parse(stdout);
  } catch (error) {
    throw new Error(`Bundled vba-dev capabilities output must be JSON: ${String(error)}`);
  }

  if (!isRecord(parsed) || parsed.contractVersion !== '1.0' || !isRecord(parsed.commands)) {
    throw new Error('Bundled vba-dev capabilities must report contractVersion 1.0 and commands.');
  }

  for (const [commandName, schemaVersion] of Object.entries(requiredCommandSchemaVersions)) {
    const command = parsed.commands[commandName];
    if (!isRecord(command) || command.outputSchemaVersion !== schemaVersion) {
      throw new Error(`Bundled vba-dev capabilities must report ${commandName} outputSchemaVersion ${schemaVersion}.`);
    }
  }
}

export function assertBundledLanguageServerVersion(stdout) {
  if (!stdout.trim().startsWith(bundledLanguageServerVersionPrefix)) {
    throw new Error(`Bundled VbaLanguageServer must run directly and print a ${bundledLanguageServerVersionPrefix.trim()} version.`);
  }
}

function assertBundledRuntimeShape(files, executablePath, label) {
  const directory = path.posix.dirname(executablePath);
  const forbiddenSidecars = files
    .filter((file) => file.startsWith(`${directory}/`) && file !== executablePath)
    .filter((file) => forbiddenBundledRuntimeSuffixes.some((suffix) => file.endsWith(suffix)));
  if (forbiddenSidecars.length > 0) {
    throw new Error(`${label} must be packaged as a self-contained single executable without runtime sidecars: ${forbiddenSidecars.join(', ')}`);
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

function isRecord(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  await verifyVsixPackaging();
  console.log('VSIX packaging verification passed.');
}
