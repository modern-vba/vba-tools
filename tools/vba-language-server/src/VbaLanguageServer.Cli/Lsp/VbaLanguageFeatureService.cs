using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Creates LSP response payloads for VBA language features from workspace snapshots.
/// </summary>
internal sealed class VbaLanguageFeatureService
{
    private readonly VbaLanguageFeaturePipeline pipeline;

    /// <summary>
    /// Creates a feature service over a language workspace.
    /// </summary>
    /// <param name="workspace">The workspace used to build project snapshots.</param>
    public VbaLanguageFeatureService(VbaLanguageWorkspace workspace)
    {
        pipeline = new VbaLanguageFeaturePipeline(workspace);
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
        return request is null ? [] : pipeline.CreateDocumentSymbols(request, cancellationToken);
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
        return request is null ? null : pipeline.CreateDefinitionLocation(request, cancellationToken);
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
        return request is null ? [] : pipeline.CreateReferenceLocations(request, cancellationToken);
    }

    /// <summary>
    /// Creates workspace/symbol response items across distinct workspace snapshots.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace symbol payload objects.</returns>
    public object[] CreateWorkspaceSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        return pipeline.CreateWorkspaceSymbols(
            VbaLspRequestContext.CreateWorkspaceSymbolQuery(parameters),
            cancellationToken);
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
        return request is null ? [] : pipeline.CreateCompletionItems(request, cancellationToken);
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
        return request is null ? null : pipeline.CreateHover(request, cancellationToken);
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
        return request is null ? null : pipeline.CreateSignatureHelp(request, cancellationToken);
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
        return request is null ? null : pipeline.CreatePrepareRename(request, cancellationToken);
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
        return request is null ? null : pipeline.CreateRenameEdit(request, cancellationToken);
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
        return request is null ? [] : pipeline.CreateFormattingEdits(request, cancellationToken);
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
            : pipeline.CreateSemanticTokens(request, cancellationToken);
    }

}
