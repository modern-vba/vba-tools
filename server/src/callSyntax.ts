import {
  getLogicalSourceRange,
  type LogicalCodeSource,
  type StatementSegment
} from './logicalSource';
import type { SyntaxDiagnostic } from './vbaSourceModel';
import {
  findMatchingParen,
  findPreviousNonWhitespace,
  getStringLiteralEnd,
  isIdentifierPart,
  isRemCommentStart,
  readIdentifierTokenAt,
  skipWhitespace,
  splitTopLevelSegments,
  trimEndIndex
} from './vbaText';

export function getCallStatementSegments(text: string): StatementSegment[] {
  const segments: StatementSegment[] = [];
  let segment_start = 0;
  let character_index = 0;
  let is_in_string = false;

  while (character_index < text.length) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
          character_index += 2;
          continue;
        }
        is_in_string = false;
      }
      character_index += 1;
      continue;
    }

    if (character === "'" || isRemCommentStart(text, character_index)) {
      break;
    }
    if (character === '"') {
      is_in_string = true;
      character_index += 1;
      continue;
    }
    if (character === ':' && text[character_index + 1] !== '=') {
      segments.push({
        start: segment_start,
        end: character_index,
        terminator: character_index,
        text: text.slice(segment_start, character_index)
      });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({
    start: segment_start,
    end: character_index,
    text: text.slice(segment_start, character_index)
  });
  return segments;
}

export function collectMalformedCallDiagnosticsForSegment(
  source: LogicalCodeSource,
  segmentStart: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const trimmed_start = skipWhitespace(source.text, segmentStart, segmentEnd);
  const trimmed_end = trimEndIndex(source.text, segmentEnd);
  if (trimmed_start >= trimmed_end) {
    return [];
  }

  const first_token = readIdentifierTokenAt(source.text, trimmed_start, trimmed_end);
  if (first_token === undefined || shouldSkipCallDiagnosticsForStatement(first_token.lowerText)) {
    return [];
  }

  if (first_token.lowerText === 'call') {
    return collectCallKeywordDiagnostics(source, first_token.end, trimmed_end);
  }

  if (first_token.lowerText === 'raiseevent') {
    return collectRaiseEventCallDiagnostics(source, first_token.end, trimmed_end);
  }

  const parenthesized_diagnostics = collectParenthesizedCallDiagnostics(source, trimmed_start, trimmed_end, {
    disallowNamedArguments: false
  });
  if (parenthesized_diagnostics.length > 0) {
    return parenthesized_diagnostics;
  }

  const target = readCallableTargetAt(source.text, trimmed_start, trimmed_end);
  if (target === undefined) {
    return [];
  }

  const args_start = skipWhitespace(source.text, target.end, trimmed_end);
  if (args_start >= trimmed_end || source.text[args_start] === '=') {
    return [];
  }

  return collectCallArgumentListDiagnostics(source, args_start, trimmed_end, {
    disallowNamedArguments: false
  });
}

export function findPreviousTopLevelComma(
  text: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;
  let comma_index: number | undefined;

  while (character_index < endCharacter) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
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
    } else if (character === "'") {
      break;
    } else if (character === '(') {
      paren_depth += 1;
    } else if (character === ')' && paren_depth > 0) {
      paren_depth -= 1;
    } else if (character === ',' && paren_depth === 0) {
      comma_index = character_index;
    }

    character_index += 1;
  }

  return comma_index;
}

