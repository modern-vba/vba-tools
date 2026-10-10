import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ConfigurationTarget, Diagnostic, DiagnosticSeverity, Range, RelativePattern, TabInputText,
  Uri, WorkspaceEdit, commands, extensions, languages, window, workspace
} from 'vscode';
import { VbaDevSessionResolver } from '../../devtool';
import { decodeProjectManifestBytes } from '../../projectManifestBytes';
import { createCallerOwnedSourceSnapshotCapture, MaterializedCallerOwnedSourceSnapshot } from '../../snapshotSourceInventory';
import { createSnapshotSourceInventoryVscodeAdapter } from '../../snapshotSourceInventoryVscodeAdapter';
import { combineVbaDevDiagnosticOutput, VbaDevDiagnosticReporter, vbaDevDiagnosticScope } from '../../toolDiagnostics';
import { createVscodeDiagnosticCollectionAdapter } from '../../vscodeAdapters';
import { windowsPathKey } from '../../windowsPathIdentity';
import { runWithExtensionHostFixtureCleanup } from '../testFixtureCleanup';

export async function runSnapshotBuildProblemsIntegrationTests(): Promise<void> {
  const parent = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(parent, 'Use the disposable native extension-host workspace.');
  const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
  const cli = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  const fixture = await mkdtemp(path.join(parent, 'snapshot-problems-'));
  const sourceRoot = path.join(fixture, 'src', '日本語');
  const callerPath = path.join(sourceRoot, 'nested', 'Caller.bas');
  const targetPath = path.join(sourceRoot, 'Target.bas');
  const callerUri = Uri.file(callerPath);
  const targetUri = Uri.file(targetPath);
  const ownedKeys = new Set([callerPath, targetPath].map(windowsPathKey));
  const binPath = path.join(fixture, 'bin', 'Book1.xlsm');
  const outputPath = path.join(fixture, 'explicit-output', 'Book1.xlsm');
  const templatePath = path.join(fixture, 'templates', 'Book1.xlsm');
  const validCaller = 'Attribute VB_Name = "Caller"\r\nPublic Sub Run()\r\n    Dim item As Long\r\n    AcceptValue item\r\nEnd Sub\r\n';
  const invalidCaller = validCaller.replace('item As Long', 'item As Integer');
  const savedCallerBytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(validCaller, 'utf8')]);
  const targetText = 'Attribute VB_Name = "Target"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n';
  const snapshots = new Set<MaterializedCallerOwnedSourceSnapshot>();
  const collection = languages.createDiagnosticCollection('snapshot-build-native');
  const reporter = new VbaDevDiagnosticReporter(createVscodeDiagnosticCollectionAdapter(collection));
  const scope = vbaDevDiagnosticScope('build', fixture, 'Book1');
  const independentScope = vbaDevDiagnosticScope('snapshot-native-independent', fixture, 'Book1');
  const foreign = languages.createDiagnosticCollection('snapshot-native-independent');
  const oldTheme = workspace.getConfiguration('workbench').inspect<string>('colorTheme')?.workspaceValue;
  await runWithExtensionHostFixtureCleanup(fixture, async () => {
    await workspace.getConfiguration('workbench').update('colorTheme', 'Default Dark Modern', ConfigurationTarget.Workspace);
    await mkdir(path.dirname(callerPath), { recursive: true });
    await mkdir(path.dirname(binPath), { recursive: true });
    await mkdir(path.dirname(outputPath), { recursive: true });
    await mkdir(path.dirname(templatePath), { recursive: true });
    const seed = path.join(fixture, 'seed');
    await promisify(execFile)(cli, ['new', 'excel', '--name', 'Book1', '--output', seed, '--format', 'json'],
      { cwd: fixture, windowsHide: true });
    const seedManifest = JSON.parse(decodeProjectManifestBytes(await readFile(path.join(seed, 'vba-project.json'))));
    const seedDocument = seedManifest.documents.Book1;
    const template = await readFile(path.resolve(seed, seedDocument.templatePath));
    await writeFile(templatePath, template);
    await writeFile(path.join(fixture, 'vba-project.json'), JSON.stringify({
      schemaVersion: 1, projectName: 'SnapshotProblemsNative', primaryDocument: 'Book1', documents: {
        Book1: { ...seedDocument, sourcePath: 'src/日本語', templatePath: 'templates/Book1.xlsm',
          binPath: 'bin/Book1.xlsm', publishPath: 'publish/Book1.xlsm' }
      }
    }) + '\n', 'utf8');
    await writeFile(callerPath, savedCallerBytes);
    await writeFile(targetPath, targetText, 'ascii');
    const previousOutput = Buffer.from('previous completed output');
    await writeFile(binPath, previousOutput);
    await writeFile(outputPath, previousOutput);
    const extension = extensions.getExtension('modern-vba.vba-tools');
    assert.ok(extension);
    await extension.activate();
    const caller = await workspace.openTextDocument(callerUri);
    await window.showTextDocument(caller);
    await replaceText(callerUri, invalidCaller);
    assert.equal(caller.isDirty, true);
    const independent = new Diagnostic(new Range(0, 0, 0, 1), 'Independent contribution', DiagnosticSeverity.Information);
    independent.source = 'snapshot-native-independent';
    foreign.set(callerUri, [independent]);
    const resolver = new VbaDevSessionResolver({ extensionRoot, configuredPath: cli });
    const capture = createCallerOwnedSourceSnapshotCapture(createSnapshotSourceInventoryVscodeAdapter({
      getActiveWindowsCodePage: () => resolver.readActiveWindowsCodePage(),
      getOpenTextDocuments: () => workspace.textDocuments.map(doc => ({ uriScheme: doc.uri.scheme,
        uriPath: doc.uri.scheme === 'file' ? doc.uri.fsPath : undefined, fileName: doc.fileName,
        isDirty: doc.isDirty, encoding: doc.encoding, getText: () => doc.getText() })),
      findSourceFiles: async root => (await workspace.findFiles(new RelativePattern(root, '**/*.{bas,cls,frm,frx}'), null)).map(uri => uri.fsPath),
      readFile: async file => workspace.fs.readFile(Uri.file(file)),
      encodeText: async (text, encoding) => workspace.encode(text, { encoding }),
      decodeText: async (bytes, encoding) => workspace.decode(bytes, { encoding })
    }), {
      createTemporaryDirectory: async () => mkdtemp(path.join(fixture, 'snapshot-')),
      createDirectory: async dir => { await mkdir(dir, { recursive: true }); },
      writeFile: async (file, bytes) => { await writeFile(file, bytes); },
      removeDirectory: async dir => {
        assert.equal(path.dirname(dir), fixture);
        assert.ok(path.basename(dir).startsWith('snapshot-'));
        await rm(dir, { recursive: true, force: true });
      }, wait: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
    });
    const buildCapturedGeneration = async (expectedCallerText: string) => {
      const snapshot = await capture(sourceRoot);
      snapshots.add(snapshot);
      let result: SnapshotBuildResult;
      try {
        assert.equal(snapshot.origins.length, 2);
        for (const origin of snapshot.origins) {
          assert.ok(origin.sourceUri, 'Every captured unit must retain its persistent source origin.');
          const originalPath = fileURLToPath(origin.sourceUri);
          assert.ok(ownedKeys.has(windowsPathKey(originalPath)));
          assert.equal(path.relative(snapshot.directoryPath, fileURLToPath(origin.snapshotUri)),
            path.relative(sourceRoot, originalPath));
        }
        const capturedCaller = await readFile(path.join(snapshot.directoryPath, 'nested', 'Caller.bas'));
        assert.equal(await workspace.decode(capturedCaller, { encoding: caller.encoding }), expectedCallerText);
        assert.deepEqual(await readFile(path.join(snapshot.directoryPath, 'Target.bas')), Buffer.from(targetText, 'ascii'));
        result = await runSnapshotBuild(cli, fixture, snapshot.directoryPath, outputPath);
        const unmapped: string[] = [];
        reporter.refreshSnapshot(scope, combineVbaDevDiagnosticOutput(result.stdout, result.stderr),
          snapshot.origins, message => { unmapped.push(message); });
        assert.deepEqual(unmapped, [], 'Build Problems must use the exact captured origin map.');
      } finally {
        const cleanup = await snapshot.cleanup();
        if (cleanup.retainedPath === undefined) snapshots.delete(snapshot);
      }
      assert.ok(!snapshots.has(snapshot), 'The completed Build must release its captured generation.');
      await assert.rejects(stat(snapshot.directoryPath), { code: 'ENOENT' });
      for (const origin of snapshot.origins) await assert.rejects(stat(fileURLToPath(origin.snapshotUri)), { code: 'ENOENT' });
      return { ...result, origins: snapshot.origins };
    };

    // Debug intentionally has no independent source-error gate. The public explicit-output
    // Build still validates this captured unsaved generation before replacing any output.
    const failed = await buildCapturedGeneration(invalidCaller);
    assert.equal(failed.exitCode, 1, combineVbaDevDiagnosticOutput(failed.stdout, failed.stderr));
    const diagnostic = collection.get(callerUri)?.find(item => item.code === 'validation.incompatibleCallArgumentList');
    assert.ok(diagnostic, 'The actual snapshot Build must report the dirty Caller, not its valid disk bytes.');
    assert.equal(diagnostic.source, 'vba-dev');
    assert.deepEqual(diagnostic.range, new Range(3, 16, 3, 20));
    const related = diagnostic.relatedInformation!;
    assert.equal(related.length, 1);
    assert.equal(windowsPathKey(related[0].location.uri.fsPath), windowsPathKey(targetPath));
    assert.deepEqual(related[0].location.range, new Range(1, 11, 1, 22));
    await waitFor(() => languages.getDiagnostics(callerUri).some(item =>
      item.source === 'vba-dev' && item.code === diagnostic.code), 'snapshot Build Problems');
    for (const [uri, range] of [[callerUri, diagnostic.range], [targetUri, related[0].location.range]] as const) {
      await commands.executeCommand('vscode.open', uri, { selection: range, preview: false });
      assert.equal(windowsPathKey(window.activeTextEditor!.document.uri.fsPath), windowsPathKey(uri.fsPath));
      assert.deepEqual(window.activeTextEditor!.selection.start, range.start);
      assert.deepEqual(window.activeTextEditor!.selection.end, range.end);
    }
    assert.equal(caller.getText(), invalidCaller);
    assert.equal(caller.isDirty, true);
    assert.deepEqual(await readFile(callerPath), savedCallerBytes);
    assert.deepEqual(await readFile(targetPath), Buffer.from(targetText, 'ascii'));
    assert.deepEqual(await readFile(binPath), previousOutput);
    assert.deepEqual(await readFile(outputPath), previousOutput);
    assert.deepEqual(await readFile(templatePath), template);

    // A separate consumer of the same real analysis evidence must survive Build's scoped clear.
    const independentProblems = reporter.refreshSnapshot(independentScope,
      combineVbaDevDiagnosticOutput(failed.stdout, failed.stderr), failed.origins,
      message => { assert.fail(message); });
    const independentCallerProblemCount = independentProblems.filter(item =>
      windowsPathKey(item.uriPath) === windowsPathKey(callerPath)).length;
    assert.ok(independentCallerProblemCount > 0);
    assert.equal(collection.get(callerUri)!.length, independentCallerProblemCount * 2);
    const correctedCaller = validCaller + "' unsaved successful generation\r\n";
    await replaceText(callerUri, correctedCaller);
    const completed = await buildCapturedGeneration(correctedCaller);
    assert.equal(completed.exitCode, 0, combineVbaDevDiagnosticOutput(completed.stdout, completed.stderr));
    const completedOutput = await readFile(outputPath);
    assert.notDeepEqual(completedOutput, previousOutput);
    assert.deepEqual(completedOutput.subarray(0, 2), Buffer.from('PK'));
    assert.equal(collection.get(callerUri)?.length, independentCallerProblemCount,
      'A clean Build must clear only its own diagnostic contribution.');
    const survivingDiagnostic = collection.get(callerUri)!.find(item => item.code === diagnostic.code);
    assert.ok(survivingDiagnostic);
    assert.deepEqual(survivingDiagnostic.range, diagnostic.range);
    reporter.refresh(independentScope, '');
    assert.equal(collection.get(callerUri)?.length ?? 0, 0);
    await waitFor(() => languages.getDiagnostics(callerUri).every(item => item.source !== 'vba-dev'), 'resolved snapshot Problems');
    assert.ok(languages.getDiagnostics(callerUri).some(item => item.source === 'snapshot-native-independent'));
    assert.equal(caller.getText(), correctedCaller);
    assert.equal(caller.isDirty, true);
    assert.deepEqual(await readFile(callerPath), savedCallerBytes);
    assert.deepEqual(await readFile(targetPath), Buffer.from(targetText, 'ascii'));
    assert.deepEqual(await readFile(binPath), previousOutput);
    assert.deepEqual(await readFile(templatePath), template);
    console.log('Native explicit snapshot Build Problems: dirty source validation, rejected output replacement, primary/related navigation after cleanup, successful output, scoped clear, and byte preservation passed.');
  }, async () => {
    for (const snapshot of snapshots) {
      const cleanup = await snapshot.cleanup();
      assert.equal(cleanup.retainedPath, undefined, 'Do not remove a fixture with retained captured generation evidence.');
    }
    collection.dispose();
    foreign.dispose();
    for (const group of window.tabGroups.all) {
      for (const tab of group.tabs) {
        if (tab.input instanceof TabInputText && ownedKeys.has(windowsPathKey(tab.input.uri.fsPath))) {
          await commands.executeCommand('vscode.open', tab.input.uri);
          await commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
        }
      }
    }
    await workspace.getConfiguration('workbench').update('colorTheme', oldTheme, ConfigurationTarget.Workspace);
    assert.equal(path.dirname(fixture), path.resolve(parent));
    assert.ok(path.basename(fixture).startsWith('snapshot-problems-'));
  });
}

