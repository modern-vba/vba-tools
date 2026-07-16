using VbaLanguageServer.Diagnostics;
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
    internal static VbaVersionedDocumentSnapshot Create(
        VbaTrackedDocument document,
        int version)
        => new(
            document.Uri,
            version,
            document.Text,
            document.SyntaxTree,
            document.SyntaxTree.Module.Kind,
            VbaDiagnosticPipeline.CollectDocument(document.SyntaxTree, document.Uri));
}
