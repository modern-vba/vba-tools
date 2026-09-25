import test from 'node:test';
import assert from 'node:assert/strict';
import { createWriteStream, promises as fs } from 'node:fs';
import { EventEmitter } from 'node:events';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { PassThrough } from 'node:stream';
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
  verifyVsixPackaging,
  runCommandWithSpawn
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
const runtimeDependencyPaths = [
  ['vscode-languageclient', ['package.json', 'node.js', 'lib/node/main.js']],
  ['vscode-languageserver-protocol', ['package.json', 'node.js', 'lib/node/main.js', 'lib/common/api.js']],
  ['vscode-jsonrpc', ['package.json', 'node.js', 'lib/node/main.js']],
  ['vscode-languageserver-types', ['package.json', 'lib/umd/main.js']],
  ['semver', ['package.json', 'index.js', 'functions/parse.js', 'functions/satisfies.js']],
  ['vscode-languageclient/node_modules/minimatch', ['package.json', 'minimatch.js', 'lib/path.js']],
  ['vscode-languageclient/node_modules/brace-expansion', ['package.json', 'index.js']],
  ['vscode-languageclient/node_modules/balanced-match', ['package.json', 'index.js']]
].flatMap(([dependency, entries]) => entries.map(entry => `node_modules/${dependency}/${entry}`));
const requiredContentPaths = [...marketplaceDocumentPaths, ...runtimeDependencyPaths];

test('failed packaging child retains bounded output and exact process completion', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-child-failure-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const childPath = path.join(root, 'fail.mjs');
  await fs.writeFile(childPath, [
    'process.stdout.write("A".repeat(80));',
    'process.stderr.write("B".repeat(80));',
    'process.exitCode = 23;'
  ].join('\n'));

  await assert.rejects(
    () => runCommandWithSpawn(process.execPath, [childPath], root, { maxOutputCharacters: 32 }),
    error => {
      assert.equal(error.file, process.execPath);
      assert.deepEqual(error.args, [childPath]);
      assert.equal(error.cwd, root);
      assert.ok(Number.isInteger(error.pid) && error.pid > 0);
      assert.equal(error.exitCode, 23);
      assert.equal(error.signal, null);
      assert.equal(error.stdout, 'A'.repeat(32));
      assert.equal(error.stderr, 'B'.repeat(32));
      assert.equal(error.stdoutCharacters, 80);
      assert.equal(error.stderrCharacters, 80);
      assert.equal(error.stdoutTruncated, true);
      assert.equal(error.stderrTruncated, true);
      return true;
    }
  );
});

test('monitor finalization failure does not replace the original child exit', async (t) => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-monitor-finalize-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const childPath = path.join(root, 'fail.mjs');
  await fs.writeFile(childPath, 'process.stderr.write("original failure\\n"); process.exitCode = 23;\n');

  await assert.rejects(
    () => runCommandWithSpawn(process.execPath, [childPath], root, {
      onSpawn: () => async () => { throw new Error('monitor evidence write failed'); }
    }),
    error => error.exitCode === 23 && error.stderr === 'original failure\n'
  );
});

test('opted-in VSIX verification preserves a failed child and partial output as local evidence', async (t) => {
  const { root, vscePath } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');

  await assert.rejects(
    () => verifyVsixPackaging({ root, diagnosticsRoot }),
    /exited with code 23/
  );

  const entries = await fs.readdir(diagnosticsRoot);
  assert.equal(entries.length, 1);
  const evidencePath = path.join(diagnosticsRoot, entries[0]);
  const evidence = JSON.parse(await fs.readFile(path.join(evidencePath, 'failure.json'), 'utf8'));
  assert.equal(evidence.kind, 'vsix-packaging-child-failure');
  assert.equal(evidence.invocation.file, process.execPath);
  assert.equal(evidence.invocation.cwd, root);
  assert.deepEqual(evidence.invocation.args.slice(0, 2), [vscePath, 'package']);
  assert.equal(evidence.invocation.exitCode, 23);
  assert.equal(evidence.invocation.signal, null);
  assert.ok(Number.isInteger(evidence.invocation.pid) && evidence.invocation.pid > 0);
  assert.equal(evidence.output.stdout, 'packaging started\n');
  assert.equal(evidence.output.stderr, 'controlled child failure\n');
  assert.equal(evidence.tools.node.version, process.version);
  assert.equal(evidence.tools.node.sha256, await hashFile(process.execPath));
  assert.equal(evidence.tools.npm.reportedVersion,
    /^npm\/([^\s]+)/.exec(process.env.npm_config_user_agent ?? '')?.[1] ?? null);
  assert.equal(evidence.tools.vsce.sha256, await hashFile(vscePath));
  assert.equal(evidence.inputs.packageLock.sha256, await hashFile(path.join(root, 'package-lock.json')));
  assert.equal(evidence.failedOutput.status, 'retained-partial');
  assert.equal(await fs.readFile(path.join(evidencePath, 'failed-output.partial'), 'utf8'), 'not-a-vsix');
  assert.equal(await fileExists(evidence.invocation.outputPath), false);
});

