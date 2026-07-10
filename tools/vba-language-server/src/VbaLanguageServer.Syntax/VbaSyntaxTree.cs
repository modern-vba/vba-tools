namespace VbaLanguageServer.Syntax;

public sealed record VbaSyntaxTree(
    string Uri,
    string Text,
    VbaTokenStream TokenStream,
    VbaModuleSyntax Module,
    IReadOnlyList<VbaSyntaxDiagnostic> Diagnostics)
{
    public static VbaSyntaxTree ParseModule(string uri, string source)
        => VbaSyntaxTreeParser.ParseModule(uri, source);
}
