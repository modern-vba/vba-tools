import * as path from 'node:path';
import type {
  Disposable,
  TextDocumentChangeEvent,
  Uri as VscodeUri
} from 'vscode';
import { Uri, workspace } from 'vscode';

const vbaSourceExtensions = new Set(['.bas', '.cls', '.frm', '.frx']);
const planRetentionMilliseconds = 5 * 60 * 1_000;
const workspaceEditApplicationGraceMilliseconds = 100;

export interface CaseOnlyVbaFileRename {
  readonly oldUri: string;
  readonly newUri: string;
}

interface TrackedCaseOnlyVbaFileRename {
  readonly oldUri: VscodeUri;
  readonly newUri: VscodeUri;
  readonly oldName: string;
  readonly newName: string;
  readonly capturedAt: number;
  state: 'before' | 'after';
}

export class CaseOnlyVbaFileRenameAdapter implements Disposable {
  private readonly tracked: TrackedCaseOnlyVbaFileRename[] = [];
  private readonly subscription: Disposable;
  private pending = Promise.resolve();

  public constructor(private readonly reportFailure: (message: string) => void) {
    this.subscription = workspace.onDidChangeTextDocument(event => {
      this.handleTextDocumentChange(event);
    });
  }

  public capture(renames: readonly CaseOnlyVbaFileRename[]): void {
    const capturedAt = Date.now();
    this.removeExpired(capturedAt);
    for (const rename of renames) {
      const oldUri = Uri.parse(rename.oldUri);
      const newUri = Uri.parse(rename.newUri);
      if (!isCaseOnlyVbaFileRename(oldUri, newUri)) {
        continue;
      }

      const trackedRename: TrackedCaseOnlyVbaFileRename = {
        oldUri,
        newUri,
        oldName: path.basename(oldUri.fsPath, path.extname(oldUri.fsPath)),
        newName: path.basename(newUri.fsPath, path.extname(newUri.fsPath)),
        capturedAt,
        state: 'before'
      };
      const existingIndex = this.tracked.findIndex(candidate =>
        pathsEqual(candidate.oldUri.fsPath, oldUri.fsPath)
          && pathsEqual(candidate.newUri.fsPath, newUri.fsPath));
      if (existingIndex >= 0) {
        this.tracked.splice(existingIndex, 1, trackedRename);
      } else {
        this.tracked.push(trackedRename);
      }
    }
    if (this.tracked.length > 32) {
      this.tracked.splice(0, this.tracked.length - 32);
    }
  }

  public dispose(): void {
    this.subscription.dispose();
    this.tracked.length = 0;
  }

  private handleTextDocumentChange(event: TextDocumentChangeEvent): void {
    this.removeExpired(Date.now());
    const authoritativeName = readLastModuleIdentity(event.document.getText());
    if (authoritativeName === undefined) {
      return;
    }

    const documentPath = event.document.uri.fsPath;
    for (const rename of this.tracked) {
      if (!pathsEqual(documentPath, rename.oldUri.fsPath)
          && !pathsEqual(documentPath, rename.newUri.fsPath)) {
        continue;
      }

      if (rename.state === 'before' && authoritativeName === rename.newName) {
        this.enqueue(rename, rename.newUri, 'after');
        this.enqueueMatchingFormSidecar(rename, 'after');
      } else if (rename.state === 'after' && authoritativeName === rename.oldName) {
        this.enqueue(rename, rename.oldUri, 'before');
        this.enqueueMatchingFormSidecar(rename, 'before');
      }
    }
  }

  private enqueueMatchingFormSidecar(
    formRename: TrackedCaseOnlyVbaFileRename,
    nextState: TrackedCaseOnlyVbaFileRename['state']
  ): void {
    if (path.extname(formRename.oldUri.fsPath).toLowerCase() !== '.frm') {
      return;
    }

    const sidecarRename = this.tracked.find(candidate =>
      path.extname(candidate.oldUri.fsPath).toLowerCase() === '.frx'
        && candidate.oldName === formRename.oldName
        && candidate.newName === formRename.newName
        && pathsEqual(
          path.dirname(candidate.oldUri.fsPath),
          path.dirname(formRename.oldUri.fsPath)
        )
        && candidate.state !== nextState);
    if (sidecarRename === undefined) {
      return;
    }

    this.enqueue(
      sidecarRename,
      nextState === 'after' ? sidecarRename.newUri : sidecarRename.oldUri,
      nextState
    );
  }

