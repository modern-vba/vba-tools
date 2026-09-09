import test from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { pathToFileURL } from 'node:url';

import {
  CallerOwnedSourceSnapshotHost,
  SnapshotSourceInventoryHost,
  captureSnapshotSourceInventory,
  createCallerOwnedSourceSnapshotCapture,
  materializeSnapshotSourceInventory
} from './snapshotSourceInventory';

test('clean BOMless ambiguous UTF-8 bytes use only the fixed ACP 1252', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const sourcePath = path.join(sourceSetPath, 'Ambiguous.bas');
  const bytes = Uint8Array.from([0xc3, 0xa9]);
  const decoded: Array<{ encoding: string; text: string }> = [];
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 1252,
    getOpenTextDocuments: () => [],
    findSourceFiles: async () => [sourcePath],
    readFile: async () => bytes,
    decodeText: async (input, encoding) => {
      const text = new TextDecoder(encoding === 'windows1252' ? 'windows-1252' : 'utf-8', { fatal: true }).decode(input);
      decoded.push({ encoding, text });
      return text;
    },
    encodeText: async (text, encoding) => encoding === 'windows1252'
      ? Uint8Array.from([...text].map(character => character.charCodeAt(0))) : new TextEncoder().encode(text)
  });

  assert.equal(inventory.entries[0].encoding, 'windows-1252');
  assert.deepEqual(inventory.entries[0].bytes, bytes);
  assert.deepEqual(decoded, [{ encoding: 'windows1252', text: 'Ã©' }]);
});

test('dirty BOMless UTF-8 requires ACP 65001 even for ASCII or empty text', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const sourcePath = path.join(sourceSetPath, 'Dirty.bas');
  for (const activeCodePage of [932, 1252]) {
    for (const text of ['', 'ASCII', 'é']) {
      await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
        getActiveWindowsCodePage: () => activeCodePage,
        getOpenTextDocuments: () => [{
          uriScheme: 'file', uriPath: sourcePath, fileName: sourcePath, isDirty: true,
          encoding: 'utf8', getText: () => text
        }],
        findSourceFiles: async () => [],
        readFile: async () => { throw new Error('Dirty source must not read disk.'); },
        encodeText: async value => new TextEncoder().encode(value),
        decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
      }), /utf8.*does not match active Windows code page/);
    }
  }
});

test('unsupported and truncated Unicode signatures never fall through to the ACP codec', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const sourcePath = path.join(sourceSetPath, 'Malformed.bas');
  for (const bytes of [
    [0x2b, 0x2f, 0x76, 0x38], [0x2b, 0x2f, 0x76, 0x39],
    [0x2b, 0x2f, 0x76, 0x2b], [0x2b, 0x2f, 0x76, 0x2f],
    [0xef], [0xef, 0xbb], [0xff], [0xfe], [0x00, 0x00, 0xfe]
  ].map(value => Uint8Array.from(value))) {
    let codecCalls = 0;
    await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
      getActiveWindowsCodePage: () => 1252,
      getOpenTextDocuments: () => [],
      findSourceFiles: async () => [sourcePath],
      readFile: async () => bytes,
      encodeText: async () => { codecCalls += 1; return bytes; },
      decodeText: async () => { codecCalls += 1; return 'permissive codec'; }
    }), /unsupported|truncated/i);
    assert.equal(codecCalls, 0);
  }
});

test('dirty produced bytes reject unsupported and truncated signatures before decoding', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const sourcePath = path.join(sourceSetPath, 'Dirty.bas');
  for (const text of ['+', '+/', '+/v', '+/v8-', '+/v9-', '+/v+-', '+/v/-']) {
    let decodes = 0;
    await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
      getActiveWindowsCodePage: () => 65001,
      getOpenTextDocuments: () => [{
        uriScheme: 'file', uriPath: sourcePath, fileName: sourcePath, isDirty: true,
        encoding: 'utf8', getText: () => text
      }],
      findSourceFiles: async () => [],
      readFile: async () => { throw new Error('Dirty sources must not read disk.'); },
      encodeText: async value => new TextEncoder().encode(value),
      decodeText: async bytes => { decodes += 1; return new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
    }), /unsupported|truncated/i);
    assert.equal(decodes, 0);
  }
});

test('snapshot capture rejects equivalent dirty editor paths even when text and encoding match', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const editorPaths = ['Σ.bas', 'ς.bas'].map(fileName => path.join(sourceSetPath, fileName));
  const text = 'Public Sub Main()\r\nEnd Sub\r\n';

  await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => editorPaths.map(filePath => ({
      uriScheme: 'file',
      uriPath: filePath,
      fileName: filePath,
      isDirty: true,
      encoding: 'utf8',
      getText: () => text
    })),
    findSourceFiles: async () => [],
    readFile: async () => { throw new Error('Dirty sources must not read disk.'); },
    encodeText: async value => new TextEncoder().encode(value),
    decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  }), (error: unknown) => {
    assert.ok(error instanceof Error);
    assert.match(error.message, /duplicate.*dirty|dirty.*duplicate/i);
    for (const editorPath of editorPaths) {
      assert.ok(error.message.includes(editorPath), error.message);
    }
    return true;
  });
});

