import type { SourcePosition, SourceRange } from './sourceRange';
import {
  C_IDENTIFIER_PATTERN,
  isIdentifierPart,
  isIdentifierStart
} from './vbaText';

export function getIdentifierPrefix(lines: string[], position: SourcePosition): string {
  const line = lines[position.line] ?? '';
  const text_before_position = line.slice(0, position.character);
  const match = new RegExp(`${C_IDENTIFIER_PATTERN.source}$`).exec(text_before_position);

  return match?.[0] ?? '';
}

export function getIdentifierAt(lines: string[], position: SourcePosition): string | undefined {
  const line = lines[position.line] ?? '';
  const identifier_pattern = new RegExp(C_IDENTIFIER_PATTERN.source, 'g');

  for (const match of line.matchAll(identifier_pattern)) {
    const start = match.index;
    const end = start + match[0].length;
    if (position.character >= start && position.character <= end) {
      return match[0];
    }
  }

  return undefined;
}

export function getIdentifierRangesInCode(line: string, lineIndex: number): SourceRange[] {
  const ranges: SourceRange[] = [];
  let character_index = 0;
  let is_in_string = false;

  while (character_index < line.length) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
        } else {
          is_in_string = false;
          character_index += 1;
        }
      } else {
        character_index += 1;
      }
      continue;
    }

    if (character === "'") {
      break;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    if (isIdentifierStart(character)) {
      const start = character_index;
      character_index += 1;
      while (character_index < line.length && isIdentifierPart(line[character_index])) {
        character_index += 1;
      }
      ranges.push({
        start: { line: lineIndex, character: start },
        end: { line: lineIndex, character: character_index }
      });
      continue;
    }

    character_index += 1;
  }

  return ranges;
}

export function isCodePosition(line: string, character: number): boolean {
  let character_index = 0;
  let is_in_string = false;

  while (character_index < Math.min(character, line.length)) {
    const current_character = line[character_index];
    if (is_in_string) {
      if (current_character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
        } else {
          is_in_string = false;
          character_index += 1;
        }
      } else {
        character_index += 1;
      }
      continue;
    }

    if (current_character === "'") {
      return false;
    }
    if (current_character === '"') {
      is_in_string = true;
    }

    character_index += 1;
  }

  return !is_in_string;
}
