import { TextDecoder } from 'node:util';

const utf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const utf16le = new TextDecoder('utf-16le', { fatal: true, ignoreBOM: true });
const utf16be = new TextDecoder('utf-16be', { fatal: true, ignoreBOM: true });

/** Decodes disk ProjectManifest bytes before any JSON projection or target selection. */
export function decodeProjectManifestBytes(bytes: Uint8Array): string {
  try {
    // UTF-32LE shares the UTF-16LE prefix and must be classified first.
    if (hasPrefix(bytes, [0xff, 0xfe, 0, 0]) || hasPrefix(bytes, [0, 0, 0xfe, 0xff])) {
      throw new TypeError('UTF-32 is not supported.');
    }
    let decoder = utf8;
    let offset = 0;
    if (hasPrefix(bytes, [0xef, 0xbb, 0xbf])) {
      offset = 3;
    } else if (hasPrefix(bytes, [0xef, 0xbb])) {
      throw new TypeError('The UTF-8 BOM is malformed or truncated.');
    } else if (hasPrefix(bytes, [0xff, 0xfe])) {
      decoder = utf16le;
      offset = 2;
    } else if (hasPrefix(bytes, [0xfe, 0xff])) {
      decoder = utf16be;
      offset = 2;
    }
    const text = decoder.decode(bytes.subarray(offset));
    if (text.includes('\u0000')) {
      throw new TypeError('NUL characters and BOM-less UTF-16 are not supported.');
    }
    return text;
  } catch (cause) {
    throw new TypeError('Project manifest encoding is invalid.', { cause });
  }
}

function hasPrefix(bytes: Uint8Array, prefix: readonly number[]): boolean {
  return bytes.length >= prefix.length && prefix.every((byte, index) => bytes[index] === byte);
}
