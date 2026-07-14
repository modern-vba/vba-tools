import * as path from 'node:path';
import { promises as fs } from 'node:fs';

import {
  ExtensionContext,
  OutputChannel,
  ProgressLocation,
  commands,
  languages,
  tests,
  window,
  workspace
} from 'vscode';
import {
  LanguageClient,
  LanguageClientOptions
} from 'vscode-languageclient/node';
import {
  promptForFirstRunDoctor,
  runDoctorCommand
} from './doctorCommand';
import {
  CommonModulesToolCommand,
  runCommonModulesAddCommand,
  runCommonModulesListCommand,
  runCommonModulesUpdateCommand
} from './commonModulesCommand';
import {
  WorkbookBackedProjectCandidate,
  findNearestProjectManifest
} from './projectDiscovery';
import {
  WorkbookBackedProjectToolCommand,
  runWorkbookBackedProjectCommand
} from './projectCommand';
import {
  ReferenceToolCommand,
  runReferenceAddCommand,
  runReferenceListCommand,
  runReferenceRemoveCommand
} from './referenceCommand';
import {
  createWorkbookBackedTestExplorer
} from './testExplorer';
import {
  registerWorkbookBackedTestExplorerRefresh
} from './testExplorerRefresh';
import {
  VbaDevDiagnosticReporter
} from './toolDiagnostics';
import {
  createVbaLanguageServerOptions,
  createVbaLanguageServerReferenceCatalogCacheRoot
} from './languageServer';
import {
  createVscodeDiagnosticCollectionAdapter,
  createVscodeTestControllerAdapter
} from './vscodeAdapters';
import {
  openVbaDevTerminal
} from './vbaDevTerminalCommand';

let client: LanguageClient | undefined;
let outputChannel: OutputChannel | undefined;
let toolDiagnosticReporter: VbaDevDiagnosticReporter | undefined;

export async function activate(context: ExtensionContext): Promise<void> {
  outputChannel = window.createOutputChannel('VBA Tools');
  context.subscriptions.push(outputChannel);
  try {
    const serverOptions = createVbaLanguageServerOptions({
      extensionRoot: context.extensionPath,
      referenceCatalogCacheRoot: createVbaLanguageServerReferenceCatalogCacheRoot(
        context.globalStorageUri.fsPath
      )
    });

    const clientOptions: LanguageClientOptions = {
      documentSelector: [
        { language: 'vba', scheme: 'file' },
        { language: 'vba', scheme: 'untitled' }
      ],
      synchronize: {
        fileEvents: workspace.createFileSystemWatcher('**/*.{bas,cls,frm}')
      }
    };

    client = new LanguageClient(
      'vbaLanguageServer',
      'VBA Language Server',
      serverOptions,
      clientOptions
    );

    context.subscriptions.push(client);
  } catch (error) {
    void window.showWarningMessage(error instanceof Error ? error.message : String(error));
  }

  const toolDiagnosticCollection = languages.createDiagnosticCollection('vba-dev');
  context.subscriptions.push(toolDiagnosticCollection);
  toolDiagnosticReporter = new VbaDevDiagnosticReporter(
    createVscodeDiagnosticCollectionAdapter(toolDiagnosticCollection)
  );
  const testController = tests.createTestController(
    'vbaTools.workbookBackedProjects',
    'VBA Workbook Tests'
  );
  context.subscriptions.push(testController);
  const workbookBackedTestExplorer = createWorkbookBackedTestExplorer({
    controller: createVscodeTestControllerAdapter(testController),
    extensionRoot: context.extensionPath,
    configuredDevToolPath: getConfiguredDevToolPath(),
    workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    findProjectManifests,
    readTextFile,
    outputChannel,
    showErrorMessage: (message: string) => window.showErrorMessage(message)
  });
  const projectManifestWatcher = workspace.createFileSystemWatcher('**/vba-project.json');
  context.subscriptions.push(projectManifestWatcher);
  registerWorkbookBackedTestExplorerRefresh({
    watcher: projectManifestWatcher,
    subscriptions: context.subscriptions,
    explorer: workbookBackedTestExplorer,
    showErrorMessage: (message) => window.showErrorMessage(message)
  });
  context.subscriptions.push(commands.registerCommand('vbaTools.doctor', async () => {
    await runDoctorWithProgress(context);
  }));
  context.subscriptions.push(commands.registerCommand('vbaTools.openVbaDevTerminal', async () => {
    await openVbaDevTerminalCommand(context);
  }));
  for (const command of WorkbookBackedProjectCommands) {
    context.subscriptions.push(commands.registerCommand(command.commandId, async () => {
      await runWorkbookBackedProjectCommandWithProgress(context, command.toolCommandName, command.title);
    }));
  }
  for (const command of CommonModulesCommands) {
    context.subscriptions.push(commands.registerCommand(command.commandId, async () => {
      await runCommonModulesCommandWithProgress(context, command.toolCommandName, command.title);
    }));
  }
  for (const command of ReferenceCommands) {
    context.subscriptions.push(commands.registerCommand(command.commandId, async () => {
      await runReferenceCommandWithProgress(context, command.toolCommandName, command.title);
    }));
  }

  await client?.start();
  await workbookBackedTestExplorer.refresh();
  await promptForActiveWorkbookBackedProject(context);
}

