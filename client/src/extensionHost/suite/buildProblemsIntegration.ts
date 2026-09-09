import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import {
  ConfigurationTarget,
  Diagnostic,
  DiagnosticSeverity,
  Range,
  TabInputText,
  Uri,
  WorkspaceEdit,
  commands,
  languages,
  window,
  workspace
} from 'vscode';
import { resolveCommandPaletteProjectTargetFromManifestText } from '../../commandPaletteTarget';
import { resolveCommandPalettePathIdentity } from '../../commandPaletteTargetAdapter';
import { resolveCompatibleVbaDev } from '../../devtool';
import { runWorkbookBackedProjectCommand } from '../../projectCommand';
import { VbaDevDiagnosticReporter } from '../../toolDiagnostics';
import { createVscodeDiagnosticCollectionAdapter } from '../../vscodeAdapters';
import { windowsPathKey } from '../../windowsPathIdentity';

const invalidFirst = [
  'Attribute VB_Name = "AFirst"',
  'Public Sub Run(ByVal name As String, ByVal name As Long)',
  '    value = "unterminated',
  'End Sub',
  ''
].join('\n');
const correctedFirst = [
  'Attribute VB_Name = "AFirst"',
  'Public Sub Run(ByVal name As String, ByVal count As Long)',
  '    value = "closed"',
  'End Sub',
  ''
].join('\n');
const invalidLater = [
  'Attribute VB_Name = "ZLater"',
  'Public Sub Run()',
  '    Example(Arg1:=1, ARG1:=2)',
  'End Sub',
  ''
].join('\n');

