import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { promises as fs } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  createStandaloneVbaDevArchive,
  writeReleaseChecksums
} from './vbaDevReleasePackage.mjs';
import {
  assertExtensionDebugPackage,
  assertMarketplacePackageMetadata,
  assertPackagedMarkdownLinks,
  assertPackagedVsixMetadata,
  assertVsixContents,
  inspectVsixPackage,
  readDistributionManifest
} from './vsixPackagingRules.mjs';

const canonicalSemVerPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;

export function validateReleaseInputs({ extensionVersion, channel, vbaDevVersion }) {
  const extensionMatch = canonicalSemVerPattern.exec(extensionVersion ?? '');
  if (!extensionMatch || !canonicalSemVerPattern.test(vbaDevVersion ?? '')) {
    throw new Error('Release input versions must use canonical three-part SemVer.');
  }
  if (channel !== 'pre-release' && channel !== 'stable') {
    throw new Error('Release input channel must be pre-release or stable.');
  }

  const minorVersion = Number(extensionMatch[2]);
  if (
    channel === 'pre-release' && minorVersion % 2 === 0 ||
    channel === 'stable' && minorVersion % 2 !== 0
  ) {
    throw new Error('Release input channel violates the odd-minor pre-release and even-minor stable policy.');
  }

  return {
    extensionVersion,
    channel,
    vbaDevVersion,
    extensionTag: `vba-tools-v${extensionVersion}`,
    cliTag: `vba-dev-v${vbaDevVersion}`
  };
}

export function parseReleaseCommandArguments(args) {
  const command = args[0];
  const commandFields = {
    prepare: new Map([
      ['--extension-version', 'extensionVersion'],
      ['--channel', 'channel'],
      ['--vba-dev-version', 'vbaDevVersion']
    ]),
    tag: new Map([
      ['--version', 'extensionVersion'],
      ['--extension-version', 'extensionVersion'],
      ['--channel', 'channel'],
      ['--vba-dev-version', 'vbaDevVersion'],
      ['--verified-commit', 'verifiedCommit'],
      ['--windows-excel-result', 'windowsExcelResult'],
      ['--clean-windows-smoke', 'cleanWindowsSmoke'],
      ['--clean-windows-smoke-reason', 'cleanWindowsSmokeReason']
    ]),
    artifacts: new Map([
      ['--extension-version', 'extensionVersion'],
      ['--channel', 'channel'],
      ['--vba-dev-version', 'vbaDevVersion'],
      ['--output', 'outputDirectory']
    ])
  };
  const allowedFields = commandFields[command];
  if (!allowedFields) {
    throw new Error('Release command must be prepare, tag, or artifacts.');
  }

  const parsed = { command };
  for (let index = 1; index < args.length; index += 2) {
    const argument = args[index];
    const field = allowedFields.get(argument);
    if (!field) {
      throw new Error(`Unknown release argument ${argument}.`);
    }
    const value = args[index + 1];
    if (!value || value.startsWith('--')) {
      throw new Error(`Release argument ${argument} requires a value.`);
    }
    if (parsed[field] !== undefined) {
      throw new Error(`Release argument ${argument} was supplied more than once.`);
    }
    parsed[field] = field === 'outputDirectory' ? path.resolve(value) : value;
  }

  const requiredFields = command === 'tag'
    ? [
      'extensionVersion',
      'channel',
      'vbaDevVersion',
      'verifiedCommit',
      'windowsExcelResult',
      'cleanWindowsSmoke'
    ]
    : ['extensionVersion', 'channel', 'vbaDevVersion'];
  const missingFields = requiredFields.filter((field) => parsed[field] === undefined);
  if (missingFields.length > 0) {
    const names = missingFields.map((field) => field.replace(/[A-Z]/g, (letter) => (
      `-${letter.toLowerCase()}`
    )));
    throw new Error(`Required release arguments are missing: ${names.join(', ')}.`);
  }
  return parsed;
}

export async function runReleaseCommand(args = process.argv.slice(2)) {
  const parsed = parseReleaseCommandArguments(args);
  let result;
  if (parsed.command === 'prepare') {
    result = await prepareRelease(parsed);
  } else if (parsed.command === 'tag') {
    result = await createReleaseTag(parsed);
  } else {
    result = await assembleReleaseArtifacts(parsed);
  }
  console.log(JSON.stringify(result, null, 2));
  return result;
}

