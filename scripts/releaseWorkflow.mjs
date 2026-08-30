import { spawn } from 'node:child_process';
import { promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import yauzl from 'yauzl';

import {
  assertBundledCliCapabilities,
  assertBundledDebugAdapterCapabilities,
  assertBundledLanguageServerVersion,
  assertPackagedVsixMetadata,
  assertVsixContents,
  inspectVsixPackage,
  readDistributionManifest,
  readRequiredVbaDebugAdapterContract,
  readRequiredVbaDevContract
} from './vsixPackagingRules.mjs';
import {
  assertVsixReleaseChannel,
  validateReleaseInputs,
  verifyReleaseArtifactSet
} from './releaseManagement.mjs';

const fullCommitPattern = /^[0-9a-f]{40}$/;
const tagPattern = /^vba-tools-v((?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$/;
const requiredTrailers = [
  'Channel',
  'Windows-Excel-Verification-Commit',
  'Windows-Excel-Verification-Result',
  'Clean-Windows-Smoke'
];
const allowedTrailers = new Set([
  ...requiredTrailers,
  'Clean-Windows-Smoke-Reason'
]);

export function validateAnnotatedReleaseTag({
  tagName,
  tagType,
  tagObject,
  packageJson,
  packageLock,
  vbaDevProps
}) {
  const tagMatch = tagPattern.exec(tagName ?? '');
  if (!tagMatch) {
    throw new Error('Release tag must use the vba-tools-vX.Y.Z namespace and canonical SemVer.');
  }
  if (tagType !== 'tag') {
    throw new Error('A release must start from an annotated tag, not a lightweight tag.');
  }

  const { headers, messageLines } = parseTagObject(tagObject);
  const extensionVersion = tagMatch[1];
  if (
    headers.get('type') !== 'commit' ||
    headers.get('tag') !== tagName ||
    !fullCommitPattern.test(headers.get('object') ?? '')
  ) {
    throw new Error('Annotated release tag headers do not bind the expected tag name to one commit.');
  }
  const targetCommit = headers.get('object');
  const title = messageLines.shift();
  if (title !== `VBA Tools ${extensionVersion}`) {
    throw new Error(`Annotated release tag title must be VBA Tools ${extensionVersion}.`);
  }

  const trailers = new Map();
  for (const line of messageLines.filter((value) => value.length > 0)) {
    const match = /^([^:]+):\s+(.+)$/.exec(line);
    if (!match || !allowedTrailers.has(match[1])) {
      throw new Error(`Unrecognized tag trailer: ${line}`);
    }
    if (trailers.has(match[1])) {
      throw new Error(`Duplicate tag trailer: ${match[1]}`);
    }
    trailers.set(match[1], match[2]);
  }
  for (const trailerName of requiredTrailers) {
    if (!trailers.has(trailerName)) {
      throw new Error(`Annotated release tag is missing ${trailerName}.`);
    }
  }

  if (trailers.get('Windows-Excel-Verification-Commit') !== targetCommit) {
    throw new Error('Windows Excel verification commit must equal the annotated tag target.');
  }
  if (trailers.get('Windows-Excel-Verification-Result') !== 'pass') {
    throw new Error('Windows Excel verification result must be pass.');
  }
  const cleanWindowsSmoke = trailers.get('Clean-Windows-Smoke');
  if (cleanWindowsSmoke !== 'pass' && cleanWindowsSmoke !== 'not-required') {
    throw new Error('Clean Windows smoke must be pass or not-required.');
  }
  if (extensionVersion === '0.1.0' && cleanWindowsSmoke !== 'pass') {
    throw new Error('The initial 0.1.0 release requires Clean Windows smoke to pass.');
  }
  if (
    cleanWindowsSmoke === 'not-required' &&
    !trailers.get('Clean-Windows-Smoke-Reason')?.trim()
  ) {
    throw new Error('Clean Windows smoke not-required evidence needs a reason.');
  }

  const vbaDevVersion = readVbaDevVersion(vbaDevProps);
  const channel = trailers.get('Channel');
  validateReleaseInputs({ extensionVersion, channel, vbaDevVersion });
  if (
    packageJson?.name !== 'vba-tools' ||
    packageJson?.publisher !== 'modern-vba' ||
    packageJson?.version !== extensionVersion ||
    packageLock?.version !== extensionVersion ||
    packageLock?.packages?.['']?.version !== extensionVersion
  ) {
    throw new Error(`Reviewed extension version metadata must consistently identify ${extensionVersion}.`);
  }

  return {
    tagName,
    targetCommit,
    extensionVersion,
    channel,
    vbaDevVersion,
    releaseTitle: `VBA Tools ${extensionVersion}`,
    vsixName: `vba-tools-win32-x64-${extensionVersion}.vsix`,
    cliArchiveName: `vba-dev-win-x64-${vbaDevVersion}.zip`
  };
}

export function assertMarketplaceVisibility(metadata, expected) {
  const matchingVersion = findMarketplaceVisibility(metadata, expected);
  if (!matchingVersion) {
    throw new Error(
      `Marketplace version ${expected.version}, target ${expected.targetPlatform}, and channel ${expected.channel} are not visible.`
    );
  }
  return matchingVersion;
}

export function findMarketplaceVisibility(metadata, expected) {
  if (metadata?.publisher?.publisherName !== expected.publisher) {
    throw new Error(`Marketplace publisher must be ${expected.publisher}.`);
  }
  if (metadata?.extensionName !== expected.extensionName) {
    throw new Error(`Marketplace extension must be ${expected.extensionName}.`);
  }
  const matchingVersion = metadata?.versions?.find((candidate) => {
    const preReleaseProperty = candidate.properties?.find(
      (property) => property.key === 'Microsoft.VisualStudio.Code.PreRelease'
    );
    const isPreRelease = preReleaseProperty?.value === 'true';
    return candidate.version === expected.version &&
      candidate.targetPlatform === expected.targetPlatform &&
      (expected.channel === 'pre-release' ? isPreRelease : !isPreRelease);
  });
  return matchingVersion;
}

export function validateDraftReleaseMetadata(release, expected) {
  const expectedPrerelease = expected.channel === 'pre-release';
  if (
    release?.tag_name !== expected.tagName ||
    release?.name !== expected.releaseTitle ||
    release?.draft !== true ||
    release?.prerelease !== expectedPrerelease
  ) {
    throw new Error('Resume requires the existing draft release with matching tag, title, and channel.');
  }
  const expectedAssetNames = [
    'SHA256SUMS',
    expected.cliArchiveName,
    expected.vsixName
  ].sort();
  const assets = Array.isArray(release.assets) ? release.assets : [];
  const actualAssetNames = assets.map((asset) => asset.name).sort();
  if (
    JSON.stringify(actualAssetNames) !== JSON.stringify(expectedAssetNames) ||
    assets.some((asset) => asset.state !== 'uploaded' || !(asset.size > 0))
  ) {
    throw new Error(
      `Draft release must contain the exact uploaded asset set: ${expectedAssetNames.join(', ')}.`
    );
  }
  return { assetNames: expectedAssetNames };
}

export async function validateStagedReleaseAssets({
  directory,
  extensionVersion,
  channel,
  vbaDevVersion,
  sourceRoot = process.cwd(),
  runCommand = runCommandWithSpawn
}) {
  await verifyReleaseArtifactSet({
    outputDirectory: directory,
    extensionVersion,
    vbaDevVersion
  });
  const vsixPath = path.join(
    directory,
    `vba-tools-win32-x64-${extensionVersion}.vsix`
  );
  const cliArchivePath = path.join(
    directory,
    `vba-dev-win-x64-${vbaDevVersion}.zip`
  );
  const packaged = await inspectVsixPackage(vsixPath);
  if (
    packaged.packageJson?.publisher !== 'modern-vba' ||
    packaged.packageJson?.name !== 'vba-tools' ||
    packaged.packageJson?.version !== extensionVersion
  ) {
    throw new Error('Staged VSIX identity disagrees with the reviewed extension release.');
  }
  const distributionManifest = readDistributionManifest(sourceRoot);
  assertVsixContents([...packaged.files.keys()], distributionManifest);
  assertPackagedVsixMetadata(packaged.vsixManifest, packaged.packageJson, 'win32-x64');
  assertVsixReleaseChannel(packaged.vsixManifest, channel);

  const [vsixEntries, cliEntries] = await Promise.all([
    readZipEntries(vsixPath),
    readZipEntries(cliArchivePath)
  ]);
  const bundledExecutable = vsixEntries.get(
    'extension/bin/vba-dev/win-x64/vba-dev.exe'
  );
  const bundledCliContractEntry = vsixEntries.get('extension/vba-dev-contract.json');
  const bundledDebugAdapter = vsixEntries.get(
    'extension/bin/vba-debug-adapter/win-x64/vba-debug-adapter.exe'
  );
  const debugAdapterContractEntry = vsixEntries.get(
    'extension/vba-debug-adapter-contract.json'
  );
  const bundledLanguageServer = vsixEntries.get(
    'extension/bin/vba-language-server/win-x64/vba-language-server.exe'
  );
  const standaloneExecutable = cliEntries.get('vba-dev.exe');
  const standaloneCliContractEntry = cliEntries.get('vba-dev-contract.json');
  if (!bundledExecutable || !standaloneExecutable || !bundledExecutable.equals(standaloneExecutable)) {
    throw new Error('Standalone ZIP must contain the exact bundled vba-dev executable from the VSIX.');
  }

  const reviewedCliContract = readRequiredVbaDevContract(
    sourceRoot,
    distributionManifest
  );
  const reviewedDebugAdapterContract = readRequiredVbaDebugAdapterContract(
    sourceRoot,
    distributionManifest
  );
  const {
    debugAdapterVersion: reviewedDebugAdapterVersion,
    languageServerVersion: reviewedLanguageServerVersion
  } = await readReviewedBundledRuntimeVersions(sourceRoot);
  const bundledCliContract = parseJsonEntry(
    bundledCliContractEntry,
    'Staged VSIX vba-dev contract'
  );
  const standaloneCliContract = parseJsonEntry(
    standaloneCliContractEntry,
    'Standalone vba-dev contract'
  );
  const bundledDebugAdapterContract = parseJsonEntry(
    debugAdapterContractEntry,
    'Staged VSIX vba-debug-adapter contract'
  );
  if (
    !isDeepStrictEqual(bundledCliContract, reviewedCliContract)
    || !isDeepStrictEqual(standaloneCliContract, reviewedCliContract)
  ) {
    throw new Error('Staged artifacts must contain the reviewed vba-dev contract.');
  }
  if (!isDeepStrictEqual(bundledDebugAdapterContract, reviewedDebugAdapterContract)) {
    throw new Error('Staged VSIX must contain the reviewed vba-debug-adapter contract.');
  }

  const cliEntryNames = [...cliEntries.keys()].sort();
  const requiredNames = ['LICENSE', 'README.md', 'vba-dev-contract.json', 'vba-dev.exe'];
  const pdbNames = cliEntryNames.filter((name) => name.toLowerCase().endsWith('.pdb'));
  const expectedNames = [...requiredNames, ...pdbNames].sort();
  if (
    pdbNames.length === 0 ||
    JSON.stringify(cliEntryNames) !== JSON.stringify(expectedNames)
  ) {
    throw new Error(
      'Standalone ZIP must contain only vba-dev.exe, every published PDB, README.md, LICENSE, and vba-dev-contract.json.'
    );
  }

  const temporaryDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-release-assets-'));
  try {
    const executablePath = path.join(temporaryDirectory, 'vba-dev.exe');
    const debugAdapterPath = path.join(temporaryDirectory, 'vba-debug-adapter.exe');
    const languageServerPath = path.join(temporaryDirectory, 'vba-language-server.exe');
    await fs.writeFile(executablePath, standaloneExecutable);
    if (!bundledDebugAdapter || !debugAdapterContractEntry) {
      throw new Error('Staged VSIX must contain the independent vba-debug-adapter executable and contract.');
    }
    if (!bundledLanguageServer) {
      throw new Error('Staged VSIX must contain the independent vba-language-server executable.');
    }
    await fs.writeFile(debugAdapterPath, bundledDebugAdapter);
    await fs.writeFile(languageServerPath, bundledLanguageServer);
    await fs.chmod(executablePath, 0o755);
    await fs.chmod(debugAdapterPath, 0o755);
    await fs.chmod(languageServerPath, 0o755);
    const versionProbe = await runCommand(executablePath, ['--version'], temporaryDirectory);
    if (
      versionProbe.stdout.replace(/\r\n/g, '\n') !== `vba-dev ${vbaDevVersion}\n` ||
      versionProbe.stderr !== ''
    ) {
      throw new Error(`Staged vba-dev --version must identify ${vbaDevVersion}.`);
    }
    const helpProbe = await runCommand(executablePath, ['--help'], temporaryDirectory);
    const normalizedHelp = helpProbe.stdout.replace(/\r\n/g, '\n');
    const hasRootInvocation = normalizedHelp
      .split('\n')
      .some((line) => line.trim() === 'vba-dev [command] [options]');
    const hasPublicGraph = /^[ \t]*build[ \t]+/m.test(normalizedHelp)
      && /^[ \t]*capabilities[ \t]+/m.test(normalizedHelp)
      && normalizedHelp.includes('--help');
    if (!hasRootInvocation || !hasPublicGraph || helpProbe.stderr !== '') {
      throw new Error('Staged vba-dev --help probe failed.');
    }
    const capabilitiesProbe = await runCommand(
      executablePath,
      ['capabilities', '--format', 'json'],
      temporaryDirectory
    );
    let capabilities;
    try {
      capabilities = JSON.parse(capabilitiesProbe.stdout);
    } catch (error) {
      throw new Error(`Staged vba-dev capabilities must be JSON: ${String(error)}`);
    }
    const validatedCapabilities = assertBundledCliCapabilities(
      JSON.stringify(capabilities),
      reviewedCliContract
    );
    if (validatedCapabilities.toolVersion !== vbaDevVersion) {
      throw new Error(`Staged vba-dev capabilities must identify ${vbaDevVersion}.`);
    }
    const debugAdapterProbe = await runCommand(
      debugAdapterPath,
      ['capabilities', '--format', 'json'],
      temporaryDirectory
    );
    if (debugAdapterProbe.stderr !== '') {
      throw new Error('Staged vba-debug-adapter capabilities must not write to stderr.');
    }
    const validatedDebugAdapterCapabilities = assertBundledDebugAdapterCapabilities(
      debugAdapterProbe.stdout,
      reviewedDebugAdapterContract
    );
    if (validatedDebugAdapterCapabilities.toolVersion !== reviewedDebugAdapterVersion) {
      throw new Error(
        `Staged vba-debug-adapter capabilities must identify ${reviewedDebugAdapterVersion}.`
      );
    }
    const languageServerProbe = await runCommand(
      languageServerPath,
      ['--version'],
      temporaryDirectory
    );
    if (languageServerProbe.stderr !== '') {
      throw new Error('Staged vba-language-server version probe must not write to stderr.');
    }
    try {
      assertBundledLanguageServerVersion(languageServerProbe.stdout);
    } catch (error) {
      throw new Error(`Staged vba-language-server version probe failed: ${String(error)}`);
    }
    if (
      languageServerProbe.stdout.replace(/\r\n/g, '\n') !==
      `vba-language-server ${reviewedLanguageServerVersion}\n`
    ) {
      throw new Error(
        `Staged vba-language-server --version must identify ${reviewedLanguageServerVersion}.`
      );
    }
  } finally {
    await fs.rm(temporaryDirectory, { recursive: true, force: true });
  }

  return { vsixPath, cliArchivePath };
}

export async function runReleaseWorkflowCommand(
  args = process.argv.slice(2),
  { root = process.cwd(), runCommand = runCommandWithSpawn } = {}
) {
  const parsed = parseWorkflowCommandArguments(args);
  let result;
  if (parsed.command === 'validate-tag') {
    const tagRef = `refs/tags/${parsed.tag}`;
    const [{ stdout: tagType }, { stdout: tagObject }] = await Promise.all([
      runCommand('git', ['cat-file', '-t', tagRef], root),
      runCommand('git', ['cat-file', '-p', tagRef], root)
    ]);
    const targetRef = `${tagRef}^{commit}`;
    const [packageJsonText, packageLockText, vbaDevPropsResult] = await Promise.all([
      runCommand('git', ['show', `${targetRef}:package.json`], root),
      runCommand('git', ['show', `${targetRef}:package-lock.json`], root),
      runCommand('git', ['show', `${targetRef}:tools/vba-dev/Directory.Build.props`], root)
    ]);
    const packageJson = JSON.parse(packageJsonText.stdout);
    const packageLock = JSON.parse(packageLockText.stdout);
    const vbaDevProps = vbaDevPropsResult.stdout;
    result = validateAnnotatedReleaseTag({
      tagName: parsed.tag,
      tagType: tagType.trim(),
      tagObject,
      packageJson,
      packageLock,
      vbaDevProps
    });
    const resolvedCommit = (await runCommand(
      'git',
      ['rev-parse', targetRef],
      root
    )).stdout.trim();
    if (resolvedCommit !== result.targetCommit) {
      throw new Error('Annotated release tag target does not resolve to its declared commit.');
    }
    const containingBranches = (await runCommand(
      'git',
      ['branch', '--remotes', '--contains', resolvedCommit],
      root
    )).stdout.split(/\r?\n/).map((branch) => branch.trim());
    if (!containingBranches.includes('origin/main')) {
      throw new Error('Release tag target must be contained in origin/main.');
    }
    await fs.writeFile(parsed.metadata, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
    await writeGithubOutputs(parsed.githubOutput, {
      tag_name: result.tagName,
      target_commit: result.targetCommit,
      extension_version: result.extensionVersion,
      channel: result.channel,
      vba_dev_version: result.vbaDevVersion,
      release_title: result.releaseTitle,
      vsix_name: result.vsixName,
      cli_archive_name: result.cliArchiveName
    });
  } else if (parsed.command === 'validate-assets') {
    result = await validateStagedReleaseAssets({
      directory: parsed.directory,
      extensionVersion: parsed.extensionVersion,
      channel: parsed.channel,
      vbaDevVersion: parsed.vbaDevVersion,
      sourceRoot: root
    });
  } else if (parsed.command === 'validate-draft') {
    const release = await readJson(parsed.releaseJson);
    const expected = releaseExpectation(parsed);
    validateDraftReleaseMetadata(release, expected);
    result = await validateStagedReleaseAssets({
      directory: parsed.directory,
      extensionVersion: parsed.extensionVersion,
      channel: parsed.channel,
      vbaDevVersion: parsed.vbaDevVersion,
      sourceRoot: root
    });
  } else {
    const metadata = await readJson(parsed.marketplaceJson);
    const expected = {
      publisher: 'modern-vba',
      extensionName: 'vba-tools',
      version: parsed.extensionVersion,
      targetPlatform: 'win32-x64',
      channel: parsed.channel
    };
    const matchingVersion = parsed.command === 'verify-marketplace'
      ? assertMarketplaceVisibility(metadata, expected)
      : findMarketplaceVisibility(metadata, expected);
    result = { marketplaceVisible: Boolean(matchingVersion) };
    await writeGithubOutputs(parsed.githubOutput, {
      marketplace_visible: String(result.marketplaceVisible)
    });
  }
  console.log(JSON.stringify(result, null, 2));
  return result;
}

function parseWorkflowCommandArguments(args) {
  const command = args[0];
  const commandOptions = {
    'validate-tag': new Map([
      ['--tag', 'tag'],
      ['--metadata', 'metadata'],
      ['--github-output', 'githubOutput']
    ]),
    'validate-assets': assetOptions(),
    'validate-draft': new Map([
      ...assetOptions(),
      ['--release-json', 'releaseJson'],
      ['--tag', 'tag'],
      ['--title', 'releaseTitle'],
      ['--vsix-name', 'vsixName'],
      ['--cli-archive-name', 'cliArchiveName']
    ]),
    'marketplace-state': new Map([
      ['--marketplace-json', 'marketplaceJson'],
      ['--extension-version', 'extensionVersion'],
      ['--channel', 'channel'],
      ['--github-output', 'githubOutput']
    ]),
    'verify-marketplace': new Map([
      ['--marketplace-json', 'marketplaceJson'],
      ['--extension-version', 'extensionVersion'],
      ['--channel', 'channel']
    ])
  };
  const allowedOptions = commandOptions[command];
  if (!allowedOptions) {
    throw new Error('Release workflow command is not recognized.');
  }
  const parsed = { command };
  for (let index = 1; index < args.length; index += 2) {
    const option = args[index];
    const field = allowedOptions.get(option);
    const value = args[index + 1];
    if (!field || !value || value.startsWith('--')) {
      throw new Error(`Invalid release workflow option ${option}.`);
    }
    if (parsed[field] !== undefined) {
      throw new Error(`Release workflow option ${option} was supplied more than once.`);
    }
    parsed[field] = ['metadata', 'directory', 'releaseJson', 'marketplaceJson', 'githubOutput']
      .includes(field)
      ? path.resolve(value)
      : value;
  }
  const optional = new Set(['githubOutput']);
  const missing = [...allowedOptions.values()].filter(
    (field) => !optional.has(field) && parsed[field] === undefined
  );
  if (missing.length > 0) {
    throw new Error(`Release workflow options are missing: ${missing.join(', ')}.`);
  }
  return parsed;
}

function assetOptions() {
  return new Map([
    ['--directory', 'directory'],
    ['--extension-version', 'extensionVersion'],
    ['--channel', 'channel'],
    ['--vba-dev-version', 'vbaDevVersion']
  ]);
}

function releaseExpectation(parsed) {
  return {
    tagName: parsed.tag,
    releaseTitle: parsed.releaseTitle,
    channel: parsed.channel,
    vsixName: parsed.vsixName,
    cliArchiveName: parsed.cliArchiveName
  };
}

async function writeGithubOutputs(outputPath, values) {
  if (!outputPath) {
    return;
  }
  const lines = Object.entries(values).map(([name, value]) => {
    if (String(value).includes('\n')) {
      throw new Error(`GitHub output ${name} must be a single line.`);
    }
    return `${name}=${value}`;
  });
  await fs.appendFile(outputPath, `${lines.join('\n')}\n`, 'utf8');
}

async function readJson(filePath) {
  return JSON.parse(await fs.readFile(filePath, 'utf8'));
}

async function readReviewedBundledRuntimeVersions(sourceRoot) {
  const [debugAdapterProps, languageServerSource] = await Promise.all([
    fs.readFile(
      path.join(sourceRoot, 'tools/vba-debug-adapter/Directory.Build.props'),
      'utf8'
    ),
    fs.readFile(
      path.join(sourceRoot, 'tools/vba-language-server/src/VbaLanguageServer.Cli/Program.cs'),
      'utf8'
    )
  ]);
  const debugAdapterMatch = debugAdapterProps.match(
    /<VbaDebugAdapterReleaseVersion>\s*(\d+\.\d+\.\d+)\s*<\/VbaDebugAdapterReleaseVersion>/
  );
  const languageServerMatch = languageServerSource.match(
    /Console\.WriteLine\("vba-language-server (\d+\.\d+\.\d+)"\)/
  );
  if (!debugAdapterMatch || !languageServerMatch) {
    throw new Error('Reviewed bundled runtime version metadata is incomplete.');
  }
  return {
    debugAdapterVersion: debugAdapterMatch[1],
    languageServerVersion: languageServerMatch[1]
  };
}

function parseJsonEntry(entry, label) {
  if (!entry) {
    throw new Error(`${label} is missing.`);
  }
  try {
    return JSON.parse(entry.toString('utf8'));
  } catch (error) {
    throw new Error(`${label} must be JSON: ${String(error)}`);
  }
}

function parseTagObject(tagObject) {
  const lines = String(tagObject ?? '').replace(/\r\n/g, '\n').split('\n');
  const blankLineIndex = lines.indexOf('');
  if (blankLineIndex < 0) {
    throw new Error('Annotated release tag object is missing its message.');
  }
  const headers = new Map();
  for (const line of lines.slice(0, blankLineIndex)) {
    const separator = line.indexOf(' ');
    if (separator <= 0) {
      throw new Error(`Invalid annotated tag header: ${line}`);
    }
    const name = line.slice(0, separator);
    if (headers.has(name)) {
      throw new Error(`Duplicate annotated tag header: ${name}`);
    }
    headers.set(name, line.slice(separator + 1));
  }
  const messageLines = lines.slice(blankLineIndex + 1);
  while (messageLines.at(-1) === '') {
    messageLines.pop();
  }
  return { headers, messageLines };
}

function readVbaDevVersion(props) {
  const versionMatch = String(props ?? '').match(
    /<VbaDevReleaseVersion>\s*([^<]+?)\s*<\/VbaDevReleaseVersion>/
  );
  const boundProperties = ['Version', 'VersionPrefix', 'PackageVersion', 'InformationalVersion'];
  if (
    !versionMatch ||
    !/^\d+\.\d+\.\d+$/.test(versionMatch[1]) ||
    !boundProperties.every((propertyName) => new RegExp(
      `<${propertyName}>\\$\\(VbaDevReleaseVersion\\)</${propertyName}>`
    ).test(props))
  ) {
    throw new Error('Reviewed vba-dev version metadata is inconsistent.');
  }
  return versionMatch[1];
}

function runCommandWithSpawn(file, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd, windowsHide: true });
    child.stdin?.end();
    let stdout = '';
    let stderr = '';
    child.stdout?.on('data', (chunk) => { stdout += chunk.toString('utf8'); });
    child.stderr?.on('data', (chunk) => { stderr += chunk.toString('utf8'); });
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

function readZipEntries(zipPath) {
  return new Promise((resolve, reject) => {
    yauzl.open(zipPath, { lazyEntries: true }, (openError, zipFile) => {
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

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    await runReleaseWorkflowCommand();
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