test('snapshot capture overlays Unicode-equivalent paths and preserves disk layout and editor URI spelling', async () => {
  const sourceSetPath = path.resolve('snapshot', 'Σ', 'Book1');
  const diskPath = path.resolve('snapshot', 'ς', 'Book1', 'MiXeD', 'Σ.bas');
  const editorPath = path.resolve('snapshot', 'σ', 'book1', 'mixed', 'ς.BAS');
  const text = 'Public Sub Captured()\r\nEnd Sub\r\n';
  let textReads = 0;
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [{
      uriScheme: 'file',
      uriPath: editorPath,
      fileName: editorPath,
      isDirty: true,
      encoding: 'utf8',
      getText: () => { textReads += 1; return text; }
    }],
    findSourceFiles: async () => [diskPath],
    readFile: async () => { throw new Error('The captured dirty editor must replace its equivalent disk source.'); },
    encodeText: async value => new TextEncoder().encode(value),
    decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  });

  assert.equal(inventory.sourceSetPath, sourceSetPath);
  assert.deepEqual(inventory.entries, [{
    relativePath: path.join('MiXeD', 'Σ.bas'),
    sourceUri: pathToFileURL(editorPath).href,
    encoding: 'utf8',
    bytes: new TextEncoder().encode(text)
  }]);
  assert.equal(textReads, 1);
});

test('snapshot capture identifies both equivalent disk paths when rejecting a duplicate inventory', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const diskPaths = ['µ.bas', 'μ.BAS'].map(fileName => path.join(sourceSetPath, fileName));

  await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [],
    findSourceFiles: async () => diskPaths,
    readFile: async () => new TextEncoder().encode('Public Sub Main()\r\nEnd Sub\r\n'),
    encodeText: async value => new TextEncoder().encode(value),
    decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  }), (error: unknown) => {
    assert.ok(error instanceof Error);
    assert.match(error.message, /duplicate/i);
    for (const diskPath of diskPaths) {
      assert.ok(error.message.includes(diskPath), error.message);
    }
    return true;
  });
});

test('snapshot capture overlays micro-sign and supplementary case aliases after normalizing path segments', async () => {
  const sourceSetPath = path.resolve('snapshot', 'µ');
  const diskPath = [path.resolve('snapshot', 'μ'), 'Unused', '..', 'Kept', '𐐨.bas'].join(path.sep);
  const editorPath = path.resolve('snapshot', 'Μ', 'kept', '𐐀.BAS');
  const text = 'Public Sub Captured()\r\nEnd Sub\r\n';
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [{
      uriScheme: 'file', uriPath: editorPath, fileName: editorPath,
      isDirty: true, encoding: 'utf8', getText: () => text
    }],
    findSourceFiles: async () => [diskPath],
    readFile: async () => { throw new Error('An equivalent dirty editor must replace the disk source.'); },
    encodeText: async value => new TextEncoder().encode(value),
    decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  });

  assert.deepEqual(inventory.entries, [{
    relativePath: path.join('Kept', '𐐨.bas'),
    sourceUri: pathToFileURL(editorPath).href,
    encoding: 'utf8',
    bytes: new TextEncoder().encode(text)
  }]);
});

test('snapshot capture keeps Kelvin-sign and canonically distinct Unicode paths separate', async () => {
  const sourceSetPath = path.resolve('snapshot');
  const fileNames = ['K.bas', 'K.bas', 'é.cls', 'e\u0301.cls'];
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => fileNames.map(fileName => ({
      uriScheme: 'file', uriPath: path.join(sourceSetPath, fileName),
      fileName: path.join(sourceSetPath, fileName), isDirty: true,
      encoding: 'utf8', getText: () => `Source: ${fileName}`
    })),
    findSourceFiles: async () => [],
    readFile: async () => { throw new Error('Dirty sources must not read disk.'); },
    encodeText: async value => new TextEncoder().encode(value),
    decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  });

  assert.equal(inventory.entries.length, fileNames.length);
  for (const fileName of fileNames) {
    assert.deepEqual(inventory.entries.find(entry => entry.relativePath === fileName), {
      relativePath: fileName,
      sourceUri: pathToFileURL(path.join(sourceSetPath, fileName)).href,
      encoding: 'utf8',
      bytes: new TextEncoder().encode(`Source: ${fileName}`)
    });
  }
});

