namespace VbaTools.Semantics;

internal sealed record VbaSemanticDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    IReadOnlyList<VbaDiagnosticDetail>? Details = null);
