using System.Text.Json.Nodes;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Represents a request that targets a text document.
/// </summary>
/// <param name="Uri">The document URI.</param>
internal sealed record VbaTextDocumentRequest(string Uri);

/// <summary>
/// Represents a request that targets a text document position.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Line">The zero-based line.</param>
/// <param name="Character">The zero-based character.</param>
internal sealed record VbaTextDocumentPositionRequest(string Uri, int Line, int Character);

/// <summary>
/// Represents a textDocument/rename request.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Line">The zero-based line.</param>
/// <param name="Character">The zero-based character.</param>
/// <param name="NewName">The requested new name.</param>
internal sealed record VbaRenameRequest(string Uri, int Line, int Character, string NewName);

/// <summary>
/// Represents a textDocument/formatting request.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="TabSize">The requested tab size.</param>
internal sealed record VbaFormattingRequest(string Uri, int TabSize);

/// <summary>
/// Decodes LSP JSON request parameters into language-server request models.
/// </summary>
internal static class VbaLspRequestContext
{
    public static VbaTextDocumentRequest? CreateTextDocumentRequest(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        return string.IsNullOrEmpty(uri) ? null : new VbaTextDocumentRequest(uri);
    }

    public static VbaTextDocumentPositionRequest? CreateTextDocumentPositionRequest(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        var position = parameters?["position"];
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        return string.IsNullOrEmpty(uri) || line is null || character is null || line < 0 || character < 0
            ? null
            : new VbaTextDocumentPositionRequest(uri, line.Value, character.Value);
    }

    public static string CreateWorkspaceSymbolQuery(JsonNode? parameters)
        => parameters?["query"]?.GetValue<string>() ?? "";

    public static VbaRenameRequest? CreateRenameRequest(JsonNode? parameters)
    {
        var position = CreateTextDocumentPositionRequest(parameters);
        var newName = parameters?["newName"]?.GetValue<string>();
        return position is null || string.IsNullOrWhiteSpace(newName)
            ? null
            : new VbaRenameRequest(position.Uri, position.Line, position.Character, newName);
    }

    public static VbaFormattingRequest? CreateFormattingRequest(JsonNode? parameters)
    {
        var document = CreateTextDocumentRequest(parameters);
        if (document is null)
        {
            return null;
        }

        var tabSize = parameters?["options"]?["tabSize"]?.GetValue<int>() ?? 4;
        return new VbaFormattingRequest(document.Uri, tabSize);
    }
}
