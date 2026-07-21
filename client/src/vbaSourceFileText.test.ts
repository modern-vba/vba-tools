import test from 'node:test';
import assert from 'node:assert/strict';
import { Buffer } from 'node:buffer';

import { decodeVbaSourceFileText } from './vbaSourceFileText';

test('VBA source bytes decode BOM encodings, strict UTF-8, and CP932 fallback', () => {
  const unicodeText = 'Debug.Print "日本語"';
  const utf8 = Buffer.from(unicodeText, 'utf8');
  const utf16LittleEndian = Buffer.from(unicodeText, 'utf16le');
  const utf16BigEndian = swapUtf16ByteOrder(utf16LittleEndian);
  const cp932 = Uint8Array.from([
    ...Buffer.from('Debug.Print "', 'ascii'),
    0x93, 0xfa,
    0x96, 0x7b,
    0x8c, 0xea,
    0x22
  ]);

  assert.equal(decodeVbaSourceFileText(Uint8Array.from([])), '');
  assert.equal(decodeVbaSourceFileText(utf8), unicodeText);
  assert.equal(
    decodeVbaSourceFileText(Uint8Array.from([0xef, 0xbb, 0xbf, ...utf8])),
    unicodeText
  );
  assert.equal(
    decodeVbaSourceFileText(Uint8Array.from([0xff, 0xfe, ...utf16LittleEndian])),
    unicodeText
  );
  assert.equal(
    decodeVbaSourceFileText(Uint8Array.from([0xfe, 0xff, ...utf16BigEndian])),
    unicodeText
  );
  assert.equal(decodeVbaSourceFileText(cp932), unicodeText);
});

test('CP932 fallback matches Windows single-byte mappings without rewriting trail bytes', () => {
  assert.equal(
    decodeVbaSourceFileText(Uint8Array.from([
      0x80,
      0x1a,
      0x1c,
      0x7f,
      0xa0,
      0xfd,
      0xfe,
      0xff
    ])),
    '\u0080\u001a\u001c\u007f\uf8f0\uf8f1\uf8f2\uf8f3'
  );
  assert.equal(
    decodeVbaSourceFileText(Uint8Array.from([0x81, 0x80, 0x81, 0xa0])),
    '\u00f7\u25a1'
  );
});

function swapUtf16ByteOrder(littleEndianBytes: Uint8Array): Uint8Array {
  const bigEndianBytes = Uint8Array.from(littleEndianBytes);
  for (let index = 0; index < bigEndianBytes.length; index += 2) {
    [bigEndianBytes[index], bigEndianBytes[index + 1]] = [
      bigEndianBytes[index + 1],
      bigEndianBytes[index]
    ];
  }

  return bigEndianBytes;
}
