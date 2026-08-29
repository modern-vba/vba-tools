import assert from 'node:assert/strict';
import * as path from 'node:path';
import {
  Position,
  ProgressLocation,
  Selection,
  TextDocument,
  TextEditor,
  ViewColumn,
  commands,
  window,
  workspace
} from 'vscode';

import {
  captureCommandPaletteInvocationSnapshot
} from '../../commandPaletteInvocationSnapshot';
import {
  CommandPaletteDocumentQuickPickItem,
  chooseCommandPaletteDocumentWithQuickPick
} from '../../commandPaletteTargetAdapter';
import type { CommandPaletteDocumentTarget } from '../../commandPaletteTarget';
import { runVbaDevProjectCommand } from '../../devtoolRuntime';

export async function runCommandPaletteTargetIntegrationTests(): Promise<void> {
  await runTest(
    'Command Palette invocation snapshots copy actual active, visible, and open VS Code file paths',
    async () => {
      const fixtureRoot = process.env.VBA_TOOLS_EXTENSION_HOST_FIXTURE_ROOT;
      assert.ok(fixtureRoot, 'The Command Palette fixture root must be provided.');
      const activePath = path.join(fixtureRoot, 'src', 'Book1', 'DebugModule.bas');
      const inactivePath = path.join(fixtureRoot, 'outside', 'Outside.bas');
      const openPath = path.join(fixtureRoot, 'outside', 'InvoiceModule.bas');
      let activeDocument: TextDocument | undefined;
      let inactiveDocument: TextDocument | undefined;
      let openDocument: TextDocument | undefined;
      let inactiveEditor: TextEditor | undefined;

      try {
        await commands.executeCommand('workbench.action.closeAllEditors');
        activeDocument = await workspace.openTextDocument(activePath);
        inactiveDocument = await workspace.openTextDocument(inactivePath);
        openDocument = await workspace.openTextDocument(openPath);
        inactiveEditor = await window.showTextDocument(inactiveDocument, {
          viewColumn: ViewColumn.Beside,
          preserveFocus: false,
          preview: false
        });
        await window.showTextDocument(activeDocument, {
          viewColumn: ViewColumn.One,
          preserveFocus: false,
          preview: false
        });
        const expectedActivePath = activeDocument.uri.fsPath;
        const expectedInactivePath = inactiveDocument.uri.fsPath;
        const expectedOpenPath = openDocument.uri.fsPath;
        const expectedWorkspaceRoot = workspace.workspaceFolders?.[0]?.uri.fsPath;
        assert.equal(window.activeTextEditor?.document.uri.fsPath, expectedActivePath);

        inactiveEditor.selection = new Selection(
          new Position(0, 1),
          new Position(0, 1)
        );
        const beforeCursorMove = captureVscodeSnapshot();
        inactiveEditor.selection = new Selection(
          new Position(1, 0),
          new Position(1, 0)
        );
        const afterCursorMove = captureVscodeSnapshot();

        assert.equal(beforeCursorMove.activeFilePath, expectedActivePath);
        assert.equal(beforeCursorMove.activeEditorFilePath, expectedActivePath);
        assert.ok(beforeCursorMove.visibleEditorFilePaths.includes(expectedActivePath));
        assert.ok(beforeCursorMove.visibleEditorFilePaths.includes(expectedInactivePath));
        assert.ok(beforeCursorMove.openDocumentFilePaths.includes(expectedActivePath));
        assert.ok(beforeCursorMove.openDocumentFilePaths.includes(expectedInactivePath));
        assert.ok(beforeCursorMove.openDocumentFilePaths.includes(expectedOpenPath));
        assert.ok(expectedWorkspaceRoot);
        assert.ok(beforeCursorMove.workspaceRoots?.includes(expectedWorkspaceRoot));
        assert.deepEqual(afterCursorMove, beforeCursorMove);

        const copiedVisiblePaths = [...beforeCursorMove.visibleEditorFilePaths];
        const copiedOpenPaths = [...beforeCursorMove.openDocumentFilePaths];
        await commands.executeCommand('workbench.action.closeAllEditors');
        assert.deepEqual(beforeCursorMove.visibleEditorFilePaths, copiedVisiblePaths);
        assert.deepEqual(beforeCursorMove.openDocumentFilePaths, copiedOpenPaths);
      } finally {
        await commands.executeCommand('workbench.action.closeAllEditors');
      }
    }
  );

  await runTest(
    'a real VS Code document QuickPick focuses without accepting and hide cancels',
    async () => {
      const first = documentTarget('Book1');
      const second = documentTarget('Book2');
      let quickPick: ReturnType<typeof window.createQuickPick<CommandPaletteDocumentQuickPickItem>> | undefined;
      const selected = chooseCommandPaletteDocumentWithQuickPick(
        () => {
          quickPick = window.createQuickPick<CommandPaletteDocumentQuickPickItem>();
          return quickPick;
        },
        [first, second],
        second
      );

      assert.ok(quickPick);
      assert.equal(quickPick.activeItems[0]?.document, second);
      assert.deepEqual(quickPick.selectedItems, []);
      let settled = false;
      void selected.then(() => {
        settled = true;
      });
      await delay(0);
      assert.equal(settled, false);

      quickPick.hide();
      assert.equal(await selected, undefined);
    }
  );

  await runTest(
    'actual VS Code progress and Output receive the target before fake companion and child start',
    async () => {
      const events: string[] = [];
      let processArgs: readonly string[] = [];
      const outputChannel = window.createOutputChannel(
        'VBA Tools Command Palette Target Integration Test'
      );
      const document = documentTarget('Book2');
      const projectRoot = path.join('C:\\work', 'Project');
      const target = {
        project: {
          projectRoot,
          manifestPath: path.join(projectRoot, 'vba-project.json'),
          projectName: 'FixtureProject',
          primaryDocument: 'Book1',
          documents: [document]
        },
        document
      };

      try {
        await window.withProgress(
          {
            location: ProgressLocation.Notification,
            title: 'VBA Tools: Verify Command Palette target receipt',
            cancellable: true
          },
          async (progress, token) => {
            await runVbaDevProjectCommand({
              extensionRoot: 'C:\\extensions\\vba-tools',
              workspaceRoots: [],
              activeFilePath: undefined,
              fileExists: async () => false,
              findProjectManifests: async () => [],
              chooseProject: async () => undefined,
              resolveCommandPaletteTarget: async (scope) => {
                assert.equal(scope, 'document');
                return target;
              },
              vbaDevResolver: {
                resolve: async () => {
                  events.push('companion:resolve');
                  return {
                    executablePath: 'C:\\tools\\vba-dev.exe',
                    capabilities: {
                      toolVersion: '0.1.0',
                      contractVersion: '1.0',
                      featureVersions: {},
                      commands: {}
                    },
                    bundledPath: 'C:\\tools\\vba-dev.exe',
                    source: 'bundled'
                  };
                }
              },
              outputChannel: {
                append: (value) => {
                  events.push(`output:${value}`);
                  outputChannel.append(value);
                },
                appendLine: (value) => {
                  events.push(`output:${value}`);
                  outputChannel.appendLine(value);
                },
                show: (preserveFocus) => outputChannel.show(preserveFocus)
              },
              revealOutput: false,
              reportCancellationProgress: (message) => {
                events.push(`progress:${message}`);
                progress.report({ message });
              },
              cancellationToken: token,
              startProcess: (_file, args) => {
                events.push('process:start');
                processArgs = args;
                return {
                  onStdout: () => undefined,
                  onStderr: () => undefined,
                  onExit: (listener) => listener(0, null),
                  kill: () => undefined
                };
              },
              showErrorMessage: async () => undefined
            }, ['build'], [], 'document');
          }
        );

        assert.deepEqual(events.slice(0, 6), [
          'output:Command Palette target:',
          `output:  Project: FixtureProject (${projectRoot})`,
          'output:  Document: Book2',
          `progress:Project: FixtureProject (${projectRoot}); Document: Book2`,
          'companion:resolve',
          'process:start'
        ]);
        assert.deepEqual(processArgs, [
          'build',
          '--project', projectRoot,
          '--document', 'Book2'
        ]);
      } finally {
        outputChannel.dispose();
      }
    }
  );
}

function captureVscodeSnapshot() {
  return captureCommandPaletteInvocationSnapshot({
    activeTextEditor: window.activeTextEditor,
    visibleTextEditors: window.visibleTextEditors,
    textDocuments: workspace.textDocuments,
    workspaceFolders: workspace.workspaceFolders
  });
}

function documentTarget(name: string): CommandPaletteDocumentTarget {
  const sourceRoot = path.join('C:\\work\\Project\\src', name);
  return {
    name,
    sourcePath: `src/${name}`,
    sourceRoot,
    sourceRootIdentity: {
      canonicalPath: sourceRoot,
      kind: 'directory'
    }
  };
}

async function delay(milliseconds: number): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve, milliseconds));
}

async function runTest(name: string, body: () => Promise<void>): Promise<void> {
  const startedAt = Date.now();
  try {
    await body();
    console.log(`PASS ${name} (${Date.now() - startedAt} ms)`);
  } catch (error) {
    console.error(`FAIL ${name}`);
    throw error;
  }
}