test('snapshot inventory admits only strict descendants under ordinal source-root identity', async () => {
  const sourceSetPath = path.resolve('snapshot', 'K');
  const outsidePaths = [
    sourceSetPath,
    path.resolve('snapshot', 'KSibling', 'Outside.bas'),
    path.resolve(sourceSetPath, '..', 'Outside.bas'),
    path.resolve('snapshot', 'K', 'Outside.bas')
  ];
  for (const filePath of outsidePaths) {
    const reads: string[] = [];
    await assert.rejects(captureSnapshotSourceInventory(sourceSetPath, {
      getActiveWindowsCodePage: () => 65001,
      getOpenTextDocuments: () => [],
      findSourceFiles: async () => [filePath],
      readFile: async requestedPath => { reads.push(requestedPath); return new Uint8Array(); },
      encodeText: async value => new TextEncoder().encode(value),
      decodeText: async bytes => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
    }), (error: unknown) => {
      assert.ok(error instanceof Error);
      assert.match(error.message, /outside the selected source set/i);
      assert.ok(error.message.includes(filePath), error.message);
      return true;
    });
    assert.deepEqual(reads, []);
  }
});

test('snapshot inventory overlays capture-start dirty source including a file absent from disk', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const cleanPath = path.join(sourceSetPath, 'Clean.bas');
  const dirtyPath = path.join(sourceSetPath, 'Dirty.bas');
  const addedPath = path.join(sourceSetPath, 'Added.cls');
  const diskBytes = new Map([
    [cleanPath, Uint8Array.from([0x63, 0x6c, 0x65, 0x61, 0x6e])],
    [dirtyPath, Uint8Array.from([0x6f, 0x6c, 0x64])]
  ]);
  const reads: string[] = [];
  const textReads = new Map<string, number>();
  const dirtyDocument = (uriPath: string, text: string) => ({
    uriScheme: 'file',
    uriPath,
    fileName: uriPath,
    isDirty: true,
    encoding: 'utf8',
    getText: () => {
      textReads.set(uriPath, (textReads.get(uriPath) ?? 0) + 1);
      return text;
    }
  });
  const host: SnapshotSourceInventoryHost = {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [
      dirtyDocument(dirtyPath, 'dirty'),
      dirtyDocument(addedPath, 'added')
    ],
    findSourceFiles: async () => [cleanPath, dirtyPath],
    readFile: async (filePath) => {
      reads.push(filePath);
      const bytes = diskBytes.get(filePath);
      if (bytes === undefined) {
        throw new Error(`Unexpected disk read: ${filePath}`);
      }
      return bytes;
    },
    encodeText: async (text) => new TextEncoder().encode(text),
    decodeText: async (bytes) => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  };

  const inventory = await captureSnapshotSourceInventory(sourceSetPath, host);

  assert.deepEqual(
    inventory.entries.map((entry) => ({
      relativePath: entry.relativePath,
      text: new TextDecoder().decode(entry.bytes)
    })),
    [
      { relativePath: 'Added.cls', text: 'added' },
      { relativePath: 'Clean.bas', text: 'clean' },
      { relativePath: 'Dirty.bas', text: 'dirty' }
    ]
  );
  assert.deepEqual(reads, [cleanPath]);
  assert.deepEqual([...textReads.entries()], [
    [dirtyPath, 1],
    [addedPath, 1]
  ]);
});

test('dirty UTF-8 snapshot source carries persistent transport identity without saving', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Dirty.bas');
  const bytes = new TextEncoder().encode('Public Sub Dirty()\r\nEnd Sub\r\n');
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [{
      uriScheme: 'file',
      uriPath: sourcePath,
      fileName: sourcePath,
      isDirty: true,
      encoding: 'utf8',
      getText: () => 'Public Sub Dirty()\r\nEnd Sub\r\n'
    }],
    findSourceFiles: async () => [],
    readFile: async () => {
      throw new Error('Dirty source must not be read from disk.');
    },
    encodeText: async () => bytes,
    decodeText: async () => 'Public Sub Dirty()\r\nEnd Sub\r\n'
  });

  assert.deepEqual(inventory.entries, [{
    relativePath: 'Dirty.bas',
    sourceUri: pathToFileURL(sourcePath).href,
    encoding: 'utf8',
    bytes
  }]);
});