const WorkbookBackedProjectCommands: ReadonlyArray<{
  commandId: string;
  toolCommandName: WorkbookBackedProjectToolCommand;
  title: string;
}> = [
  { commandId: 'vbaTools.build', toolCommandName: 'build', title: 'VBA Tools: Build' },
  { commandId: 'vbaTools.test', toolCommandName: 'test', title: 'VBA Tools: Test' },
  { commandId: 'vbaTools.publish', toolCommandName: 'publish', title: 'VBA Tools: Publish' },
  { commandId: 'vbaTools.export', toolCommandName: 'export', title: 'VBA Tools: Export' }
];

const CommonModulesCommands: ReadonlyArray<{
  commandId: string;
  toolCommandName: CommonModulesToolCommand;
  title: string;
}> = [
  { commandId: 'vbaTools.commonModules.add', toolCommandName: 'add', title: 'VBA Tools: Add Common Module' },
  { commandId: 'vbaTools.commonModules.list', toolCommandName: 'list', title: 'VBA Tools: List Common Modules' },
  { commandId: 'vbaTools.commonModules.update', toolCommandName: 'update', title: 'VBA Tools: Update Common Modules' }
];

const ReferenceCommands: ReadonlyArray<{
  commandId: string;
  toolCommandName: ReferenceToolCommand;
  title: string;
}> = [
  { commandId: 'vbaTools.references.list', toolCommandName: 'list', title: 'VBA Tools: List References' },
  { commandId: 'vbaTools.references.add', toolCommandName: 'add', title: 'VBA Tools: Add Reference' },
  { commandId: 'vbaTools.references.remove', toolCommandName: 'remove', title: 'VBA Tools: Remove Reference' }
];

export async function deactivate(): Promise<void> {
  await client?.stop();
  client = undefined;
  outputChannel = undefined;
  toolDiagnosticReporter = undefined;
}

async function promptForActiveWorkbookBackedProject(context: ExtensionContext): Promise<void> {
  const activeFilePath = getActiveFilePath();
  if (!activeFilePath) {
    return;
  }

  const manifestPath = await findNearestProjectManifest(activeFilePath, fileExists);
  if (!manifestPath) {
    return;
  }

  await promptForFirstRunDoctor({
    workspaceState: context.workspaceState,
    showInformationMessage: (message, ...items) => window.showInformationMessage(message, ...items),
    runDoctor: async () => {
      await runDoctorWithProgress(context);
    }
  });
}

async function runDoctorWithProgress(context: ExtensionContext): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title: 'VBA Tools: Doctor',
      cancellable: true
    },
    async (_progress, token) => {
      await runDoctorCommand({
        extensionRoot: context.extensionPath,
        configuredDevToolPath: getConfiguredDevToolPath(),
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message) => window.showErrorMessage(message),
        cancellationToken: token
      });
    }
  );
}

async function openVbaDevTerminalCommand(context: ExtensionContext): Promise<void> {
  await openVbaDevTerminal({
    extensionRoot: context.extensionPath,
    configuredDevToolPath: getConfiguredDevToolPath(),
    activeFilePath: getActiveFilePath(),
    workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
    chooseWorkspaceRoot,
    createTerminal: (options) => window.createTerminal(options),
    showErrorMessage: (message) => window.showErrorMessage(message)
  });
}

async function runWorkbookBackedProjectCommandWithProgress(
  context: ExtensionContext,
  toolCommandName: WorkbookBackedProjectToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (_progress, token) => {
      await runWorkbookBackedProjectCommand({
        toolCommandName,
        title,
        extensionRoot: context.extensionPath,
        configuredDevToolPath: getConfiguredDevToolPath(),
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message) => window.showErrorMessage(message),
        cancellationToken: token
      });
    }
  );
}