test('opted-in exact-child monitor records attachment without replacing the packaging failure', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');
  const procDumpSha256 = await hashFile(procDumpPath);
  const spawnMonitor = (_file, args) => {
    const monitor = new EventEmitter();
    monitor.pid = 12345;
    monitor.stdout = new PassThrough();
    monitor.stderr = new PassThrough();
    monitor.kill = () => true;
    const targetPid = Number(args.at(-2));
    setImmediate(() => {
      monitor.stdout.end(Buffer.from(
        `Process: node.exe (${targetPid})\r\nPress Ctrl-C to end monitoring without terminating the process.\r\n`,
        'utf16le'));
      monitor.stderr.end();
      monitor.emit('close', 0, null);
    });
    return monitor;
  };

  let originalError;
  await assert.rejects(
    () => verifyVsixPackaging({
      root,
      diagnosticsRoot,
      procDump: { path: procDumpPath, sha256: procDumpSha256, spawnMonitor }
    }),
    error => {
      originalError = error;
      assert.equal(error.exitCode, 23);
      assert.equal(error.stderr, 'controlled child failure\n');
      return true;
    }
  );

  const entries = await fs.readdir(diagnosticsRoot);
  const failurePath = path.join(diagnosticsRoot, entries.find(entry => entry.startsWith('failure-')));
  const monitorPath = path.join(diagnosticsRoot, entries.find(entry => entry.startsWith('monitor-')));
  const failure = JSON.parse(await fs.readFile(path.join(failurePath, 'failure.json'), 'utf8'));
  const observation = JSON.parse(await fs.readFile(path.join(monitorPath, 'monitor.json'), 'utf8'));
  assert.match(observation.invocationId, /^[0-9a-f]{8}-[0-9a-f-]{27}$/i);
  assert.equal(observation.invocationId, failure.invocationId);
  assert.equal(observation.target.pid, originalError.pid);
  assert.equal(observation.target.pid, failure.invocation.pid);
  assert.equal(observation.target.exitCode, 23);
  assert.match(observation.monitor.attachReadyUtc, /^\d{4}-\d{2}-\d{2}T/);
  assert.equal(observation.monitor.exitCode, 0);
  assert.deepEqual(observation.dumps, []);
  assert.equal(observation.absence, 'no-exception-dump');
});

test('exact-child monitor is stopped within a bounded grace after the packaging child closes', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');
  const procDumpSha256 = await hashFile(procDumpPath);
  let killCalls = 0;
  const spawnMonitor = (_file, args) => {
    const monitor = new EventEmitter();
    monitor.pid = 12346;
    monitor.stdout = new PassThrough();
    monitor.stderr = new PassThrough();
    monitor.kill = () => {
      killCalls += 1;
      setImmediate(() => monitor.emit('close', null, 'SIGTERM'));
      return true;
    };
    setImmediate(() => monitor.stdout.write(Buffer.from(
      `Process: node.exe (${args.at(-2)})\r\nPress Ctrl-C to end monitoring without terminating the process.\r\n`)));
    return monitor;
  };

  const started = Date.now();
  await assert.rejects(
    () => verifyVsixPackaging({
      root, diagnosticsRoot,
      procDump: {
        path: procDumpPath, sha256: procDumpSha256,
        spawnMonitor, monitorGraceMilliseconds: 20
      }
    }),
    error => error.exitCode === 23 && error.stderr === 'controlled child failure\n'
  );
  assert.ok(Date.now() - started < 2000);
  assert.equal(killCalls, 1);
  const observation = await readExactChildMonitor(diagnosticsRoot);
  assert.equal(observation.monitor.timedOut, true);
  assert.match(observation.monitor.killRequestedUtc, /^\d{4}-\d{2}-\d{2}T/);
  assert.equal(observation.monitor.killReturned, true);
  assert.equal(observation.monitor.closeUnobservedAfterKill, false);
  assert.equal(observation.monitor.signal, 'SIGTERM');
  assert.equal(observation.absence, 'monitor-incomplete');
});

