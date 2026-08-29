import * as fs from 'node:fs/promises';
import * as path from 'node:path';

import {
  CommandPaletteDocumentTarget,
  CommandPalettePathIdentity
} from './commandPaletteTarget';

export interface CommandPaletteQuickPickDisposable {
  dispose(): void;
}

export interface CommandPaletteDocumentQuickPickItem {
  label: string;
  description: string;
  detail: string;
  document: CommandPaletteDocumentTarget;
}

export interface CommandPaletteDocumentQuickPick {
  title: string | undefined;
  items: readonly CommandPaletteDocumentQuickPickItem[];
  activeItems: readonly CommandPaletteDocumentQuickPickItem[];
  selectedItems: readonly CommandPaletteDocumentQuickPickItem[];
  onDidAccept(listener: () => void): CommandPaletteQuickPickDisposable;
  onDidHide(listener: () => void): CommandPaletteQuickPickDisposable;
  show(): void;
  dispose(): void;
}

export async function resolveCommandPalettePathIdentity(
  filePath: string
): Promise<CommandPalettePathIdentity> {
  const requestedPath = path.resolve(filePath);
  validateUnambiguousPath(requestedPath);
  const missingComponents: string[] = [];
  let existingPath = requestedPath;
  let canonicalPath: string;
  let stats: Awaited<ReturnType<typeof fs.stat>>;

  while (true) {
    try {
      canonicalPath = await fs.realpath(existingPath);
      stats = await fs.stat(canonicalPath, { bigint: true }) as typeof stats;
      break;
    } catch (error) {
      if (!isMissingPathError(error)) {
        throw error;
      }
      try {
        await fs.lstat(existingPath);
        throw new Error(`Filesystem-canonical path identity cannot be established: ${filePath}`);
      } catch (entryError) {
        if (!isMissingPathError(entryError)) {
          throw entryError;
        }
      }

      const trimmed = trimEndingSeparator(existingPath);
      const parent = path.dirname(trimmed);
      const component = path.basename(trimmed);
      if (parent === existingPath || parent === trimmed || component.length === 0) {
        throw new Error(`Filesystem-canonical path identity cannot be established: ${filePath}`);
      }
      validateMissingComponent(component, filePath);
      missingComponents.unshift(component);
      existingPath = parent;
    }
  }

  if (missingComponents.length > 0 && !stats.isDirectory()) {
    throw new Error(`Filesystem-canonical path identity cannot be established: ${filePath}`);
  }
  for (const component of missingComponents) {
    canonicalPath = path.join(canonicalPath, component);
  }

  return {
    canonicalPath,
    objectIdentity: missingComponents.length === 0
      ? `${stats.dev.toString(16)}:${stats.ino.toString(16)}`
      : undefined,
    kind: missingComponents.length > 0 || stats.isDirectory()
      ? 'directory'
      : 'file'
  };
}

function isMissingPathError(error: unknown): boolean {
  return typeof error === 'object' &&
    error !== null &&
    'code' in error &&
    (error as { code?: unknown }).code === 'ENOENT';
}

function trimEndingSeparator(value: string): string {
  const root = path.parse(value).root;
  return value === root ? value : value.replace(/[\\/]+$/u, '');
}

function validateMissingComponent(component: string, fullPath: string): void {
  if (component === '.' || component === '..' ||
      (process.platform === 'win32' &&
       (component.includes(':') || component.endsWith(' ') || component.endsWith('.')))) {
    throw new Error(`Filesystem-canonical path identity cannot be established: ${fullPath}`);
  }
}

function validateUnambiguousPath(filePath: string): void {
  if (process.platform !== 'win32') {
    return;
  }
  if (filePath.startsWith('\\\\?\\') || filePath.startsWith('\\\\.\\')) {
    throw new Error(`Filesystem-canonical path identity cannot be established: ${filePath}`);
  }

  const root = path.parse(filePath).root;
  for (const component of filePath.slice(root.length).split(/[\\/]+/u)) {
    if (component.length > 0 &&
        (component.includes(':') || component.endsWith(' ') || component.endsWith('.'))) {
      throw new Error(`Filesystem-canonical path identity cannot be established: ${filePath}`);
    }
  }
}

export function chooseCommandPaletteDocumentWithQuickPick(
  createQuickPick: () => CommandPaletteDocumentQuickPick,
  documents: readonly CommandPaletteDocumentTarget[],
  initiallyFocused: CommandPaletteDocumentTarget,
  title = 'Select VBA document'
): Promise<CommandPaletteDocumentTarget | undefined> {
  const quickPick = createQuickPick();
  const items = documents.map((document) => ({
    label: document.name,
    description: document.sourcePath,
    detail: document.sourceRoot,
    document
  }));
  quickPick.title = title;
  quickPick.items = items;
  const initialItem = items.find((item) => item.document === initiallyFocused) ??
    items.find((item) => item.document.name === initiallyFocused.name);
  quickPick.activeItems = initialItem === undefined ? [] : [initialItem];

  return new Promise((resolve) => {
    let settled = false;
    const subscriptions: CommandPaletteQuickPickDisposable[] = [];
    const settle = (document: CommandPaletteDocumentTarget | undefined): void => {
      if (settled) {
        return;
      }
      settled = true;
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      quickPick.dispose();
      resolve(document);
    };

    subscriptions.push(
      quickPick.onDidAccept(() => settle(quickPick.selectedItems[0]?.document)),
      quickPick.onDidHide(() => settle(undefined))
    );
    quickPick.show();
  });
}
