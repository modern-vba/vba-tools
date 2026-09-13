import * as path from 'node:path';
import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

declare const sourceIdentityBrand: unique symbol;
export type SourceIdentityKey = string & { readonly [sourceIdentityBrand]: true };

export interface AdmittedSourceUri {
  readonly originalUri: string;
  readonly filePath: string;
  readonly identity: SourceIdentityKey;
  readonly isWindowsPath: boolean;
}

/** Compares resolved Windows paths without changing the caller's resolution basis. */
export function windowsPathKey(resolvedPath: string): SourceIdentityKey {
  const { root, segments } = splitWindowsPath(resolvedPath);
  return (ordinalIgnoreCaseKey(root) + segments.map(ordinalIgnoreCaseKey).join('\\')) as SourceIdentityKey;
}

/** Admits Windows file URI spelling without consulting a filesystem or rewriting presentation data. */
export function tryParseWindowsSourceUri(originalUri: string): AdmittedSourceUri | undefined {
  return parseSourceUri(originalUri, false);
}

/** Native POSIX file paths retain a separate identity on non-Windows hosts. */
export function tryParseSourceUri(originalUri: string,
  nativePathStyle: 'windows' | 'posix' = process.platform === 'win32' ? 'windows' : 'posix'): AdmittedSourceUri | undefined {
  return parseSourceUri(originalUri, nativePathStyle === 'posix');
}

export function sourcePathIdentity(resolvedPath: string): SourceIdentityKey {
  return isWindowsAbsolutePath(resolvedPath)
    ? windowsPathKey(resolvedPath)
    : path.posix.normalize(resolvedPath) as SourceIdentityKey;
}

export function relativeSourceDescendantPath(resolvedRoot: string, resolvedCandidate: string): string | undefined {
  if (isWindowsAbsolutePath(resolvedRoot) || isWindowsAbsolutePath(resolvedCandidate)) {
    return isWindowsAbsolutePath(resolvedRoot) && isWindowsAbsolutePath(resolvedCandidate)
      ? relativeWindowsDescendantPath(resolvedRoot, resolvedCandidate) : undefined;
  }
  if (!resolvedRoot.startsWith('/') || !resolvedCandidate.startsWith('/')) return undefined;
  const parent = path.posix.normalize(resolvedRoot).split('/').filter(segment => segment.length !== 0);
  const candidate = path.posix.normalize(resolvedCandidate).split('/').filter(segment => segment.length !== 0);
  if (candidate.length <= parent.length || parent.some((segment, index) => segment !== candidate[index])) return undefined;
  return candidate.slice(parent.length).join('/');
}

function isWindowsAbsolutePath(value: string): boolean {
  return /^[a-z]:[\\/]|^[\\/]{2}/i.test(value);
}

function parseSourceUri(originalUri: string, allowNativePosix: boolean): AdmittedSourceUri | undefined {
  if (!/^file:/i.test(originalUri)) return undefined;
  const rawPath = originalUri.slice(5).split(/[?#]/, 1)[0];
  if (/%(?:2f|5c)/i.test(rawPath)) return undefined;
  let decoded: string;
  try { decoded = decodeURIComponent(rawPath); } catch { return undefined; }
  if (decoded.includes('\0') || [...decoded].some(character => {
    const code = character.codePointAt(0)!;
    return code >= 0xd800 && code <= 0xdfff;
  })) return undefined;
  if (allowNativePosix) {
    let nativePath: string | undefined;
    if (decoded.startsWith('///') && !decoded.startsWith('////')) nativePath = decoded.slice(2);
    else if (ordinalIgnoreCaseKey(decoded.slice(0, 12)) === ordinalIgnoreCaseKey('//localhost/')) {
      nativePath = decoded.slice(11);
    } else if (decoded.startsWith('/') && !decoded.startsWith('//')) nativePath = decoded;
    if (nativePath !== undefined && !/^[a-z]:\//i.test(nativePath.replace(/^\/+/, '').replaceAll('\\', '/'))) {
      const filePath = path.posix.normalize(nativePath);
      return Object.freeze({ originalUri, filePath, identity: filePath as SourceIdentityKey, isWindowsPath: false });
    }
  }
  const uriPath = decoded.replaceAll('\\', '/');
  const leadingSlashes = /^\/*/.exec(uriPath)![0].length;
  const drivePath = uriPath.slice(leadingSlashes);
  let windowsPath: string;
  if (leadingSlashes <= 3 && /^[a-z]:\//i.test(drivePath)) windowsPath = drivePath;
  else {
    if (leadingSlashes !== 2 && leadingSlashes < 4) return undefined;
    const authorityEnd = uriPath.indexOf('/', leadingSlashes);
    if (authorityEnd < 0) return undefined;
    const authority = uriPath.slice(leadingSlashes, authorityEnd);
    if (!authority || authority === '.' || authority === '..' || /[@:\[\]\x00-\x20\x7f]/.test(authority)) {
      return undefined;
    }
    const remainder = uriPath.slice(authorityEnd + 1).replace(/^\/+/, '');
    if (ordinalIgnoreCaseKey(authority) === ordinalIgnoreCaseKey('localhost') && /^[a-z]:\//i.test(remainder)) {
      windowsPath = remainder;
    } else {
      const share = remainder.split('/', 1)[0];
      if (!share || share === '.' || share === '..') return undefined;
      windowsPath = '//' + authority + '/' + remainder;
    }
  }
  const normalizedPath = path.win32.normalize(windowsPath);
  const filePath = normalizedPath.length > path.win32.parse(normalizedPath).root.length
    ? normalizedPath.replace(/\\+$/, '') : normalizedPath;
  return Object.freeze({ originalUri, filePath, identity: windowsPathKey(filePath), isWindowsPath: true });
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