test('capture fixes disk inventory and editor values once without a closing retry', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const cleanPath = path.join(sourceSetPath, 'Clean.bas');
  const dirtyPath = path.join(sourceSetPath, 'Dirty.bas');
  const laterPath = path.join(sourceSetPath, 'Later.bas');
  const laterEditorPath = path.join(sourceSetPath, 'LaterEditor.cls');
  const diskInventory = [cleanPath, dirtyPath];
  let editorText = 'captured';
  let activeCodePageReads = 0;
  let openDocumentReads = 0;
  let inventoryReads = 0;
  let textReads = 0;
  let uriReads = 0;
  let encodingReads = 0;
  const documents = [{
    uriScheme: 'file',
    get uriPath() {
      uriReads += 1;
      return dirtyPath;
    },
    fileName: dirtyPath,
    isDirty: true,
    get encoding() {
      encodingReads += 1;
      return 'utf8';
    },
    getText: () => {
      textReads += 1;
      documents.push({
        uriScheme: 'file',
        uriPath: laterEditorPath,
        fileName: laterEditorPath,
        isDirty: true,
        encoding: 'utf8',
        getText: () => 'later editor'
      } as typeof documents[number]);
      return editorText;
    }
  }];
  const host: SnapshotSourceInventoryHost = {
    getActiveWindowsCodePage: () => {
      activeCodePageReads += 1;
      return 65001;
    },
    getOpenTextDocuments: () => {
      openDocumentReads += 1;
      return documents;
    },
    findSourceFiles: async () => {
      inventoryReads += 1;
      return diskInventory;
    },
    readFile: async (filePath) => {
      if (filePath === cleanPath) {
        editorText = 'later edit';
        diskInventory.push(laterPath);
        return new TextEncoder().encode('clean');
      }
      if (filePath === laterPath) {
        return new TextEncoder().encode('late inventory');
      }
      throw new Error(`Unexpected read: ${filePath}`);
    },
    encodeText: async (text) => new TextEncoder().encode(text),
    decodeText: async (bytes) => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  };

  const inventory = await captureSnapshotSourceInventory(sourceSetPath, host);

  assert.deepEqual(inventory.entries.map((entry) => entry.relativePath), [
    'Clean.bas',
    'Dirty.bas'
  ]);
  assert.equal(
    new TextDecoder().decode(inventory.entries.find((entry) => entry.relativePath === 'Dirty.bas')!.bytes),
    'captured');
  assert.deepEqual(
    { activeCodePageReads, openDocumentReads, inventoryReads, uriReads, encodingReads, textReads },
    {
      activeCodePageReads: 1,
      openDocumentReads: 1,
      inventoryReads: 1,
      uriReads: 1,
      encodingReads: 1,
      textReads: 1
    });
});

test('snapshot inventory cancellation stops before reading a later inventoried file', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const firstPath = path.join(sourceSetPath, 'First.bas');
  const laterPath = path.join(sourceSetPath, 'Later.bas');
  const reads: string[] = [];
  let cancellationRequested = false;
  const host: SnapshotSourceInventoryHost = {
    getActiveWindowsCodePage: () => 65001,
    getOpenTextDocuments: () => [],
    findSourceFiles: async () => [firstPath, laterPath],
    readFile: async (filePath) => {
      reads.push(filePath);
      cancellationRequested = true;
      return new TextEncoder().encode('source');
    },
    encodeText: async (text) => new TextEncoder().encode(text),
    decodeText: async (bytes) => new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  };

  await assert.rejects(
    captureSnapshotSourceInventory(
      sourceSetPath,
      host,
      {
        get isCancellationRequested() {
          return cancellationRequested;
        }
      }),
    /snapshot capture was cancelled/i);
  assert.deepEqual(reads, [firstPath]);
});

test('dirty legacy source is accepted only when its editor encoding matches the fixed active code page', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Legacy.bas');
  const createHost = (encoding: string, text: string): SnapshotSourceInventoryHost => ({
    getActiveWindowsCodePage: () => 932,
    getOpenTextDocuments: () => [{
      uriScheme: 'file',
      uriPath: sourcePath,
      fileName: sourcePath,
      isDirty: true,
      encoding,
      getText: () => text
    }],
    findSourceFiles: async () => [],
    readFile: async () => {
      throw new Error('No disk source should be read');
    },
    encodeText: async (value, selectedEncoding) => {
      if (selectedEncoding === 'shiftjis' && value === 'あ') {
        return Uint8Array.from([0x82, 0xa0]);
      }
      if (selectedEncoding === 'windows1252' && value === 'é') {
        return Uint8Array.from([0xe9]);
      }
      throw new Error(`Unexpected encode: ${selectedEncoding} ${value}`);
    },
    decodeText: async (bytes, selectedEncoding) => {
      if (selectedEncoding === 'shiftjis' && bytes[0] === 0x82 && bytes[1] === 0xa0) {
        return 'あ';
      }
      if (selectedEncoding === 'windows1252' && bytes[0] === 0xe9) {
        return 'é';
      }
      throw new Error(`Unexpected decode: ${selectedEncoding}`);
    }
  });

  const accepted = await captureSnapshotSourceInventory(
    sourceSetPath,
    createHost('shiftjis', 'あ'));

  assert.deepEqual([...accepted.entries[0].bytes], [0x82, 0xa0]);
  await assert.rejects(
    captureSnapshotSourceInventory(
      sourceSetPath,
      createHost('windows1252', 'é')),
    /does not match active Windows code page 932/);
});

