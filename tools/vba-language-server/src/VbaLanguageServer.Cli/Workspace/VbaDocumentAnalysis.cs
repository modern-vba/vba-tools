using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Describes one document analysis build for deterministic instrumentation.
/// </summary>
/// <param name="Uri">The source URI being analyzed.</param>
/// <param name="ClientVersion">The client version, or null for disk-authoritative analysis.</param>
internal sealed record VbaDocumentAnalysisBuildContext(
    string Uri,
    int? ClientVersion);

/// <summary>
/// Observes document analysis builds without replacing the real parser or analysis pipeline.
/// </summary>
internal interface IVbaDocumentAnalysisBuildObserver
{
    void BeforeBuild(
        VbaDocumentAnalysisBuildContext context,
        CancellationToken cancellationToken);
}

internal sealed class NullVbaDocumentAnalysisBuildObserver
    : IVbaDocumentAnalysisBuildObserver
{
    public static NullVbaDocumentAnalysisBuildObserver Instance { get; } = new();

    public void BeforeBuild(
        VbaDocumentAnalysisBuildContext context,
        CancellationToken cancellationToken)
    {
    }
}

/// <summary>
/// Owns every immutable document-local artifact produced from one VBA source state.
/// </summary>
/// <param name="Uri">The canonical source URI.</param>
/// <param name="Text">The complete source text.</param>
/// <param name="ClientVersion">The client document version, or null for disk-authoritative analysis.</param>
/// <param name="SourceText">The source-coordinate model for the text.</param>
/// <param name="SyntaxTree">The syntax tree parsed from the text.</param>
/// <param name="ModuleKind">The module kind projected from the syntax tree.</param>
/// <param name="SourceDocument">The source definitions and semantic shape projected from the syntax tree.</param>
/// <param name="Diagnostics">The category-preserving document-local diagnostics.</param>
internal sealed record VbaDocumentAnalysis(
    string Uri,
    string Text,
    int? ClientVersion,
    VbaSourceText SourceText,
    VbaSyntaxTree SyntaxTree,
    VbaModuleKind ModuleKind,
    VbaSourceDocument SourceDocument,
    VbaDiagnosticPipelineResult Diagnostics)
{
    internal static VbaDocumentAnalysis Create(
        string uri,
        string text,
        VbaDocumentAnalysis? previousAnalysis,
        int? clientVersion)
    {
        var previousSyntaxTree = previousAnalysis?.SyntaxTree;
        var changeSet = VbaSyntaxTree.ParseOrUpdate(uri, text, previousSyntaxTree);
        var syntaxTree = changeSet.SyntaxTree;
        return new VbaDocumentAnalysis(
            uri,
            text,
            clientVersion,
            syntaxTree.SourceText,
            syntaxTree,
            syntaxTree.Module.Kind,
            VbaSourceDocumentProjector.Project(
                uri,
                changeSet,
                previousAnalysis?.SourceDocument),
            VbaDiagnosticPipeline.CollectDocument(syntaxTree, uri));
    }
}

/// <summary>
/// Captures a diagnostics publication candidate and the workspace revision that owns it.
/// </summary>
/// <param name="Analysis">The immutable document analysis to publish.</param>
/// <param name="ClientVersion">The client document version, or null for disk-authoritative analysis.</param>
/// <param name="LifecycleEpoch">The document lifecycle epoch that owns the analysis.</param>
/// <param name="ReservationToken">The analysis reservation token that owns the analysis.</param>
internal sealed record VbaDocumentDiagnosticsSnapshot(
    VbaDocumentAnalysis Analysis,
    int? ClientVersion,
    long LifecycleEpoch,
    long ReservationToken);

/// <summary>
/// Represents one document tracked in workspace memory or project source inventory.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The latest document text.</param>
/// <param name="SyntaxTree">The latest parsed syntax tree.</param>
/// <param name="SourceDocument">The projected source document, when already available.</param>
public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSourceDocument? SourceDocument = null);
