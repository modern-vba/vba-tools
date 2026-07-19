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
/// <param name="SourceText">The source-coordinate model for the text.</param>
/// <param name="SyntaxTree">The syntax tree parsed from the text.</param>
/// <param name="ModuleKind">The module kind projected from the syntax tree.</param>
/// <param name="SourceDocument">The source definitions and semantic shape projected from the syntax tree.</param>
/// <param name="Diagnostics">The category-preserving document-local diagnostics.</param>
/// <param name="LastParseUpdateKind">The parse granularity used to create the analysis.</param>
/// <param name="LastMemberUpdate">The safe ModuleMember update metadata, when available.</param>
internal sealed record VbaDocumentAnalysis(
    string Uri,
    string Text,
    VbaSourceText SourceText,
    VbaSyntaxTree SyntaxTree,
    VbaModuleKind ModuleKind,
    VbaSourceDocument SourceDocument,
    VbaDiagnosticPipelineResult Diagnostics,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind,
    VbaModuleMemberIncrementalUpdate? LastMemberUpdate)
{
    internal static VbaDocumentAnalysis Create(
        string uri,
        string text,
        VbaDocumentAnalysis? previousAnalysis)
    {
        var previousSyntaxTree = previousAnalysis?.SyntaxTree;
        var parseResult = VbaSyntaxTree.ParseOrUpdate(uri, text, previousSyntaxTree);
        var syntaxTree = parseResult.SyntaxTree;
        return new VbaDocumentAnalysis(
            uri,
            text,
            syntaxTree.SourceText,
            syntaxTree,
            syntaxTree.Module.Kind,
            VbaSourceIndex.CreateDocument(
                uri,
                syntaxTree,
                previousSyntaxTree,
                previousAnalysis?.SourceDocument,
                parseResult.MemberUpdate),
            VbaDiagnosticPipeline.CollectDocument(syntaxTree, uri),
            parseResult.UpdateKind,
            parseResult.MemberUpdate);
    }
}

/// <summary>
/// Represents one document tracked in workspace memory or project source inventory.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The latest document text.</param>
/// <param name="SyntaxTree">The latest parsed syntax tree.</param>
/// <param name="LastParseUpdateKind">The last parse update granularity.</param>
/// <param name="LastMemberUpdate">The last safe ModuleMember update plan.</param>
/// <param name="SourceDocument">The projected source document, when already available.</param>
public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind,
    VbaModuleMemberIncrementalUpdate? LastMemberUpdate = null,
    VbaSourceDocument? SourceDocument = null);
