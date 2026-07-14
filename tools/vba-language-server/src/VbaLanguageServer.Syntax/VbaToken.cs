namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents one lexical token produced from VBA source text.
/// </summary>
/// <param name="Kind">The token kind.</param>
/// <param name="Text">The exact token source text.</param>
/// <param name="Range">The source range covered by the token.</param>
public sealed record VbaToken(VbaTokenKind Kind, string Text, VbaSyntaxRange Range);
