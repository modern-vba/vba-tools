import {
  getCodeContinuationMarkerStart,
  getCodeEndCharacter,
  getLogicalSourceRange,
  hasCommentContinuationMarker,
  type LogicalSourceText
} from './logicalSource';
import { parseMemberChainFrom } from './memberChainSyntax';
import type { SourcePosition } from './sourceRange';
import {
  findPreviousNonWhitespace,
  getCodeTextForStructure,
  skipWhitespace
} from './vbaText';
import type {
  MemberChainExpression,
  WithReceiverDeclaration,
  WithReceiverSourceText
} from './vbaSourceModel';

export function getWithReceiverDeclarationAt(
  lines: string[],
  lineIndex: number
): WithReceiverDeclaration | undefined {
  const line = lines[lineIndex] ?? '';
  const code_text = getCodeTextForStructure(line);
  const with_match = /^\s*With\b/i.exec(code_text);
  if (with_match === null) {
    return undefined;
  }

  const first_line_end = getCodeContinuationMarkerStart(line) ?? getCodeEndCharacter(line);
  const receiver_source = getWithReceiverSourceText(lines, lineIndex, with_match[0].length);
  if (receiver_source === undefined) {
    return {
      end: { line: lineIndex, character: first_line_end }
    };
  }
  if (receiver_source.hasCommentContinuation) {
    return {
      end: {
        line: receiver_source.endLine,
        character: receiver_source.endCharacter
      }
    };
  }

  const receiver_chain = getWithReceiverChainFromSource(receiver_source);
  return {
    chain: receiver_chain,
    end: {
      line: receiver_source.endLine,
      character: receiver_source.endCharacter
    }
  };
}

function getWithReceiverSourceText(
  lines: string[],
  lineIndex: number,
  receiverStart: number
): WithReceiverSourceText | undefined {
  const text_parts: string[] = [];
  const positions: SourcePosition[] = [];
  let has_comment_continuation = false;

  for (let current_line_index = lineIndex; current_line_index < lines.length; current_line_index += 1) {
    const line = lines[current_line_index] ?? '';
    const line_start = current_line_index === lineIndex ? receiverStart : 0;
    const continuation_marker = getCodeContinuationMarkerStart(line);
    const line_end = continuation_marker ?? getCodeEndCharacter(line);
    has_comment_continuation = has_comment_continuation || hasCommentContinuationMarker(line);

    text_parts.push(line.slice(line_start, line_end));
    for (let character = line_start; character < line_end; character += 1) {
      positions.push({ line: current_line_index, character });
    }

    if (continuation_marker === undefined) {
      return {
        text: text_parts.join(''),
        positions,
        endLine: current_line_index,
        endCharacter: line_end,
        hasCommentContinuation: has_comment_continuation
      };
    }
  }

  return undefined;
}

function getWithReceiverChainFromSource(
  source: LogicalSourceText
): MemberChainExpression | undefined {
  const expression_end = findPreviousNonWhitespace(source.text, source.text.length - 1);
  if (expression_end === undefined) {
    return undefined;
  }
  if (source.text[expression_end] === '.') {
    return undefined;
  }

  const code_end = expression_end + 1;
  let receiver_start = skipWhitespace(source.text, 0, code_end);
  let uses_with_receiver = false;
  if (source.text[receiver_start] === '.') {
    uses_with_receiver = true;
    receiver_start = skipWhitespace(source.text, receiver_start + 1, code_end);
  }

  const receiver_chain = parseMemberChainFrom(
    source.text,
    source.positions[receiver_start]?.line ?? 0,
    receiver_start,
    code_end,
    (start, end) => getLogicalSourceRange(source, start, end)
  );
  if (receiver_chain === undefined || receiver_chain.endIndex !== code_end) {
    return undefined;
  }

  return {
    segments: receiver_chain.segments,
    targetSegmentIndex: receiver_chain.segments.length - 1,
    usesWithReceiver: uses_with_receiver
  };
}
