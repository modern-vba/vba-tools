import * as path from 'node:path';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

/** Compares resolved Windows paths without changing the caller's resolution basis. */
export function windowsPathKey(resolvedPath: string): string {
  const { root, segments } = splitWindowsPath(resolvedPath);
  return ordinalIgnoreCaseKey(root) + segments.map(ordinalIgnoreCaseKey).join('\\');
}

/** Returns the candidate's original-spelled suffix only after proving strict descent. */
export function relativeWindowsDescendantPath(resolvedRoot: string, resolvedCandidate: string): string | undefined {
  const parent = splitWindowsPath(resolvedRoot);
  const candidate = splitWindowsPath(resolvedCandidate);
  if (ordinalIgnoreCaseKey(parent.root) !== ordinalIgnoreCaseKey(candidate.root)
      || candidate.segments.length <= parent.segments.length
      || parent.segments.some((segment, index) =>
        ordinalIgnoreCaseKey(segment) !== ordinalIgnoreCaseKey(candidate.segments[index]))) {
    return undefined;
  }
  return candidate.segments.slice(parent.segments.length).join(path.sep);
}

function splitWindowsPath(resolvedPath: string): { root: string; segments: readonly string[] } {
  const normalizedPath = path.win32.normalize(resolvedPath);
  const root = path.win32.parse(normalizedPath).root;
  const segments = normalizedPath.slice(root.length).split('\\').filter(segment => segment.length !== 0);
  return { root, segments };
}
