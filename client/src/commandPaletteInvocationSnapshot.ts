import type { CommandPaletteInvocationSnapshot } from './commandPaletteTarget';

export interface CommandPaletteSnapshotUri {
  readonly scheme: string;
  readonly fsPath: string;
}

export interface CommandPaletteSnapshotDocument {
  readonly uri: CommandPaletteSnapshotUri;
}

export interface CommandPaletteSnapshotEditor {
  readonly document: CommandPaletteSnapshotDocument;
}

export interface CommandPaletteSnapshotWorkspaceFolder {
  readonly uri: CommandPaletteSnapshotUri;
}

export interface CommandPaletteInvocationSnapshotHost {
  readonly activeTextEditor?: CommandPaletteSnapshotEditor | undefined;
  readonly visibleTextEditors: readonly CommandPaletteSnapshotEditor[];
  readonly textDocuments: readonly CommandPaletteSnapshotDocument[];
  readonly workspaceFolders?: readonly CommandPaletteSnapshotWorkspaceFolder[] | undefined;
}

export function captureCommandPaletteInvocationSnapshot(
  host: CommandPaletteInvocationSnapshotHost
): CommandPaletteInvocationSnapshot {
  const activeEditor = host.activeTextEditor;
  const activeEditorFilePath = activeEditor?.document.uri.scheme === 'file'
    ? activeEditor.document.uri.fsPath
    : undefined;
  return {
    activeFilePath: activeEditorFilePath,
    activeEditorFilePath,
    visibleEditorFilePaths: host.visibleTextEditors
      .filter((editor) => editor.document.uri.scheme === 'file')
      .map((editor) => editor.document.uri.fsPath),
    openDocumentFilePaths: host.textDocuments
      .filter((document) => document.uri.scheme === 'file')
      .map((document) => document.uri.fsPath),
    workspaceRoots: host.workspaceFolders
      ?.filter((folder) => folder.uri.scheme === 'file')
      .map((folder) => folder.uri.fsPath) ?? []
  };
}