test('dirty Unicode source preserves the editor encoding and enforces its BOM policy', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Unicode.bas');
  const encodedByName = new Map<string, Uint8Array>([
    ['utf8', Uint8Array.from([0x41])],
    ['utf8bom', Uint8Array.from([0xef, 0xbb, 0xbf, 0x41])],
    ['utf16le', Uint8Array.from([0xff, 0xfe, 0x41, 0x00])],
    ['utf16be', Uint8Array.from([0xfe, 0xff, 0x00, 0x41])]
  ]);
  const createHost = (
    encoding: string,
    overrideBytes?: Uint8Array
  ): SnapshotSourceInventoryHost => ({
    getActiveWindowsCodePage: () => encoding === 'utf8' ? 65001 : 932,
    getOpenTextDocuments: () => [{
      uriScheme: 'file',
      uriPath: sourcePath,
      fileName: sourcePath,
      isDirty: true,
      encoding,
      getText: () => 'A'
    }],
    findSourceFiles: async () => [],
    readFile: async () => {
      throw new Error('No disk source should be read');
    },
    encodeText: async () => overrideBytes ?? encodedByName.get(encoding)!,
    decodeText: async () => 'A'
  });

  for (const [encoding, expectedBytes] of encodedByName) {
    const inventory = await captureSnapshotSourceInventory(
      sourceSetPath,
      createHost(encoding));
    assert.deepEqual([...inventory.entries[0].bytes], [...expectedBytes], encoding);
  }

  await assert.rejects(
    captureSnapshotSourceInventory(
      sourceSetPath,
      createHost('utf16le', Uint8Array.from([0x41, 0x00]))),
    /utf16le.*BOM/);
});

test('dirty source rejects lossy substitution in an otherwise supported editor encoding', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Lossy.bas');
  const host: SnapshotSourceInventoryHost = {
    getActiveWindowsCodePage: () => 932,
    getOpenTextDocuments: () => [{
      uriScheme: 'file',
      uriPath: sourcePath,
      fileName: sourcePath,
      isDirty: true,
      encoding: 'shiftjis',
      getText: () => '😀'
    }],
    findSourceFiles: async () => [],
    readFile: async () => {
      throw new Error('No disk source should be read');
    },
    encodeText: async (_text, encoding) => {
      assert.equal(encoding, 'shiftjis');
      return Uint8Array.from([0x3f]);
    },
    decodeText: async (bytes, encoding) => {
      assert.equal(encoding, 'shiftjis');
      assert.deepEqual([...bytes], [0x3f]);
      return '?';
    }
  };

  await assert.rejects(
    captureSnapshotSourceInventory(sourceSetPath, host),
    /cannot round-trip exactly.*Lossy\.bas/);
});

test('clean text source strictly round-trips exact bytes while frx sidecars remain binary', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const files = new Map<string, Uint8Array>([
    [path.join(sourceSetPath, 'Ascii.bas'), Uint8Array.from([0x41])],
    [path.join(sourceSetPath, 'Utf8Bom.cls'), Uint8Array.from([0xef, 0xbb, 0xbf, 0x41])],
    [path.join(sourceSetPath, 'Utf16.frm'), Uint8Array.from([0xff, 0xfe, 0x41, 0x00])],
    [path.join(sourceSetPath, 'Utf16.frx'), Uint8Array.from([0xff, 0x00, 0x81, 0xfe])],
    [path.join(sourceSetPath, 'Legacy.bas'), Uint8Array.from([0x82, 0xa0])]
  ]);
  const encodeText = async (text: string, encoding: string): Promise<Uint8Array> => {
    if (text === 'A' && encoding === 'shiftjis') {
      return Uint8Array.from([0x41]);
    }
    if (text === 'A' && encoding === 'utf8bom') {
      return Uint8Array.from([0xef, 0xbb, 0xbf, 0x41]);
    }
    if (text === 'A' && encoding === 'utf16le') {
      return Uint8Array.from([0xff, 0xfe, 0x41, 0x00]);
    }
    if (text === 'あ' && encoding === 'shiftjis') {
      return Uint8Array.from([0x82, 0xa0]);
    }
    throw new Error(`Unencodable fixture: ${encoding} ${text}`);
  };
  const decodeText = async (bytes: Uint8Array, encoding: string): Promise<string> => {
    if (encoding === 'shiftjis' && bytes.length === 1 && bytes[0] === 0x41) {
      return 'A';
    }
    if (
      encoding === 'utf8bom'
      && bytes.length === 4
      && bytes[0] === 0xef
      && bytes[1] === 0xbb
      && bytes[2] === 0xbf
      && bytes[3] === 0x41
    ) {
      return 'A';
    }
    if (
      encoding === 'utf16le'
      && bytes.length === 4
      && bytes[0] === 0xff
      && bytes[1] === 0xfe
      && bytes[2] === 0x41
      && bytes[3] === 0x00
    ) {
      return 'A';
    }
    if (encoding === 'shiftjis' && bytes.length === 2 && bytes[0] === 0x82 && bytes[1] === 0xa0) {
      return 'あ';
    }
    throw new Error(`Undecodable fixture: ${encoding}`);
  };
  const host = (selectedFiles: Map<string, Uint8Array>): SnapshotSourceInventoryHost => ({
    getActiveWindowsCodePage: () => 932,
    getOpenTextDocuments: () => [],
    findSourceFiles: async () => [...selectedFiles.keys()],
    readFile: async (filePath) => selectedFiles.get(filePath)!,
    encodeText,
    decodeText
  });

  const inventory = await captureSnapshotSourceInventory(sourceSetPath, host(files));

  for (const entry of inventory.entries) {
    assert.deepEqual(
      [...entry.bytes],
      [...files.get(path.join(sourceSetPath, entry.relativePath))!],
      entry.relativePath);
  }

  const malformedPath = path.join(sourceSetPath, 'Malformed.bas');
  await assert.rejects(
    captureSnapshotSourceInventory(
      sourceSetPath,
      host(new Map([[malformedPath, Uint8Array.from([0xef, 0xbb, 0xbf, 0xff])]]))),
    /could not round-trip.*Malformed\.bas/);
});

