import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const releaseVersionPath = 'tools/vba-dev/Directory.Build.props';
const defaultPublishPath = 'bin/vba-dev/win-x64';
const cliReadmePath = 'tools/vba-dev/README.md';
const licensePath = 'LICENSE';
const contractPath = 'vba-dev-contract.json';

export async function createStandaloneVbaDevArchive({
  root = defaultRoot,
  outputDirectory = path.join(root, '.tmp', 'release'),
  publishDirectory = path.join(root, defaultPublishPath),
  archiveDirectory = compressDirectory,
  extractArchive = expandArchive,
  runCommand = runCommandWithSpawn
} = {}) {
  const version = await readVbaDevReleaseVersion(root);
  const executablePath = path.join(publishDirectory, 'vba-dev.exe');
  const publishedFiles = await fs.readdir(publishDirectory, { withFileTypes: true });
  const pdbNames = publishedFiles
    .filter((entry) => entry.isFile() && entry.name.toLowerCase().endsWith('.pdb'))
    .map((entry) => entry.name)
    .sort((left, right) => left.localeCompare(right, 'en'));
  if (pdbNames.length === 0) {
    throw new Error('The standalone vba-dev archive requires every published PDB, but the publish directory contains none.');
  }

  const sourceFiles = [
    [executablePath, 'vba-dev.exe'],
    ...pdbNames.map((name) => [path.join(publishDirectory, name), name]),
    [path.join(root, cliReadmePath), 'README.md'],
    [path.join(root, licensePath), 'LICENSE'],
    [path.join(root, contractPath), 'vba-dev-contract.json']
  ];
  await Promise.all(sourceFiles.map(([sourcePath]) => fs.access(sourcePath)));

  await fs.mkdir(outputDirectory, { recursive: true });
  const archivePath = path.join(outputDirectory, `vba-dev-win-x64-${version}.zip`);
  await fs.rm(archivePath, { force: true });

  const temporaryRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-dev-release-'));
  const stagingDirectory = path.join(temporaryRoot, 'archive');
  const extractionDirectory = path.join(temporaryRoot, 'extracted');
  await fs.mkdir(stagingDirectory, { recursive: true });
  for (const [sourcePath, archiveName] of sourceFiles) {
    await fs.copyFile(sourcePath, path.join(stagingDirectory, archiveName));
  }

  await archiveDirectory(stagingDirectory, archivePath);
  await fs.mkdir(extractionDirectory, { recursive: true });
  await extractArchive(archivePath, extractionDirectory);

  const expectedFiles = sourceFiles.map(([, archiveName]) => archiveName).sort();
  const extractedFiles = (await fs.readdir(extractionDirectory)).sort();
  if (JSON.stringify(extractedFiles) !== JSON.stringify(expectedFiles)) {
    throw new Error(`Standalone vba-dev archive contents disagree with the release contract. Expected ${expectedFiles.join(', ')}; received ${extractedFiles.join(', ')}.`);
  }

  const extractedExecutablePath = path.join(extractionDirectory, 'vba-dev.exe');
  const [publishedHash, extractedHash] = await Promise.all([
    sha256(executablePath),
    sha256(extractedExecutablePath)
  ]);
  if (publishedHash !== extractedHash) {
    throw new Error('The executable extracted from the standalone archive differs from the published vba-dev executable.');
  }

  const versionProbe = await runCommand(extractedExecutablePath, ['--version'], extractionDirectory);
  const expectedVersionOutput = `vba-dev ${version}\n`;
  if (normalizeNewlines(versionProbe.stdout) !== expectedVersionOutput || versionProbe.stderr !== '') {
    throw new Error(`Standalone vba-dev --version must print exactly ${JSON.stringify(expectedVersionOutput)} to stdout.`);
  }

  const capabilitiesProbe = await runCommand(
    extractedExecutablePath,
    ['capabilities', '--format', 'json'],
    extractionDirectory
  );
  let capabilities;
  try {
    capabilities = JSON.parse(capabilitiesProbe.stdout);
  } catch (error) {
    throw new Error(`Standalone vba-dev capabilities output must be JSON: ${String(error)}`);
  }
  const contract = JSON.parse(await fs.readFile(path.join(extractionDirectory, 'vba-dev-contract.json'), 'utf8'));
  assertReleaseContract(capabilities, contract, version);

  return {
    archivePath,
    extractionDirectory,
    version,
    cleanup: () => fs.rm(temporaryRoot, { recursive: true, force: true })
  };
}