test('denied monitor kill records unconfirmed cleanup without replacing the packaging failure', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');
  const procDumpSha256 = await hashFile(procDumpPath);
  let killCalls = 0;
  const spawnMonitor = (_file, args) => {
    const monitor = new EventEmitter();
    monitor.pid = 12348;
    monitor.stdout = new PassThrough();
    monitor.stderr = new PassThrough();
    monitor.kill = () => { killCalls += 1; return false; };
    setImmediate(() => monitor.stdout.write(Buffer.from(
      `Process: node.exe (${args.at(-2)})\r\nPress Ctrl-C to end monitoring without terminating the process.\r\n`)));
    return monitor;
  };

  await assert.rejects(
    () => verifyVsixPackaging({
      root, diagnosticsRoot,
      procDump: { path: procDumpPath, sha256: procDumpSha256,
        spawnMonitor, monitorGraceMilliseconds: 20 }
    }),
    error => error.exitCode === 23 && error.stderr === 'controlled child failure\n'
  );
  assert.equal(killCalls, 1);
  const observation = await readExactChildMonitor(diagnosticsRoot);
  assert.equal(observation.monitor.timedOut, true);
  assert.equal(observation.monitor.killReturned, false);
  assert.equal(observation.monitor.closeUnobservedAfterKill, true);
  assert.equal(observation.monitor.closeUtc, null);
  assert.equal(observation.absence, 'monitor-incomplete');
});

test('exact-child monitor reports an unconfirmed attach instead of claiming no exception', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');
  const procDumpSha256 = await hashFile(procDumpPath);
  const spawnMonitor = (_file, args) => {
    const monitor = new EventEmitter();
    monitor.pid = 12347;
    monitor.stdout = new PassThrough();
    monitor.stderr = new PassThrough();
    monitor.kill = () => true;
    setImmediate(() => {
      monitor.stdout.end(Buffer.from(
        `Process: other.exe (${args.at(-2)})\r\nPress Ctrl-C to end monitoring without terminating the process.\r\n`));
      monitor.emit('close', 0, null);
    });
    return monitor;
  };

  await assert.rejects(
    () => verifyVsixPackaging({
      root, diagnosticsRoot,
      procDump: { path: procDumpPath, sha256: procDumpSha256, spawnMonitor }
    }),
    error => error.exitCode === 23
  );
  const observation = await readExactChildMonitor(diagnosticsRoot);
  assert.equal(observation.monitor.attachReadyUtc, null);
  assert.equal(observation.monitor.attachReadyBeforeTargetClose, false);
  assert.equal(observation.absence, 'attach-not-confirmed');
});

test('ProcDump launch failure cannot replace the original packaging child failure', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');
  const procDumpSha256 = await hashFile(procDumpPath);

  await assert.rejects(
    () => verifyVsixPackaging({
      root, diagnosticsRoot,
      procDump: {
        path: procDumpPath, sha256: procDumpSha256,
        spawnMonitor: () => { throw new Error('monitor launch failed'); }
      }
    }),
    error => error.exitCode === 23 && error.stderr === 'controlled child failure\n'
  );
  const observation = await readExactChildMonitor(diagnosticsRoot);
  assert.match(observation.monitor.error, /monitor launch failed/);
  assert.equal(observation.absence, 'attach-not-confirmed');
});

