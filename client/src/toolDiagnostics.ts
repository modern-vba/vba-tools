import {
  VbaDevDiagnostic,
  parseVbaDevDiagnostics
} from './vbaDevOutputContract';
import { windowsPathKey } from './windowsPathIdentity';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

export {
  VbaDevDiagnostic,
  VbaDevDiagnosticPosition,
  VbaDevDiagnosticRange,
  VbaDevDiagnosticSeverity,
  parseVbaDevDiagnostics
} from './vbaDevOutputContract';

export interface VbaDevDiagnosticCollection {
  set(uriPath: string, diagnostics: readonly VbaDevDiagnostic[]): void;
  delete(uriPath: string): void;
}

export interface VbaDevDiagnosticReporterLike {
  refresh(scopeKey: string, output: string): readonly VbaDevDiagnostic[];
}

export function vbaDevDiagnosticScope(commandName: string, projectRoot: string, documentName?: string): string {
  return JSON.stringify([
    'vba-dev',
    commandName,
    windowsPathKey(projectRoot),
    documentName === undefined ? null : ordinalIgnoreCaseKey(documentName)
  ]);
}

export function combineVbaDevDiagnosticOutput(stdout: string, stderr: string): string {
  if (stdout.length > 0 && stderr.length > 0) {
    return `${stdout}\n${stderr}`;
  }

  return stdout.length > 0 ? stdout : stderr;
}

export class VbaDevDiagnosticReporter implements VbaDevDiagnosticReporterLike {
  private readonly diagnosticsByScope = new Map<string, Map<string, DiagnosticContribution>>();
  private readonly publishedUriPaths = new Map<string, string>();

  public constructor(private readonly collection: VbaDevDiagnosticCollection) {
  }

  public refresh(scopeKey: string, output: string): VbaDevDiagnostic[] {
    const diagnostics = parseVbaDevDiagnostics(output);
    const diagnosticsByUri = groupByUriPath(diagnostics);
    const affectedUris = new Set([
      ...(this.diagnosticsByScope.get(scopeKey)?.keys() ?? []),
      ...diagnosticsByUri.keys()
    ]);
    if (diagnosticsByUri.size === 0) {
      this.diagnosticsByScope.delete(scopeKey);
    } else {
      this.diagnosticsByScope.set(scopeKey, diagnosticsByUri);
    }

    for (const uriKey of affectedUris) {
      let uriPath = this.publishedUriPaths.get(uriKey);
      const combined: VbaDevDiagnostic[] = [];
      for (const contributions of this.diagnosticsByScope.values()) {
        const contribution = contributions.get(uriKey);
        if (contribution !== undefined) {
          uriPath ??= contribution.uriPath;
          combined.push(...contribution.diagnostics);
        }
      }

      if (combined.length === 0) {
        if (uriPath !== undefined) this.collection.delete(uriPath);
        this.publishedUriPaths.delete(uriKey);
      } else {
        this.collection.set(uriPath!, combined);
        this.publishedUriPaths.set(uriKey, uriPath!);
      }
    }

    return diagnostics;
  }
}

interface DiagnosticContribution {
  uriPath: string;
  diagnostics: VbaDevDiagnostic[];
}

function groupByUriPath(diagnostics: readonly VbaDevDiagnostic[]): Map<string, DiagnosticContribution> {
  const result = new Map<string, DiagnosticContribution>();
  for (const diagnostic of diagnostics) {
    const key = windowsPathKey(diagnostic.uriPath);
    const group = result.get(key) ?? { uriPath: diagnostic.uriPath, diagnostics: [] };
    group.diagnostics.push(diagnostic);
    result.set(key, group);
  }

  return result;
}
