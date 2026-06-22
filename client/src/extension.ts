import * as path from 'node:path';

import { ExtensionContext, workspace } from 'vscode';
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind
} from 'vscode-languageclient/node';

let client: LanguageClient | undefined;

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
  await client.start();
}

export async function deactivate(): Promise<void> {
  await client?.stop();
  client = undefined;
}
