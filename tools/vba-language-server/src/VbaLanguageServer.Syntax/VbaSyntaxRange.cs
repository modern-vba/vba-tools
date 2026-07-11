namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents a half-open source range in a VBA document.
/// </summary>
/// <param name="Start">The inclusive start position.</param>
/// <param name="End">The exclusive end position.</param>
public sealed record VbaSyntaxRange(VbaSyntaxPosition Start, VbaSyntaxPosition End);
