import {
  VBA_SEMANTIC_TOKEN_MODIFIERS,
  VBA_SEMANTIC_TOKEN_TYPES,
  VbaSemanticToken
} from './vbaProject';

export function encodeSemanticTokens(tokens: VbaSemanticToken[]): { data: number[] } {
  const data: number[] = [];
  let previous_line = 0;
  let previous_character = 0;

  for (const token of tokens) {
    const token_type = VBA_SEMANTIC_TOKEN_TYPES.indexOf(token.tokenType);
    if (token_type === -1) {
      continue;
    }

    const line_delta = token.range.start.line - previous_line;
    const character_delta = line_delta === 0
      ? token.range.start.character - previous_character
      : token.range.start.character;
    data.push(
      line_delta,
      character_delta,
      token.range.end.character - token.range.start.character,
      token_type,
      encodeSemanticTokenModifiers(token)
    );
    previous_line = token.range.start.line;
    previous_character = token.range.start.character;
  }

  return { data };
}

function encodeSemanticTokenModifiers(token: VbaSemanticToken): number {
  return (token.tokenModifiers ?? []).reduce((bitset, modifier) => {
    const modifier_index = VBA_SEMANTIC_TOKEN_MODIFIERS.indexOf(modifier);
    return modifier_index === -1 ? bitset : bitset | (1 << modifier_index);
  }, 0);
}