  private enqueue(
    rename: TrackedCaseOnlyVbaFileRename,
    requestedUri: VscodeUri,
    nextState: TrackedCaseOnlyVbaFileRename['state']
  ): void {
    this.pending = this.pending.then(async () => {
      await new Promise(resolve => setTimeout(
        resolve,
        workspaceEditApplicationGraceMilliseconds
      ));
      await realizeCaseOnlyFileName(requestedUri);
      rename.state = nextState;
    }).catch((error: unknown) => {
      this.reportFailure(
        'WorkspaceEditApplicationFailure: the requested case-only VBA file '
          + `Rename could not be completed (${String(error)}). Use Undo, repair `
          + 'the destination or filesystem condition, and retry Rename.'
      );
    });
  }

  private removeExpired(now: number): void {
    for (let index = this.tracked.length - 1; index >= 0; index--) {
      if (now - this.tracked[index].capturedAt > planRetentionMilliseconds) {
        this.tracked.splice(index, 1);
      }
    }
  }
}

function isCaseOnlyVbaFileRename(oldUri: VscodeUri, newUri: VscodeUri): boolean {
  if (oldUri.scheme !== 'file' || newUri.scheme !== 'file') {
    return false;
  }

  const oldPath = path.resolve(oldUri.fsPath);
  const newPath = path.resolve(newUri.fsPath);
  return vbaSourceExtensions.has(path.extname(newPath).toLowerCase())
    && oldPath !== newPath
    && pathsEqual(oldPath, newPath);
}

function readLastModuleIdentity(text: string): string | undefined {
  let identity: string | undefined;
  const pattern = /^Attribute[ \t]+VB_Name[ \t]*=[ \t]*"([^"]+)"[ \t]*$/gmi;
  for (const match of text.matchAll(pattern)) {
    identity = match[1];
  }
  return identity;
}

async function realizeCaseOnlyFileName(requestedUri: VscodeUri): Promise<void> {
  const directoryUri = Uri.file(path.dirname(requestedUri.fsPath));
  const requestedName = path.basename(requestedUri.fsPath);
  const entries = await workspace.fs.readDirectory(directoryUri);
  if (entries.some(([name]) => name === requestedName)) {
    return;
  }

  const matchingNames = entries
    .map(([name]) => name)
    .filter(name => name.toLowerCase() === requestedName.toLowerCase());
  if (matchingNames.length !== 1) {
    throw new Error(
      matchingNames.length === 0
        ? `source '${requestedUri.fsPath}' is missing`
        : `source '${requestedUri.fsPath}' is ambiguous`
    );
  }

  const existingNames = new Set(entries.map(([name]) => name.toLowerCase()));
  const extension = path.extname(requestedName);
  const baseName = path.basename(requestedName, extension);
  let stagingName: string | undefined;
  for (let index = 0; index < 1_000; index++) {
    const suffix = index === 0 ? '' : `-${index}`;
    const candidate = `.vba-tools-case-rename-${baseName}${suffix}${extension}`;
    if (!existingNames.has(candidate.toLowerCase())) {
      stagingName = candidate;
      break;
    }
  }
  if (stagingName === undefined) {
    throw new Error(`no collision-free staging name exists beside '${requestedUri.fsPath}'`);
  }

  const actualUri = Uri.joinPath(directoryUri, matchingNames[0]);
  const stagingUri = Uri.joinPath(directoryUri, stagingName);
  const finalUri = Uri.joinPath(directoryUri, requestedName);
  await workspace.fs.rename(actualUri, stagingUri, { overwrite: false });
  await workspace.fs.rename(stagingUri, finalUri, { overwrite: false });
}

function pathsEqual(left: string, right: string): boolean {
  return path.resolve(left).toLowerCase() === path.resolve(right).toLowerCase();
}
