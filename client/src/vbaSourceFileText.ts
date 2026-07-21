import { TextDecoder } from 'node:util';

const utf8Preamble = Uint8Array.from([0xef, 0xbb, 0xbf]);
const utf16LittleEndianPreamble = Uint8Array.from([0xff, 0xfe]);
const utf16BigEndianPreamble = Uint8Array.from([0xfe, 0xff]);
const utf8Strict = new TextDecoder('utf-8', { fatal: true });
const utf16LittleEndian = new TextDecoder('utf-16le');
const utf16BigEndian = new TextDecoder('utf-16be');
const cp932 = new TextDecoder('shift_jis');
const cp932SingleByteOverrides = new Map<number, string>([
  [0x1a, '\u001a'],
  [0x1c, '\u001c'],
  [0x7f, '\u007f'],
  [0x80, '\u0080'],
  [0xa0, '\uf8f0'],
  [0xfd, '\uf8f1'],
  [0xfe, '\uf8f2'],
  [0xff, '\uf8f3']
]);

export function decodeVbaSourceFileText(bytes: Uint8Array): string {
  if (bytes.length === 0) {
    return '';
  }

  if (hasPrefix(bytes, utf8Preamble)) {
    return utf8Strict.decode(bytes.subarray(utf8Preamble.length));
  }

  if (hasPrefix(bytes, utf16LittleEndianPreamble)) {
    return utf16LittleEndian.decode(bytes.subarray(utf16LittleEndianPreamble.length));
  }

  if (hasPrefix(bytes, utf16BigEndianPreamble)) {
    return utf16BigEndian.decode(bytes.subarray(utf16BigEndianPreamble.length));
  }

  try {
    return utf8Strict.decode(bytes);
  } catch (error) {
    if (!(error instanceof TypeError)) {
      throw error;
    }

    return decodeCp932(bytes);
  }
}

function decodeCp932(bytes: Uint8Array): string {
  let decoded = '';
  let segmentStart = 0;
  for (let index = 0; index < bytes.length; index += 1) {
    if (
      isCp932LeadByte(bytes[index])
      && index + 1 < bytes.length
      && isCp932TrailByte(bytes[index + 1])
    ) {
      index += 1;
      continue;
    }

    const override = cp932SingleByteOverrides.get(bytes[index]);
    if (override === undefined) {
      continue;
    }

    decoded += cp932.decode(bytes.subarray(segmentStart, index));
    decoded += override;
    segmentStart = index + 1;
  }

  return decoded + cp932.decode(bytes.subarray(segmentStart));
}

function isCp932LeadByte(value: number): boolean {
  return (0x81 <= value && value <= 0x9f) || (0xe0 <= value && value <= 0xfc);
}

function isCp932TrailByte(value: number): boolean {
  return (0x40 <= value && value <= 0x7e) || (0x80 <= value && value <= 0xfc);
}

function hasPrefix(bytes: Uint8Array, prefix: Uint8Array): boolean {
  return bytes.length >= prefix.length
    && prefix.every((value, index) => bytes[index] === value);
}