test('clean UTF-8 BOM source carries its exact transport encoding and persistent URI', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Clean.cls');
  const bytes = Uint8Array.from([0xef, 0xbb, 0xbf, 0x41]);
  const inventory = await captureSnapshotSourceInventory(sourceSetPath, {
    getActiveWindowsCodePage: () => 932,
    getOpenTextDocuments: () => [],
    findSourceFiles: async () => [sourcePath],
    readFile: async () => bytes,
    encodeText: async (_text, encoding) => {
      assert.equal(encoding, 'utf8bom');
      return bytes;
    },
    decodeText: async (_bytes, encoding) => {
      assert.equal(encoding, 'utf8bom');
      return 'A';
    }
  });

  assert.deepEqual(inventory.entries, [{
    relativePath: 'Clean.cls',
    sourceUri: pathToFileURL(sourcePath).href,
    encoding: 'utf8bom',
    bytes
  }]);
});

test('clean UTF-32 source is rejected before a permissive UTF-16 round trip can accept it', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const utf32ByName = new Map<string, Uint8Array>([
    ['Utf32Le.bas', Uint8Array.from([0xff, 0xfe, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00])],
    ['Utf32Be.cls', Uint8Array.from([0x00, 0x00, 0xfe, 0xff, 0x00, 0x00, 0x00, 0x41])]
  ]);

  for (const [fileName, bytes] of utf32ByName) {
    const sourcePath = path.join(sourceSetPath, fileName);
    const host: SnapshotSourceInventoryHost = {
      getActiveWindowsCodePage: () => 932,
      getOpenTextDocuments: () => [],
      findSourceFiles: async () => [sourcePath],
      readFile: async () => bytes,
      encodeText: async (_text, encoding) => {
        if (fileName === 'Utf32Le.bas' && encoding === 'utf16le') {
          return bytes;
        }
        throw new Error(`Unexpected encode: ${encoding}`);
      },
      decodeText: async (_bytes, encoding) => {
        if (fileName === 'Utf32Le.bas' && encoding === 'utf16le') {
          return '\u0000A\u0000';
        }
        throw new Error(`Unexpected decode: ${encoding}`);
      }
    };

    await assert.rejects(
      captureSnapshotSourceInventory(sourceSetPath, host),
      /unsupported UTF-32.*Utf32(Le|Be)\.(bas|cls)/i);
  }
});

test('a participating pathless dirty source fails snapshot capture', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const requestedPath = path.join(sourceSetPath, 'Unsaved.bas');
  let activeCodePageReads = 0;
  let diskInventoryReads = 0;
  const host: SnapshotSourceInventoryHost = {
    getActiveWindowsCodePage: () => {
      activeCodePageReads += 1;
      return 932;
    },
    getOpenTextDocuments: () => [{
      uriScheme: 'untitled',
      uriPath: undefined,
      fileName: requestedPath,
      isDirty: true,
      encoding: 'utf8',
      getText: () => 'unsaved'
    }],
    findSourceFiles: async () => {
      diskInventoryReads += 1;
      return [];
    },
    readFile: async () => {
      throw new Error('No disk source should be read');
    },
    encodeText: async (text) => new TextEncoder().encode(text),
    decodeText: async (bytes) => new TextDecoder().decode(bytes)
  };

  await assert.rejects(
    captureSnapshotSourceInventory(sourceSetPath, host),
    /must be saved under the selected source set.*Unsaved\.bas/);
  assert.deepEqual({ activeCodePageReads, diskInventoryReads }, {
    activeCodePageReads: 0,
    diskInventoryReads: 0
  });
});

