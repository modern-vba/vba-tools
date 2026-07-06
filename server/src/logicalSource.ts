import type { SourcePosition, SourceRange } from './sourceRange';
import { findPreviousNonWhitespace, isRemCommentStart } from './vbaText';

export interface LogicalSourceText {
  text: string;
  positions: SourcePosition[];
}

export interface LogicalCodeSource extends LogicalSourceText {
  startLine: number;
  endLine: number;
}

export interface LogicalSourceSpan extends LogicalSourceText {
  startLine: number;
  endLine: number;
  endCharacter: number;
  hasCommentContinuation: boolean;
}

export interface StatementSegment {
  start: number;
  end: number;
  terminator?: number;
  text: string;
}

export function sourceLineRange(source: LogicalCodeSource): number[] {
  const lines: number[] = [];
  for (let line_index = source.startLine; line_index <= source.endLine; line_index += 1) {
    lines.push(line_index);
  }

  return lines;
}

export function isContinuationTail(lines: string[], lineIndex: number): boolean {
  return lineIndex > 0 && getCodeContinuationMarkerStart(lines[lineIndex - 1] ?? '') !== undefined;
}

export function getLogicalCodeSourceFromLine(
  lines: string[],
  startLine: number
): LogicalCodeSource | undefined {
  const source = getLogicalSourceSpanFromLine(lines, startLine);
  return source === undefined
    ? undefined
    : {
        text: source.text,
        positions: source.positions,
        startLine: source.startLine,
        endLine: source.endLine
      };
}

export function getLogicalSourceSpanFromLine(
  lines: string[],
  startLine: number,
  startCharacter = 0
): LogicalSourceSpan | undefined {
  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];
  let has_comment_continuation = false;

  for (let line_index = startLine; line_index < lines.length; line_index += 1) {
    const line = lines[line_index] ?? '';
    const line_start = line_index === startLine ? startCharacter : 0;
    const continuation_marker = getCodeContinuationMarkerStart(line);
    const line_end = continuation_marker ?? getCodeEndCharacter(line);
    has_comment_continuation = has_comment_continuation || hasCommentContinuationMarker(line);

    text_parts.push(line.slice(line_start, line_end));
    for (let character = line_start; character < line_end; character += 1) {
      positions.push({ line: line_index, character });
    }

    if (continuation_marker === undefined) {
      return {
        text: text_parts.join(''),
        positions,
        startLine,
        endLine: line_index,
        endCharacter: line_end,
        hasCommentContinuation: has_comment_continuation
      };
    }
  }

  return undefined;
}

export function getContinuedSourceTextEndingBefore(
  lines: string[],
  lineIndex: number,
  endCharacter: number
): LogicalSourceText | undefined {
  let start_line = lineIndex;
  while (start_line > 0 && getCodeContinuationMarkerStart(lines[start_line - 1] ?? '') !== undefined) {
    start_line -= 1;
  }

  if (start_line === lineIndex) {
    return undefined;
  }

  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];
  for (let current_line_index = start_line; current_line_index <= lineIndex; current_line_index += 1) {
    const line = lines[current_line_index] ?? '';
    const line_end = current_line_index === lineIndex
      ? Math.min(endCharacter, line.length)
      : getCodeContinuationMarkerStart(line);
    if (line_end === undefined) {
      return undefined;
    }

    text_parts.push(line.slice(0, line_end));
    for (let character = 0; character < line_end; character += 1) {
      positions.push({ line: current_line_index, character });
    }
  }

  return {
    text: text_parts.join(''),
    positions
  };
}

export function getSingleLineLogicalSourceText(
  line: string,
  lineIndex: number,
  endCharacter: number
): LogicalSourceText {
  const positions: SourcePosition[] = [];
  for (let character = 0; character < endCharacter; character += 1) {
    positions.push({ line: lineIndex, character });
  }

  return {
    text: line.slice(0, endCharacter),
    positions
  };
}

export function getLogicalSourceRange(
  source: LogicalSourceText,
  start: number,
  end: number
): SourceRange {
  const start_position = source.positions[start];
  const last_position = source.positions[end - 1];
  return {
    start: start_position,
    end: {
      line: last_position.line,
      character: last_position.character + 1
    }
  };
}

export function getCodeContinuationMarkerStart(line: string): number | undefined {
  const code_end = getCodeEndCharacter(line);
  if (code_end < line.length) {
    return undefined;
  }

  const marker_index = findPreviousNonWhitespace(line, code_end - 1);
  if (
    marker_index === undefined
    || line[marker_index] !== '_'
    || marker_index === 0
    || !/\s/.test(line[marker_index - 1])
  ) {
    return undefined;
  }

  return marker_index;
}

export function hasSourceText(line: string): boolean {
  return line.slice(0, getCodeEndCharacter(line)).trim().length > 0;
}

export function hasCommentContinuationMarker(line: string): boolean {
  const code_end = getCodeEndCharacter(line);
  if (code_end >= line.length) {
    return false;
  }

  const marker_index = findPreviousNonWhitespace(line, line.length - 1);
  return marker_index !== undefined
    && marker_index > code_end
    && line[marker_index] === '_'
    && marker_index > 0
    && /\s/.test(line[marker_index - 1]);
}

export function getCodeEndCharacter(line: string): number {
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
      return character_index;
    }
    if (isRemCommentStart(line, character_index)) {
      return character_index;
    }
    if (character === '"') {
      is_in_string = true;
    }

    character_index += 1;
  }

  return line.length;
}

export function getTopLevelStatementSegments(line: string): StatementSegment[] {
  const code_end = getCodeEndCharacter(line);
  const segments: StatementSegment[] = [];
  let segment_start = 0;
  let character_index = 0;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < code_end) {
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
    } else if (character === ':' && line[character_index + 1] !== '=' && paren_depth === 0) {
      segments.push({
        start: segment_start,
        end: character_index,
        terminator: character_index,
        text: line.slice(segment_start, character_index)
      });
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  segments.push({
    start: segment_start,
    end: code_end,
    text: line.slice(segment_start, code_end)
  });
  return segments;
}

export function getStatementSegmentAtPosition(
  line: string,
  positionCharacter: number
): StatementSegment | undefined {
  const code_end = getCodeEndCharacter(line);
  if (positionCharacter > code_end) {
    return undefined;
  }

  let segment_start = 0;
  let character_index = 0;
  let is_in_string = false;
  let paren_depth = 0;

  while (character_index < code_end) {
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
    } else if (character === ':' && line[character_index + 1] !== '=' && paren_depth === 0) {
      if (positionCharacter <= character_index) {
        return {
          start: segment_start,
          end: character_index,
          terminator: character_index,
          text: line.slice(segment_start, character_index)
        };
      }
      segment_start = character_index + 1;
    }

    character_index += 1;
  }

  return {
    start: segment_start,
    end: code_end,
    text: line.slice(segment_start, code_end)
  };
}
