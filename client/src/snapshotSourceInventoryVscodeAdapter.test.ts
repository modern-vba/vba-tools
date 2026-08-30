import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  SnapshotSourceTextDocument
} from './snapshotSourceInventory';
import {
  createSnapshotSourceInventoryVscodeAdapter
} from './snapshotSourceInventoryVscodeAdapter';

test('one VS Code snapshot adapter reads changed invocation state for every capture', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const firstSourcePath = path.join(sourceSetPath, 'First.bas');
  const laterSourcePath = path.join(sourceSetPath, 'Later.cls');
  let activeWindowsCodePage = 932;
  let openDocuments: readonly SnapshotSourceTextDocument[] = [{
    uriScheme: 'file',
    uriPath: firstSourcePath,
    fileName: firstSourcePath,
    isDirty: true,
    encoding: 'shiftjis',
    getText: () => 'あ'
  }];
  const adapter = createSnapshotSourceInventoryVscodeAdapter({
    getActiveWindowsCodePage: () => activeWindowsCodePage,
    getOpenTextDocuments: () => openDocuments,
    findSourceFiles: async () => [],
    readFile: async () => {
      throw new Error('Dirty invocation sources must not be read from disk.');
    },
    encodeText: async (text, encoding) => {
      if (encoding === 'shiftjis' && text === 'あ') {
        return Uint8Array.from([0x82, 0xa0]);
      }
      if (encoding === 'utf8') {
        return new TextEncoder().encode(text);
      }
      throw new Error(`Unexpected encode: ${encoding} ${text}`);
    },
    decodeText: async (bytes, encoding) => {
      if (encoding === 'shiftjis' && bytes[0] === 0x82 && bytes[1] === 0xa0) {
        return 'あ';
      }
      if (encoding === 'utf8') {
        return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
      }
      throw new Error(`Unexpected decode: ${encoding}`);
    }
  });

  const first = await adapter(sourceSetPath);

  activeWindowsCodePage = 65001;
  openDocuments = [{
    uriScheme: 'file',
    uriPath: laterSourcePath,
    fileName: laterSourcePath,
    isDirty: true,
    encoding: 'utf8',
    getText: () => 'later text'
  }];
  const later = await adapter(sourceSetPath);

  assert.equal(first.activeWindowsCodePage, 932);
  assert.deepEqual(first.entries, [{
    relativePath: 'First.bas',
    sourceUri: pathToFileURL(firstSourcePath).href,
    encoding: 'windows-932',
    bytes: Uint8Array.from([0x82, 0xa0])
  }]);
  assert.equal(later.activeWindowsCodePage, 65001);
  assert.deepEqual(later.entries, [{
    relativePath: 'Later.cls',
    sourceUri: pathToFileURL(laterSourcePath).href,
    encoding: 'utf8',
    bytes: new TextEncoder().encode('later text')
  }]);
});
