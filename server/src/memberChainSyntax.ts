import {
  getContinuedSourceTextEndingBefore,
  getLogicalSourceRange,
  type LogicalSourceText
} from './logicalSource';
import type { SourceRange } from './sourceRange';
import { getIdentifierRangesInCode } from './vbaIdentifierSource';
import {
  findMatchingParen,
  findPreviousNonWhitespace,
  readIdentifierAt,
  skipWhitespace
} from './vbaText';
import type {
  MemberChainExpression,
  MemberChainSegment
} from './vbaSourceModel';

export interface ParsedMemberChain {
  segments: MemberChainSegment[];
  endIndex: number;
}

export interface ParenthesisFreeCallableTarget {
  chain: MemberChainExpression;
  targetEnd: number;
}

export function parseMemberChainEndingAt(
  line: string,
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  return parseMemberChainEndingBefore(line, lineIndex, endCharacter);
}

export function parseMemberChainEndingBefore(
  line: string,
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(line, endCharacter - 1);
  if (expression_end === undefined) {
    return undefined;
  }

  const end_index = expression_end + 1;
  const candidates: ParsedMemberChain[] = [];
  for (const range of getIdentifierRangesInCode(line, lineIndex)) {
    if (range.start.character >= end_index) {
      continue;
    }

    const candidate = parseMemberChainFrom(line, lineIndex, range.start.character, end_index);
    if (candidate !== undefined && candidate.endIndex === end_index) {
      candidates.push(candidate);
    }
  }

  const selected = candidates.sort((left, right) =>
    right.segments.length - left.segments.length
      || left.segments[0].range.start.character - right.segments[0].range.start.character
  )[0];
  return selected === undefined
    ? undefined
    : {
        segments: selected.segments,
        targetSegmentIndex: selected.segments.length - 1,
        usesWithReceiver: isLeadingDotChain(line, selected.segments[0].range.start.character)
      };
}

export function parseMemberChainFrom(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number,
  getRange: (start: number, end: number) => SourceRange = (start, end) => ({
    start: { line: lineIndex, character: start },
    end: { line: lineIndex, character: end }
  })
): ParsedMemberChain | undefined {
  const segments: MemberChainSegment[] = [];
  let character_index = startCharacter;

  while (character_index < endCharacter) {
    character_index = skipWhitespace(line, character_index, endCharacter);
    const identifier = readIdentifierAt(line, character_index);
    if (identifier === undefined) {
      return undefined;
    }

    character_index = identifier.end;
    character_index = skipWhitespace(line, character_index, endCharacter);
    let has_call = false;
    if (character_index < endCharacter && line[character_index] === '(') {
      const close_paren = findMatchingParen(line, character_index, endCharacter);
      if (close_paren === undefined) {
        return undefined;
      }

      has_call = true;
      character_index = close_paren + 1;
      character_index = skipWhitespace(line, character_index, endCharacter);
    }

    segments.push({
      name: identifier.name,
      range: getRange(identifier.start, identifier.end),
      hasCall: has_call
    });

    if (character_index >= endCharacter || line[character_index] !== '.') {
      break;
    }

    character_index += 1;
  }

  const end_index = skipWhitespace(line, character_index, endCharacter);
  return segments.length === 0 ? undefined : { segments, endIndex: end_index };
}

export function parseMemberChainEndingBeforeSource(
  source: LogicalSourceText,
  endCharacter: number,
  startCharacter = 0
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(source.text, endCharacter - 1);
  if (expression_end === undefined) {
    return undefined;
  }

  const end_index = expression_end + 1;
  const candidates: Array<ParsedMemberChain & { startIndex: number }> = [];
  for (const range of getIdentifierRangesInCode(source.text, source.positions[0]?.line ?? 0)) {
    if (range.start.character < startCharacter || range.start.character >= end_index) {
      continue;
    }

    const candidate = parseMemberChainFrom(
      source.text,
      source.positions[range.start.character]?.line ?? 0,
      range.start.character,
      end_index,
      (start, end) => getLogicalSourceRange(source, start, end)
    );
    if (candidate !== undefined && candidate.endIndex === end_index) {
      candidates.push({
        ...candidate,
        startIndex: range.start.character
      });
    }
  }

  const selected = candidates.sort((left, right) =>
    right.segments.length - left.segments.length
      || left.startIndex - right.startIndex
  )[0];
  return selected === undefined
    ? undefined
    : {
        segments: selected.segments,
        targetSegmentIndex: selected.segments.length - 1,
        usesWithReceiver: isLeadingDotChainInRange(source.text, selected.startIndex, startCharacter)
      };
}

