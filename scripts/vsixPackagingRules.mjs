import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export const requiredBundledCliPath = 'bin/vba-dev/win-x64/vba-dev.exe';

const excludedDevToolSourcePrefix = 'tools/vba-dev/';
const cliProjectPath = 'tools/vba-dev/src/VbaDev.Cli/VbaDev.Cli.csproj';
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

  await fs.access(bundledCliPath);
  assertCliPublishSettings(await fs.readFile(path.join(root, cliProjectPath), 'utf8'));

  const fileListResult = await runCommand(process.execPath, [
    path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce'),
    'ls',
    '--no-dependencies'
  ], root);
  assertVsixContents(parseVsceFileList(fileListResult.stdout));

  const capabilitiesResult = await runCommand(bundledCliPath, ['capabilities', '--format', 'json'], root);
  assertBundledCliCapabilities(capabilitiesResult.stdout);
}

export function parseVsceFileList(stdout) {
  return stdout
    .split(/\r?\n/)
    .map((line) => line.trim().replaceAll('\\', '/').replace(/^\.\//, ''))
    .filter((line) => line.length > 0);
}

export function assertVsixContents(files) {
  const normalized = files.map((file) => file.replaceAll('\\', '/').replace(/^\.\//, ''));
  if (!normalized.includes(requiredBundledCliPath)) {
    throw new Error(`VSIX file list must include ${requiredBundledCliPath}.`);
  }

  const sourceFiles = normalized.filter((file) => (
    file === 'tools/vba-dev' || file.startsWith(excludedDevToolSourcePrefix)
  ));
  if (sourceFiles.length > 0) {
    throw new Error(`VSIX file list must exclude tools/vba-dev source files: ${sourceFiles.join(', ')}`);
  }
}

export function assertCliPublishSettings(csprojText) {
  assertProjectProperty(csprojText, 'AssemblyName', 'vba-dev');
  assertProjectProperty(csprojText, 'RuntimeIdentifier', 'win-x64');
  assertProjectProperty(csprojText, 'SelfContained', 'true');
  assertProjectProperty(csprojText, 'PublishSingleFile', 'true');
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

function assertProjectProperty(csprojText, propertyName, expectedValue) {
  const pattern = new RegExp(`<${propertyName}>\\s*${escapeRegExp(expectedValue)}\\s*</${propertyName}>`, 'i');
  if (!pattern.test(csprojText)) {
    throw new Error(`VbaDev.Cli.csproj must set ${propertyName} to ${expectedValue}.`);
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
