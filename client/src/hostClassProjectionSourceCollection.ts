import * as path from 'node:path';

export interface HostClassProjectionDiskFormSource {
  readonly filePath: string;
  readonly sourceUri: string;
}

export interface HostClassProjectionOpenFormSource {
  readonly scheme: string;
  readonly filePath: string;
  readonly sourceUri: string;
  readonly text: string;
}

export interface HostClassProjectionFormSourceCandidate {
  readonly sourceUri: string;
  readonly kind: 'form';
  readonly text: string;
}

export async function collectHostClassProjectionFormSources(
  sourceSetPath: string,
  diskSources: readonly HostClassProjectionDiskFormSource[],
  openSources: readonly HostClassProjectionOpenFormSource[],
  readDiskText: (source: HostClassProjectionDiskFormSource) => Promise<string>
): Promise<readonly HostClassProjectionFormSourceCandidate[]> {
  const merged = new Map<string, {
    readonly filePath: string;
    readonly sourceUri: string;
    readonly diskSource?: HostClassProjectionDiskFormSource;
    readonly openText?: string;
  }>();
  for (const source of diskSources) {
    if (path.extname(source.filePath).toLowerCase() !== '.frm' ||
      !pathContains(sourceSetPath, source.filePath)) {
      continue;
    }
    merged.set(canonicalPathKey(source.filePath), {
      filePath: source.filePath,
      sourceUri: source.sourceUri,
      diskSource: source
    });
  }
  for (const source of openSources) {
    if (source.scheme !== 'file' ||
      path.extname(source.filePath).toLowerCase() !== '.frm' ||
      !pathContains(sourceSetPath, source.filePath)) {
      continue;
    }

    const key = canonicalPathKey(source.filePath);
    merged.set(key, {
      filePath: source.filePath,
      sourceUri: source.sourceUri,
      diskSource: merged.get(key)?.diskSource,
      openText: source.text
    });
  }

  const sorted = [...merged.values()].sort((left, right) =>
    comparePaths(left.filePath, right.filePath)
  );
  return Promise.all(sorted.map(async (source) => ({
    sourceUri: source.sourceUri,
    kind: 'form' as const,
    text: source.openText ?? await readDiskText(source.diskSource!)
  })));
}

function comparePaths(left: string, right: string): number {
  const leftKey = canonicalPathKey(left);
  const rightKey = canonicalPathKey(right);
  return leftKey.localeCompare(rightKey) || left.localeCompare(right);
}

function canonicalPathKey(value: string): string {
  return path.normalize(path.resolve(value)).toLowerCase();
}

function pathContains(parent: string, candidate: string): boolean {
  const parentKey = canonicalPathKey(parent);
  const candidateKey = canonicalPathKey(candidate);
  return candidateKey === parentKey ||
    candidateKey.startsWith(`${parentKey}${path.sep}`);
}
