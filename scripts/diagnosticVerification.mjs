import { spawn, execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { existsSync, promises as fs } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const maxStreamBytes = 8 * 1024 * 1024;

// These commands build their own prerequisites. Unlike verify:release, this
// diagnostic profile continues to the next independent command after failure.
export const diagnosticReleaseScripts = Object.freeze([
  'verify:architecture',
  'test:extension',
  'test:extension-host',
  'test:devtool',
  'test:debug-adapter',
  'test:language-server',
  'test:syntax-core',
  'test:project-metadata',
  'test:process-invocation',
  'test:capability-admission',
  'test:source-identity',
  'test:cross-product-integration',
  'test:packaging',
  'test:compatibility',
  'package:verify'
]);

const windowsExcelScripts = Object.freeze([
  'build:devtool',
  'build:language-server',
  'test:devtool:windows-excel',
  'test:debug-adapter:windows-excel',
  'test:cross-product:windows-excel'
]);

const prerequisites = Object.freeze({
  'test:devtool:windows-excel': ['build:devtool'],
  'test:debug-adapter:windows-excel': ['build:devtool'],
  'test:cross-product:windows-excel': ['build:devtool', 'build:language-server']
});

export async function runDiagnosticVerification(options = {}) {
  const root = path.resolve(options.root ?? process.cwd());
  if (process.platform === 'win32' && /^(?:\\\\|\/\/)/.test(root)) {
    throw new Error('Diagnostic verification requires a local checkout, not a UNC or device path.');
  }
  const canonicalRoot = await fs.realpath(root);
  if ((process.platform === 'win32' ? canonicalRoot.toLowerCase() : canonicalRoot)
      !== (process.platform === 'win32' ? root.toLowerCase() : root)) {
    throw new Error('Diagnostic verification requires an unlinked local checkout path.');
  }
  const scripts = options.scripts ?? [
    ...diagnosticReleaseScripts,
    ...(options.includeWindowsExcel ? windowsExcelScripts : [])
  ];
  const knownScripts = new Set([...diagnosticReleaseScripts, ...windowsExcelScripts]);
  if (scripts.length === 0 || scripts.some((script) => !knownScripts.has(script))) {
    throw new Error('Diagnostic verification requires known nonempty release-stage scripts.');
  }
  if (scripts.some((script) => windowsExcelScripts.includes(script))
      && (options.hostPlatform ?? process.platform) !== 'win32') {
    throw new Error('Windows Excel diagnostic profile requires Windows.');
  }
  const logLimit = options.maxLogBytes ?? maxStreamBytes;
  if (!Number.isSafeInteger(logLimit) || logLimit < 1 || logLimit > maxStreamBytes) {
    throw new Error('Diagnostic verification log limit must be within the supported bound.');
  }

  const outputBase = path.join(root, '.tmp', 'diagnostic-verification');
  await ensureUnlinkedDirectory(path.join(root, '.tmp'));
  await ensureUnlinkedDirectory(outputBase);
  const started = (options.now?.() ?? new Date()).toISOString();
  const runId = `run-${started.replace(/[-:.]/g, '')}-${options.randomHex?.() ?? randomBytes(8).toString('hex')}`;
  if (!/^run-\d{8}T\d{9}Z-[0-9a-f]{16}$/.test(runId)) {
    throw new Error('Diagnostic verification run ID is invalid.');
  }
  const runRoot = path.join(outputBase, runId);
  await fs.mkdir(runRoot);

  const identity = await (options.getIdentity ?? collectDiagnosticIdentity)(root);
  const manifest = {
    kind: 'vba-tools-diagnostic-verification',
    schemaVersion: 1,
    releaseGate: false,
    profile: scripts.some((script) => windowsExcelScripts.includes(script)) ? 'windows-excel' : 'non-excel',
    startedUtc: started,
    finishedUtc: null,
    complete: false,
    identity,
    plannedScripts: scripts,
    stages: []
  };
  await saveManifest(runRoot, manifest);

  const execute = options.execute ?? executeNpmScript;
  const env = {
    ...process.env,
    VBA_TOOLS_DIAGNOSTIC_RUN_ROOT: runRoot,
    VBA_TOOLS_DIAGNOSTIC_RUN_ID: runId
  };
  for (const [index, script] of scripts.entries()) {
    const prefix = `${String(index + 1).padStart(2, '0')}-${script.replace(/[^a-z0-9._-]/gi, '-')}`;
    const stdoutFile = `${prefix}.stdout.log`;
    const stderrFile = `${prefix}.stderr.log`;
    const stage = {
      script,
      command: `npm run ${script}`,
      status: 'running',
      startedUtc: new Date().toISOString(),
      finishedUtc: null,
      launcherPid: null,
      exitCode: null,
      signal: null,
      stdoutFile,
      stderrFile,
      stdoutTruncated: false,
      stderrTruncated: false,
      stdoutOmittedBytes: 0,
      stderrOmittedBytes: 0,
      blockedBy: null,
      failure: null
    };
    manifest.stages.push(stage);
    await saveManifest(runRoot, manifest);

    const blockedBy = (prerequisites[script] ?? []).filter((required) =>
      manifest.stages.find((prior) => prior.script === required)?.status !== 'passed');
    if (blockedBy.length > 0) {
      stage.status = 'skipped-prerequisite';
      stage.blockedBy = blockedBy;
      stage.stdoutFile = null;
      stage.stderrFile = null;
      stage.finishedUtc = new Date().toISOString();
      await saveManifest(runRoot, manifest);
      continue;
    }

    const openLog = options.openLog ?? createBoundedLog;
    let stdout;
    let stderr;
    try {
      stdout = await openLog(path.join(runRoot, stdoutFile), options.echo === false ? null : process.stdout, logLimit);
      stderr = await openLog(path.join(runRoot, stderrFile), options.echo === false ? null : process.stderr, logLimit);
    } catch (error) {
      await stdout?.close().catch(() => {});
      stage.status = 'capture-failed';
      stage.failure = boundedError(error);
      stage.stdoutFile = stdout ? stdoutFile : null;
      stage.stderrFile = stderr ? stderrFile : null;
      stage.finishedUtc = new Date().toISOString();
      await saveManifest(runRoot, manifest);
      continue;
    }
    try {
      const result = await execute({
        script,
        root,
        env,
        onStdout: stdout.write,
        onStderr: stderr.write
      });
      stage.launcherPid = result.pid ?? null;
      stage.exitCode = result.exitCode ?? null;
      stage.signal = result.signal ?? null;
      stage.status = result.exitCode === 0 && !result.signal ? 'passed' : 'failed';
    } catch (error) {
      stage.status = 'failed';
      stage.failure = boundedError(error);
    } finally {
      await Promise.all([stdout.close(), stderr.close()]);
      stage.stdoutTruncated = stdout.truncated();
      stage.stderrTruncated = stderr.truncated();
      stage.stdoutOmittedBytes = stdout.omittedBytes();
      stage.stderrOmittedBytes = stderr.omittedBytes();
      stage.failure ??= stdout.error() ?? stderr.error();
      if (stage.failure) {
        stage.status = 'failed';
      }
      stage.finishedUtc = new Date().toISOString();
      await saveManifest(runRoot, manifest);
    }
  }

  manifest.complete = true;
  manifest.finishedUtc = new Date().toISOString();
  await saveManifest(runRoot, manifest);
  return {
    runRoot,
    exitCode: manifest.stages.every((stage) => stage.status === 'passed') ? 0 : 1,
    manifest
  };
}

async function createBoundedLog(filePath, mirror, limit) {
  const file = await fs.open(filePath, 'wx');
  const headLimit = Math.max(1, Math.floor(limit / 4));
  const tailLimit = limit - headLimit;
  const tail = Buffer.alloc(tailLimit);
  let headBytes = 0;
  let tailBytes = 0;
  let tailNext = 0;
  let totalBytes = 0;
  let failure = null;
  let pending = Promise.resolve();
  const writeMirror = (value) => {
    try {
      mirror?.write(value);
    } catch (error) {
      failure ??= boundedError(error);
    }
  };
  const queueFile = (value) => {
    pending = pending.then(() => file.writeFile(value)).catch((error) => {
      failure ??= boundedError(error);
    });
  };
  const omittedBytes = () => Math.max(0, totalBytes - headBytes - tailBytes);
  return {
    write(chunk) {
      const value = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      totalBytes += value.length;
      const headCount = Math.min(value.length, headLimit - headBytes);
      if (headCount > 0) {
        const head = Buffer.from(value.subarray(0, headCount));
        headBytes += headCount;
        writeMirror(head);
        queueFile(head);
      }
      const remainder = value.subarray(headCount);
      if (tailLimit === 0 || remainder.length === 0) return;
      if (remainder.length >= tailLimit) {
        remainder.copy(tail, 0, remainder.length - tailLimit);
        tailBytes = tailLimit;
        tailNext = 0;
        return;
      }
      const first = Math.min(remainder.length, tailLimit - tailNext);
      remainder.copy(tail, tailNext, 0, first);
      remainder.copy(tail, 0, first);
      tailNext = (tailNext + remainder.length) % tailLimit;
      tailBytes = Math.min(tailLimit, tailBytes + remainder.length);
    },
    async close() {
      await pending;
      const omitted = omittedBytes();
      if (omitted > 0) {
        const marker = Buffer.from(`\n...[omitted ${omitted} bytes]...\n`);
        writeMirror(marker);
        queueFile(marker);
      }
      if (tailBytes > 0) {
        const ordered = tailBytes === tailLimit
          ? Buffer.concat([tail.subarray(tailNext), tail.subarray(0, tailNext)])
          : Buffer.from(tail.subarray(0, tailBytes));
        writeMirror(ordered);
        queueFile(ordered);
      }
      await pending;
      try {
        await file.close();
      } catch (error) {
        failure ??= boundedError(error);
      }
    },
    truncated: () => omittedBytes() > 0,
    omittedBytes,
    error: () => failure
  };
}

async function saveManifest(runRoot, manifest) {
  const temporary = path.join(runRoot, 'manifest.json.tmp');
  await fs.writeFile(temporary, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
  await fs.rename(temporary, path.join(runRoot, 'manifest.json'));
}

async function ensureUnlinkedDirectory(directory) {
  let entry;
  try {
    entry = await fs.lstat(directory);
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    await fs.mkdir(directory);
    entry = await fs.lstat(directory);
  }
  if (entry.isSymbolicLink() || !entry.isDirectory()) {
    throw new Error(`Refusing linked diagnostic evidence directory: ${directory}`);
  }
}

async function collectDiagnosticIdentity(root) {
  const readGit = (args) => {
    try {
      return execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
    } catch (error) {
      return `unavailable: ${boundedError(error)}`;
    }
  };
  const packageJson = JSON.parse(await fs.readFile(path.join(root, 'package.json'), 'utf8'));
  const packageLock = await fs.readFile(path.join(root, 'package-lock.json'));
  let installedVsceVersion = null;
  try {
    installedVsceVersion = JSON.parse(await fs.readFile(
      path.join(root, 'node_modules', '@vscode', 'vsce', 'package.json'), 'utf8')).version;
  } catch {
    // npm ci may not have run yet. This is evidence of an unavailable identity.
  }
  let npmVersion = null;
  try {
    const npmCli = resolveNpmCli(process.env);
    npmVersion = execFileSync(process.execPath, [npmCli, '--version'], {
      cwd: root,
      encoding: 'utf8'
    }).trim();
  } catch {
    // Keep the missing tool identity visible in the manifest.
  }
  let dotnetSdkVersion = null;
  try {
    dotnetSdkVersion = execFileSync('dotnet', ['--version'], {
      cwd: root,
      encoding: 'utf8'
    }).trim();
  } catch {
    // A missing SDK is a stage failure, not a reason to skip all other stages.
  }
  return {
    commit: readGit(['rev-parse', 'HEAD']),
    dirtyPaths: readGit(['status', '--porcelain=v1', '--untracked-files=normal']).split(/\r?\n/).filter(Boolean),
    dirtyContent: await collectDirtyContentIdentity(root),
    platform: process.platform,
    architecture: process.arch,
    osRelease: os.release(),
    nodeVersion: process.version,
    npmVersion,
    npmUserAgent: process.env.npm_config_user_agent ?? null,
    dotnetSdkVersion,
    packageVersion: packageJson.version,
    packageManager: packageJson.packageManager ?? null,
    installedVsceVersion,
    packageLockSha256: createHash('sha256').update(packageLock).digest('hex')
  };
}

async function collectDirtyContentIdentity(root) {
  const maximumBytes = 64 * 1024 * 1024;
  try {
    const diff = execFileSync('git', ['diff', '--binary', 'HEAD'], {
      cwd: root,
      maxBuffer: maximumBytes + 1
    });
    const changed = execFileSync('git', ['diff', '--name-only', '-z', 'HEAD'], {
      cwd: root,
      maxBuffer: 1024 * 1024
    }).toString('utf8').split('\0').filter(Boolean);
    const untracked = execFileSync('git', ['ls-files', '--others', '--exclude-standard', '-z'], {
      cwd: root,
      maxBuffer: 1024 * 1024
    }).toString('utf8').split('\0').filter(Boolean);
    const paths = [...new Set([...changed, ...untracked])].sort();
    if (paths.length > 256 || diff.length > maximumBytes) {
      return { complete: false, reason: 'Dirty content exceeds the bounded fingerprint inventory.' };
    }
    let bytesHashed = diff.length;
    const files = [];
    for (const relative of paths) {
      const absolute = path.resolve(root, relative);
      if (!absolute.startsWith(`${root}${path.sep}`)) {
        return { complete: false, reason: 'A dirty path escaped the checkout.' };
      }
      let stat;
      try {
        stat = await fs.lstat(absolute);
      } catch (error) {
        if (error.code === 'ENOENT') {
          files.push({ path: relative, kind: 'deleted' });
          continue;
        }
        throw error;
      }
      if (stat.isSymbolicLink()) {
        const target = await fs.readlink(absolute);
        files.push({ path: relative, kind: 'symbolic-link',
          sha256: createHash('sha256').update(target).digest('hex') });
        continue;
      }
      if (!stat.isFile() || bytesHashed + stat.size > maximumBytes) {
        return { complete: false, reason: 'A dirty file is unsupported or exceeds the fingerprint bound.' };
      }
      const contents = await fs.readFile(absolute);
      bytesHashed += contents.length;
      files.push({ path: relative, kind: 'file', bytes: contents.length,
        sha256: createHash('sha256').update(contents).digest('hex') });
    }
    return {
      complete: true,
      fingerprint: createHash('sha256').update(diff).update(JSON.stringify(files)).digest('hex'),
      files,
      bytesHashed
    };
  } catch (error) {
    return { complete: false, reason: boundedError(error) };
  }
}

function boundedError(error) {
  const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
  return message.slice(0, 2048);
}

function executeNpmScript({ script, root, env, onStdout, onStderr }) {
  return new Promise((resolve, reject) => {
    let npmCli;
    try {
      npmCli = resolveNpmCli(env);
    } catch (error) {
      reject(error);
      return;
    }
    const child = spawn(process.execPath, [npmCli, 'run', script], {
      cwd: root,
      env,
      windowsHide: true,
      stdio: ['inherit', 'pipe', 'pipe']
    });
    child.stdout.on('data', onStdout);
    child.stderr.on('data', onStderr);
    child.once('error', reject);
    child.once('close', (exitCode, signal) => {
      resolve({ exitCode, signal, pid: child.pid });
    });
  });
}

function resolveNpmCli(env) {
  const candidate = [
    env.npm_execpath,
    path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npm-cli.js'),
    path.join(path.dirname(path.dirname(process.execPath)), 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js')
  ].find((entry) => entry && path.isAbsolute(entry) && existsSync(entry));
  if (!candidate) {
    throw new Error('The pinned npm CLI could not be located for diagnostic verification.');
  }
  return candidate;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.some((arg) => arg !== '--windows-excel') || args.length > 1) {
    console.error('Usage: node scripts/diagnosticVerification.mjs [--windows-excel]');
    process.exitCode = 2;
  } else {
    try {
      const result = await runDiagnosticVerification({ includeWindowsExcel: args.includes('--windows-excel') });
      console.error(`Diagnostic evidence saved: ${result.runRoot}`);
      console.error('This diagnostic profile is not a release gate.');
      process.exitCode = result.exitCode;
    } catch (error) {
      console.error(`Diagnostic verification could not complete: ${boundedError(error)}`);
      process.exitCode = 1;
    }
  }
}
