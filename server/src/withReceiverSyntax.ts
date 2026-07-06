import {
  getCodeContinuationMarkerStart,
  getCodeEndCharacter,
  getLogicalSourceRange,
  getLogicalSourceSpanFromLine,
  type LogicalSourceText
} from './logicalSource';
import { parseMemberChainFrom } from './memberChainSyntax';
import {
  findPreviousNonWhitespace,
  getCodeTextForStructure,
  skipWhitespace
} from './vbaText';
import type {
  MemberChainExpression,
  WithReceiverDeclaration
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
  const receiver_source = getLogicalSourceSpanFromLine(lines, lineIndex, with_match[0].length);
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
