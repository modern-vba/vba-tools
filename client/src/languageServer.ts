import * as path from 'node:path';

import type { ServerOptions } from 'vscode-languageclient/node';

const stdioTransportKind = 0;

export interface VbaLanguageServerPathOptions {
  readonly extensionRoot: string;
}

export function resolveVbaLanguageServerPath(options: VbaLanguageServerPathOptions): string {
  return path.join(
    options.extensionRoot,
    'bin',
    'vba-language-server',
    'win-x64',
    'vba-language-server.exe'
  );
}

export function createVbaLanguageServerOptions(options: VbaLanguageServerPathOptions): ServerOptions {
  const executablePath = resolveVbaLanguageServerPath(options);

  return {
    run: {
      command: executablePath,
      transport: stdioTransportKind
    },
    debug: {
      command: executablePath,
      transport: stdioTransportKind
    }
  };
}
