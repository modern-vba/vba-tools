import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as path from 'node:path';
import { captureSnapshotSourceInventory } from './snapshotSourceInventory';

interface EncodingCase {
  readonly id: string;
  readonly activeCodePage: number;
  readonly fileName: string;
  readonly bytesBase64: string;
  readonly expectedText?: string;
  readonly expectedEncoding?: string;
  readonly expectedFailure?: boolean;
}

const corpus = JSON.parse(readFileSync(path.resolve(
  __dirname, '..', '..', 'fixtures', 'vba-source-encoding', 'cases.json'), 'utf8')) as {
    schemaVersion: number; cases: EncodingCase[];
  };
assert.equal(corpus.schemaVersion, 1);

for (const fixture of corpus.cases) {
  test(`snapshot producer encoding corpus: ${fixture.id}`, async () => {
    const sourceSetPath = path.resolve('corpus-snapshot');
    const bytes = Buffer.from(fixture.bytesBase64, 'base64');
    const capture = captureSnapshotSourceInventory(sourceSetPath, {
      getActiveWindowsCodePage: () => fixture.activeCodePage,
      getOpenTextDocuments: () => [],
      findSourceFiles: async () => [path.join(sourceSetPath, fixture.fileName)],
      readFile: async () => bytes,
      encodeText: async (text, encoding) => encodeWithNodeCodec(text, encoding),
      decodeText: async (input, encoding) => decodeWithNodeCodec(input, encoding)
    });
    if (fixture.expectedFailure) {
      await assert.rejects(capture);
      return;
    }
    const inventory = await capture;
    assert.equal(inventory.entries[0].encoding, fixture.expectedEncoding);
    assert.deepEqual(Buffer.from(inventory.entries[0].bytes), bytes);
    assert.equal(decodeWithNodeCodec(bytes, editorEncoding(fixture.expectedEncoding!)), fixture.expectedText);
    // ACP projection is independently VbaDev-owned; supported BOM text remains
    // valid producer input even for the corpus's projection-failure examples.
  });
}

function editorEncoding(token: string): string {
  return token === 'windows-932' ? 'shiftjis' : token === 'windows-1252' ? 'windows1252' : token;
}

function decodeWithNodeCodec(bytes: Uint8Array, encoding: string): string {
  const label = encoding === 'shiftjis' ? 'shift_jis' : encoding === 'windows1252' ? 'windows-1252'
    : encoding === 'utf16le' ? 'utf-16le' : encoding === 'utf16be' ? 'utf-16be' : 'utf-8';
  return new TextDecoder(label, { fatal: true }).decode(bytes);
}

const legacyEncoders = new Map<string, Map<string, Uint8Array>>();

/** Test-only inverse of Node's actual decoder tables, not a VS Code codec proof. */
function encodeWithNodeCodec(text: string, encoding: string): Uint8Array {
  if (encoding === 'shiftjis' || encoding === 'windows1252') {
    let table = legacyEncoders.get(encoding);
    if (table === undefined) {
      table = new Map();
      const add = (values: number[]) => {
        const bytes = Uint8Array.from(values);
        try {
          const character = decodeWithNodeCodec(bytes, encoding);
          if ([...character].length === 1 && !table!.has(character)) {
            table!.set(character, bytes);
          }
        } catch { /* Undefined byte sequences have no inverse. */ }
      };
      for (let value = 0; value <= 255; value += 1) { add([value]); }
      if (encoding === 'shiftjis') {
        for (let lead = 0x81; lead <= 0xfc; lead += 1) {
          if (lead > 0x9f && lead < 0xe0) { continue; }
          for (let trail = 0x40; trail <= 0xfc; trail += 1) { add([lead, trail]); }
        }
      }
      legacyEncoders.set(encoding, table);
    }
    return Uint8Array.from([...text].flatMap(character => {
      const bytes = table!.get(character);
      if (bytes === undefined) { throw new Error(`No ${encoding} representation.`); }
      return [...bytes];
    }));
  }
  if ([...text].some(character => {
    const value = character.codePointAt(0)!;
    return value >= 0xd800 && value <= 0xdfff;
  })) { throw new Error('Malformed Unicode text.'); }
  if (encoding === 'utf16le' || encoding === 'utf16be') {
    const bytes = Buffer.from(`\ufeff${text}`, 'utf16le');
    return encoding === 'utf16be' ? bytes.swap16() : bytes;
  }
  return Buffer.from(`${encoding === 'utf8bom' ? '\ufeff' : ''}${text}`, 'utf8');
}
