export interface SourcePosition {
  line: number;
  character: number;
}

export interface SourceRange {
  start: SourcePosition;
  end: SourcePosition;
}

export function containsPosition(range: SourceRange, position: SourcePosition): boolean {
  return comparePosition(range.start, position) <= 0
    && comparePosition(position, range.end) <= 0;
}

export function containsRange(outerRange: SourceRange, innerRange: SourceRange): boolean {
  return containsPosition(outerRange, innerRange.start)
    && containsPosition(outerRange, innerRange.end);
}

export function comparePosition(left: SourcePosition, right: SourcePosition): number {
  if (left.line !== right.line) {
    return left.line - right.line;
  }

  return left.character - right.character;
}

export function sameRange(left: SourceRange, right: SourceRange): boolean {
  return comparePosition(left.start, right.start) === 0
    && comparePosition(left.end, right.end) === 0;
}