test('exact-child monitor rejects an unverified ProcDump executable before packaging starts', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'diagnostics');
  const procDumpPath = path.join(root, 'procdump64.exe');
  await fs.writeFile(procDumpPath, 'test-only external monitor');

  await assert.rejects(
    () => verifyVsixPackaging({
      root, diagnosticsRoot,
      procDump: { path: procDumpPath, sha256: '0'.repeat(64) }
    }),
    /ProcDump SHA-256 does not match/
  );
  assert.equal(await fileExists(diagnosticsRoot), false);
});

test('evidence storage failure never replaces the packaging child failure', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = path.join(root, 'not-a-directory');
  await fs.writeFile(diagnosticsRoot, 'sentinel');

  await assert.rejects(
    () => verifyVsixPackaging({ root, diagnosticsRoot }),
    error => {
      assert.match(error.message, /exited with code 23/);
      assert.equal(error.exitCode, 23);
      assert.equal(error.stderr, 'controlled child failure\n');
      return true;
    }
  );
  assert.equal(await fs.readFile(diagnosticsRoot, 'utf8'), 'sentinel');
});

test('UNC diagnostic destination is rejected without replacing the packaging failure', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const diagnosticsRoot = '\\\\remote-server\\private-share\\vsix-packaging';

  await assert.rejects(
    () => verifyVsixPackaging({ root, diagnosticsRoot }),
    error => error.exitCode === 23 && /exited with code 23/.test(error.message)
  );
});

test('linked diagnostic destination cannot redirect packaging evidence', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const outside = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-outside-'));
  t.after(() => fs.rm(outside, { recursive: true, force: true }));
  const diagnosticsRoot = path.join(root, 'diagnostics-link');
  try {
    await fs.symlink(outside, diagnosticsRoot, process.platform === 'win32' ? 'junction' : 'dir');
  } catch (error) {
    if (error.code === 'EPERM') return t.skip('Directory links require local privileges.');
    throw error;
  }

  await assert.rejects(
    () => verifyVsixPackaging({ root, diagnosticsRoot }),
    error => error.exitCode === 23 && /exited with code 23/.test(error.message)
  );
  assert.deepEqual(await fs.readdir(outside), []);
});

test('unified diagnostic run captures packaging evidence under its shared run root', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const runRoot = path.join(root, 'diagnostic-run');
  await fs.mkdir(runRoot);
  const originalRoot = process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT;
  const originalId = process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ID;
  process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT = runRoot;
  process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ID = 'run-fixture';
  try {
    await assert.rejects(() => verifyVsixPackaging({ root }), /exited with code 23/);
  } finally {
    if (originalRoot === undefined) delete process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT;
    else process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT = originalRoot;
    if (originalId === undefined) delete process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ID;
    else process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ID = originalId;
  }

  const [entry] = await fs.readdir(path.join(runRoot, 'vsix-packaging'));
  const evidence = JSON.parse(await fs.readFile(
    path.join(runRoot, 'vsix-packaging', entry, 'failure.json'), 'utf8'));
  assert.equal(evidence.diagnosticRunId, 'run-fixture');
});

test('ordinary VSIX verification fails and discards a partial package without diagnostic opt-in', async (t) => {
  const { root } = await createPackagingFailureFixture(t);
  const originalRoot = process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT;
  delete process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT;
  let failure;
  try {
    await assert.rejects(() => verifyVsixPackaging({ root }), error => {
      failure = error;
      assert.match(error.message, /exited with code 23/);
      return true;
    });
  } finally {
    if (originalRoot !== undefined) process.env.VBA_TOOLS_DIAGNOSTIC_RUN_ROOT = originalRoot;
  }
  const outputPath = failure.args[failure.args.indexOf('--out') + 1];
  assert.equal(await fileExists(outputPath), false);
  assert.equal(await fileExists(path.join(root, 'vsix-packaging')), false);
});

