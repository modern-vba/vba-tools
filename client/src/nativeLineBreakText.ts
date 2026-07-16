export function isNativeLineBreakText(text: string): boolean {
  const indentation = text.startsWith('\r\n')
    ? text.slice(2)
    : text.startsWith('\r') || text.startsWith('\n')
      ? text.slice(1)
      : undefined;
  return indentation !== undefined
    && [...indentation].every((character) => character === ' ' || character === '\t');
}

export function isPostNativeRequestVersion(
  nativeDocumentVersion: number,
  requestDocumentVersion: number
): boolean {
  return requestDocumentVersion === nativeDocumentVersion;
}
