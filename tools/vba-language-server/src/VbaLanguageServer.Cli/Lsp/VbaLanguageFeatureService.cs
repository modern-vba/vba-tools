using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Creates LSP response payloads for VBA language features from workspace snapshots.
/// </summary>
internal sealed class VbaLanguageFeatureService
{
    private readonly VbaLanguageWorkspace workspace;

    /// <summary>
    /// Creates a feature service over a language workspace.
    /// </summary>
    /// <param name="workspace">The workspace used to build project snapshots.</param>
    public VbaLanguageFeatureService(VbaLanguageWorkspace workspace)
    {
        this.workspace = workspace;
    }

    /// <summary>
    /// Creates the initialize response payload with language-server capabilities.
    /// </summary>
    /// <returns>The initialize result payload.</returns>
    public static object CreateInitializeResult()
        => VbaLspFeatureProjection.CreateInitializeResult();

    /// <summary>
    /// Creates LSP diagnostic payloads by parsing source text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="text">The source text.</param>
    /// <returns>The diagnostic payload objects.</returns>
    public static object[] CreateDiagnostics(string uri, string text)
        => CreateDiagnostics(uri, VbaSyntaxTree.ParseModule(uri, text));

    /// <summary>
    /// Creates LSP diagnostic payloads from a parsed syntax tree.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="tree">The parsed syntax tree.</param>
    /// <returns>The diagnostic payload objects.</returns>
    public static object[] CreateDiagnostics(string uri, VbaSyntaxTree tree)
        => VbaLspFeatureProjection.CreateDiagnostics(VbaDocumentDiagnostics.Collect(tree, uri));

    /// <summary>
    /// Creates textDocument/documentSymbol response items.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The document symbol payload objects, or an empty array for invalid input.</returns>
    public object[] CreateDocumentSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentRequest(parameters);
        return request is null
            ? []
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateDocumentSymbols(
                    sourceIndex.GetDocumentDefinitions(request.Uri)));
    }

    /// <summary>
    /// Creates a textDocument/definition response location.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The definition location payload, or null when unresolved.</returns>
    public object? CreateDefinitionLocation(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? null
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateLocation(
                    sourceIndex.ResolveDefinition(request.Uri, request.Line, request.Character)));
    }

    /// <summary>
    /// Creates textDocument/references response locations.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The reference location payload objects, or an empty array for invalid input.</returns>
    public object[] CreateReferenceLocations(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? []
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateLocations(
                    sourceIndex.FindReferences(request.Uri, request.Line, request.Character)));
    }

    /// <summary>
    /// Creates workspace/symbol response items across distinct workspace snapshots.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace symbol payload objects.</returns>
    public object[] CreateWorkspaceSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var query = VbaLspRequestContext.CreateWorkspaceSymbolQuery(parameters);
        var symbols = workspace.CreateProjectSnapshots(cancellationToken)
            .SelectMany(snapshot => snapshot.SourceIndex.GetWorkspaceSymbols(query))
            .ToArray();
        return VbaLspFeatureProjection.CreateWorkspaceSymbols(symbols);
    }

    /// <summary>
    /// Creates textDocument/completion response items.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The completion item payload objects, or an empty array for invalid input.</returns>
    public object[] CreateCompletionItems(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? []
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateCompletionItems(
                    sourceIndex.GetCompletionResult(request.Uri, request.Line, request.Character)));
    }

    /// <summary>
    /// Creates a textDocument/hover response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The hover payload, or null when no definition resolves.</returns>
    public object? CreateHover(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? null
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateHover(
                    sourceIndex.ResolveSourceDefinition(request.Uri, request.Line, request.Character)));
    }

    /// <summary>
    /// Creates a textDocument/signatureHelp response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The signature help payload, or null when no callable resolves.</returns>
    public object? CreateSignatureHelp(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? null
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateSignatureHelp(
                    sourceIndex.GetSignatureHelp(request.Uri, request.Line, request.Character)));
    }

    /// <summary>
    /// Creates a textDocument/prepareRename response range.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The rename target range, or null when rename is unavailable.</returns>
    public object? CreatePrepareRename(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentPositionRequest(parameters);
        return request is null
            ? null
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => sourceIndex.PrepareRename(request.Uri, request.Line, request.Character));
    }

    /// <summary>
    /// Creates a textDocument/rename workspace edit response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace edit payload, or null when rename is invalid.</returns>
    public object? CreateRenameEdit(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateRenameRequest(parameters);
        return request is null
            ? null
            : WithSourceIndex(
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
    }

    /// <summary>
    /// Creates textDocument/formatting response edits.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The formatting edit payload objects, or an empty array when no edit is needed.</returns>
    public object[] CreateFormattingEdits(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateFormattingRequest(parameters);
        return request is null
            ? []
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateFormattingEdits(
                    sourceIndex.FormatDocument(request.Uri, request.TabSize)));
    }

    /// <summary>
    /// Creates a textDocument/semanticTokens/full response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The semantic tokens payload.</returns>
    public object CreateSemanticTokens(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var request = VbaLspRequestContext.CreateTextDocumentRequest(parameters);
        return request is null
            ? VbaLspFeatureProjection.CreateSemanticTokens([])
            : WithSourceIndex(
                request,
                cancellationToken,
                sourceIndex => VbaLspFeatureProjection.CreateSemanticTokens(
                    sourceIndex.GetSemanticTokenData(request.Uri)));
    }

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
