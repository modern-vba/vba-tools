import test from 'node:test';
import assert from 'node:assert/strict';

import {
  VBA_SEMANTIC_TOKEN_MODIFIERS,
  VBA_SEMANTIC_TOKEN_TYPES
} from './vbaProject';
import { encodeSemanticTokens } from './semanticTokenEncoding';

test('semantic token legend exposes macro and readonly modifier', () => {
  assert.ok(VBA_SEMANTIC_TOKEN_TYPES.includes('macro'));
  assert.ok(VBA_SEMANTIC_TOKEN_MODIFIERS.includes('readonly'));
});

test('semantic token encoding emits modifier bitsets', () => {
  const variable_index = VBA_SEMANTIC_TOKEN_TYPES.indexOf('variable');
  const macro_index = VBA_SEMANTIC_TOKEN_TYPES.indexOf('macro');
  const readonly_bitset = 1 << VBA_SEMANTIC_TOKEN_MODIFIERS.indexOf('readonly');

  assert.deepEqual(encodeSemanticTokens([
    {
      range: {
        start: { line: 0, character: 0 },
        end: { line: 0, character: 5 }
      },
      tokenType: 'variable',
      tokenModifiers: ['readonly']
    },
    {
      range: {
        start: { line: 1, character: 2 },
        end: { line: 1, character: 7 }
      },
      tokenType: 'macro'
    }
  ]), {
    data: [
      0, 0, 5, variable_index, readonly_bitset,
      1, 2, 5, macro_index, 0
    ]
  });
});