export async function readVbaDevReleaseVersion(root = defaultRoot) {
  const props = await fs.readFile(path.join(root, releaseVersionPath), 'utf8');
  const match = props.match(/<VbaDevReleaseVersion>\s*([^<]+?)\s*<\/VbaDevReleaseVersion>/);
  if (!match || !/^\d+\.\d+\.\d+$/.test(match[1])) {
    throw new Error(`${releaseVersionPath} must define VbaDevReleaseVersion as canonical three-part SemVer.`);
  }

  return match[1];
}

export async function writeReleaseChecksums(outputDirectory, assetPaths) {
  const assets = assetPaths
    .map((assetPath) => ({ assetPath, fileName: path.basename(assetPath) }))
    .sort((left, right) => left.fileName.localeCompare(right.fileName, 'en'));
  if (assets.length === 0 || new Set(assets.map(({ fileName }) => fileName)).size !== assets.length) {
    throw new Error('SHA256SUMS requires at least one release asset and unique asset file names.');
  }

  const lines = [];
  for (const { assetPath, fileName } of assets) {
    lines.push(`${await sha256(assetPath)}  ${fileName}`);
  }

  await fs.mkdir(outputDirectory, { recursive: true });
  const checksumPath = path.join(outputDirectory, 'SHA256SUMS');
  await fs.writeFile(checksumPath, `${lines.join('\n')}\n`, 'utf8');
  return checksumPath;
}

function assertReleaseContract(capabilities, contract, version) {
  if (capabilities?.toolVersion !== version) {
    throw new Error(`Standalone vba-dev capabilities toolVersion must be ${version}.`);
  }
  if (typeof contract?.contractVersion !== 'string' || capabilities.contractVersion !== contract.contractVersion) {
    throw new Error('Standalone vba-dev capabilities contractVersion disagrees with vba-dev-contract.json.');
  }

  for (const [commandName, schemaVersion] of Object.entries(contract.commandSchemaVersions ?? {})) {
    if (capabilities.commands?.[commandName]?.outputSchemaVersion !== schemaVersion) {
      throw new Error(`Standalone vba-dev capabilities disagree with ${commandName} outputSchemaVersion ${schemaVersion}.`);
    }
  }

  if (contract.debugAdapterProtocolVersion !== undefined &&
      capabilities.debugAdapter?.protocolVersion !== contract.debugAdapterProtocolVersion) {
    throw new Error(`Standalone vba-dev capabilities disagree with debug adapter protocolVersion ${contract.debugAdapterProtocolVersion}.`);
  }
}

async function compressDirectory(sourceDirectory, archivePath) {
  const command = "& { param([string]$Source, [string]$Destination) Compress-Archive -Path (Join-Path $Source '*') -DestinationPath $Destination -CompressionLevel Optimal -Force }";
  await runPowerShell(command, [sourceDirectory, archivePath]);
}

async function expandArchive(archivePath, destinationDirectory) {
  const command = '& { param([string]$Archive, [string]$Destination) Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force }';
  await runPowerShell(command, [archivePath, destinationDirectory]);
}

async function runPowerShell(command, args) {
  const windowsDirectory = process.env.SystemRoot ?? 'C:\\Windows';
  const executable = path.join(windowsDirectory, 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
  await runCommandWithSpawn(executable, ['-NoProfile', '-NonInteractive', '-Command', command, ...args]);
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

async function sha256(filePath) {
  const contents = await fs.readFile(filePath);
  return createHash('sha256').update(contents).digest('hex');
}

function normalizeNewlines(value) {
  return value.replace(/\r\n/g, '\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const outputArgumentIndex = process.argv.indexOf('--output');
  const outputDirectory = outputArgumentIndex >= 0
    ? path.resolve(process.argv[outputArgumentIndex + 1])
    : path.join(defaultRoot, '.tmp', 'release');
  const result = await createStandaloneVbaDevArchive({ outputDirectory });
  console.log(result.archivePath);
  await result.cleanup();
}
