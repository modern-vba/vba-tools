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
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return VbaLspFeatureProjection.CreateDocumentSymbols(snapshot.SourceIndex.GetDocumentDefinitions(request.Uri));
    }

    public object? CreateDefinitionLocation(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return VbaLspFeatureProjection.CreateLocation(
            snapshot.SourceIndex.ResolveDefinition(request.Uri, request.Line, request.Character));
    }

    public object[] CreateReferenceLocations(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return VbaLspFeatureProjection.CreateLocations(
            snapshot.SourceIndex.FindReferences(request.Uri, request.Line, request.Character));
    }

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
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        var completion = snapshot.SourceIndex.GetCompletionResult(request.Uri, request.Line, request.Character);
        return VbaLspFeatureProjection.CreateCompletionItems(completion);
    }

    public object? CreateHover(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveSourceDefinition(request.Uri, request.Line, request.Character);
        return VbaLspFeatureProjection.CreateHover(definition);
    }

    public object? CreateSignatureHelp(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        var signatureHelp = snapshot.SourceIndex.GetSignatureHelp(request.Uri, request.Line, request.Character);
        return VbaLspFeatureProjection.CreateSignatureHelp(signatureHelp);
    }

    public object? CreatePrepareRename(
        VbaTextDocumentPositionRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return snapshot.SourceIndex.PrepareRename(request.Uri, request.Line, request.Character);
    }

    public object? CreateRenameEdit(
        VbaRenameRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        var renamePlan = snapshot.SourceIndex.CreateRenamePlan(
            request.Uri,
            request.Line,
            request.Character,
            request.NewName);
        return VbaLspFeatureProjection.CreateWorkspaceEdit(renamePlan?.Changes);
    }

    public object[] CreateFormattingEdits(
        VbaFormattingRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        var edit = snapshot.SourceIndex.FormatDocument(request.Uri, request.TabSize);
        return VbaLspFeatureProjection.CreateFormattingEdits(edit);
    }

    public object CreateSemanticTokens(
        VbaTextDocumentRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return VbaLspFeatureProjection.CreateSemanticTokens(snapshot.SourceIndex.GetSemanticTokenData(request.Uri));
    }
}
