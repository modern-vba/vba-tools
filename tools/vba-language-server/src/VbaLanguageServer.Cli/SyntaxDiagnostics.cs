using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Diagnostics;

public sealed record VbaPosition(int Line, int Character);

public sealed record VbaRange(VbaPosition Start, VbaPosition End);

public sealed record VbaDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public sealed record VbaValidationDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

public static class VbaSyntaxDiagnostics
{
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(string source, string fileName)
    {
        var tree = VbaSyntaxTree.ParseModule(fileName, source);
        return VbaSyntaxDiagnosticCollector.Collect(tree, fileName);
    }
}

public static class VbaDocumentDiagnostics
{
    public static IReadOnlyList<VbaDiagnostic> Collect(string source, string uri)
    {
        var tree = VbaSyntaxTree.ParseModule(uri, source);
        return VbaSyntaxDiagnosticCollector.Collect(tree, uri)
            .Select(ToDocumentDiagnostic)
            .Concat(VbaDocumentValidationDiagnosticCollector.Collect(tree, uri).Select(ToDocumentDiagnostic))
            .ToArray();
    }

    private static VbaDiagnostic ToDocumentDiagnostic(VbaSyntaxDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);

    private static VbaDiagnostic ToDocumentDiagnostic(VbaValidationDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);
}

public static class VbaSyntaxDiagnosticCollector
{
    public static IReadOnlyList<VbaSyntaxDiagnostic> Collect(VbaSyntaxTree tree, string uri)
        => tree.Diagnostics
            .Select(diagnostic => new VbaSyntaxDiagnostic(
                diagnostic.Code,
                diagnostic.Message,
                new VbaRange(
                    new VbaPosition(diagnostic.Range.Start.Line, diagnostic.Range.Start.Character),
                    new VbaPosition(diagnostic.Range.End.Line, diagnostic.Range.End.Character)),
                diagnostic.Severity,
                diagnostic.Source))
            .ToArray();
}

public static class VbaDocumentValidationDiagnosticCollector
{
    public static IReadOnlyList<VbaValidationDiagnostic> Collect(VbaSyntaxTree tree, string uri)
        => [];
}