test('packaging evidence records an absent output when the child fails before writing one', async (t) => {
  const { root } = await createPackagingFailureFixture(t, { writeOutput: false });
  const diagnosticsRoot = path.join(root, 'diagnostics');

  await assert.rejects(() => verifyVsixPackaging({ root, diagnosticsRoot }), /exited with code 23/);

  const [entry] = await fs.readdir(diagnosticsRoot);
  const evidencePath = path.join(diagnosticsRoot, entry);
  const evidence = JSON.parse(await fs.readFile(path.join(evidencePath, 'failure.json'), 'utf8'));
  assert.deepEqual(evidence.failedOutput, { status: 'absent' });
  assert.equal(await fileExists(path.join(evidencePath, 'failed-output.partial')), false);
});

test('VSIX content rules reject every missing runtime dependency entry including nested transitive entries', () => {
  const manifest = readDistributionManifest();
  const files = [...new Set([
    ...manifest.vsix.requiredFiles,
    ...runtimeDependencyPaths,
    ...Object.values(manifest.runtimes).flatMap(runtime => [runtime.executablePath, runtime.contractPath].filter(Boolean))
  ])];
  assert.doesNotThrow(() => assertVsixContents(files));
  for (const required of runtimeDependencyPaths) {
    assert.throws(() => assertVsixContents(files.filter(file => file !== required)),
      { message: `VSIX file list must include ${required}.` });
  }
});

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
  assert.match(changelog, /^## \[0\.1\.0\] - \d{4}-\d{2}-\d{2}/m);
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
      ...requiredContentPaths,
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
    ...requiredContentPaths,
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
    ...requiredContentPaths,
    'package.json',
    'client/out/extension.js'
  ]) {
    assert.throws(
      () => assertVsixContents([
        ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
      ...requiredContentPaths,
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
    'tools/vba-capability-admission/src/Capability.cs',
    'fixtures/capability-admission/cases.json',
    'tools/vba-source-identity/src/Identity.cs',
    'fixtures/source-identity/cases.json',
    '.tmp/old-smoke/output.bas',
    'temp/old-smoke/output.xlsm'
  ]) {
    assert.throws(
      () => assertVsixContents([
        ...requiredContentPaths,
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
      ...requiredContentPaths,
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

  assert.throws(() => assertBundledCliCapabilities(JSON.stringify({
    ...compatibleCapabilities,
    commands: { ...commands, publish: { outputSchemaVersion: '1.0' } }
  })), /publish/);

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
    featureVersions: { 'doctor.stdinCancellation': '1.0', 'snapshotBuild.diagnostics': '1.0' },
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '2.0', 'build.sourceSnapshotAnalysis': '1.0' }
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
    /only build\.sourceSnapshot 2\.0 and build\.sourceSnapshotAnalysis 1\.0/i
  );
});

test('packaging admits reordered and additive adapter offers with repeated entries', () => {
  const contract = readRequiredVbaDebugAdapterContract();
  assert.doesNotThrow(() => assertBundledDebugAdapterCapabilities(JSON.stringify({
    toolVersion: '0.1.0', ...contract,
    commands: ['doctor', 'inspect', 'cleanup', 'doctor'],
    transports: ['socket', 'stdio', 'stdio'],
    commandSchemaVersions: { inspect: '9.0', ...contract.commandSchemaVersions },
    featureVersions: { future: '9.0', ...contract.featureVersions }
  })));
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
  assert.deepEqual(adapterContract.requiredVbaDevFeatureVersions, { 'build.sourceSnapshot': '2.0', 'build.sourceSnapshotAnalysis': '1.0' });
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

test('packaging rejects missing snapshot analysis or diagnostic transport capabilities', () => {
  const cliContract = readRequiredVbaDevContract();
  const adapterContract = readRequiredVbaDebugAdapterContract();
  const cliFeatures = { ...cliContract.featureVersions };
  delete cliFeatures['build.sourceSnapshotAnalysis'];
  assert.throws(() => assertBundledCliCapabilities(JSON.stringify({
    contractVersion: cliContract.contractVersion, featureVersions: cliFeatures, activeWindowsCodePage: 932,
    commands: Object.fromEntries(Object.entries(cliContract.commandSchemaVersions)
      .map(([name, version]) => [name, { outputSchemaVersion: version }]))
  })), /build\.sourceSnapshotAnalysis/);
  const adapterFeatures = { ...adapterContract.featureVersions };
  delete adapterFeatures['snapshotBuild.diagnostics'];
  assert.throws(() => assertBundledDebugAdapterCapabilities(JSON.stringify({
    toolVersion: '0.1.0', ...adapterContract, featureVersions: adapterFeatures
  })), /snapshotBuild\.diagnostics/);
  assert.throws(() => assertBundledDebugAdapterCapabilities(JSON.stringify({
    toolVersion: '0.1.0', ...adapterContract,
    requiredVbaDevFeatureVersions: { 'build.sourceSnapshot': '2.0' }
  })), /build\.sourceSnapshotAnalysis/);
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
    ...requiredContentPaths.map((file) => [
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

  const admissionProbes = [];
  const verifyLanguageServerAdmission = async (...args) => { admissionProbes.push(args); };
  await verifyVsixPackaging({ root, runCommand, inspectPackage, verifyLanguageServerAdmission });

  assert.deepEqual(admissionProbes, [[
    path.join(root, requiredBundledLanguageServerPath),
    ['--stdio', '--vba-dev', path.join(root, requiredBundledDebugAdapterPath)],
    root
  ]]);

  const packageCalls = calls.filter(call => call.args.includes('package'));
  assert.equal(packageCalls.length, 1);
  const packageArgs = packageCalls[0].args;
  assert.equal(packageArgs[packageArgs.indexOf('--target') + 1], 'win32-x64');
  assert.ok(!packageArgs.includes('--no-dependencies'));

  assert.deepEqual(calls.filter(call => !call.args.includes('package')).map(call => call.args), [
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
  assert.match(scripts['test:process-invocation'], /vba-process-invocation\/tests\/VbaTools\.ProcessInvocation\.Tests/);
  assert.match(scripts['test:source-identity'], /vba-source-identity\/tests\/VbaTools\.SourceIdentity\.Tests/);
  assert.match(scripts.test, /npm run test:source-identity/);
  assert.match(scripts.test, /npm run test:process-invocation/);
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
    'test:process-invocation',
    'test:source-identity',
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

async function createPackagingFailureFixture(t, { writeOutput = true } = {}) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'vba-tools-vsix-failure-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const manifest = readDistributionManifest();
  const paths = [
    distributionManifestPath,
    'package.json',
    'package-lock.json',
    requiredVbaDevContractPath,
    requiredVbaDebugAdapterContractPath,
    ...Object.values(manifest.runtimes).map(runtime => runtime.projectPath)
  ];
  for (const relativePath of paths) {
    const destination = path.join(root, relativePath);
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.copyFile(new URL(`../${relativePath}`, import.meta.url), destination);
  }
  for (const runtime of Object.values(manifest.runtimes)) {
    const destination = path.join(root, runtime.executablePath);
    await fs.mkdir(path.dirname(destination), { recursive: true });
    await fs.writeFile(destination, '');
  }
  const vscePath = path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce');
  await fs.mkdir(path.dirname(vscePath), { recursive: true });
  await fs.writeFile(path.join(path.dirname(vscePath), 'package.json'),
    JSON.stringify({ name: '@vscode/vsce', version: '3.9.2', type: 'commonjs' }));
  await fs.writeFile(vscePath, [
    'const fs = require("node:fs");',
    'const output = process.argv[process.argv.indexOf("--out") + 1];',
    ...(writeOutput ? ['fs.writeFileSync(output, "not-a-vsix");'] : []),
    'process.stdout.write("packaging started\\n");',
    'process.stderr.write("controlled child failure\\n");',
    'process.exitCode = 23;'
  ].join('\n'));
  return { root, vscePath };
}

async function readExactChildMonitor(diagnosticsRoot) {
  const entries = await fs.readdir(diagnosticsRoot);
  const monitorPath = path.join(diagnosticsRoot, entries.find(entry => entry.startsWith('monitor-')));
  return JSON.parse(await fs.readFile(path.join(monitorPath, 'monitor.json'), 'utf8'));
}

async function fileExists(filePath) {
  try {
    await fs.access(filePath);
    return true;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

async function hashFile(filePath) {
  return createHash('sha256').update(await fs.readFile(filePath)).digest('hex');
}