interface SnapshotBuildResult {
  exitCode: number;
  stdout: string;
  stderr: string;
}

async function runSnapshotBuild(cli: string, project: string, snapshot: string, output: string): Promise<SnapshotBuildResult> {
  // Build's public grammar has no --format option; its result/analysis output is already JSON.
  const args = ['build', '--source-snapshot', snapshot, '--output', output,
    '--project', project, '--document', 'Book1'];
  try {
    const result = await promisify(execFile)(cli, args, { cwd: project, windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
    return { exitCode: 0, stdout: result.stdout, stderr: result.stderr };
  } catch (error) {
    const failure = error as { code?: number | string; stdout?: unknown; stderr?: unknown };
    if (typeof failure.code !== 'number' || typeof failure.stdout !== 'string' || typeof failure.stderr !== 'string') throw error;
    return { exitCode: failure.code, stdout: failure.stdout, stderr: failure.stderr };
  }
}

async function replaceText(uri: Uri, text: string): Promise<void> {
  const document = await workspace.openTextDocument(uri);
  const edit = new WorkspaceEdit();
  edit.replace(uri, new Range(document.positionAt(0), document.positionAt(document.getText().length)), text);
  assert.equal(await workspace.applyEdit(edit), true);
}

async function waitFor(predicate: () => boolean, description: string): Promise<void> {
  const deadline = Date.now() + 30_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${description}.`);
    await new Promise(resolve => setTimeout(resolve, 50));
  }
}
