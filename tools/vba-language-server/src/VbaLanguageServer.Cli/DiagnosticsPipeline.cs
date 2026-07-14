using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Diagnostics;

/// <summary>
/// Represents a project-wide validation diagnostic.
/// </summary>
/// <param name="Code">The stable diagnostic code.</param>
/// <param name="Message">The diagnostic message.</param>
/// <param name="Range">The diagnostic source range.</param>
/// <param name="Severity">The diagnostic severity value.</param>
/// <param name="Source">The diagnostic source name.</param>
public sealed record VbaProjectValidationDiagnostic(
    string Code,
    string Message,
    VbaRange Range,
    string Severity = "error",
    string Source = "vba-language-server");

/// <summary>
/// Represents category-preserving diagnostics collected for publication.
/// </summary>
/// <param name="SyntaxDiagnostics">Parser recovery and malformed-source diagnostics.</param>
/// <param name="DocumentValidationDiagnostics">Document-local parsed-source validation diagnostics.</param>
/// <param name="ProjectValidationDiagnostics">Project-aware validation diagnostics.</param>
public sealed record VbaDiagnosticPipelineResult(
    IReadOnlyList<VbaSyntaxDiagnostic> SyntaxDiagnostics,
    IReadOnlyList<VbaValidationDiagnostic> DocumentValidationDiagnostics,
    IReadOnlyList<VbaProjectValidationDiagnostic> ProjectValidationDiagnostics)
{
    /// <summary>
    /// Gets diagnostics projected into the legacy publishable diagnostic shape.
    /// </summary>
    public IReadOnlyList<VbaDiagnostic> Diagnostics
        => SyntaxDiagnostics
            .Select(ToDocumentDiagnostic)
            .Concat(DocumentValidationDiagnostics.Select(ToDocumentDiagnostic))
            .Concat(ProjectValidationDiagnostics.Select(ToDocumentDiagnostic))
            .ToArray();

    private static VbaDiagnostic ToDocumentDiagnostic(VbaSyntaxDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);

    private static VbaDiagnostic ToDocumentDiagnostic(VbaValidationDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);

    private static VbaDiagnostic ToDocumentDiagnostic(VbaProjectValidationDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.Range, diagnostic.Severity, diagnostic.Source);
}

/// <summary>
/// Collects diagnostics by category and projects them only at the LSP boundary.
/// </summary>
public static class VbaDiagnosticPipeline
{
    /// <summary>
    /// Collects diagnostics that depend only on one parsed document.
    /// </summary>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <param name="uri">The document URI.</param>
    /// <returns>The category-preserving diagnostic result.</returns>
    public static VbaDiagnosticPipelineResult CollectDocument(VbaSyntaxTree tree, string uri)
        => Collect(tree, uri, []);

    /// <summary>
    /// Collects document diagnostics plus project-aware diagnostics supplied by a snapshot validator.
    /// </summary>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <param name="uri">The document URI.</param>
    /// <param name="projectValidationDiagnostics">Project-aware validation diagnostics.</param>
    /// <returns>The category-preserving diagnostic result.</returns>
    public static VbaDiagnosticPipelineResult Collect(
        VbaSyntaxTree tree,
        string uri,
        IReadOnlyList<VbaProjectValidationDiagnostic> projectValidationDiagnostics)
        => new(
            VbaSyntaxDiagnosticCollector.Collect(tree, uri),
            VbaDocumentValidationDiagnosticCollector.Collect(tree, uri),
            projectValidationDiagnostics);
}
