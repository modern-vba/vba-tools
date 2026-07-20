using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents one immutable, exact-version open document state for latency-sensitive editor features.
/// </summary>
/// <param name="Uri">The canonical tracked document URI.</param>
/// <param name="Version">The client document version captured with the state.</param>
/// <param name="Text">The complete open-buffer text.</param>
/// <param name="SyntaxTree">The syntax tree parsed from the same text.</param>
/// <param name="ModuleKind">The module kind from the same syntax tree.</param>
/// <param name="Diagnostics">The document-local diagnostics derived from the same syntax tree.</param>
/// <param name="SourceDocument">The source projection owned by the same document analysis.</param>
public sealed record VbaVersionedDocumentSnapshot(
    string Uri,
    int Version,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaModuleKind ModuleKind,
    VbaDiagnosticPipelineResult Diagnostics,
    VbaSourceDocument SourceDocument)
{
    public VbaSourceText SourceText { get; init; } = SyntaxTree.SourceText;

    internal VbaDocumentAnalysis? Analysis { get; init; }

    internal bool IsOwnedByAnalysis
        => Analysis is { } analysis
            && Version == analysis.ClientVersion
            && Uri.Equals(analysis.Uri, StringComparison.Ordinal)
            && ReferenceEquals(Text, analysis.Text)
            && ReferenceEquals(SourceText, analysis.SourceText)
            && ReferenceEquals(SyntaxTree, analysis.SyntaxTree)
            && ModuleKind == analysis.ModuleKind
            && ReferenceEquals(SourceDocument, analysis.SourceDocument)
            && ReferenceEquals(Diagnostics, analysis.Diagnostics);

    internal static VbaVersionedDocumentSnapshot Create(VbaDocumentAnalysis analysis)
    {
        var version = analysis.ClientVersion
            ?? throw new ArgumentException(
                "An exact document snapshot requires a client-owned analysis version.",
                nameof(analysis));

        return new(
            analysis.Uri,
            version,
            analysis.Text,
            analysis.SyntaxTree,
            analysis.ModuleKind,
            analysis.Diagnostics,
            analysis.SourceDocument)
        {
            SourceText = analysis.SourceText,
            Analysis = analysis
        };
    }
}
