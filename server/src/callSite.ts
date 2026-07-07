import {
  getCodeContinuationMarkerStart,
  getCodeEndCharacter,
  getContinuedSourceTextEndingBefore,
  getLogicalSourceRange,
  getSingleLineLogicalSourceText,
  getStatementSegmentAtPosition,
  getTopLevelStatementSegments,
  hasSourceText,
  type LogicalCodeSource,
  type LogicalSourceText,
  type StatementSegment
} from './logicalSource';
import {
  parseContinuedMemberChainEndingBefore,
  parseMemberChainEndingBefore,
  parseMemberChainEndingBeforeSource,
  readParenthesisFreeCallableTargetAt,
  readParenthesisFreeCallableTargetInSource,
  toMemberChainExpression
} from './memberChainSyntax';
import type { SourcePosition } from './sourceRange';
import type { CallExpression, SyntaxDiagnostic } from './vbaSourceModel';
import {
  countTopLevelCommas,
  findMatchingParen,
  findPreviousNonWhitespace,
  getStringLiteralEnd,
  isIdentifierPart,
  readIdentifierTokenAt,
  skipWhitespace,
  splitTopLevelSegments,
  trimEndIndex
} from './vbaText';

export function getCallStatementSegments(text: string): StatementSegment[] {
  return getTopLevelStatementSegments(text);
}

export function collectMalformedCallSiteDiagnostics(source: LogicalCodeSource): SyntaxDiagnostic[] {
  const diagnostics: SyntaxDiagnostic[] = [];

  for (const segment of getCallStatementSegments(source.text)) {
    diagnostics.push(...collectMalformedCallDiagnosticsForSegment(source, segment.start, segment.end));
  }

  return diagnostics;
}

function collectMalformedCallDiagnosticsForSegment(
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

export function getCallSiteAt(
  lines: string[],
  position: SourcePosition
): CallExpression | undefined {
  const line = lines[position.line] ?? '';
  const effective_character = Math.min(position.character, line.length);
  const open_paren = findActiveCallOpenParen(line, effective_character);
  if (open_paren === undefined) {
    const continued_raise_event_call = getContinuedRaiseEventCallExpressionAt(lines, position, effective_character);
    if (
      continued_raise_event_call !== undefined
      || isContinuedRaiseEventArgumentListAt(lines, position, effective_character)
    ) {
      return continued_raise_event_call;
    }

    const continued_call_statement_call = getContinuedCallStatementCallExpressionAt(
      lines,
      position,
      effective_character
    );
    if (
      continued_call_statement_call !== undefined
      || isContinuedCallStatementArgumentListAt(lines, position, effective_character)
    ) {
      return continued_call_statement_call;
    }

    return getContinuedCallExpressionAt(lines, position, effective_character)
      ?? getContinuedParenthesisFreeCallExpressionAt(lines, position, effective_character)
      ?? getParenthesisFreeCallExpressionAt(lines, position, effective_character);
  }

  const raise_event_call = getRaiseEventCallExpressionAt(line, position.line, open_paren, effective_character);
  if (raise_event_call !== undefined || isRaiseEventArgumentListOpenParen(line, open_paren, effective_character)) {
    return raise_event_call;
  }

  const call_statement_call = getCallStatementCallExpressionAt(line, position.line, open_paren, effective_character);
  if (
    call_statement_call !== undefined
    || isCallStatementArgumentListOpenParen(line, open_paren, effective_character)
  ) {
    return call_statement_call;
  }

  const chain = parseContinuedMemberChainEndingBefore(lines, position.line, open_paren)
    ?? parseMemberChainEndingBefore(line, position.line, open_paren);
  const target_segment = chain?.segments.at(-1);
  if (target_segment === undefined) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(line.slice(open_paren + 1, effective_character)),
    namedArgumentName: getNamedArgumentNameInActiveArgument(line, open_paren + 1, effective_character),
    chain
  };
}

function getRaiseEventCallExpressionAt(
  line: string,
  lineIndex: number,
  openParen: number,
  effectiveCharacter: number
): CallExpression | undefined {
  return getRaiseEventCallExpressionInSource(
    getSingleLineLogicalSourceText(line, lineIndex, line.length),
    openParen,
    effectiveCharacter
  );
}