export async function prepareRelease({
  root = process.cwd(),
  extensionVersion,
  channel,
  vbaDevVersion,
  releaseDate = new Date().toISOString().slice(0, 10),
  runCommand = runCommandWithSpawn
}) {
  const inputs = validateReleaseInputs({ extensionVersion, channel, vbaDevVersion });
  await assertCleanWorktree(root, runCommand);

  const packagePath = path.join(root, 'package.json');
  const packageLockPath = path.join(root, 'package-lock.json');
  const propsPath = path.join(root, 'tools/vba-dev/Directory.Build.props');
  const extensionChangelogPath = path.join(root, 'CHANGELOG.md');
  const cliChangelogPath = path.join(root, 'tools/vba-dev/CHANGELOG.md');
  const [packageJson, packageLock, props, extensionChangelog, cliChangelog] = await Promise.all([
    readJson(packagePath),
    readJson(packageLockPath),
    fs.readFile(propsPath, 'utf8'),
    fs.readFile(extensionChangelogPath, 'utf8'),
    fs.readFile(cliChangelogPath, 'utf8')
  ]);
  if (
    packageJson.version !== packageLock.version ||
    packageJson.version !== packageLock.packages?.['']?.version
  ) {
    throw new Error('Release preparation found inconsistent extension version metadata.');
  }

  const currentCliVersion = readCanonicalVbaDevVersion(
    props,
    'Release preparation found inconsistent vba-dev version metadata.'
  );
  const cliVersionChanged = currentCliVersion !== vbaDevVersion;

  await assertUnusedTag(root, inputs.extensionTag, runCommand);
  if (cliVersionChanged) {
    await assertUnusedTag(root, inputs.cliTag, runCommand);
  }

  packageJson.version = extensionVersion;
  packageLock.version = extensionVersion;
  packageLock.packages[''].version = extensionVersion;
  const updatedProps = cliVersionChanged
    ? props.replace(
      /(<VbaDevReleaseVersion>)\s*[^<]+?\s*(<\/VbaDevReleaseVersion>)/,
      `$1${vbaDevVersion}$2`
    )
    : props;
  const updatedExtensionChangelog = await prepareChangelog({
    changelog: extensionChangelog,
    version: extensionVersion,
    releaseDate,
    tagPrefix: 'vba-tools-v',
    root,
    runCommand
  });
  const updatedCliChangelog = cliVersionChanged
    ? await prepareChangelog({
      changelog: cliChangelog,
      version: vbaDevVersion,
      releaseDate,
      tagPrefix: 'vba-dev-v',
      root,
      runCommand
    })
    : setReleaseDate(cliChangelog, vbaDevVersion, releaseDate, true);

  await Promise.all([
    writeJson(packagePath, packageJson),
    writeJson(packageLockPath, packageLock),
    fs.writeFile(propsPath, updatedProps, 'utf8'),
    fs.writeFile(extensionChangelogPath, updatedExtensionChangelog, 'utf8'),
    fs.writeFile(cliChangelogPath, updatedCliChangelog, 'utf8')
  ]);

  return {
    extensionVersion,
    channel,
    vbaDevVersion,
    cliVersionChanged,
    expectedArtifacts: [
      `vba-tools-win32-x64-${extensionVersion}.vsix`,
      `vba-dev-win-x64-${vbaDevVersion}.zip`,
      'SHA256SUMS'
    ]
  };
}

