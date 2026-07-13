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
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return VbaLspFeatureProjection.CreateDocumentSymbols(snapshot.SourceIndex.GetDocumentDefinitions(uri));
    }

    /// <summary>
    /// Creates a textDocument/definition response location.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The definition location payload, or null when unresolved.</returns>
    public object? CreateDefinitionLocation(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        var position = parameters?["position"];
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        if (string.IsNullOrEmpty(uri) || line is null || character is null)
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return VbaLspFeatureProjection.CreateLocation(
            snapshot.SourceIndex.ResolveDefinition(uri, line.Value, character.Value));
    }

    /// <summary>
    /// Creates textDocument/references response locations.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The reference location payload objects, or an empty array for invalid input.</returns>
    public object[] CreateReferenceLocations(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return VbaLspFeatureProjection.CreateLocations(snapshot.SourceIndex.FindReferences(uri, line, character));
    }

    /// <summary>
    /// Creates workspace/symbol response items across distinct workspace snapshots.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace symbol payload objects.</returns>
    public object[] CreateWorkspaceSymbols(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var query = parameters?["query"]?.GetValue<string>() ?? "";
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
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return [];
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var completion = snapshot.SourceIndex.GetCompletionResult(uri, line, character);
        return VbaLspFeatureProjection.CreateCompletionItems(completion);
    }

    /// <summary>
    /// Creates a textDocument/hover response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The hover payload, or null when no definition resolves.</returns>
    public object? CreateHover(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveSourceDefinition(uri, line, character);
        return VbaLspFeatureProjection.CreateHover(definition);
    }

    /// <summary>
    /// Creates a textDocument/signatureHelp response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The signature help payload, or null when no callable resolves.</returns>
    public object? CreateSignatureHelp(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var signatureHelp = snapshot.SourceIndex.GetSignatureHelp(uri, line, character);
        return VbaLspFeatureProjection.CreateSignatureHelp(signatureHelp);
    }

    /// <summary>
    /// Creates a textDocument/prepareRename response range.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The rename target range, or null when rename is unavailable.</returns>
    public object? CreatePrepareRename(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var definition = snapshot.SourceIndex.ResolveSourceDefinition(uri, line, character);
        return definition is null ? null : definition.Range;
    }

    /// <summary>
    /// Creates a textDocument/rename workspace edit response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The workspace edit payload, or null when rename is invalid.</returns>
    public object? CreateRenameEdit(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        if (!TryGetTextDocumentPosition(parameters, out var uri, out var line, out var character))
        {
            return null;
        }

        var newName = parameters?["newName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return null;
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var changes = snapshot.SourceIndex.CreateRenameChanges(uri, line, character, newName);
        return VbaLspFeatureProjection.CreateWorkspaceEdit(changes);
    }

    /// <summary>
    /// Creates textDocument/formatting response edits.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The formatting edit payload objects, or an empty array when no edit is needed.</returns>
    public object[] CreateFormattingEdits(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return [];
        }

        var tabSize = parameters?["options"]?["tabSize"]?.GetValue<int>() ?? 4;
        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        var edit = snapshot.SourceIndex.FormatDocument(uri, tabSize);
        return VbaLspFeatureProjection.CreateFormattingEdits(edit);
    }

    /// <summary>
    /// Creates a textDocument/semanticTokens/full response.
    /// </summary>
    /// <param name="parameters">The LSP request parameters.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The semantic tokens payload.</returns>
    public object CreateSemanticTokens(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return VbaLspFeatureProjection.CreateSemanticTokens([]);
        }

        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        return VbaLspFeatureProjection.CreateSemanticTokens(snapshot.SourceIndex.GetSemanticTokenData(uri));
    }

    private static bool TryGetTextDocumentPosition(
        JsonNode? parameters,
        out string uri,
        out int line,
        out int character)
    {
        uri = parameters?["textDocument"]?["uri"]?.GetValue<string>() ?? "";
        line = parameters?["position"]?["line"]?.GetValue<int>() ?? -1;
        character = parameters?["position"]?["character"]?.GetValue<int>() ?? -1;
        return !string.IsNullOrEmpty(uri) && line >= 0 && character >= 0;
    }

}
