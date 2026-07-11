import type { ServerOptions } from 'vscode-languageclient/node';
import { resolveBundledRuntimePath } from './distributionManifest';

const stdioTransportKind = 0;

type PlatformName = NodeJS.Platform | string;

export interface VbaLanguageServerPathOptions {
  readonly extensionRoot: string;
}

export interface VbaLanguageServerOptions extends VbaLanguageServerPathOptions {
  readonly platform?: PlatformName;
}

export function resolveVbaLanguageServerPath(options: VbaLanguageServerPathOptions): string {
  return resolveBundledRuntimePath(options.extensionRoot, 'vbaLanguageServer');
}

export function isVbaLanguageServerPlatformSupported(platform: PlatformName = process.platform): boolean {
  return platform === 'win32';
}

export function createUnsupportedVbaLanguageServerPlatformMessage(platform: PlatformName = process.platform): string {
  return `The bundled VBA Language Server is currently supported only on Windows. Current platform: ${platform}.`;
}

export function createVbaLanguageServerOptions(options: VbaLanguageServerOptions): ServerOptions {
  const platform = options.platform ?? process.platform;
  if (!isVbaLanguageServerPlatformSupported(platform)) {
    throw new Error(createUnsupportedVbaLanguageServerPlatformMessage(platform));
  }

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