export async function createReleaseTag({
  root = process.cwd(),
  extensionVersion,
  channel,
  vbaDevVersion,
  verifiedCommit,
  windowsExcelResult,
  cleanWindowsSmoke,
  cleanWindowsSmokeReason,
  runCommand = runCommandWithSpawn
}) {
  const inputs = validateReleaseInputs({ extensionVersion, channel, vbaDevVersion });
  await assertCleanWorktree(root, runCommand);
  if (!/^[0-9a-f]{40}$/.test(verifiedCommit ?? '')) {
    throw new Error('The manually verified commit must be a full Git commit SHA.');
  }
  if (windowsExcelResult !== 'pass') {
    throw new Error('Windows Excel verification result must be pass before tagging.');
  }
  if (cleanWindowsSmoke !== 'pass' && cleanWindowsSmoke !== 'not-required') {
    throw new Error('Clean Windows smoke must be pass or not-required.');
  }
  if (extensionVersion === '0.1.0' && cleanWindowsSmoke !== 'pass') {
    throw new Error('The initial 0.1.0 release requires Clean Windows smoke to pass.');
  }
  if (cleanWindowsSmoke === 'not-required' && !cleanWindowsSmokeReason?.trim()) {
    throw new Error('Clean Windows smoke not-required evidence needs a concise reason.');
  }

  const head = (await runCommand('git', ['rev-parse', 'HEAD'], root)).stdout.trim();
  if (head !== verifiedCommit) {
    throw new Error(`The manually verified commit ${verifiedCommit} must equal HEAD ${head}.`);
  }
  const originMain = (await runCommand(
    'git',
    ['rev-parse', '--verify', 'refs/remotes/origin/main'],
    root
  )).stdout.trim();
  if (originMain !== head) {
    throw new Error(`HEAD ${head} must exactly match origin/main ${originMain}.`);
  }

  await assertReleaseMetadata(root, inputs);
  await assertUnusedTag(root, inputs.extensionTag, runCommand);
  const messageLines = [
    `VBA Tools ${extensionVersion}`,
    '',
    `Channel: ${channel}`,
    `Windows-Excel-Verification-Commit: ${verifiedCommit}`,
    `Windows-Excel-Verification-Result: ${windowsExcelResult}`,
    `Clean-Windows-Smoke: ${cleanWindowsSmoke}`
  ];
  if (cleanWindowsSmoke === 'not-required') {
    messageLines.push(`Clean-Windows-Smoke-Reason: ${cleanWindowsSmokeReason.trim()}`);
  }
  await runCommand(
    'git',
    ['tag', '--annotate', inputs.extensionTag, verifiedCommit, '--message', messageLines.join('\n')],
    root
  );
  return {
    tagName: inputs.extensionTag,
    targetCommit: verifiedCommit,
    pushed: false
  };
}

export async function assembleReleaseArtifacts({
  root = process.cwd(),
  outputDirectory = path.join(root, '.tmp', 'release-set'),
  extensionVersion,
  channel,
  vbaDevVersion,
  runCommand = runCommandWithSpawn,
  createCliArchive = createStandaloneVbaDevArchive,
  validateVsix = validateReleaseVsix
}) {
  const inputs = validateReleaseInputs({ extensionVersion, channel, vbaDevVersion });
  await assertReleaseMetadata(root, inputs);
  await assertEmptyStagingDirectory(outputDirectory);
  await fs.mkdir(outputDirectory, { recursive: true });

  const npmCliPath = process.env.npm_execpath ?? path.join(
    path.dirname(process.execPath),
    'node_modules',
    'npm',
    'bin',
    'npm-cli.js'
  );
  await runCommand(process.execPath, [npmCliPath, 'run', 'verify:release'], root);
  const vsixPath = path.join(
    outputDirectory,
    `vba-tools-win32-x64-${extensionVersion}.vsix`
  );
  const vsceArguments = [
    path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce'),
    'package',
    '--no-dependencies',
    '--target',
    'win32-x64'
  ];
  if (channel === 'pre-release') {
    vsceArguments.push('--pre-release');
  }
  vsceArguments.push('--out', vsixPath);
  await runCommand(process.execPath, vsceArguments, root);

  let cliArchive;
  try {
    cliArchive = await createCliArchive({ root, outputDirectory });
    if (
      cliArchive.version !== vbaDevVersion ||
      path.basename(cliArchive.archivePath) !== `vba-dev-win-x64-${vbaDevVersion}.zip`
    ) {
      throw new Error('Standalone vba-dev archive metadata disagrees with the requested release set.');
    }
    await validateVsix({ root, vsixPath, extensionVersion, channel });
    const checksumPath = await writeReleaseChecksums(
      outputDirectory,
      [vsixPath, cliArchive.archivePath]
    );
    await verifyReleaseArtifactSet({
      outputDirectory,
      extensionVersion,
      vbaDevVersion
    });
    return {
      vsixPath,
      cliArchivePath: cliArchive.archivePath,
      checksumPath
    };
  } finally {
    await cliArchive?.cleanup?.();
  }
}