test('a captured inventory materializes its complete relative layout in a caller-owned directory', async () => {
  const snapshotPath = path.join('C:', 'temp', 'vba-tools-snapshot-1');
  const writes = new Map<string, Uint8Array>();
  const createdDirectories: string[] = [];
  const removedDirectories: string[] = [];
  const host: CallerOwnedSourceSnapshotHost = {
    createTemporaryDirectory: async () => snapshotPath,
    createDirectory: async (directoryPath) => {
      createdDirectories.push(directoryPath);
    },
    writeFile: async (filePath, bytes) => {
      writes.set(filePath, Uint8Array.from(bytes));
    },
    removeDirectory: async (directoryPath) => {
      removedDirectories.push(directoryPath);
    },
    wait: async () => undefined
  };

  const lease = await materializeSnapshotSourceInventory({
    sourceSetPath: path.join('C:', 'work', 'BookProject', 'src', 'Book1'),
    activeWindowsCodePage: 932,
    entries: [
      { relativePath: 'Root.bas', bytes: Uint8Array.from([0x41]) },
      { relativePath: path.join('Nested', 'Form.frm'), bytes: Uint8Array.from([0x42]) },
      { relativePath: path.join('Nested', 'Form.frx'), bytes: Uint8Array.from([0x00, 0xff]) }
    ]
  }, host);

  assert.equal(lease.directoryPath, snapshotPath);
  assert.deepEqual([...writes.entries()].map(([filePath, bytes]) => [filePath, [...bytes]]), [
    [path.join(snapshotPath, 'Root.bas'), [0x41]],
    [path.join(snapshotPath, 'Nested', 'Form.frm'), [0x42]],
    [path.join(snapshotPath, 'Nested', 'Form.frx'), [0x00, 0xff]]
  ]);
  assert.ok(createdDirectories.includes(snapshotPath));
  assert.ok(createdDirectories.includes(path.join(snapshotPath, 'Nested')));

  assert.deepEqual(await lease.cleanup(), {});
  assert.deepEqual(removedDirectories, [snapshotPath]);
});

test('the snapshot capture port returns a materialized caller-owned lease', async () => {
  const sourceSetPath = path.join('C:', 'work', 'BookProject', 'src', 'Book1');
  const sourcePath = path.join(sourceSetPath, 'Module.bas');
  const snapshotPath = path.join('C:', 'temp', 'vba-tools-snapshot-port');
  const writes = new Map<string, Uint8Array>();
  const capturedSourceSets: string[] = [];
  const capture = createCallerOwnedSourceSnapshotCapture(async (capturedSourceSetPath) => {
    capturedSourceSets.push(capturedSourceSetPath);
    return {
      sourceSetPath: capturedSourceSetPath,
      activeWindowsCodePage: 65001,
      entries: [{
        relativePath: 'Module.bas',
        sourceUri: pathToFileURL(sourcePath).href,
        encoding: 'utf8',
        bytes: Uint8Array.from([0x41])
      }]
    };
  }, {
    createTemporaryDirectory: async () => snapshotPath,
    createDirectory: async () => undefined,
    writeFile: async (filePath, bytes) => {
      writes.set(filePath, Uint8Array.from(bytes));
    },
    removeDirectory: async () => undefined,
    wait: async () => undefined
  });

  const lease = await capture(sourceSetPath);

  assert.equal(lease.directoryPath, snapshotPath);
  assert.deepEqual(capturedSourceSets, [sourceSetPath]);
  assert.deepEqual([...writes.get(path.join(snapshotPath, 'Module.bas'))!], [0x41]);
});

test('caller-owned snapshot retains exact immutable module and form origins beyond cleanup', async () => {
  const directoryPath = path.resolve('C:/temp/test-snapshot');
  const entries = [
    { relativePath: 'nested/日本語/Caller.bas', sourceUri: 'file:///C:/authoring/nested/日本語/Caller.bas', bytes: new Uint8Array([65]) },
    { relativePath: 'Dialog.frm', sourceUri: 'file:///C:/authoring/Dialog.frm', bytes: new Uint8Array([66]) },
    { relativePath: 'Dialog.frx', bytes: new Uint8Array([67]) }
  ];
  const writes: number[][] = [];
  let removed = false;
  const lease = await materializeSnapshotSourceInventory({
    sourceSetPath: path.resolve('C:/authoring'), activeWindowsCodePage: 932, entries
  }, {
    createTemporaryDirectory: async () => {
      entries[0]!.sourceUri = 'file:///C:/later/Caller.bas';
      entries[0]!.bytes[0] = 90;
      return directoryPath;
    },
    createDirectory: async () => undefined,
    writeFile: async (_file, bytes) => { writes.push([...bytes]); },
    removeDirectory: async () => { removed = true; }, wait: async () => undefined
  });
  await lease.cleanup();
  assert.equal(removed, true);
  assert.deepEqual(writes, [[65], [66], [67]]);
  const origins = Reflect.get(lease, 'origins');
  assert.deepEqual(origins, [
    { snapshotUri: pathToFileURL(path.join(directoryPath, 'nested/日本語/Caller.bas')).href,
      sourceUri: 'file:///C:/authoring/nested/日本語/Caller.bas' },
    { snapshotUri: pathToFileURL(path.join(directoryPath, 'Dialog.frm')).href, sourceUri: 'file:///C:/authoring/Dialog.frm' },
    { snapshotUri: pathToFileURL(path.join(directoryPath, 'Dialog.frx')).href, sourceUri: undefined }
  ]);
  assert.ok(Object.isFrozen(origins));
  assert.ok(origins.every(Object.isFrozen));
});

