import * as assert from 'node:assert/strict';
import { test } from 'node:test';

import { ordinalIgnoreCaseKey } from './ordinalIgnoreCase';

test('matches .NET OrdinalIgnoreCase for the Greek final sigma', () => {
  assert.equal(ordinalIgnoreCaseKey('\u03a3'), ordinalIgnoreCaseKey('\u03c2'));
});

test('matches .NET OrdinalIgnoreCase for the micro sign and Greek mu', () => {
  assert.equal(ordinalIgnoreCaseKey('\u00b5'), ordinalIgnoreCaseKey('\u039c'));
});

test('matches .NET simple casing for precomposed Greek extended letters', () => {
  assert.equal(ordinalIgnoreCaseKey('\u1f80'), ordinalIgnoreCaseKey('\u1f88'));
});

test('matches .NET OrdinalIgnoreCase for supplementary-plane case pairs', () => {
  assert.equal(
    ordinalIgnoreCaseKey('\u{10400}'),
    ordinalIgnoreCaseKey('\u{10428}')
  );
});

test('does not apply culture, compatibility, expansion, or normalization folding', () => {
  const distinctPairs = [
    ['I', '\u0131'],
    ['K', '\u212a'],
    ['\u00df', 'SS'],
    ['\u00c5', 'A\u030a']
  ] as const;

  for (const [left, right] of distinctPairs) {
    assert.notEqual(
      ordinalIgnoreCaseKey(left),
      ordinalIgnoreCaseKey(right),
      `${JSON.stringify(left)} and ${JSON.stringify(right)}`
    );
  }
});
