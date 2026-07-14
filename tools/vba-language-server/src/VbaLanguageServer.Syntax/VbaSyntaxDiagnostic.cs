namespace VbaLanguageServer.Syntax;

/// <summary>
/// Represents a parser recovery or malformed-source diagnostic produced by the syntax layer.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The source range reported for the diagnostic.</param>
/// <param name="Severity">The diagnostic severity value.</param>
/// <param name="Source">The diagnostic source name.</param>
public sealed record VbaSyntaxDiagnostic(
    string Code,
    string Message,
    VbaSyntaxRange Range,
    string Severity = "error",
    string Source = "vba-language-server");
