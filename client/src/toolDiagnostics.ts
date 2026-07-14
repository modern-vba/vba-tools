import {
  VbaDevDiagnostic,
  parseVbaDevDiagnostics
} from './vbaDevOutputContract';

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

export function projectDiagnosticScope(projectRoot: string): string {
  return `project:${projectRoot}`;
}

export function combineVbaDevDiagnosticOutput(stdout: string, stderr: string): string {
  if (stdout.length > 0 && stderr.length > 0) {
    return `${stdout}\n${stderr}`;
  }

  return stdout.length > 0 ? stdout : stderr;
}

export class VbaDevDiagnosticReporter implements VbaDevDiagnosticReporterLike {
  private readonly uriPathsByScope = new Map<string, Set<string>>();

  public constructor(private readonly collection: VbaDevDiagnosticCollection) {
  }

  public refresh(scopeKey: string, output: string): VbaDevDiagnostic[] {
    const previous = this.uriPathsByScope.get(scopeKey);
    if (previous) {
      for (const uriPath of previous) {
        this.collection.delete(uriPath);
      }
    }

    const diagnostics = parseVbaDevDiagnostics(output);
    const diagnosticsByUri = groupByUriPath(diagnostics);
    const next = new Set<string>();
    for (const [uriPath, items] of diagnosticsByUri) {
      this.collection.set(uriPath, items);
      next.add(uriPath);
    }

    this.uriPathsByScope.set(scopeKey, next);
    return diagnostics;
  }
}

function groupByUriPath(diagnostics: readonly VbaDevDiagnostic[]): Map<string, VbaDevDiagnostic[]> {
  const result = new Map<string, VbaDevDiagnostic[]>();
  for (const diagnostic of diagnostics) {
    const group = result.get(diagnostic.uriPath) ?? [];
    group.push(diagnostic);
    result.set(diagnostic.uriPath, group);
  }

  return result;
}
