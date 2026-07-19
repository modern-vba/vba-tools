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
public sealed record VbaVersionedDocumentSnapshot(
    string Uri,
    int Version,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaModuleKind ModuleKind,
    VbaDiagnosticPipelineResult Diagnostics)
{
    public VbaSourceText SourceText { get; init; } = SyntaxTree.SourceText;

    public VbaSourceDocument SourceDocument { get; init; } =
        VbaSourceIndex.CreateDocument(Uri, SyntaxTree);

    public VbaSyntaxTreeParseUpdateKind LastParseUpdateKind { get; init; }

    public VbaModuleMemberIncrementalUpdate? LastMemberUpdate { get; init; }

    internal VbaDocumentAnalysis? Analysis { get; init; }

    internal bool IsOwnedByAnalysis
        => Analysis is { } analysis
            && Uri.Equals(analysis.Uri, StringComparison.Ordinal)
            && ReferenceEquals(Text, analysis.Text)
            && ReferenceEquals(SourceText, analysis.SourceText)
            && ReferenceEquals(SyntaxTree, analysis.SyntaxTree)
            && ModuleKind == analysis.ModuleKind
            && ReferenceEquals(SourceDocument, analysis.SourceDocument)
            && ReferenceEquals(Diagnostics, analysis.Diagnostics)
            && LastParseUpdateKind == analysis.LastParseUpdateKind
            && ReferenceEquals(LastMemberUpdate, analysis.LastMemberUpdate);

    internal static VbaVersionedDocumentSnapshot Create(
        VbaDocumentAnalysis analysis,
        int version)
        => new(
            analysis.Uri,
            version,
            analysis.Text,
            analysis.SyntaxTree,
            analysis.ModuleKind,
            analysis.Diagnostics)
        {
            SourceText = analysis.SourceText,
            SourceDocument = analysis.SourceDocument,
            LastParseUpdateKind = analysis.LastParseUpdateKind,
            LastMemberUpdate = analysis.LastMemberUpdate,
            Analysis = analysis
        };
}