function getRaiseEventCallExpressionInSource(
  source: LogicalSourceText,
  openParen: number,
  effectiveCharacter: number
): CallExpression | undefined {
  const segment = getStatementSegmentAtPosition(source.text, effectiveCharacter);
  if (segment === undefined) {
    return undefined;
  }

  const segment_start = skipWhitespace(source.text, segment.start, segment.end);
  const first_token = readIdentifierTokenAt(source.text, segment_start, openParen);
  if (first_token?.lowerText !== 'raiseevent') {
    return undefined;
  }

  const event_start = skipWhitespace(source.text, first_token.end, openParen);
  const event_token = readIdentifierTokenAt(source.text, event_start, openParen);
  if (event_token === undefined || skipWhitespace(source.text, event_token.end, openParen) !== openParen) {
    return undefined;
  }

  if (findTopLevelNamedArgumentSeparator(source.text, openParen + 1, effectiveCharacter) !== undefined) {
    return undefined;
  }

  const event_range = getLogicalSourceRange(source, event_token.start, event_token.end);
  return {
    name: event_token.text,
    nameStart: event_range.start.character,
    activeParameter: countTopLevelCommas(source.text.slice(openParen + 1, effectiveCharacter)),
    eventReference: true,
    chain: toMemberChainExpression([{
      name: event_token.text,
      range: event_range,
      hasCall: false
    }], false)
  };
}

function isRaiseEventArgumentListOpenParen(
  line: string,
  openParen: number,
  effectiveCharacter: number
): boolean {
  return isRaiseEventArgumentListOpenParenInSource(
    getSingleLineLogicalSourceText(line, 0, line.length),
    openParen,
    effectiveCharacter
  );
}

function isRaiseEventArgumentListOpenParenInSource(
  source: LogicalSourceText,
  openParen: number,
  effectiveCharacter: number
): boolean {
  const segment = getStatementSegmentAtPosition(source.text, effectiveCharacter);
  if (segment === undefined) {
    return false;
  }

  const segment_start = skipWhitespace(source.text, segment.start, segment.end);
  const first_token = readIdentifierTokenAt(source.text, segment_start, openParen);
  if (first_token?.lowerText !== 'raiseevent') {
    return false;
  }

  const event_start = skipWhitespace(source.text, first_token.end, openParen);
  const event_token = readIdentifierTokenAt(source.text, event_start, openParen);
  return event_token !== undefined && skipWhitespace(source.text, event_token.end, openParen) === openParen;
}

function getContinuedRaiseEventCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  return open_paren === undefined
    ? undefined
    : getRaiseEventCallExpressionInSource(logical_source, open_paren, logical_source.text.length);
}

function isContinuedRaiseEventArgumentListAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): boolean {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return false;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  return open_paren !== undefined
    && isRaiseEventArgumentListOpenParenInSource(logical_source, open_paren, logical_source.text.length);
}

function getCallStatementCallExpressionAt(
  line: string,
  lineIndex: number,
  openParen: number,
  effectiveCharacter: number
): CallExpression | undefined {
  return getCallStatementCallExpressionInSource(
    getSingleLineLogicalSourceText(line, lineIndex, line.length),
    openParen,
    effectiveCharacter
  );
}

function getCallStatementCallExpressionInSource(
  source: LogicalSourceText,
  openParen: number,
  effectiveCharacter: number
): CallExpression | undefined {
  const segment = getStatementSegmentAtPosition(source.text, effectiveCharacter);
  if (segment === undefined) {
    return undefined;
  }

  const segment_start = skipWhitespace(source.text, segment.start, segment.end);
  const first_token = readIdentifierTokenAt(source.text, segment_start, openParen);
  if (first_token?.lowerText !== 'call') {
    return undefined;
  }

  const target_start = skipWhitespace(source.text, first_token.end, openParen);
  const chain = parseMemberChainEndingBeforeSource(source, openParen, target_start);
  const target_segment = chain?.segments.at(-1);
  if (target_segment === undefined) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(source.text.slice(openParen + 1, effectiveCharacter)),
    namedArgumentName: getNamedArgumentNameInActiveArgument(source.text, openParen + 1, effectiveCharacter),
    chain
  };
}