export function parseContinuedMemberChainEndingBefore(
  lines: string[],
  lineIndex: number,
  endCharacter: number
): MemberChainExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, lineIndex, endCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  return parseMemberChainEndingBeforeSource(logical_source, logical_source.text.length);
}

export function readParenthesisFreeCallableTargetAt(
  line: string,
  lineIndex: number,
  startCharacter: number,
  endCharacter: number
): ParenthesisFreeCallableTarget | undefined {
  return readParenthesisFreeCallableTarget(
    line,
    startCharacter,
    endCharacter,
    (start, end) => ({
      start: { line: lineIndex, character: start },
      end: { line: lineIndex, character: end }
    })
  );
}

export function readParenthesisFreeCallableTargetInSource(
  source: LogicalSourceText,
  startCharacter: number,
  endCharacter: number
): ParenthesisFreeCallableTarget | undefined {
  return readParenthesisFreeCallableTarget(
    source.text,
    startCharacter,
    endCharacter,
    (start, end) => getLogicalSourceRange(source, start, end)
  );
}

export function toMemberChainExpression(
  segments: MemberChainSegment[],
  usesWithReceiver: boolean
): MemberChainExpression {
  return {
    segments,
    targetSegmentIndex: segments.length - 1,
    usesWithReceiver
  };
}

function readParenthesisFreeCallableTarget(
  text: string,
  startCharacter: number,
  endCharacter: number,
  getRange: (start: number, end: number) => SourceRange
): ParenthesisFreeCallableTarget | undefined {
  let character_index = skipWhitespace(text, startCharacter, endCharacter);
  let uses_with_receiver = false;
  if (text[character_index] === '.') {
    uses_with_receiver = true;
    character_index = skipWhitespace(text, character_index + 1, endCharacter);
  }

  const segments: MemberChainSegment[] = [];
  while (character_index < endCharacter) {
    character_index = skipWhitespace(text, character_index, endCharacter);
    const identifier = readIdentifierAt(text, character_index);
    if (identifier === undefined || identifier.end > endCharacter) {
      return undefined;
    }

    const segment: MemberChainSegment = {
      name: identifier.name,
      range: getRange(identifier.start, identifier.end),
      hasCall: false
    };

    const after_identifier = identifier.end;
    const next_code = skipWhitespace(text, after_identifier, endCharacter);
    if (text[next_code] === '(') {
      const close_paren = findMatchingParen(text, next_code, endCharacter);
      if (close_paren === undefined) {
        return undefined;
      }

      const after_call = skipWhitespace(text, close_paren + 1, endCharacter);
      if (text[after_call] === '.') {
        segment.hasCall = true;
        segments.push(segment);
        character_index = after_call + 1;
        continue;
      }

      if (next_code === after_identifier) {
        segment.hasCall = true;
      }
      segments.push(segment);
      return {
        chain: toMemberChainExpression(segments, uses_with_receiver),
        targetEnd: after_identifier
      };
    }

    segments.push(segment);
    if (text[next_code] !== '.') {
      return {
        chain: toMemberChainExpression(segments, uses_with_receiver),
        targetEnd: after_identifier
      };
    }

    character_index = next_code + 1;
  }

  return undefined;
}

function isLeadingDotChain(line: string, firstSegmentStart: number): boolean {
  const dot_index = findPreviousNonWhitespace(line, firstSegmentStart - 1);
  return dot_index !== undefined
    && line[dot_index] === '.'
    && findPreviousNonWhitespace(line, dot_index - 1) === undefined;
}

function isLeadingDotChainInRange(
  line: string,
  firstSegmentStart: number,
  rangeStart: number
): boolean {
  const dot_index = findPreviousNonWhitespace(line, firstSegmentStart - 1);
  if (dot_index === undefined || line[dot_index] !== '.') {
    return false;
  }

  const previous_code = findPreviousNonWhitespace(line, dot_index - 1);
  return previous_code === undefined || previous_code < rangeStart;
}
