import { createHash } from 'node:crypto';
import { closeSync, lstatSync, openSync, readSync, readdirSync } from 'node:fs';
import * as path from 'node:path';

interface SnapshotEvidenceLimits {
  readonly maxFiles: number;
  readonly maxBytes: number;
  readonly maxDepth: number;
  readonly maxEntries: number;
}

const defaultLimits: SnapshotEvidenceLimits = {
  maxFiles: 64,
  maxBytes: 16 * 1024 * 1024,
  maxDepth: 16,
  maxEntries: 256
};

interface SnapshotFileEvidence {
  readonly path: string;
  readonly bytes: number;
  readonly sha256: string;
}

export type SnapshotInputEvidence = {
  readonly status: 'complete';
  readonly files: readonly SnapshotFileEvidence[];
  readonly totalBytes: number;
  readonly treeSha256: string;
} | {
  readonly status: 'unavailable';
  readonly reason: string;
  readonly observedFiles: number;
  readonly observedBytes: number;
  readonly errorCode?: string | undefined;
};

class EvidenceLimitError extends Error {
  constructor(readonly reason: string) { super(reason); }
}

export function captureSnapshotInputEvidence(
  root: string | undefined,
  limits: Partial<SnapshotEvidenceLimits> = {}
): SnapshotInputEvidence {
  const budget = { ...defaultLimits, ...limits };
  const files: SnapshotFileEvidence[] = [];
  let totalBytes = 0;
  let entries = 0;
  if (root === undefined) {
    return { status: 'unavailable', reason: 'missing-snapshot-path', observedFiles: 0, observedBytes: 0 };
  }
  try {
    const rootInfo = lstatSync(root);
    if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) throw new EvidenceLimitError('linked-or-invalid-root');
    const visit = (directory: string, relative: string, depth: number): void => {
      if (depth > budget.maxDepth) throw new EvidenceLimitError('depth-limit');
      const children = readdirSync(directory, { withFileTypes: true }).sort((left, right) =>
        left.name < right.name ? -1 : left.name > right.name ? 1 : 0);
      for (const child of children) {
        entries += 1;
        if (entries > budget.maxEntries) throw new EvidenceLimitError('entry-count-limit');
        const nextPath = path.join(directory, child.name);
        const nextRelative = relative === '' ? child.name : `${relative}/${child.name}`;
        const info = lstatSync(nextPath);
        if (info.isSymbolicLink()) throw new EvidenceLimitError('linked-entry');
        if (info.isDirectory()) {
          visit(nextPath, nextRelative, depth + 1);
          continue;
        }
        if (!info.isFile()) throw new EvidenceLimitError('non-file-entry');
        if (files.length >= budget.maxFiles) throw new EvidenceLimitError('file-count-limit');
        if (info.size > budget.maxBytes - totalBytes) throw new EvidenceLimitError('byte-limit');
        const hash = createHash('sha256');
        const chunk = Buffer.allocUnsafe(64 * 1024);
        let fileBytes = 0;
        const handle = openSync(nextPath, 'r');
        try {
          while (true) {
            const remaining = budget.maxBytes - totalBytes;
            const read = readSync(handle, chunk, 0, Math.min(chunk.length, remaining + 1), null);
            if (read === 0) break;
            totalBytes += read;
            fileBytes += read;
            if (totalBytes > budget.maxBytes) throw new EvidenceLimitError('byte-limit');
            hash.update(chunk.subarray(0, read));
          }
        } finally {
          closeSync(handle);
        }
        const after = lstatSync(nextPath);
        if (!after.isFile() || after.isSymbolicLink()
            || after.size !== info.size || after.mtimeMs !== info.mtimeMs || fileBytes !== info.size) {
          throw new EvidenceLimitError('changed-during-read');
        }
        files.push({ path: nextRelative, bytes: fileBytes, sha256: hash.digest('hex') });
      }
    };
    visit(root, '', 0);
    files.sort((left, right) => left.path < right.path ? -1 : left.path > right.path ? 1 : 0);
    const treeSha256 = createHash('sha256').update(files
      .map(file => `${file.path}\0${file.bytes}\0${file.sha256}\n`).join(''), 'utf8').digest('hex');
    return { status: 'complete', files, totalBytes, treeSha256 };
  } catch (error) {
    return { status: 'unavailable', reason: error instanceof EvidenceLimitError ? error.reason : 'read-failed',
      observedFiles: files.length, observedBytes: totalBytes,
      ...((error as NodeJS.ErrnoException).code ? { errorCode: (error as NodeJS.ErrnoException).code } : {}) };
  }
}
