import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';
import { randomUUID } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
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
import { decodeProjectManifestBytes } from '../../projectManifestBytes';

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
const invalidCaller = [
  'Attribute VB_Name = "Caller"',
  'Public Sub CallTarget()',
  '    Dim item As Integer',
  '    AcceptValue item',
  '    Dim values As Scripting.Dictionary',
  '    Dim key As String',
  '    Set values = New Scripting.Dictionary',
  '    values.Exists key',
  'End Sub',
  ''
].join('\n');
const correctedCaller = invalidCaller.replace('item As Integer', 'item As Long');
const targetSource = 'Attribute VB_Name = "Target"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n';
const correctedLater = 'Attribute VB_Name = "ZLater"\nPublic Sub LaterRun()\nEnd Sub\n';

export async function runBuildProblemsIntegrationTests(): Promise<void> {
  await runOutputProblemsIntegrationTests('build');
}

export async function runPublishProblemsIntegrationTests(): Promise<void> {
  await runOutputProblemsIntegrationTests('publish');
}

async function runOutputProblemsIntegrationTests(command: 'build' | 'publish'): Promise<void> {
  const caption = command === 'build' ? 'Build' : 'Publish';
  assert.equal(process.platform, 'win32', 'This is a native Windows vba-dev integration.');
  const fixtureParent = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
  assert.ok(fixtureParent, 'Use the disposable extension-host workspace fixture.');
  const extensionRoot = path.resolve(__dirname, '..', '..', '..', '..');
  const executablePath = path.join(extensionRoot, 'bin', 'vba-dev', 'win-x64', 'vba-dev.exe');
  assert.equal((await stat(executablePath)).isFile(), true, 'Publish the current vba-dev first.');
  const provider = await resolveCompatibleVbaDev({ extensionRoot, configuredPath: executablePath });
  assert.equal(provider.executablePath, executablePath);
  assert.equal(provider.capabilities.commands[command]?.outputSchemaVersion, '3.0');

  const fixtureRoot = await mkdtemp(path.join(fixtureParent, `${command}-problems-`));
  const sourceRoot = path.join(fixtureRoot, 'src', 'Book1');
  const manifestPath = path.join(fixtureRoot, 'vba-project.json');
  const aPath = path.join(sourceRoot, 'AFirst.bas');
  const zPath = path.join(sourceRoot, 'ZLater.bas');
  const callerPath = path.join(sourceRoot, 'Caller.bas');
  const targetPath = path.join(sourceRoot, 'Target.bas');
  const excludedPath = path.join(sourceRoot, 'Excluded.bas');
  const excludedBytes = Buffer.from(`'#ExcludePublish\n${invalidFirst}`, 'ascii');
  const templatePath = path.join(fixtureRoot, 'templates', 'Book1.xlsm');
  const aUri = Uri.file(aPath);
  const zUri = Uri.file(zPath);
  const callerUri = Uri.file(callerPath);
  const targetUri = Uri.file(targetPath);
  const ownedUriKeys = new Set([aPath, zPath, callerPath, targetPath].map(windowsPathKey));
  const outputPath = path.join(fixtureRoot, command === 'build' ? 'bin' : 'publish', 'Book1.xlsm');
  const priorOutputBytes = Buffer.from('previous completed output sentinel', 'ascii');
  const toolCollection = languages.createDiagnosticCollection(`vba-dev-native-${randomUUID()}`);
  const otherCollection = languages.createDiagnosticCollection(`other-tool-native-${randomUUID()}`);
  const reporter = new VbaDevDiagnosticReporter(createVscodeDiagnosticCollectionAdapter(toolCollection));
  const channel = window.createOutputChannel(`VBA Tools ${caption} Problems Native Integration`);
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
    await mkdir(path.dirname(outputPath), { recursive: true });
    const seedRoot = path.join(fixtureRoot, 'initial-project');
    await promisify(execFile)(executablePath,
      ['new', 'excel', '--name', 'Book1', '--output', seedRoot, '--format', 'json'],
      { cwd: fixtureRoot, windowsHide: true });
    const seedManifest = JSON.parse(decodeProjectManifestBytes(
      await readFile(path.join(seedRoot, 'vba-project.json'))));
    const seedDocument = seedManifest.documents.Book1;
    const templateBytes = await readFile(path.resolve(seedRoot, seedDocument.templatePath));
    await mkdir(path.dirname(templatePath), { recursive: true });
    await writeFile(templatePath, templateBytes);
    const manifestText = `${JSON.stringify({
      schemaVersion: 1,
      projectName: `${caption}ProblemsNative`,
      primaryDocument: 'Book1',
      documents: {
        Book1: {
          kind: 'excel',
          sourcePath: 'src/Book1',
          templatePath: 'templates/Book1.xlsm',
          binPath: 'bin/Book1.xlsm',
          publishPath: 'publish/Book1.xlsm',
          commonModules: [],
          references: [...seedDocument.references.filter((reference: { name: string }) =>
            reference.name.toLowerCase() !== 'microsoft scripting runtime'), { name: 'Microsoft Scripting Runtime', requested: true }]
        }
      }
    }, undefined, 2)}\n`;
    await writeFile(manifestPath, manifestText, 'utf8');
    // ASCII fixture bytes are identical in every supported ACP; no encoding guess.
    await writeFile(aPath, invalidFirst, 'ascii');
    await writeFile(zPath, invalidLater, 'ascii');
    await writeFile(callerPath, invalidCaller, 'ascii');
    await writeFile(targetPath, targetSource, 'ascii');
    if (command === 'publish') await writeFile(excludedPath, excludedBytes);
    await writeFile(outputPath, priorOutputBytes);
    // An actual owned Excel creation supplies project identity and baseline references.
    // Invalid source sets must preserve this exact template and the previous output.
    const project = await resolveCommandPaletteProjectTargetFromManifestText(
      manifestPath, manifestText, resolveCommandPalettePathIdentity
    );
    assert.ok(project);
    const document = project.documents.find(candidate => candidate.name === 'Book1');
    assert.ok(document);

    const foreign = new Diagnostic(new Range(0, 0, 0, 9),
      `Independent tool contribution survives ${caption} refresh.`, DiagnosticSeverity.Information);
    foreign.source = 'other-tool-native';
    foreign.code = 'integration.independentFinding';
    otherCollection.set(aUri, [foreign]);

    const options = {
      toolCommandName: command,
      title: `VBA Tools: ${caption}`,
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
    assert.equal(firstReports.length, 1, output.join(''));
    assert.equal(firstReports[0].schemaVersion, '3.0');
    assert.equal(firstReports[0].complete, true, output.join(''));
    assert.deepEqual(firstReports[0].failures, []);
    assert.equal(firstReports[0].diagnostics.length, 6);
    assertSourcesClosed(ownedUriKeys);

    const aDiagnostics = toolCollection.get(aUri) ?? [];
    const zDiagnostics = toolCollection.get(zUri) ?? [];
    assert.deepEqual(aDiagnostics.map(diagnosticFact), [
      { source: 'vba-dev', code: 'syntax.unterminatedStringLiteral',
        message: 'String literal is missing a closing double quote.', severity: DiagnosticSeverity.Error,
        range: [2, 12, 2, 25] },
      { source: 'vba-dev', code: 'validation.duplicateCallableParameterName',
        message: "Duplicate callable parameter name 'name'.", severity: DiagnosticSeverity.Error,
        range: [1, 43, 1, 47] },
      { source: 'vba-dev', code: 'validation.duplicateDeclaration',
        message: "Declaration 'name' conflicts with another declaration in this scope.", severity: DiagnosticSeverity.Error,
        range: [1, 21, 1, 25] },
      { source: 'vba-dev', code: 'validation.duplicateDeclaration',
        message: "Declaration 'name' conflicts with another declaration in this scope.", severity: DiagnosticSeverity.Error,
        range: [1, 43, 1, 47] }
    ]);
    assert.deepEqual(zDiagnostics.map(diagnosticFact), [
      { source: 'vba-dev', code: 'validation.duplicateNamedCallArgument',
        message: "Duplicate named call argument 'ARG1'.", severity: DiagnosticSeverity.Error,
        range: [2, 21, 2, 25] }
    ]);
    const callerDiagnostic = toolCollection.get(callerUri) ?? [];
    assert.deepEqual(callerDiagnostic.map(diagnosticFact), [{
      source: 'vba-dev', code: 'validation.incompatibleCallArgumentList',
      message: 'No available callable signature accepts this argument list.',
      severity: DiagnosticSeverity.Error, range: [3, 16, 3, 20]
    }]);
    const related = callerDiagnostic[0].relatedInformation ?? [];
    assert.equal(related.length, 1);
    assert.equal(windowsPathKey(related[0].location.uri.fsPath), windowsPathKey(targetPath));
    assert.deepEqual(related[0].location.range, new Range(1, 11, 1, 22));
    assert.equal(related[0].message,
      "Candidate signature: Sub AcceptValue(ByRef value As Long). Mismatches: argument 1 for parameter 'value' ByRef type: expected Long, found Integer.");
    await waitFor(() => languages.getDiagnostics(aUri).some(item => item.source === 'other-tool-native')
      && languages.getDiagnostics(aUri).filter(item => item.source === 'vba-dev').length === 4
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
    await commands.executeCommand('vscode.open', related[0].location.uri, {
      selection: related[0].location.range, preserveFocus: false, preview: false
    });
    assert.equal(windowsPathKey(window.activeTextEditor!.document.uri.fsPath), windowsPathKey(targetUri.fsPath));
    assert.deepEqual(window.activeTextEditor!.selection.start, related[0].location.range.start);
    assert.deepEqual(window.activeTextEditor!.selection.end, related[0].location.range.end);
    assert.deepEqual(await readFile(aPath), Buffer.from(invalidFirst, 'ascii'));
    assert.deepEqual(await readFile(zPath), Buffer.from(invalidLater, 'ascii'));
    assert.deepEqual(await readFile(outputPath), priorOutputBytes);
    if (command === 'publish') assert.deepEqual(await readFile(excludedPath), excludedBytes);
    assert.deepEqual(await readFile(templatePath), templateBytes);

    const sourceDocument = await workspace.openTextDocument(aUri);
    const correction = new WorkspaceEdit();
    correction.replace(aUri, new Range(sourceDocument.positionAt(0),
      sourceDocument.positionAt(sourceDocument.getText().length)), correctedFirst);
    assert.equal(await workspace.applyEdit(correction), true);
    assert.equal(await sourceDocument.save(), true);
    assert.equal(sourceDocument.isDirty, false);
    const callerDocument = await workspace.openTextDocument(callerUri);
    const callerCorrection = new WorkspaceEdit();
    callerCorrection.replace(callerUri, new Range(callerDocument.positionAt(0),
      callerDocument.positionAt(callerDocument.getText().length)), correctedCaller);
    assert.equal(await workspace.applyEdit(callerCorrection), true);
    assert.equal(await callerDocument.save(), true);
    await closeOwnedEditors(ownedUriKeys);
    // Closed editor tabs may retain cached document models; the correction is saved.

    const second = await runWorkbookBackedProjectCommand(options);
    assert.ok(second);
    assert.equal(second.exitCode, 1, 'ZLater must remain invalid, so no generation is needed.');
    assert.equal(second.cancelled, false);
    const reports = analysisRecords(output.join(''));
    assert.equal(reports.length, 2);
    assert.equal(reports[1].schemaVersion, '3.0');
    assert.equal(reports[1].complete, true, output.join(''));
    assert.deepEqual(reports[1].failures, []);
    assert.equal(reports[1].diagnostics.length, 1);
    assert.deepEqual(toolCollection.get(aUri) ?? [], []);
    assert.deepEqual(toolCollection.get(callerUri) ?? [], []);
    assert.deepEqual((toolCollection.get(zUri) ?? []).map(diagnosticFact), zDiagnostics.map(diagnosticFact));
    assert.deepEqual((otherCollection.get(aUri) ?? []).map(diagnosticFact), [diagnosticFact(foreign)]);
    await waitFor(() => languages.getDiagnostics(aUri).every(item => item.source !== 'vba-dev')
      && languages.getDiagnostics(aUri).some(item => item.source === 'other-tool-native'));
    assert.deepEqual(await readFile(aPath), Buffer.from(correctedFirst, 'ascii'));
    assert.deepEqual(await readFile(zPath), Buffer.from(invalidLater, 'ascii'));
    assert.deepEqual(await readFile(outputPath), priorOutputBytes);
    if (command === 'publish') assert.deepEqual(await readFile(excludedPath), excludedBytes);
    assert.deepEqual(await readFile(templatePath), templateBytes);
    await writeFile(zPath, correctedLater, 'ascii');
    const third = await runWorkbookBackedProjectCommand(options);
    assert.ok(third);
    assert.equal(third.exitCode, 0, output.join(''));
    assert.equal(third.cancelled, false);
    assert.deepEqual(toolCollection.get(zUri) ?? [], []);
    assert.deepEqual(toolCollection.get(aUri) ?? [], []);
    assert.deepEqual(toolCollection.get(callerUri) ?? [], []);
    assert.deepEqual((otherCollection.get(aUri) ?? []).map(diagnosticFact), [diagnosticFact(foreign)]);
    assert.notDeepEqual(await readFile(outputPath), priorOutputBytes);
    assert.equal((await readFile(outputPath)).subarray(0, 2).toString('ascii'), 'PK');
    if (command === 'publish') assert.deepEqual(await readFile(excludedPath), excludedBytes);
    assert.deepEqual(await readFile(templatePath), templateBytes);
    assert.deepEqual(await readFile(callerPath), Buffer.from(correctedCaller, 'ascii'));
    assert.deepEqual(await readFile(targetPath), Buffer.from(targetSource, 'ascii'));
    assert.equal(await readFile(manifestPath, 'utf8'), manifestText);
    assert.deepEqual(errors, [
      `${caption} failed. See the VBA Tools output for details.`,
      `${caption} failed. See the VBA Tools output for details.`
    ]);
    assert.deepEqual(warnings, []);
    console.log(`PASS real ${caption} preserves semantic related navigation and artifacts, accepts Dictionary inputs, and clears Problems after successful generation`);
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
