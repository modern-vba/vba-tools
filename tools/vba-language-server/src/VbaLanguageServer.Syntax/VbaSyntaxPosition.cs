namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents a zero-based source position with both line/character and absolute source offset.
/// </summary>
/// <param name="Line">The zero-based physical line number.</param>
/// <param name="Character">The zero-based character index within the line.</param>
/// <param name="Offset">The zero-based offset within the complete source string.</param>
public sealed record VbaSyntaxPosition(int Line, int Character, int Offset);