test('duplicate snapshot materialization fails with both paths and preserves retained-directory cleanup', async () => {
  const snapshotPath = path.resolve('temporary-snapshot');
  const relativePaths = [path.join('Nested', 'Σ.bas'), path.join('nested', 'ς.BAS')];
  const writes: string[] = [];
  const removals: string[] = [];
  const waits: number[] = [];

  await assert.rejects(materializeSnapshotSourceInventory({
    sourceSetPath: path.resolve('snapshot'),
    activeWindowsCodePage: 65001,
    entries: relativePaths.map(relativePath => ({
      relativePath,
      bytes: new TextEncoder().encode('Public Sub Main()\r\nEnd Sub\r\n')
    }))
  }, {
    createTemporaryDirectory: async () => snapshotPath,
    createDirectory: async () => undefined,
    writeFile: async filePath => { writes.push(filePath); },
    removeDirectory: async directoryPath => {
      removals.push(directoryPath);
      throw new Error('The owned snapshot directory is still locked.');
    },
    wait: async milliseconds => { waits.push(milliseconds); }
  }), (error: unknown) => {
    assert.ok(error instanceof Error);
    assert.match(error.message, /duplicate/i);
    for (const relativePath of relativePaths) {
      assert.ok(error.message.includes(relativePath), error.message);
    }
    assert.ok(error.message.includes(`Retained snapshot directory: ${snapshotPath}`), error.message);
    return true;
  });
  assert.deepEqual(writes, [path.join(snapshotPath, relativePaths[0])]);
  assert.deepEqual(removals, [snapshotPath, snapshotPath, snapshotPath]);
  assert.deepEqual(waits, [25, 100]);
});

test('failed or cancelled snapshot admission removes its caller-owned directory', async () => {
  const snapshotPath = path.resolve('temporary-snapshot');
  for (const scenario of [
    { relativePath: '.', cancel: false },
    { relativePath: path.join('..', 'Outside.bas'), cancel: false },
    { relativePath: 'Later.bas', cancel: true }
  ]) {
    let cancellationRequested = false;
    const writtenFiles = new Set<string>();
    const removals: string[] = [];
    await assert.rejects(materializeSnapshotSourceInventory({
      sourceSetPath: path.resolve('snapshot'),
      activeWindowsCodePage: 65001,
      entries: ['First.bas', scenario.relativePath].map(relativePath => ({
        relativePath, bytes: Uint8Array.from([0x41])
      }))
    }, {
      createTemporaryDirectory: async () => snapshotPath,
      createDirectory: async () => undefined,
      writeFile: async filePath => {
        writtenFiles.add(filePath);
        cancellationRequested = scenario.cancel;
      },
      removeDirectory: async directoryPath => {
        removals.push(directoryPath);
        writtenFiles.clear();
      },
      wait: async () => undefined
    }, {
      get isCancellationRequested() { return cancellationRequested; }
    }), scenario.cancel ? /snapshot capture was cancelled/i : /escapes its caller-owned directory/i);
    assert.deepEqual(removals, [snapshotPath]);
    assert.equal(writtenFiles.size, 0);
  }
});

test('caller-owned snapshot cleanup retries are bounded and report a retained directory', async () => {
  const snapshotPath = path.join('C:', 'temp', 'vba-tools-retained-snapshot');
  let removeAttempts = 0;
  const waits: number[] = [];
  const lease = await materializeSnapshotSourceInventory({
    sourceSetPath: path.join('C:', 'work', 'BookProject', 'src', 'Book1'),
    activeWindowsCodePage: 932,
    entries: []
  }, {
    createTemporaryDirectory: async () => snapshotPath,
    createDirectory: async () => undefined,
    writeFile: async () => undefined,
    removeDirectory: async () => {
      removeAttempts += 1;
      throw new Error('synthetic sharing violation');
    },
    wait: async (milliseconds) => {
      waits.push(milliseconds);
    }
  });

  assert.deepEqual(await lease.cleanup(), { retainedPath: snapshotPath });
  assert.equal(removeAttempts, 3);
  assert.deepEqual(waits, [25, 100]);

  assert.deepEqual(await lease.cleanup(), { retainedPath: snapshotPath });
  assert.equal(removeAttempts, 3);
});