function isCallStatementArgumentListOpenParen(
  line: string,
  openParen: number,
  effectiveCharacter: number
): boolean {
  return isCallStatementArgumentListOpenParenInSource(
    getSingleLineLogicalSourceText(line, 0, line.length),
    openParen,
    effectiveCharacter
  );
}

function isCallStatementArgumentListOpenParenInSource(
  source: LogicalSourceText,
  openParen: number,
  effectiveCharacter: number
): boolean {
  const segment = getStatementSegmentAtPosition(source.text, effectiveCharacter);
  if (segment === undefined) {
    return false;
  }

  const segment_start = skipWhitespace(source.text, segment.start, segment.end);
  const first_token = readIdentifierTokenAt(source.text, segment_start, openParen);
  if (first_token?.lowerText !== 'call') {
    return false;
  }

  const target_start = skipWhitespace(source.text, first_token.end, openParen);
  const chain = parseMemberChainEndingBeforeSource(source, openParen, target_start);
  return chain?.segments.at(-1) !== undefined;
}

function getContinuedCallStatementCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  return open_paren === undefined
    ? undefined
    : getCallStatementCallExpressionInSource(logical_source, open_paren, logical_source.text.length);
}

function isContinuedCallStatementArgumentListAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): boolean {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return false;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  return open_paren !== undefined
    && isCallStatementArgumentListOpenParenInSource(logical_source, open_paren, logical_source.text.length);
}

function getParenthesisFreeCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const line = lines[position.line] ?? '';
  const code_end = getCodeEndCharacter(line);
  if (
    effectiveCharacter > code_end
    || getCodeContinuationMarkerStart(line) !== undefined
    || (position.line > 0 && getCodeContinuationMarkerStart(lines[position.line - 1] ?? '') !== undefined)
  ) {
    return undefined;
  }

  const segment = getStatementSegmentAtPosition(line, effectiveCharacter);
  if (segment === undefined) {
    return undefined;
  }

  const segment_start = skipWhitespace(line, segment.start, segment.end);
  const segment_end = trimEndIndex(line, segment.end);
  if (segment_start >= segment_end) {
    return undefined;
  }

  const first_token = readIdentifierTokenAt(line, segment_start, segment_end);
  if (
    first_token !== undefined
    && (
      first_token.lowerText === 'call'
      || first_token.lowerText === 'raiseevent'
      || first_token.lowerText === 'set'
      || first_token.lowerText === 'let'
      || shouldSkipCallDiagnosticsForStatement(first_token.lowerText)
    )
  ) {
    return undefined;
  }

  const target = readParenthesisFreeCallableTargetAt(line, position.line, segment_start, segment_end);
  const target_segment = target?.chain.segments.at(-1);
  if (target === undefined || target_segment === undefined || target_segment.hasCall) {
    return undefined;
  }

  const argument_start = skipWhitespace(line, target.targetEnd, segment_end);
  if (argument_start <= target.targetEnd || line[argument_start] === '=') {
    return undefined;
  }

  if (effectiveCharacter <= target.targetEnd) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(line.slice(target.targetEnd, effectiveCharacter)),
    namedArgumentName: getNamedArgumentNameInActiveArgument(line, target.targetEnd, effectiveCharacter),
    chain: target.chain
  };
}

function getContinuedParenthesisFreeCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const line = lines[position.line] ?? '';
  if (effectiveCharacter > getCodeEndCharacter(line)) {
    return undefined;
  }

  const logical_source = getContinuedParenthesisFreeCallSourceTextAt(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  const segment = getStatementSegmentAtPosition(logical_source.text, logical_source.text.length);
  if (segment === undefined) {
    return undefined;
  }

  const segment_start = skipWhitespace(logical_source.text, segment.start, segment.end);
  const segment_end = trimEndIndex(logical_source.text, segment.end);
  if (segment_start >= segment_end) {
    return undefined;
  }

  const first_token = readIdentifierTokenAt(logical_source.text, segment_start, segment_end);
  if (
    first_token !== undefined
    && (
      first_token.lowerText === 'call'
      || first_token.lowerText === 'raiseevent'
      || first_token.lowerText === 'set'
      || first_token.lowerText === 'let'
      || shouldSkipCallDiagnosticsForStatement(first_token.lowerText)
    )
  ) {
    return undefined;
  }

  const target = readParenthesisFreeCallableTargetInSource(logical_source, segment_start, segment_end);
  const target_segment = target?.chain.segments.at(-1);
  if (target === undefined || target_segment === undefined || target_segment.hasCall) {
    return undefined;
  }

  const argument_start = skipWhitespace(logical_source.text, target.targetEnd, segment_end);
  if (argument_start <= target.targetEnd || logical_source.text[argument_start] === '=') {
    return undefined;
  }

  if (logical_source.text.length <= target.targetEnd) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(logical_source.text.slice(target.targetEnd)),
    namedArgumentName: getNamedArgumentNameInActiveArgument(
      logical_source.text,
      target.targetEnd,
      logical_source.text.length
    ),
    chain: target.chain
  };
}

function getContinuedParenthesisFreeCallSourceTextAt(
  lines: string[],
  lineIndex: number,
  effectiveCharacter: number
): LogicalSourceText | undefined {
  const line = lines[lineIndex] ?? '';
  const continuation_marker = getCodeContinuationMarkerStart(line);
  if (continuation_marker !== undefined && effectiveCharacter >= continuation_marker) {
    if (!hasSourceText(lines[lineIndex + 1] ?? '')) {
      return undefined;
    }

    return getContinuedSourceTextEndingBefore(lines, lineIndex, continuation_marker)
      ?? getSingleLineLogicalSourceText(line, lineIndex, continuation_marker);
  }

  return getContinuedSourceTextEndingBefore(lines, lineIndex, effectiveCharacter);
}

function getContinuedCallExpressionAt(
  lines: string[],
  position: SourcePosition,
  effectiveCharacter: number
): CallExpression | undefined {
  const logical_source = getContinuedSourceTextEndingBefore(lines, position.line, effectiveCharacter);
  if (logical_source === undefined) {
    return undefined;
  }

  const open_paren = findActiveCallOpenParen(logical_source.text, logical_source.text.length);
  if (open_paren === undefined) {
    return undefined;
  }

  const chain = parseMemberChainEndingBeforeSource(logical_source, open_paren);
  const target_segment = chain?.segments.at(-1);
  if (target_segment === undefined) {
    return undefined;
  }

  return {
    name: target_segment.name,
    nameStart: target_segment.range.start.character,
    activeParameter: countTopLevelCommas(logical_source.text.slice(open_paren + 1)),
    namedArgumentName: getNamedArgumentNameInActiveArgument(
      logical_source.text,
      open_paren + 1,
      logical_source.text.length
    ),
    chain
  };
}

function getNamedArgumentNameInActiveArgument(
  text: string,
  argumentListStart: number,
  cursorCharacter: number
): string | undefined {
  const previous_comma = findPreviousTopLevelComma(text, argumentListStart, cursorCharacter);
  const active_argument_start = previous_comma === undefined
    ? argumentListStart
    : previous_comma + 1;
  const name_start = skipWhitespace(text, active_argument_start, cursorCharacter);
  const name_token = readIdentifierTokenAt(text, name_start, cursorCharacter);
  if (name_token === undefined) {
    return undefined;
  }

  const separator_start = skipWhitespace(text, name_token.end, cursorCharacter);
  return separator_start + 1 < cursorCharacter
    && text[separator_start] === ':'
    && text[separator_start + 1] === '='
    ? name_token.text
    : undefined;
}

function findActiveCallOpenParen(line: string, positionCharacter: number): number | undefined {
  const open_parens: number[] = [];
  let character_index = 0;
  let is_in_string = false;

  while (character_index < positionCharacter) {
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
    if (character === '(') {
      open_parens.push(character_index);
    } else if (character === ')') {
      open_parens.pop();
    }

    character_index += 1;
  }

  return open_parens.at(-1);
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
