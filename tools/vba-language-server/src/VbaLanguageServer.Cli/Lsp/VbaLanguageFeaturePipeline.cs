using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Runs decoded LSP feature requests against workspace snapshots.
/// </summary>
internal sealed class VbaLanguageFeaturePipeline
{
    private readonly VbaLanguageWorkspace workspace;

    public VbaLanguageFeaturePipeline(VbaLanguageWorkspace workspace)
    {
        this.workspace = workspace;
    }

    public object[] CreateDocumentSymbols(
        VbaTextDocumentRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateDocumentSymbols(sourceIndex.GetDocumentDefinitions(request.Uri)));

    public object? CreateDefinitionLocation(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateLocation(
                sourceIndex.ResolveDefinition(request.Uri, request.Line, request.Character)));

    public object[] CreateReferenceLocations(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateLocations(
                sourceIndex.FindReferences(request.Uri, request.Line, request.Character)));

    public object[] CreateWorkspaceSymbols(string query, CancellationToken cancellationToken)
    {
        var symbols = workspace.CreateProjectSnapshots(cancellationToken)
            .SelectMany(snapshot => snapshot.SourceIndex.GetWorkspaceSymbols(query))
            .ToArray();
        return VbaLspFeatureProjection.CreateWorkspaceSymbols(symbols);
    }

    public object[] CreateCompletionItems(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateCompletionItems(
                sourceIndex.GetCompletionResult(request.Uri, request.Line, request.Character)));

    public object? CreateHover(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateHover(
                sourceIndex.ResolveSourceDefinition(request.Uri, request.Line, request.Character)));

    public object? CreateSignatureHelp(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateSignatureHelp(
                sourceIndex.GetSignatureHelp(request.Uri, request.Line, request.Character)));

    public object? CreatePrepareRename(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => sourceIndex.PrepareRename(request.Uri, request.Line, request.Character));

    public object? CreateRenameEdit(
        VbaRenameRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex =>
            {
                var renamePlan = sourceIndex.CreateRenamePlan(
                    request.Uri,
                    request.Line,
                    request.Character,
                    request.NewName);
                return VbaLspFeatureProjection.CreateWorkspaceEdit(renamePlan?.Changes);
            });

    public object[] CreateFormattingEdits(
        VbaFormattingRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateFormattingEdits(
                sourceIndex.FormatDocument(request.Uri, request.TabSize)));

    public object CreateSemanticTokens(
        VbaTextDocumentRequest request,
        CancellationToken cancellationToken)
        => WithSourceIndex(
            request,
            cancellationToken,
            sourceIndex => VbaLspFeatureProjection.CreateSemanticTokens(sourceIndex.GetSemanticTokenData(request.Uri)));

    private TResult WithSourceIndex<TRequest, TResult>(
        TRequest request,
        CancellationToken cancellationToken,
        Func<VbaSourceIndex, TResult> createResult)
        where TRequest : IVbaTextDocumentRequest
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return createResult(snapshot.SourceIndex);
    }
}
