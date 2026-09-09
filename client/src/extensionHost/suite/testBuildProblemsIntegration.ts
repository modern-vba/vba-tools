import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { ConfigurationTarget, Range, RelativePattern, TabInputText, Uri, WorkspaceEdit,
  commands, languages, tests, window, workspace } from 'vscode';
import { decodeProjectManifestBytes } from '../../projectManifestBytes';
import { VbaDevSessionResolver } from '../../devtool';
import { createWorkbookBackedTestExplorer } from '../../testExplorer';
import { createVscodeDiagnosticCollectionAdapter, createVscodeTestControllerAdapter } from '../../vscodeAdapters';
import { createSnapshotSourceInventoryVscodeAdapter } from '../../snapshotSourceInventoryVscodeAdapter';
import { createCallerOwnedSourceSnapshotCapture } from '../../snapshotSourceInventory';
import { VbaDevDiagnosticReporter } from '../../toolDiagnostics';
import { runWorkbookBackedProjectCommand } from '../../projectCommand';
import { windowsPathKey } from '../../windowsPathIdentity';

export async function runTestBuildProblemsIntegrationTests(): Promise<void> {
  const parent = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(parent);
  const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
  const cli = path.join(extensionRoot, 'bin/vba-dev/win-x64/vba-dev.exe');
  const fixture = await mkdtemp(path.join(parent, 'test-build-problems-'));
  const source = path.join(fixture, 'src/日本語');
  const callerPath = path.join(source, 'nested/Caller.bas');
  const targetPath = path.join(source, 'Target.bas');
  const templatePath = path.join(fixture, 'templates/Book1.xlsm');
  const binPath = path.join(fixture, 'bin/Book1.xlsm');
  const marker = path.join(fixture, 'executed.txt');
  const callerUri = Uri.file(callerPath);
  const valid = 'Attribute VB_Name = "Caller"\r\nPublic Sub Run()\r\n    Dim item As Long\r\n    AcceptValue item\r\nEnd Sub\r\n';
  const invalid = valid.replace('As Long', 'As Integer');
  const encode = (text: string) => Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(text, 'utf8')]);
  const collection = languages.createDiagnosticCollection('test-build-native');
  const reporter = new VbaDevDiagnosticReporter(createVscodeDiagnosticCollectionAdapter(collection));
  const controller = tests.createTestController('vbaTools.testBuild.native', 'Native Test Build Verification');
  const channel = window.createOutputChannel('Native Test Build Verification');
  const oldTheme = workspace.getConfiguration('workbench').inspect<string>('colorTheme')?.workspaceValue;
  const snapshots: string[] = [];
  const events: string[] = [];
  const token = { isCancellationRequested: false, onCancellationRequested: () => ({ dispose: () => undefined }) };
  try {
    await workspace.getConfiguration('workbench').update('colorTheme', 'Default Dark Modern', ConfigurationTarget.Workspace);
    await mkdir(path.dirname(callerPath), { recursive: true });
    await mkdir(path.dirname(templatePath), { recursive: true });
    await mkdir(path.dirname(binPath), { recursive: true });
    const seed = path.join(fixture, 'seed');
    await promisify(execFile)(cli, ['new', 'excel', '--name', 'Book1', '--output', seed, '--format', 'json'],
      { cwd: fixture, windowsHide: true });
    const seedManifest = JSON.parse(decodeProjectManifestBytes(await readFile(path.join(seed, 'vba-project.json'))));
    const seedDocument = seedManifest.documents.Book1;
    const template = await readFile(path.resolve(seed, seedDocument.templatePath));
    await writeFile(templatePath, template);
    const manifestPath = path.join(fixture, 'vba-project.json');
    await writeFile(manifestPath, JSON.stringify({ schemaVersion: 1, projectName: 'NativeTestBuild', primaryDocument: 'Book1',
      documents: { Book1: { ...seedDocument, sourcePath: 'src/日本語', templatePath: 'templates/Book1.xlsm',
        binPath: 'bin/Book1.xlsm', publishPath: 'publish/Book1.xlsm' } } }) + '\n');
    await writeFile(callerPath, encode(invalid));
    await writeFile(targetPath, 'Attribute VB_Name = "Target"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n', 'ascii');
    await writeFile(path.join(source, 'Test_Module.bas'), 'Attribute VB_Name = "Test_Module"\nPublic Sub Test_Passes()\nEnd Sub\n', 'ascii');
    const harness = [
      'Attribute VB_Name = "Harness"',
      'Public Sub UnitTestMain(Optional ByVal moduleName As String = "", Optional ByVal procedureName As String = "")',
      '    Dim results As Worksheet',
      '    Test_Module.Test_Passes',
      '    Open "' + marker + '" For Output As #1',
      '    Print #1, "executed"',
      '    Close #1',
      '    Set results = ThisWorkbook.Worksheets.Add',
      '    results.Name = "UNIT_TEST_SHEET"',
      '    results.Cells(1, 1).Value = "Module"',
      '    results.Cells(2, 1).Value = "Test_Module"',
      '    results.Cells(2, 2).Value = "Test_Passes"',
      '    results.Cells(2, 3).Value = "OK"',
      'End Sub', ''
    ].join('\n');
    await writeFile(path.join(source, 'Harness.bas'), encode(harness));
    const previous = Buffer.from('previous completed workbook');
    await writeFile(binPath, previous);
    const resolver = new VbaDevSessionResolver({ extensionRoot, configuredPath: cli });
    const common = { extensionRoot, vbaDevResolver: resolver, outputChannel: channel,
      showErrorMessage: async (message: string) => { events.push('notice:' + message); } };
    const document = { name: 'Book1', sourcePath: 'src/日本語', sourceRoot: source,
      sourceRootIdentity: { canonicalPath: source } };
    const palette = await runWorkbookBackedProjectCommand({ ...common, toolCommandName: 'test', title: 'VBA Tools: Test',
      workspaceRoots: [fixture], fileExists: async candidate => candidate === manifestPath,
      findProjectManifests: async () => [manifestPath], chooseProject: async () => undefined,
      resolveCommandPaletteTarget: async () => ({ project: { projectRoot: fixture, manifestPath,
        projectName: 'NativeTestBuild', primaryDocument: 'Book1', documents: [document] }, document }),
      diagnosticReporter: reporter, showWarningMessage: async () => undefined });
    assert.equal(palette?.exitCode, 1);
    assert.ok(collection.get(callerUri)?.some(item => item.code === 'validation.incompatibleCallArgumentList'));
    await assert.rejects(stat(marker), { code: 'ENOENT' });
    assert.deepEqual(await readFile(binPath), previous);
    await writeFile(callerPath, encode(valid));
    const caller = await workspace.openTextDocument(callerUri);
    await window.showTextDocument(caller);
    await replaceText(callerUri, invalid);

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
      createTemporaryDirectory: async () => { const result = await mkdtemp(path.join(fixture, 'snapshot-')); snapshots.push(result); return result; },
      createDirectory: async dir => { await mkdir(dir, { recursive: true }); },
      writeFile: async (file, bytes) => { await writeFile(file, bytes); },
      removeDirectory: async dir => {
        assert.equal(path.dirname(dir), fixture);
        assert.ok(path.basename(dir).startsWith('snapshot-'));
        await rm(dir, { recursive: true, force: true });
      }, wait: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds))
    });
    const adapter = createVscodeTestControllerAdapter(controller);
    const explorer = createWorkbookBackedTestExplorer({ ...common, controller: { ...adapter,
      createTestRun: request => {
        const run = adapter.createTestRun(request);
        return { ...run, passed: item => { events.push('passed:' + item.label); run.passed(item); },
          errored: (item, message) => { events.push('errored:' + message); run.errored(item, message); } };
      } }, workspaceRoots: [fixture], findProjectManifests: async () => [manifestPath],
      readTextFile: async file => decodeProjectManifestBytes(await readFile(file)),
      openTextDocuments: () => workspace.textDocuments.map(doc => ({ uriPath: doc.uri.fsPath, isDirty: doc.isDirty })),
      captureSourceSnapshot: capture, diagnosticReporter: reporter });
    await explorer.refresh();
    const root = [...controller.items][0][1];
    const item = [...root.children][0][1];
    await explorer.run({ include: [item] }, token);
    assert.ok(events.some(event => event.startsWith('errored:VBA source validation')));
    assert.ok(!events.some(event => event.startsWith('passed:')));
    await assert.rejects(stat(marker), { code: 'ENOENT' });
    assert.equal(caller.isDirty, true);
    const diagnostic = collection.get(callerUri)!.find(value => value.code === 'validation.incompatibleCallArgumentList')!;
    assert.deepEqual(diagnostic.range, new Range(3, 16, 3, 20));
    const related = diagnostic.relatedInformation![0].location;
    assert.equal(windowsPathKey(related.uri.fsPath), windowsPathKey(targetPath));
    assert.deepEqual(related.range, new Range(1, 11, 1, 22));
    for (const dir of snapshots) await assert.rejects(stat(dir), { code: 'ENOENT' });
    for (const [uri, range] of [[callerUri, diagnostic.range], [related.uri, related.range]] as const) {
      await commands.executeCommand('vscode.open', uri, { selection: range, preview: false });
      assert.equal(windowsPathKey(window.activeTextEditor!.document.uri.fsPath), windowsPathKey(uri.fsPath));
      assert.deepEqual(window.activeTextEditor!.selection.start, range.start);
    }
    await replaceText(callerUri, valid + "' unsaved valid run\r\n");
    await explorer.run({ include: [item] }, token);
    assert.ok(events.includes('passed:Test_Passes'), events.join('\n'));
    assert.match(await readFile(marker, 'utf8'), /executed/);
    assert.equal(collection.get(callerUri)?.length ?? 0, 0);
    assert.equal(caller.isDirty, true);
    assert.deepEqual(await readFile(callerPath), encode(valid));
    assert.deepEqual(await readFile(templatePath), template);
    assert.deepEqual(await readFile(binPath), previous);
    for (const dir of snapshots) await assert.rejects(stat(dir), { code: 'ENOENT' });
    console.log('Native Test build validation passed: command and Explorer errors, related navigation after cleanup, zero macro on failure, real corrected test execution, scoped clear and byte preservation.');
  } finally {
    controller.dispose(); collection.dispose(); channel.dispose();
    for (const group of window.tabGroups.all) for (const tab of group.tabs) {
      if (tab.input instanceof TabInputText && [callerPath, targetPath].map(windowsPathKey).includes(windowsPathKey(tab.input.uri.fsPath))) {
        await commands.executeCommand('vscode.open', tab.input.uri);
        await commands.executeCommand('workbench.action.revertAndCloseActiveEditor');
      }
    }
    await workspace.getConfiguration('workbench').update('colorTheme', oldTheme, ConfigurationTarget.Workspace);
    assert.equal(path.dirname(fixture), path.resolve(parent));
    assert.ok(path.basename(fixture).startsWith('test-build-problems-'));
    await rm(fixture, { recursive: true, force: true });
  }
}

async function replaceText(uri: Uri, text: string): Promise<void> {
  const document = await workspace.openTextDocument(uri);
  const edit = new WorkspaceEdit();
  edit.replace(uri, new Range(document.positionAt(0), document.positionAt(document.getText().length)), text);
  assert.equal(await workspace.applyEdit(edit), true);
}
