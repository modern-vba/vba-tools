export const C_IDENTIFIER_PATTERN = /[A-Za-z_][A-Za-z0-9_]*/;

export interface VbaIdentifier {
  name: string;
  start: number;
  end: number;
}

export interface VbaIdentifierToken {
  text: string;
  lowerText: string;
  start: number;
  end: number;
}

export interface VbaTextSegment {
  start: number;
  end: number;
}

export function findClosingParenInCode(
  line: string,
  openParen: number,
  endCharacter: number
): number | undefined {
  let character_index = openParen + 1;
  let is_in_string = false;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }

        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === ')') {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

export function findKeywordOutsideLiterals(
  line: string,
  keyword: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  const lower_keyword = keyword.toLowerCase();

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }

    if (isKeywordTokenAt(line, character_index, lower_keyword, endCharacter)) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

export function findMatchingParen(
  line: string,
  openParen: number,
  endCharacter: number
): number | undefined {
  let depth = 0;
  let character_index = openParen;
  let is_in_string = false;

  while (character_index < endCharacter) {
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
      return undefined;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }
    if (character === '(') {
      depth += 1;
    } else if (character === ')') {
      depth -= 1;
      if (depth === 0) {
        return character_index;
      }
    }

    character_index += 1;
  }

  return undefined;
}

export function findPreviousNonWhitespace(line: string, startCharacter: number): number | undefined {
  for (let character_index = startCharacter; character_index >= 0; character_index -= 1) {
    if (!/\s/.test(line[character_index])) {
      return character_index;
    }
  }

  return undefined;
}

export function findTopLevelAssignmentEquals(
  line: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === '=' && paren_depth === 0 && isAssignmentEquals(line, character_index)) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

export function findTopLevelEquals(
  line: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === '=' && paren_depth === 0) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

export function countTopLevelCommas(text: string): number {
  let count = 0;
  let depth = 0;
  let character_index = 0;
  let is_in_string = false;

  while (character_index < text.length) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
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
    if (character === '(') {
      depth += 1;
    } else if (character === ')') {
      depth = Math.max(0, depth - 1);
    } else if (character === ',' && depth === 0) {
      count += 1;
    }

    character_index += 1;
  }

  return count;
}

export function isIdentifierName(value: string): boolean {
  return new RegExp(`^${C_IDENTIFIER_PATTERN.source}$`).test(value);
}

export function isIdentifierStart(character: string): boolean {
  return /[A-Za-z_]/.test(character);
}

export function isIdentifierPart(character: string): boolean {
  return /[A-Za-z0-9_]/.test(character);
}

export function isRemCommentStart(line: string, characterIndex: number): boolean {
  if (!/^Rem\b/i.test(line.slice(characterIndex))) {
    return false;
  }

  const before = line.slice(0, characterIndex).trimEnd();
  return before === '' || before.endsWith(':');
}

export function isPlausibleConstantInitializer(text: string): boolean {
  const trimmed_text = text.trim();
  return trimmed_text !== ''
    && !/[,+\-*/\\^&=<>]\s*$/.test(trimmed_text)
    && !/^(?:,|[*/\\^&=<>])/.test(trimmed_text);
}

export function getStringLiteralEnd(line: string, startCharacter: number): number | undefined {
  let character_index = startCharacter + 1;
  while (character_index < line.length) {
    if (line[character_index] !== '"') {
      character_index += 1;
      continue;
    }

    if (line[character_index + 1] === '"') {
      character_index += 2;
      continue;
    }

    return character_index + 1;
  }

  return undefined;
}

export function readIdentifierAt(
  line: string,
  startCharacter: number
): VbaIdentifier | undefined {
  if (!isIdentifierStart(line[startCharacter] ?? '')) {
    return undefined;
  }

  let character_index = startCharacter + 1;
  while (character_index < line.length && isIdentifierPart(line[character_index])) {
    character_index += 1;
  }

  return {
    name: line.slice(startCharacter, character_index),
    start: startCharacter,
    end: character_index
  };
}

export function readIdentifierEnd(line: string, startCharacter: number, endCharacter: number): number {
  let character_index = startCharacter + 1;
  while (character_index < endCharacter && isIdentifierPart(line[character_index])) {
    character_index += 1;
  }

  return character_index;
}

export function readIdentifierTokenAt(
  line: string,
  startCharacter: number,
  endCharacter: number
): VbaIdentifierToken | undefined {
  if (startCharacter >= endCharacter || !isIdentifierStart(line[startCharacter])) {
    return undefined;
  }

  const token_end = readIdentifierEnd(line, startCharacter, endCharacter);
  const text = line.slice(startCharacter, token_end);
  return {
    text,
    lowerText: text.toLowerCase(),
    start: startCharacter,
    end: token_end
  };
}

export function skipWhitespace(line: string, startCharacter: number, endCharacter: number): number {
  let character_index = startCharacter;
  while (character_index < endCharacter && /\s/.test(line[character_index])) {
    character_index += 1;
  }

  return character_index;
}

export function splitTopLevelSegments(
  line: string,
  startCharacter: number,
  endCharacter: number
): VbaTextSegment[] {
  const segments: VbaTextSegment[] = [];
  let segment_start = startCharacter;
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter) {
    const character = line[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (line[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === '"') {
      is_in_string = true;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === ',' && paren_depth === 0) {
      segments.push({ start: segment_start, end: character_index });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({ start: segment_start, end: endCharacter });
  return segments;
}

export function startsWithKeywordAt(
  line: string,
  startCharacter: number,
  keyword: string,
  endCharacter: number
): boolean {
  const token = readIdentifierTokenAt(line, startCharacter, endCharacter);
  return token?.lowerText === keyword.toLowerCase();
}

export function trimEndIndex(line: string, endCharacter: number): number {
  let character_index = endCharacter;
  while (character_index > 0 && /\s/.test(line[character_index - 1])) {
    character_index -= 1;
  }

  return character_index;
}

function isAssignmentEquals(line: string, equalsIndex: number): boolean {
  const previous_character = findPreviousNonWhitespace(line, equalsIndex - 1);
  if (
    previous_character !== undefined
    && (line[previous_character] === '<' || line[previous_character] === '>' || line[previous_character] === ':')
  ) {
    return false;
  }

  return true;
}

function isKeywordTokenAt(
  line: string,
  characterIndex: number,
  lowerKeyword: string,
  endCharacter: number
): boolean {
  if (line.slice(characterIndex, characterIndex + lowerKeyword.length).toLowerCase() !== lowerKeyword) {
    return false;
  }

  const before = characterIndex === 0 ? '' : line[characterIndex - 1];
  const after_index = characterIndex + lowerKeyword.length;
  const after = after_index >= endCharacter ? '' : line[after_index];
  return (before === '' || !isIdentifierPart(before))
    && (after === '' || !isIdentifierPart(after));
}