export async function runBuildProblemsIntegrationTests(): Promise<void> {
  assert.equal(process.platform, 'win32', 'This is a native Windows vba-dev integration.');
  const fixtureParent = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureParent, 'Use the disposable extension-host workspace fixture.');
  const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
  const executablePath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  assert.equal((await stat(executablePath)).isFile(), true, 'Publish the current vba-dev first.');
  const provider = await resolveCompatibleVbaDev({ extensionRoot, configuredPath: executablePath });
  assert.equal(provider.executablePath, executablePath);
  assert.equal(provider.capabilities.commands.build?.outputSchemaVersion, '2.0');

  const fixtureRoot = await mkdtemp(path.join(fixtureParent, 'build-problems-'));
  const sourceRoot = path.join(fixtureRoot, 'src', 'Book1');
  const manifestPath = path.join(fixtureRoot, 'vba-project.json');
  const aPath = path.join(sourceRoot, 'AFirst.bas');
  const zPath = path.join(sourceRoot, 'ZLater.bas');
  const aUri = Uri.file(aPath);
  const zUri = Uri.file(zPath);
  const ownedUriKeys = new Set([windowsPathKey(aPath), windowsPathKey(zPath)]);
  const binPath = path.join(fixtureRoot, 'bin', 'Book1.xlsm');
  const priorOutputBytes = Buffer.from('previous completed output sentinel', 'ascii');
  const toolCollection = languages.createDiagnosticCollection(`vba-dev-native-${randomUUID()}`);
  const otherCollection = languages.createDiagnosticCollection(`other-tool-native-${randomUUID()}`);
  const reporter = new VbaDevDiagnosticReporter(createVscodeDiagnosticCollectionAdapter(toolCollection));
  const channel = window.createOutputChannel('VBA Tools Build Problems Native Integration');
  const configuration = workspace.getConfiguration('workbench');
  const oldWorkspaceTheme = configuration.inspect<string>('colorTheme')?.workspaceValue;
  const oldEditor = window.activeTextEditor;
  const oldSelection = oldEditor?.selection;
  const output: string[] = [];
  const errors: string[] = [];
  const warnings: string[] = [];

  try {
    await configuration.update('colorTheme', 'Default Dark Modern', ConfigurationTarget.Workspace);
    await mkdir(sourceRoot, { recursive: true });
    await mkdir(path.dirname(binPath), { recursive: true });
    const manifestText = `${JSON.stringify({
      schemaVersion: 1,
      projectName: 'BuildProblemsNative',
      primaryDocument: 'Book1',
      documents: {
        Book1: {
          kind: 'excel',
          sourcePath: 'src/Book1',
          templatePath: 'templates/Book1.xlsm',
          binPath: 'bin/Book1.xlsm',
          publishPath: 'publish/Book1.xlsm',
          commonModules: [],
          references: []
        }
      }
    }, undefined, 2)}\n`;
    await writeFile(manifestPath, manifestText, 'utf8');
    // ASCII fixture bytes are identical in every supported ACP; no encoding guess.
    await writeFile(aPath, invalidFirst, 'ascii');
    await writeFile(zPath, invalidLater, 'ascii');
    await writeFile(binPath, priorOutputBytes);
    // The template is deliberately absent. Source analysis must win first;
    // a gate regression still stops at template validation before Excel startup.
    const project = await resolveCommandPaletteProjectTargetFromManifestText(
      manifestPath, manifestText, resolveCommandPalettePathIdentity
    );
    assert.ok(project);
    const document = project.documents.find(candidate => candidate.name === 'Book1');
    assert.ok(document);

    const foreign = new Diagnostic(new Range(0, 0, 0, 9),
      'Independent tool contribution survives Build refresh.', DiagnosticSeverity.Information);
    foreign.source = 'other-tool-native';
    foreign.code = 'integration.independentFinding';
    otherCollection.set(aUri, [foreign]);

    const options = {
      toolCommandName: 'build' as const,
      title: 'VBA Tools: Build',
      extensionRoot,
      configuredDevToolPath: executablePath,
      workspaceRoots: [fixtureRoot],
      activeFilePath: manifestPath,
      fileExists: async (filePath: string) => {
        try { return (await stat(filePath)).isFile(); } catch { return false; }
      },
      findProjectManifests: async () => [manifestPath],
      chooseProject: async () => project,
      resolveCommandPaletteTarget: async () => ({ project, document }),
      diagnosticReporter: reporter,
      outputChannel: {
        append: (value: string) => { output.push(value); channel.append(value); },
        appendLine: (value: string) => { output.push(`${value}\n`); channel.appendLine(value); },
        show: (preserveFocus?: boolean) => channel.show(preserveFocus)
      },
      revealOutput: false,
      showErrorMessage: async (message: string) => { errors.push(message); },
      showWarningMessage: async (message: string) => { warnings.push(message); return undefined; }
      // No resolver, capabilities-process, or child-process test doubles.
    };

    assertSourcesClosed(ownedUriKeys);
    const first = await runWorkbookBackedProjectCommand(options);
    assert.ok(first);
    assert.equal(first.exitCode, 1);
    assert.equal(first.cancelled, false);
    assert.equal(first.cancellationRequested, false);
    const firstReports = analysisRecords(output.join(''));
    assert.equal(firstReports.length, 1);
    assert.equal(firstReports[0].schemaVersion, '2.0');
    assert.equal(firstReports[0].complete, true);
    assert.deepEqual(firstReports[0].failures, []);
    assert.equal(firstReports[0].diagnostics.length, 3);
    assertSourcesClosed(ownedUriKeys);

    const aDiagnostics = toolCollection.get(aUri) ?? [];
    const zDiagnostics = toolCollection.get(zUri) ?? [];
    assert.deepEqual(aDiagnostics.map(diagnosticFact), [
      { source: 'vba-dev', code: 'syntax.unterminatedStringLiteral',
        message: 'String literal is missing a closing double quote.', severity: DiagnosticSeverity.Error,
        range: [2, 12, 2, 25] },
      { source: 'vba-dev', code: 'validation.duplicateCallableParameterName',
        message: "Duplicate callable parameter name 'name'.", severity: DiagnosticSeverity.Error,
        range: [1, 43, 1, 47] }
    ]);
    assert.deepEqual(zDiagnostics.map(diagnosticFact), [
      { source: 'vba-dev', code: 'validation.duplicateNamedCallArgument',
        message: "Duplicate named call argument 'ARG1'.", severity: DiagnosticSeverity.Error,
        range: [2, 21, 2, 25] }
    ]);
    await waitFor(() => languages.getDiagnostics(aUri).some(item => item.source === 'other-tool-native')
      && languages.getDiagnostics(aUri).filter(item => item.source === 'vba-dev').length === 2
      && languages.getDiagnostics(zUri).filter(item => item.source === 'vba-dev').length === 1);

    for (const [uri, diagnostics] of [[aUri, aDiagnostics], [zUri, zDiagnostics]] as const) {
      for (const diagnostic of diagnostics) {
        await commands.executeCommand('vscode.open', uri, {
          selection: diagnostic.range, preserveFocus: false, preview: false
        });
        const editor = window.activeTextEditor;
        assert.ok(editor);
        assert.equal(windowsPathKey(editor.document.uri.fsPath), windowsPathKey(uri.fsPath));
        assert.deepEqual(editor.selection.start, diagnostic.range.start);
        assert.deepEqual(editor.selection.end, diagnostic.range.end);
      }
    }
    assert.deepEqual(await readFile(aPath), Buffer.from(invalidFirst, 'ascii'));
    assert.deepEqual(await readFile(zPath), Buffer.from(invalidLater, 'ascii'));
    assert.deepEqual(await readFile(binPath), priorOutputBytes);

    const sourceDocument = await workspace.openTextDocument(aUri);
    const correction = new WorkspaceEdit();
    correction.replace(aUri, new Range(sourceDocument.positionAt(0),
      sourceDocument.positionAt(sourceDocument.getText().length)), correctedFirst);
    assert.equal(await workspace.applyEdit(correction), true);
    assert.equal(await sourceDocument.save(), true);
    assert.equal(sourceDocument.isDirty, false);
    await closeOwnedEditors(ownedUriKeys);
    // Closed editor tabs may retain cached document models; the correction is saved.

    const second = await runWorkbookBackedProjectCommand(options);
    assert.ok(second);
    assert.equal(second.exitCode, 1, 'ZLater must remain invalid, so no generation is needed.');
    assert.equal(second.cancelled, false);
    const reports = analysisRecords(output.join(''));
    assert.equal(reports.length, 2);
    assert.equal(reports[1].schemaVersion, '2.0');
    assert.equal(reports[1].complete, true);
    assert.deepEqual(reports[1].failures, []);
    assert.equal(reports[1].diagnostics.length, 1);
    assert.deepEqual(toolCollection.get(aUri) ?? [], []);
    assert.deepEqual((toolCollection.get(zUri) ?? []).map(diagnosticFact), zDiagnostics.map(diagnosticFact));
    assert.deepEqual((otherCollection.get(aUri) ?? []).map(diagnosticFact), [diagnosticFact(foreign)]);
    await waitFor(() => languages.getDiagnostics(aUri).every(item => item.source !== 'vba-dev')
      && languages.getDiagnostics(aUri).some(item => item.source === 'other-tool-native'));
    assert.deepEqual(await readFile(aPath), Buffer.from(correctedFirst, 'ascii'));
    assert.deepEqual(await readFile(zPath), Buffer.from(invalidLater, 'ascii'));
    assert.deepEqual(await readFile(binPath), priorOutputBytes);
    assert.equal(await readFile(manifestPath, 'utf8'), manifestText);
    assert.deepEqual(errors, [
      'Build failed. See the VBA Tools output for details.',
      'Build failed. See the VBA Tools output for details.'
    ]);
    assert.deepEqual(warnings, []);
    console.log('PASS real Build reports closed saved sources, navigates original ranges, and refreshes only its Problems contribution');
  } finally {
    toolCollection.dispose();
    otherCollection.dispose();
    channel.dispose();
    try {
      // Revert only our own source documents if a save/assertion interrupted the test.
      for (const document of workspace.textDocuments) {
        if (!document.isClosed && document.isDirty && ownedUriKeys.has(windowsPathKey(document.uri.fsPath))) {
          await window.showTextDocument(document, { preserveFocus: false });
          await commands.executeCommand('workbench.action.files.revert');
        }
      }
      await closeOwnedEditors(ownedUriKeys);
    } finally {
      try {
        await configuration.update('colorTheme', oldWorkspaceTheme, ConfigurationTarget.Workspace);
        if (oldEditor !== undefined && !oldEditor.document.isClosed) {
          const restored = await window.showTextDocument(oldEditor.document, {
            viewColumn: oldEditor.viewColumn, preserveFocus: false, preview: false
          });
          if (oldSelection !== undefined) restored.selection = oldSelection;
        }
      } finally {
        const ownedRoot = path.resolve(fixtureRoot);
        const relative = path.relative(path.resolve(fixtureParent), ownedRoot);
        assert.ok(relative !== '' && relative !== '..' && !relative.startsWith(`..${path.sep}`)
          && !path.isAbsolute(relative), 'Cleanup must remain inside the owned fixture parent.');
        await rm(ownedRoot, { recursive: true, force: true });
      }
    }
  }
}

