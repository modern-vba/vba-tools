import {
  VbaDevDiagnostic,
  parseVbaDevDiagnostics
} from './vbaDevOutputContract';
import { windowsPathKey } from './windowsPathIdentity';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';
import { fileURLToPath } from 'node:url';

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

export interface VbaDevSnapshotDiagnosticReporterLike extends VbaDevDiagnosticReporterLike {
  refreshSnapshot(scopeKey: string, output: string, origins: readonly SnapshotDiagnosticOrigin[],
    reportUnmapped: (message: string) => void): readonly VbaDevDiagnostic[];
}

export interface SnapshotDiagnosticOrigin {
  readonly snapshotUri: string;
  readonly sourceUri?: string | null;
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

export class VbaDevDiagnosticReporter implements VbaDevSnapshotDiagnosticReporterLike {
  private readonly diagnosticsByScope = new Map<string, Map<string, DiagnosticContribution>>();
  private readonly publishedUriPaths = new Map<string, string>();

  public constructor(private readonly collection: VbaDevDiagnosticCollection) {
  }

  public refreshSnapshot(
    scopeKey: string,
    output: string,
    origins: readonly SnapshotDiagnosticOrigin[],
    reportUnmapped: (message: string) => void
  ): VbaDevDiagnostic[] {
    const originPaths = new Map<string, string | undefined>();
    for (const origin of origins) {
      const key = windowsPathKey(fileURLToPath(origin.snapshotUri));
      if (originPaths.has(key)) throw new Error(`Duplicate snapshot diagnostic origin '${origin.snapshotUri}'.`);
      originPaths.set(key, originalFilePath(origin.sourceUri));
    }
    const warnings: string[] = [];
    const originalPath = (snapshotPath: string): string | undefined => {
      const original = originPaths.get(windowsPathKey(snapshotPath));
      if (original === undefined) warnings.push(
        `Snapshot diagnostic location '${snapshotPath}' has no supported captured source origin; navigation was omitted.`);
      return original;
    };
    const diagnostics = parseVbaDevDiagnostics(output).flatMap(diagnostic => {
      const uriPath = originalPath(diagnostic.uriPath);
      if (uriPath === undefined) return [];
      return [{
        ...diagnostic, uriPath,
        ...(diagnostic.relatedInformation === undefined ? {} : {
          relatedInformation: diagnostic.relatedInformation.flatMap(related => {
            const relatedPath = originalPath(related.location.uriPath);
            return relatedPath === undefined ? [] : [{
              ...related, location: { ...related.location, uriPath: relatedPath }
            }];
          })
        })
      }];
    });
    for (const warning of warnings) reportUnmapped(warning);
    return this.replace(scopeKey, diagnostics);
  }

  public refresh(scopeKey: string, output: string): VbaDevDiagnostic[] {
    const diagnostics = parseVbaDevDiagnostics(output);
    return this.replace(scopeKey, diagnostics);
  }

  private replace(scopeKey: string, diagnostics: VbaDevDiagnostic[]): VbaDevDiagnostic[] {
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

function originalFilePath(uri: string | null | undefined): string | undefined {
  if (uri == null) return undefined;
  try { return fileURLToPath(uri); } catch { return undefined; }
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
