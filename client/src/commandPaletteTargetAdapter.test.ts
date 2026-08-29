import assert from 'node:assert/strict';
import test from 'node:test';
import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import * as path from 'node:path';

import {
  CommandPaletteDocumentQuickPick,
  CommandPaletteDocumentQuickPickItem,
  chooseCommandPaletteDocumentWithQuickPick,
  resolveCommandPalettePathIdentity
} from './commandPaletteTargetAdapter';
import { CommandPaletteDocumentTarget } from './commandPaletteTarget';

test('document QuickPick sets initial focus without accepting it', async () => {
  const first = documentTarget('Book1');
  const second = documentTarget('Book2');
  const host = quickPickHost();
  let settled = false;
  const selected = chooseCommandPaletteDocumentWithQuickPick(
    () => host.quickPick,
    [first, second],
    second
  );
  void selected.then(() => {
    settled = true;
  });

  assert.equal(host.shown, true);
  assert.equal(host.quickPick.activeItems[0]?.document, second);
  assert.deepEqual(host.quickPick.selectedItems, []);
  await Promise.resolve();
  assert.equal(settled, false);

  host.quickPick.selectedItems = [host.quickPick.items[0]!];
  host.accept();
  assert.equal(await selected, first);
});

test('hiding document QuickPick cancels selection', async () => {
  const first = documentTarget('Book1');
  const host = quickPickHost();
  const selected = chooseCommandPaletteDocumentWithQuickPick(
    () => host.quickPick,
    [first],
    first
  );

  host.hide();
  assert.equal(await selected, undefined);
  assert.equal(host.disposed, true);
});

test('filesystem identity adapter resolves a canonical directory object', async () => {
  const identity = await resolveCommandPalettePathIdentity(process.cwd());

  assert.equal(identity.kind, 'directory');
  assert.ok(identity.canonicalPath.length > 0);
  assert.ok(identity.objectIdentity !== undefined && identity.objectIdentity.length > 0);
});

test('filesystem identity adapter preserves #282 safely resolvable missing suffixes', async () => {
  const suffix = path.join('.missing-command-palette-target', 'Nested');
  const identity = await resolveCommandPalettePathIdentity(path.join(process.cwd(), suffix));

  assert.equal(identity.canonicalPath, path.join(await fs.realpath(process.cwd()), suffix));
  assert.equal(identity.kind, 'directory');
  assert.equal(identity.objectIdentity, undefined);
});

test('filesystem identity adapter rejects a dangling filesystem alias', async () => {
  const root = await fs.mkdtemp(path.join(tmpdir(), 'vba-tools-command-target-'));
  const target = path.join(root, 'target');
  const alias = path.join(root, 'alias');
  try {
    await fs.mkdir(target);
    await fs.symlink(target, alias, process.platform === 'win32' ? 'junction' : 'dir');
    await fs.rm(target, { recursive: true, force: true });

    await assert.rejects(
      resolveCommandPalettePathIdentity(alias),
      /identity cannot be established/u
    );
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('filesystem identity adapter rejects Windows-ambiguous path spellings', {
  skip: process.platform !== 'win32'
}, async () => {
  await assert.rejects(
    resolveCommandPalettePathIdentity(path.join(process.cwd(), 'ambiguous.', 'Book1')),
    /identity cannot be established/u
  );
});

function documentTarget(name: string): CommandPaletteDocumentTarget {
  return {
    name,
    sourcePath: `src/${name}`,
    sourceRoot: `C:\\work\\Project\\src\\${name}`,
    sourceRootIdentity: {
      canonicalPath: `C:\\work\\Project\\src\\${name}`,
      kind: 'directory'
    }
  };
}

function quickPickHost(): {
  quickPick: CommandPaletteDocumentQuickPick & {
    selectedItems: readonly CommandPaletteDocumentQuickPickItem[];
  };
  accept: () => void;
  hide: () => void;
  readonly shown: boolean;
  readonly disposed: boolean;
} {
  let acceptListener = (): void => undefined;
  let hideListener = (): void => undefined;
  let shown = false;
  let disposed = false;
  const quickPick = {
    title: '',
    items: [] as readonly CommandPaletteDocumentQuickPickItem[],
    activeItems: [] as readonly CommandPaletteDocumentQuickPickItem[],
    selectedItems: [] as readonly CommandPaletteDocumentQuickPickItem[],
    onDidAccept: (listener: () => void) => {
      acceptListener = listener;
      return { dispose: () => undefined };
    },
    onDidHide: (listener: () => void) => {
      hideListener = listener;
      return { dispose: () => undefined };
    },
    show: () => {
      shown = true;
    },
    dispose: () => {
      disposed = true;
    }
  };
  return {
    quickPick,
    accept: () => acceptListener(),
    hide: () => hideListener(),
    get shown() {
      return shown;
    },
    get disposed() {
      return disposed;
    }
  };
}