export async function verifyReleaseArtifactSet({
  outputDirectory,
  extensionVersion,
  vbaDevVersion
}) {
  const expectedAssetNames = [
    'SHA256SUMS',
    `vba-dev-win-x64-${vbaDevVersion}.zip`,
    `vba-tools-win32-x64-${extensionVersion}.vsix`
  ].sort();
  const actualNames = (await fs.readdir(outputDirectory)).sort();
  if (JSON.stringify(actualNames) !== JSON.stringify(expectedAssetNames)) {
    throw new Error(
      `Release staging directory must contain exactly ${expectedAssetNames.join(', ')}.`
    );
  }

  const checksumLines = (await fs.readFile(
    path.join(outputDirectory, 'SHA256SUMS'),
    'utf8'
  )).trimEnd().split(/\r?\n/);
  const checksums = new Map(checksumLines.map((line) => {
    const match = /^([0-9a-f]{64})  ([^/\\]+)$/.exec(line);
    if (!match) {
      throw new Error(`Invalid SHA256SUMS entry: ${line}`);
    }
    return [match[2], match[1]];
  }));
  const assetNames = expectedAssetNames.filter((fileName) => fileName !== 'SHA256SUMS');
  if (checksumLines.length !== assetNames.length || checksums.size !== assetNames.length) {
    throw new Error('SHA256SUMS must describe exactly the VSIX and standalone CLI ZIP.');
  }
  for (const assetName of assetNames) {
    const contents = await fs.readFile(path.join(outputDirectory, assetName));
    const actualHash = createHash('sha256').update(contents).digest('hex');
    if (checksums.get(assetName) !== actualHash) {
      throw new Error(`SHA256SUMS does not match ${assetName}.`);
    }
  }
}

export function assertVsixReleaseChannel(vsixManifest, channel) {
  const isPreRelease = /<Property\b[^>]*\bId="Microsoft\.VisualStudio\.Code\.PreRelease"[^>]*\bValue="true"[^>]*\/>/i
    .test(vsixManifest);
  if (channel === 'pre-release' && !isPreRelease || channel === 'stable' && isPreRelease) {
    throw new Error(`Generated VSIX metadata does not identify the ${channel} Marketplace channel.`);
  }
}

async function validateReleaseVsix({ root, vsixPath, extensionVersion, channel }) {
  const packaged = await inspectVsixPackage(vsixPath);
  if (packaged.packageJson.version !== extensionVersion) {
    throw new Error(`Generated VSIX version must be ${extensionVersion}.`);
  }
  const manifest = readDistributionManifest(root);
  assertVsixContents([...packaged.files.keys()], manifest);
  assertMarketplacePackageMetadata(packaged.packageJson);
  assertExtensionDebugPackage(packaged.packageJson);
  assertPackagedVsixMetadata(packaged.vsixManifest, packaged.packageJson, 'win32-x64');
  assertPackagedMarkdownLinks(packaged.files);
  assertVsixReleaseChannel(packaged.vsixManifest, channel);
}

async function assertEmptyStagingDirectory(outputDirectory) {
  try {
    if ((await fs.readdir(outputDirectory)).length > 0) {
      throw new Error('Release staging directory must be empty before artifact assembly.');
    }
  } catch (error) {
    if (error?.code !== 'ENOENT') {
      throw error;
    }
  }
}

async function assertReleaseMetadata(root, inputs) {
  const [packageJson, packageLock, props] = await Promise.all([
    readJson(path.join(root, 'package.json')),
    readJson(path.join(root, 'package-lock.json')),
    fs.readFile(path.join(root, 'tools/vba-dev/Directory.Build.props'), 'utf8')
  ]);
  if (
    packageJson.version !== inputs.extensionVersion ||
    packageLock.version !== inputs.extensionVersion ||
    packageLock.packages?.['']?.version !== inputs.extensionVersion
  ) {
    throw new Error(`Reviewed extension metadata must consistently identify ${inputs.extensionVersion}.`);
  }
  const reviewedCliVersion = readCanonicalVbaDevVersion(
    props,
    'Reviewed vba-dev metadata is internally inconsistent.'
  );
  if (reviewedCliVersion !== inputs.vbaDevVersion) {
    throw new Error(`Reviewed vba-dev metadata must identify ${inputs.vbaDevVersion}.`);
  }
}

async function assertCleanWorktree(root, runCommand) {
  const result = await runCommand('git', ['status', '--porcelain'], root);
  if (result.stdout.trim().length > 0) {
    throw new Error('Release commands require a clean worktree at the start.');
  }
}

async function assertUnusedTag(root, tagName, runCommand) {
  const result = await runCommand('git', ['tag', '--list', tagName], root);
  if (result.stdout.trim().length > 0) {
    throw new Error(`Release tag ${tagName} already exists.`);
  }
}

