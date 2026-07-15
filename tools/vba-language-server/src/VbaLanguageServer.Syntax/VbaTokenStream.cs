namespace VbaLanguageServer.Syntax;

/// <summary>
/// Contains the source-range-preserving lexical token sequence for a VBA document.
/// </summary>
/// <param name="Tokens">The tokens in source order.</param>
public sealed record VbaTokenStream(IReadOnlyList<VbaToken> Tokens)
{
    /// <summary>
    /// Tokenizes source text into a VBA token stream.
    /// </summary>
    /// <param name="source">The complete source text.</param>
    /// <returns>The token stream for the source text.</returns>
    public static VbaTokenStream FromText(string source)
        => VbaLexer.Tokenize(source);

    internal static VbaTokenStream FromSourceText(VbaSourceText sourceText)
        => VbaLexer.Tokenize(sourceText);
}