async function runCommonModulesCommandWithProgress(
  context: ExtensionContext,
  toolCommandName: CommonModulesToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;
  const moduleNames = toolCommandName === 'add'
    ? await promptForCommonModuleNames()
    : undefined;
  if (toolCommandName === 'add' && moduleNames === undefined) {
    return;
  }

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (_progress, token) => {
      const options = {
        extensionRoot: context.extensionPath,
        configuredDevToolPath: getConfiguredDevToolPath(),
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message: string) => window.showErrorMessage(message),
        cancellationToken: token
      };

      if (toolCommandName === 'add') {
        await runCommonModulesAddCommand(options, moduleNames ?? []);
      } else if (toolCommandName === 'update') {
        await runCommonModulesUpdateCommand(options);
      } else {
        await runCommonModulesListCommand(options);
      }
    }
  );
}

async function promptForCommonModuleNames(): Promise<readonly string[] | undefined> {
  const value = await window.showInputBox({
    title: 'Add Common Module',
    prompt: 'Enter one or more CommonModuleName values separated by spaces.'
  });
  if (value === undefined) {
    return undefined;
  }

  const moduleNames = value
    .split(/\s+/)
    .map((moduleName) => moduleName.trim())
    .filter((moduleName) => moduleName.length > 0);

  return moduleNames.length > 0 ? moduleNames : undefined;
}

async function runReferenceCommandWithProgress(
  context: ExtensionContext,
  toolCommandName: ReferenceToolCommand,
  title: string
): Promise<void> {
  const channel = outputChannel ?? window.createOutputChannel('VBA Tools');
  outputChannel = channel;
  const referenceName = toolCommandName === 'list'
    ? undefined
    : await promptForReferenceName(title);
  if (toolCommandName !== 'list' && referenceName === undefined) {
    return;
  }

  await window.withProgress(
    {
      location: ProgressLocation.Notification,
      title,
      cancellable: true
    },
    async (_progress, token) => {
      const options = {
        extensionRoot: context.extensionPath,
        configuredDevToolPath: getConfiguredDevToolPath(),
        activeFilePath: getActiveFilePath(),
        workspaceRoots: workspace.workspaceFolders?.map((folder) => folder.uri.fsPath) ?? [],
        fileExists,
        findProjectManifests,
        chooseProject,
        outputChannel: channel,
        diagnosticReporter: toolDiagnosticReporter,
        showErrorMessage: (message: string) => window.showErrorMessage(message),
        cancellationToken: token
      };

      if (toolCommandName === 'add') {
        await runReferenceAddCommand(options, referenceName ?? '');
      } else if (toolCommandName === 'remove') {
        await runReferenceRemoveCommand(options, referenceName ?? '');
      } else {
        await runReferenceListCommand(options);
      }
    }
  );
}

async function promptForReferenceName(title: string): Promise<string | undefined> {
  const value = await window.showInputBox({
    title,
    prompt: 'Enter the exact Reference.Description name.'
  });
  if (value === undefined) {
    return undefined;
  }

  return value.trim();
}

function getConfiguredDevToolPath(): string | undefined {
  const configured = workspace.getConfiguration('vbaTools').get<string>('devtool.path');
  return configured && configured.trim().length > 0 ? configured : undefined;
}

function getActiveFilePath(): string | undefined {
  const editor = window.activeTextEditor;
  return editor?.document.uri.scheme === 'file' ? editor.document.uri.fsPath : undefined;
}

async function fileExists(filePath: string): Promise<boolean> {
  try {
    const stat = await fs.stat(filePath);
    return stat.isFile();
  } catch {
    return false;
  }
}

async function readTextFile(filePath: string): Promise<string> {
  const buffer = await fs.readFile(filePath);
  if (buffer.length >= 2 && buffer[0] === 0xff && buffer[1] === 0xfe) {
    return buffer.subarray(2).toString('utf16le');
  }

  if (buffer.length >= 3 && buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) {
    return buffer.subarray(3).toString('utf8');
  }

  return buffer.toString('utf8');
}

async function findProjectManifests(): Promise<readonly string[]> {
  const uris = await workspace.findFiles('**/vba-project.json', '**/{node_modules,.git}/**');
  return uris.map((uri) => uri.fsPath);
}

async function chooseProject(
  candidates: readonly WorkbookBackedProjectCandidate[]
): Promise<WorkbookBackedProjectCandidate | undefined> {
  const selected = await window.showQuickPick(
    candidates.map((candidate) => ({
      label: path.basename(candidate.projectRoot),
      description: candidate.projectRoot,
      candidate
    })),
    {
      title: 'Select WorkbookBackedProject'
    }
  );

  return selected?.candidate;
}

async function chooseWorkspaceRoot(workspaceRoots: readonly string[]): Promise<string | undefined> {
  const selected = await window.showQuickPick(
    workspaceRoots.map((workspaceRoot) => ({
      label: path.basename(workspaceRoot) || workspaceRoot,
      description: workspaceRoot,
      workspaceRoot
    })),
    {
      title: 'Select vba-dev Terminal Folder'
    }
  );

  return selected?.workspaceRoot;
}
