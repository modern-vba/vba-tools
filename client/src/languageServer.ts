import * as path from 'node:path';
import type { CompletionItem, FileSystemWatcher } from 'vscode';
import type {
  ClientCapabilities,
  CompletionMiddleware,
  FormattingMiddleware,
  LanguageClientOptions,
  RenameMiddleware,
  ServerOptions,
  SignatureHelpMiddleware,
  StaticFeature
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
  readonly vbaDevExecutablePath?: string;
  readonly referenceCatalogCacheRoot?: string;
}

type Lsp318SignatureInformationCapabilities = {
  noActiveParameterSupport?: boolean;
};

type NullableActiveParameterSignature = {
  activeParameter?: number | null;
  parameters?: readonly unknown[];
};

const provideVbaSignatureHelp: NonNullable<
  SignatureHelpMiddleware['provideSignatureHelp']
> = async (document, position, context, token, next) => {
  const result = await next(document, position, context, token);
  if (result === null || result === undefined) {
    return result;
  }

  for (const signature of result.signatures) {
    const nullableSignature = signature as typeof signature
      & NullableActiveParameterSignature;
    if (nullableSignature.activeParameter === null) {
      nullableSignature.activeParameter = nullableSignature.parameters?.length ?? 0;
    }
  }

  return result;
};

type CompletionItemWithData = CompletionItem & {
  data?: unknown;
};

const provideVbaCompletionItems: NonNullable<
  CompletionMiddleware['provideCompletionItem']
> = async (document, position, context, token, next) => {
  const result = await next(document, position, context, token);
  if (result === null || result === undefined) {
    return result;
  }

  const items = Array.isArray(result) ? result : result.items;
  for (const item of items) {
    const data = (item as CompletionItemWithData).data;
    if (typeof data !== 'object'
        || data === null
        || !('retriggerCompletion' in data)
        || (data as { retriggerCompletion?: unknown }).retriggerCompletion !== true) {
      continue;
    }

    item.command ??= {
      title: 'Continue contract completion',
      command: 'editor.action.triggerSuggest'
    };
  }

  return result;
};

export function createVbaSignatureHelpClientCapabilitiesFeature(): StaticFeature {
  return {
    fillClientCapabilities(capabilities: ClientCapabilities): void {
      const textDocument = capabilities.textDocument ??= {};
      const signatureHelp = textDocument.signatureHelp ??= {};
      const signatureInformation = signatureHelp.signatureInformation ??= {};
      signatureHelp.contextSupport = true;
      signatureInformation.activeParameterSupport = true;
      (
        signatureInformation as typeof signatureInformation
          & Lsp318SignatureInformationCapabilities
      ).noActiveParameterSupport = true;
    },
    initialize(): void {},
    getState: () => ({ kind: 'static' }),
    clear(): void {}
  };
}

export function createVbaLanguageClientOptions(
  sourceFileWatcher: FileSystemWatcher,
  projectManifestWatcher: FileSystemWatcher,
  provideDocumentFormattingEdits?: NonNullable<
    FormattingMiddleware['provideDocumentFormattingEdits']
  >,
  provideRenameEdits?: NonNullable<RenameMiddleware['provideRenameEdits']>
): LanguageClientOptions {
  return {
    documentSelector: [
      { language: 'vba', scheme: 'file' },
      { language: 'vba', scheme: 'untitled' }
    ],
    synchronize: {
      fileEvents: [sourceFileWatcher, projectManifestWatcher]
    },
    middleware: {
      provideCompletionItem: provideVbaCompletionItems,
      provideSignatureHelp: provideVbaSignatureHelp,
      ...(provideDocumentFormattingEdits === undefined
        ? {}
        : { provideDocumentFormattingEdits }),
      ...(provideRenameEdits === undefined ? {} : { provideRenameEdits })
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
  const argumentsOptions = options.vbaDevExecutablePath === undefined
    ? {}
    : { args: ['--vba-dev', options.vbaDevExecutablePath] };
  const executable = processOptions === undefined
    ? {
        command: executablePath,
        ...argumentsOptions,
        transport: stdioTransportKind
      }
    : {
        command: executablePath,
        ...argumentsOptions,
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