function setReleaseDate(changelog, version, releaseDate, preserveExistingDate = false) {
  const escapedVersion = escapeRegExp(version);
  const heading = new RegExp(
    `^(## \\[${escapedVersion}\\] - )(Unreleased|\\d{4}-\\d{2}-\\d{2})$`,
    'm'
  );
  const match = heading.exec(changelog);
  if (!match) {
    throw new Error(`Changelog must contain a curated ${version} release section.`);
  }
  if (preserveExistingDate && match[2] !== 'Unreleased') {
    return changelog;
  }
  return changelog.replace(heading, `$1${releaseDate}`);
}

function readCanonicalVbaDevVersion(props, errorMessage) {
  const cliVersionMatch = props.match(
    /<VbaDevReleaseVersion>\s*([^<]+?)\s*<\/VbaDevReleaseVersion>/
  );
  const boundVersionProperties = [
    'Version',
    'VersionPrefix',
    'PackageVersion',
    'InformationalVersion'
  ];
  if (
    !cliVersionMatch ||
    !canonicalSemVerPattern.test(cliVersionMatch[1]) ||
    !boundVersionProperties.every((propertyName) => new RegExp(
      `<${propertyName}>\\$\\(VbaDevReleaseVersion\\)</${propertyName}>`
    ).test(props))
  ) {
    throw new Error(errorMessage);
  }
  return cliVersionMatch[1];
}

async function prepareChangelog({
  changelog,
  version,
  releaseDate,
  tagPrefix,
  root,
  runCommand
}) {
  if (new RegExp(`^## \\[${escapeRegExp(version)}\\] - `, 'm').test(changelog)) {
    return setReleaseDate(changelog, version, releaseDate);
  }

  const tagsResult = await runCommand(
    'git',
    ['tag', '--list', `${tagPrefix}*`, '--sort=-v:refname'],
    root
  );
  const previousTag = tagsResult.stdout.split(/\r?\n/).find((tag) => tag.length > 0);
  if (!previousTag) {
    throw new Error(`Changelog must contain a curated ${version} release section.`);
  }
  const logResult = await runCommand(
    'git',
    ['log', '--format=%s', `${previousTag}..HEAD`],
    root
  );
  const releaseSection = formatConventionalCommitSection(
    logResult.stdout.split(/\r?\n/).filter((subject) => subject.length > 0),
    version,
    releaseDate
  );
  const firstReleaseHeading = changelog.search(/^## \[/m);
  if (firstReleaseHeading < 0) {
    return `${changelog.trimEnd()}\n\n${releaseSection}`;
  }
  return `${changelog.slice(0, firstReleaseHeading)}${releaseSection}\n${changelog.slice(firstReleaseHeading)}`;
}

function formatConventionalCommitSection(subjects, version, releaseDate) {
  const categories = new Map([
    ['Added', []],
    ['Changed', []],
    ['Fixed', []]
  ]);
  for (const subject of subjects.reverse()) {
    const match = /^(feat|fix|perf|refactor|docs)(?:\([^)]+\))?(!)?:\s+(.+)$/.exec(subject);
    if (!match) {
      continue;
    }
    const category = match[1] === 'feat'
      ? 'Added'
      : match[1] === 'fix'
        ? 'Fixed'
        : 'Changed';
    const summary = `${match[3][0].toUpperCase()}${match[3].slice(1)}`.replace(/[.!?]?$/, '.');
    categories.get(category).push(`- ${summary}`);
  }
  const populatedCategories = [...categories].filter(([, entries]) => entries.length > 0);
  if (populatedCategories.length === 0) {
    throw new Error('Release changelog generation requires at least one supported Conventional Commit.');
  }
  const body = populatedCategories
    .map(([heading, entries]) => `### ${heading}\n\n${entries.join('\n')}`)
    .join('\n\n');
  return `## [${version}] - ${releaseDate}\n\n${body}\n`;
}

async function readJson(filePath) {
  return JSON.parse(await fs.readFile(filePath, 'utf8'));
}

async function writeJson(filePath, value) {
  await fs.writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

function runCommandWithSpawn(file, args, cwd) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { cwd, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk.toString('utf8'); });
    child.stderr.on('data', (chunk) => { stderr += chunk.toString('utf8'); });
    child.on('error', reject);
    child.on('exit', (exitCode) => {
      if (exitCode !== 0) {
        reject(new Error(`${file} ${args.join(' ')} exited with ${exitCode}: ${stderr}`));
        return;
      }
      resolve({ stdout, stderr });
    });
  });
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    await runReleaseCommand();
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
