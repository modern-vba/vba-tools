import * as path from 'node:path';
import type { FileSystemWatcher } from 'vscode';
import type {
  LanguageClientOptions,
  ServerOptions
} from 'vscode-languageclient/node';
import { resolveBundledRuntimePath } from './distributionManifest';

const stdioTransportKind = 0;
const referenceCatalogCacheDirectoryName = 'reference-catalogs';

type PlatformName = NodeJS.Platform | string;

export const referenceCatalogCacheRootEnvironmentVariable = 'VBA_TOOLS_REFERENCE_CATALOG_CACHE_DIR';

export interface VbaLanguageServerPathOptions {
  readonly extensionRoot: string;
}

export interface VbaLanguageServerOptions extends VbaLanguageServerPathOptions {
  readonly platform?: PlatformName;
  readonly referenceCatalogCacheRoot?: string;
}

export function createVbaLanguageClientOptions(
  sourceFileWatcher: FileSystemWatcher,
  projectManifestWatcher: FileSystemWatcher
): LanguageClientOptions {
  return {
    documentSelector: [
      { language: 'vba', scheme: 'file' },
      { language: 'vba', scheme: 'untitled' }
    ],
    synchronize: {
      fileEvents: [sourceFileWatcher, projectManifestWatcher]
    }
  };
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

export function createVbaLanguageServerReferenceCatalogCacheRoot(globalStorageRoot: string): string {
  return path.join(globalStorageRoot, referenceCatalogCacheDirectoryName);
}

export function createVbaLanguageServerOptions(options: VbaLanguageServerOptions): ServerOptions {
  const platform = options.platform ?? process.platform;
  if (!isVbaLanguageServerPlatformSupported(platform)) {
    throw new Error(createUnsupportedVbaLanguageServerPlatformMessage(platform));
  }

  const executablePath = resolveVbaLanguageServerPath(options);
  const processOptions = createVbaLanguageServerProcessOptions(options.referenceCatalogCacheRoot);
  const executable = processOptions === undefined
    ? {
        command: executablePath,
        transport: stdioTransportKind
      }
    : {
        command: executablePath,
        transport: stdioTransportKind,
        options: processOptions
      };

  return {
    run: executable,
    debug: executable
  };
}

function createVbaLanguageServerProcessOptions(referenceCatalogCacheRoot: string | undefined): {
  readonly env: NodeJS.ProcessEnv;
} | undefined {
  if (referenceCatalogCacheRoot === undefined || referenceCatalogCacheRoot.trim().length === 0) {
    return undefined;
  }

  return {
    env: {
      ...process.env,
      [referenceCatalogCacheRootEnvironmentVariable]: referenceCatalogCacheRoot
    }
  };
}
