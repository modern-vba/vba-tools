import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  ConfigurationTarget, DebugSession, Diagnostic, DiagnosticSeverity, Range, TabInputText,
  Uri, WorkspaceEdit, commands, debug, extensions, languages, window, workspace
} from 'vscode';
import { decodeProjectManifestBytes } from '../../projectManifestBytes';
import { parseDebugSnapshotBuildReport, DebugSnapshotBuildReport } from '../../debugSnapshotBuildReport';
import { windowsPathKey } from '../../windowsPathIdentity';

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
  const templatePath = path.join(fixture, 'templates', 'Book1.xlsm');
  const validCaller = 'Attribute VB_Name = "Caller"\r\nPublic Sub Run()\r\n    Dim item As Long\r\n    AcceptValue item\r\nEnd Sub\r\n';
  const invalidCaller = validCaller.replace('item As Long', 'item As Integer');
  const savedCallerBytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(validCaller, 'utf8')]);
  const targetText = 'Attribute VB_Name = "Target"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n';
  const reports: DebugSnapshotBuildReport[] = [];
  const protocolEvents: string[][] = [];
  const ownedSessions = new Set<DebugSession>();
  const foreign = languages.createDiagnosticCollection('snapshot-native-independent');
  const oldTheme = workspace.getConfiguration('workbench').inspect<string>('colorTheme')?.workspaceValue;
  const tracker = debug.registerDebugAdapterTrackerFactory('vba', {
    createDebugAdapterTracker: session => {
      if (typeof session.configuration.project !== 'string'
          || windowsPathKey(session.configuration.project) !== windowsPathKey(fixture)) return undefined;
      ownedSessions.add(session);
      const events: string[] = [];
      protocolEvents.push(events);
      return { onDidSendMessage: message => {
        if (message.type === 'event') events.push(message.event);
        if (message.type === 'event' && message.event === 'vba/snapshotBuild') {
          reports.push(parseDebugSnapshotBuildReport(message.body));
        }
      }, onExit: () => { ownedSessions.delete(session); } };
    }
  });
  const terminated = debug.onDidTerminateDebugSession(session => { ownedSessions.delete(session); });
  try {
    await workspace.getConfiguration('workbench').update('colorTheme', 'Default Dark Modern', ConfigurationTarget.Workspace);
    await mkdir(path.dirname(callerPath), { recursive: true });
    await mkdir(path.dirname(binPath), { recursive: true });
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
    const configuration = { type: 'vba', request: 'launch', name: 'Snapshot Problems native',
      project: fixture, document: 'Book1', module: 'Caller', procedure: 'Run' };

    const failedStart = await debug.startDebugging(undefined, configuration).then(value => value, (error: unknown) => {
      // VS Code's test host rejects the expected launch-error dialog instead of displaying it.
      assert.match(String(error), /DialogService: refused to show dialog in tests/);
      assert.match(String(error), /vba-dev snapshot build exited with code 1/);
      return false;
    });
    assert.equal(failedStart, false);
    await waitFor(() => reports.length === 1 && ownedSessions.size === 0, 'failed launch cleanup');
    assert.equal(reports[0].exitCode, 1);
    assert.ok(!protocolEvents[0].includes('process'), 'A rejected snapshot cannot start runnable Excel.');
    assert.ok(!protocolEvents[0].includes('stopped'), 'A rejected snapshot cannot reach VBE execution.');
    await waitFor(() => languages.getDiagnostics(callerUri).some(diagnostic =>
      diagnostic.source === 'vba-dev' && diagnostic.code === 'validation.incompatibleCallArgumentList'), 'snapshot Problems');
    const diagnostic = languages.getDiagnostics(callerUri).find(item =>
      item.source === 'vba-dev' && item.code === 'validation.incompatibleCallArgumentList')!;
    assert.deepEqual(diagnostic.range, new Range(3, 16, 3, 20));
    const related = diagnostic.relatedInformation!;
    assert.equal(related.length, 1);
    assert.equal(windowsPathKey(related[0].location.uri.fsPath), windowsPathKey(targetPath));
    assert.deepEqual(related[0].location.range, new Range(1, 11, 1, 22));
    for (const origin of reports[0].origins) {
      await assert.rejects(stat(fileURLToPath(origin.snapshotUri)), { code: 'ENOENT' });
    }
    for (const [uri, range] of [[callerUri, diagnostic.range], [targetUri, related[0].location.range]] as const) {
      await commands.executeCommand('vscode.open', uri, { selection: range, preview: false });
      assert.equal(windowsPathKey(window.activeTextEditor!.document.uri.fsPath), windowsPathKey(uri.fsPath));
      assert.deepEqual(window.activeTextEditor!.selection.start, range.start);
      assert.deepEqual(window.activeTextEditor!.selection.end, range.end);
    }
    assert.equal(caller.getText(), invalidCaller);
    assert.equal(caller.isDirty, true);
    assert.deepEqual(await readFile(callerPath), savedCallerBytes);
    assert.deepEqual(await readFile(binPath), previousOutput);
    assert.deepEqual(await readFile(templatePath), template);

    // Leave an unsaved comment so the successful generation is also editor-owned.
    await replaceText(callerUri, validCaller + "' unsaved successful generation\r\n");
    const started = await debug.startDebugging(undefined, configuration);
    assert.equal(started, true);
    await waitFor(() => reports.length === 2, 'successful build report');
    assert.equal(reports[1].exitCode, 0);
    for (const session of [...ownedSessions]) await debug.stopDebugging(session);
    await waitFor(() => ownedSessions.size === 0, 'successful session cleanup');
    await waitFor(() => languages.getDiagnostics(callerUri).every(item => item.source !== 'vba-dev'), 'resolved snapshot Problems');
    assert.ok(languages.getDiagnostics(callerUri).some(item => item.source === 'snapshot-native-independent'));
    assert.equal(caller.isDirty, true);
    assert.deepEqual(await readFile(callerPath), savedCallerBytes);
    assert.deepEqual(await readFile(targetPath), Buffer.from(targetText, 'ascii'));
    assert.deepEqual(await readFile(binPath), previousOutput);
    assert.deepEqual(await readFile(templatePath), template);
    for (const origin of reports[1].origins) await assert.rejects(stat(fileURLToPath(origin.snapshotUri)), { code: 'ENOENT' });
    console.log('Native snapshot Problems: unsaved disagreement, related navigation after cleanup, rejected launch, successful clear, and byte preservation passed.');
  } finally {
    for (const session of [...ownedSessions]) await debug.stopDebugging(session);
    await waitFor(() => ownedSessions.size === 0, 'owned debug session teardown');
    tracker.dispose();
    terminated.dispose();
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
    await rm(fixture, { recursive: true, force: true });
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
