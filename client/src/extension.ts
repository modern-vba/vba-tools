import * as path from 'node:path';
import { promises as fs } from 'node:fs';

import {
  ExtensionContext,
  OutputChannel,
  ProgressLocation,
  commands,
  window,
  workspace
} from 'vscode';
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind
} from 'vscode-languageclient/node';
import {
  promptForFirstRunDoctor,
  runDoctorCommand
} from './doctorCommand';
import {
  WorkbookBackedProjectCandidate,
  findNearestProjectManifest
} from './projectDiscovery';
import {
  WorkbookBackedProjectToolCommand,
  runWorkbookBackedProjectCommand
} from './projectCommand';

let client: LanguageClient | undefined;
let outputChannel: OutputChannel | undefined;

export async function activate(context: ExtensionContext): Promise<void> {
  const serverModule = context.asAbsolutePath(path.join('server', 'out', 'server.js'));
  const debugOptions = { execArgv: ['--nolazy', '--inspect=6009'] };

  const serverOptions: ServerOptions = {
    run: {
      module: serverModule,
      transport: TransportKind.ipc
    },
    debug: {
      module: serverModule,
      options: debugOptions,
      transport: TransportKind.ipc
    }
  };

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
  outputChannel = window.createOutputChannel('VBA Tools');
  context.subscriptions.push(outputChannel);
  context.subscriptions.push(commands.registerCommand('vbaTools.doctor', async () => {
    await runDoctorWithProgress(context);
  }));
  for (const command of WorkbookBackedProjectCommands) {
    context.subscriptions.push(commands.registerCommand(command.commandId, async () => {
      await runWorkbookBackedProjectCommandWithProgress(context, command.toolCommandName, command.title);
    }));
  }

  await client.start();
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

export async function deactivate(): Promise<void> {
  await client?.stop();
  client = undefined;
  outputChannel = undefined;
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
        showErrorMessage: (message) => window.showErrorMessage(message),
        cancellationToken: token
      });
    }
  );
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
        showErrorMessage: (message) => window.showErrorMessage(message),
        cancellationToken: token
      });
    }
  );
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

async function findProjectManifests(): Promise<readonly string[]> {
  const uris = await workspace.findFiles('**/project.json', '**/{node_modules,.git}/**');
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
