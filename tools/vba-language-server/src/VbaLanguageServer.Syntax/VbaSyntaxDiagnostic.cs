namespace VbaLanguageServer.Syntax;

public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaSyntaxRange Range,
    string Severity = "error",
    string Source = "vba-language-server");
