import assert from 'node:assert/strict';
import test from 'node:test';
import {
  isPostNativeRequestVersion,
  isNativeLineBreakText
} from './nativeLineBreakText';

test('native line-break receipts contain exactly one EOL followed by indentation', () => {
  for (const text of ['\r\n', '\n', '\r', '\r\n    ', '\n\t\t']) {
    assert.equal(isNativeLineBreakText(text), true, JSON.stringify(text));
  }
});

test('native line-break receipts reject unrelated or ambiguous edits', () => {
  for (const text of [
    '',
    ' ',
    'x\r\n',
    '\r\nx',
    '\r\n \tx',
    '\r\n\u00a0',
    '\r\n\r\n',
    '\n\n',
    '\r\r'
  ]) {
    assert.equal(isNativeLineBreakText(text), false, JSON.stringify(text));
  }
});

test('post-native requests bind to the recorded native document version', () => {
  assert.equal(isPostNativeRequestVersion(8, 8), true);
  assert.equal(isPostNativeRequestVersion(8, 7), false);
  assert.equal(isPostNativeRequestVersion(8, 9), false);
});