function diagnosticFact(diagnostic: Diagnostic) {
  return {
    source: diagnostic.source, code: diagnostic.code, message: diagnostic.message,
    severity: diagnostic.severity,
    range: [diagnostic.range.start.line, diagnostic.range.start.character,
      diagnostic.range.end.line, diagnostic.range.end.character]
  };
}

function analysisRecords(output: string): Array<{
  schemaVersion: string; complete: boolean; diagnostics: unknown[]; failures: unknown[];
}> {
  return output.split(/\r?\n/).filter(line => line.startsWith('{"type":"sourceAnalysis"'))
    .map(line => JSON.parse(line));
}

function assertSourcesClosed(uriKeys: ReadonlySet<string>): void {
  assert.equal(workspace.textDocuments.some(document => !document.isClosed
    && uriKeys.has(windowsPathKey(document.uri.fsPath))), false);
}

async function closeOwnedEditors(uriKeys: ReadonlySet<string>): Promise<void> {
  const ownedTabs = window.tabGroups.all.flatMap(group => group.tabs).filter(tab =>
    tab.input instanceof TabInputText && uriKeys.has(windowsPathKey(tab.input.uri.fsPath)));
  if (ownedTabs.length > 0) assert.equal(await window.tabGroups.close(ownedTabs, true), true);
}

async function waitFor(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 5_000;
  while (!predicate()) {
    assert.ok(Date.now() < deadline, 'VS Code did not publish the expected diagnostics/editor state.');
    await new Promise(resolve => setTimeout(resolve, 25));
  }
}