export function findTopLevelNamedArgumentSeparator(
  text: string,
  startCharacter: number,
  endCharacter: number
): number | undefined {
  let character_index = startCharacter;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < endCharacter - 1) {
    const character = text[character_index];
    if (is_in_string) {
      if (character === '"') {
        if (text[character_index + 1] === '"') {
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
    } else if (character === ':' && text[character_index + 1] === '=' && paren_depth === 0) {
      return character_index;
    }

    character_index += 1;
  }

  return undefined;
}

export function shouldSkipCallDiagnosticsForStatement(firstToken: string): boolean {
  return firstToken === 'attribute'
    || firstToken === 'option'
    || firstToken === 'const'
    || firstToken === 'dim'
    || firstToken === 'static'
    || firstToken === 'redim'
    || firstToken === 'public'
    || firstToken === 'private'
    || firstToken === 'friend'
    || firstToken === 'declare'
    || firstToken === 'sub'
    || firstToken === 'function'
    || firstToken === 'property'
    || firstToken === 'event'
    || firstToken === 'enum'
    || firstToken === 'type'
    || firstToken === 'implements'
    || firstToken === 'end'
    || firstToken === 'if'
    || firstToken === 'elseif'
    || firstToken === 'else'
    || firstToken === 'for'
    || firstToken === 'do'
    || firstToken === 'loop'
    || firstToken === 'while'
    || firstToken === 'wend'
    || firstToken === 'with'
    || firstToken === 'select'
    || firstToken === 'case'
    || firstToken === 'next'
    || firstToken === 'exit';
}

function collectCallKeywordDiagnostics(
  source: LogicalCodeSource,
  callKeywordEnd: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const target_start = skipWhitespace(source.text, callKeywordEnd, segmentEnd);
  const target = readCallableTargetAt(source.text, target_start, segmentEnd);
  if (target === undefined) {
    return [createMalformedCallDiagnostic(
      'Call statement is missing a procedure name.',
      source,
      target_start,
      Math.min(target_start + 1, segmentEnd)
    )];
  }

  const args_start = skipWhitespace(source.text, target.end, segmentEnd);
  if (args_start >= segmentEnd) {
    return [];
  }

  if (source.text[args_start] !== '(') {
    return [createMalformedCallDiagnostic(
      'Call statement arguments must be enclosed in parentheses.',
      source,
      args_start,
      segmentEnd
    )];
  }

  return collectSingleParenthesizedCallDiagnostics(source, args_start, segmentEnd, {
    disallowNamedArguments: false
  });
}

function collectRaiseEventCallDiagnostics(
  source: LogicalCodeSource,
  raiseEventEnd: number,
  segmentEnd: number
): SyntaxDiagnostic[] {
  const target_start = skipWhitespace(source.text, raiseEventEnd, segmentEnd);
  const target = readCallableTargetAt(source.text, target_start, segmentEnd);
  if (target === undefined) {
    return [createMalformedCallDiagnostic(
      'RaiseEvent statement is missing an event name.',
      source,
      target_start,
      Math.min(target_start + 1, segmentEnd)
    )];
  }

  const args_start = skipWhitespace(source.text, target.end, segmentEnd);
  if (args_start >= segmentEnd) {
    return [];
  }

  if (source.text[args_start] === '(') {
    return collectSingleParenthesizedCallDiagnostics(source, args_start, segmentEnd, {
      disallowNamedArguments: true
    });
  }

  return collectCallArgumentListDiagnostics(source, args_start, segmentEnd, {
    disallowNamedArguments: true
  });
}

function collectParenthesizedCallDiagnostics(
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];
  let character_index = startCharacter;

  while (character_index < endCharacter) {
    const character = source.text[character_index];
    if (character === '"') {
      const string_end = getStringLiteralEnd(source.text, character_index);
      if (string_end === undefined || string_end > endCharacter) {
        break;
      }
      character_index = string_end;
      continue;
    }

    if (character === '(' && isCallArgumentListOpenParen(source.text, character_index, startCharacter)) {
      diagnostics.push(...collectSingleParenthesizedCallDiagnostics(source, character_index, endCharacter, options));
      const close_paren = findMatchingParen(source.text, character_index, endCharacter);
      if (close_paren === undefined) {
        return diagnostics;
      }
      character_index = close_paren + 1;
      continue;
    }

    character_index += 1;
  }

  return diagnostics;
}

function collectSingleParenthesizedCallDiagnostics(
  source: LogicalCodeSource,
  openParen: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  const close_paren = findMatchingParen(source.text, openParen, endCharacter);
  if (close_paren === undefined) {
    if (isInProgressCallArgumentList(source.text, openParen + 1, endCharacter)) {
      return [];
    }

    return [createMalformedCallDiagnostic(
      'Call argument list is missing a closing parenthesis.',
      source,
      openParen,
      openParen + 1
    )];
  }

  return collectCallArgumentListDiagnostics(source, openParen + 1, close_paren, options);
}

