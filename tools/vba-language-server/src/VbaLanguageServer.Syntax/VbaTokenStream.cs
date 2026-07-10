namespace VbaLanguageServer.Syntax;

public sealed record VbaTokenStream(IReadOnlyList<VbaToken> Tokens)
{
    public static VbaTokenStream FromText(string source)
        => VbaLexer.Tokenize(source);
}