function isInProgressCallArgumentList(text: string, startCharacter: number, endCharacter: number): boolean {
  const trimmed_start = skipWhitespace(text, startCharacter, endCharacter);
  if (trimmed_start >= endCharacter) {
    return false;
  }

  return findPreviousTopLevelComma(text, startCharacter, endCharacter) !== undefined;
}

function collectCallArgumentListDiagnostics(
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number,
  options: { disallowNamedArguments: boolean }
): SyntaxDiagnostic[] {
  if (startCharacter >= endCharacter) {
    return [];
  }

  const segments = splitTopLevelSegments(source.text, startCharacter, endCharacter);
  for (let segment_index = 0; segment_index < segments.length; segment_index += 1) {
    const segment = segments[segment_index];
    const trimmed_start = skipWhitespace(source.text, segment.start, segment.end);
    const trimmed_end = trimEndIndex(source.text, segment.end);
    if (trimmed_start >= trimmed_end) {
      if (segment_index === segments.length - 1 && segment_index > 0) {
        const comma_index = findPreviousNonWhitespace(source.text, segment.start - 1);
        return [createMalformedCallDiagnostic(
          'Call argument list has a missing argument after this comma.',
          source,
          comma_index ?? segment.start,
          (comma_index ?? segment.start) + 1
        )];
      }
      continue;
    }

    const named_separator = findTopLevelNamedArgumentSeparator(source.text, trimmed_start, trimmed_end);
    if (named_separator === undefined) {
      continue;
    }

    if (options.disallowNamedArguments) {
      return [createMalformedCallDiagnostic(
        'RaiseEvent arguments cannot use named-argument syntax.',
        source,
        named_separator,
        named_separator + 2
      )];
    }

    const name_end = trimEndIndex(source.text, named_separator);
    const name_token = readIdentifierTokenAt(source.text, trimmed_start, name_end);
    if (name_token === undefined || skipWhitespace(source.text, name_token.end, name_end) < name_end) {
      return [createMalformedCallDiagnostic(
        'Named argument is missing a valid name.',
        source,
        trimmed_start,
        named_separator
      )];
    }

    const value_start = skipWhitespace(source.text, named_separator + 2, trimmed_end);
    if (value_start >= trimmed_end) {
      return [createMalformedCallDiagnostic(
        'Named argument is missing a value.',
        source,
        named_separator,
        named_separator + 2
      )];
    }
  }

  return [];
}

function readCallableTargetAt(
  text: string,
  startCharacter: number,
  endCharacter: number
): { start: number; end: number } | undefined {
  let character_index = skipWhitespace(text, startCharacter, endCharacter);
  const target_start = character_index;
  if (text[character_index] === '.') {
    character_index = skipWhitespace(text, character_index + 1, endCharacter);
  }

  let token = readIdentifierTokenAt(text, character_index, endCharacter);
  if (token === undefined) {
    return undefined;
  }
  character_index = token.end;

  while (true) {
    const dot_index = skipWhitespace(text, character_index, endCharacter);
    if (text[dot_index] !== '.') {
      break;
    }

    const member_start = skipWhitespace(text, dot_index + 1, endCharacter);
    token = readIdentifierTokenAt(text, member_start, endCharacter);
    if (token === undefined) {
      break;
    }
    character_index = token.end;
  }

  return {
    start: target_start,
    end: character_index
  };
}

function isCallArgumentListOpenParen(text: string, openParen: number, startCharacter: number): boolean {
  const previous_character = findPreviousNonWhitespace(text, openParen - 1);
  return previous_character !== undefined
    && previous_character >= startCharacter
    && (isIdentifierPart(text[previous_character]) || text[previous_character] === ')');
}

function createMalformedCallDiagnostic(
  message: string,
  source: LogicalCodeSource,
  startCharacter: number,
  endCharacter: number
): SyntaxDiagnostic {
  return {
    code: 'syntax.malformedCall',
    message,
    range: getLogicalSourceRange(source, startCharacter, endCharacter),
    severity: 'error',
    source: 'vba-language-server'
  };
}
