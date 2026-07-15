using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Validates, executes, and responds to one JSON-RPC language-server request.
/// </summary>
internal sealed class VbaLspRequestExecution
{
    private static readonly VbaLspCapabilityContract CapabilityContract = new(
        TextDocumentSync: 1,
        DefinitionProvider: true,
        ReferencesProvider: true,
        DocumentSymbolProvider: true,
        WorkspaceSymbolProvider: true,
        HoverProvider: true,
        DocumentFormattingProvider: true,
        RenamePrepareProvider: true,
        SignatureHelpTriggerCharacters: ["(", ",", " "],
        CompletionTriggerCharacters: [".", " "],
        SemanticTokenTypes: VbaSourceIndex.SemanticTokenTypes,
        SemanticTokenModifiers: VbaSourceIndex.SemanticTokenModifiers,
        SemanticTokensFull: true,
        SemanticTokensRange: false,
        ServerName: "vba-language-server",
        ServerVersion: "0.1.0");

    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;

    /// <summary>
    /// Creates a request executor over the transport and workspace boundaries.
    /// </summary>
    /// <param name="transport">The transport used to write the request response.</param>
    /// <param name="workspace">The workspace used to create language feature snapshots.</param>
    public VbaLspRequestExecution(LspMessageTransport transport, VbaLanguageWorkspace workspace)
    {
        this.transport = transport;
        this.workspace = workspace;
    }

    /// <summary>
    /// Gets whether a valid shutdown request has been handled.
    /// </summary>
    public bool ShutdownRequested { get; private set; }

    /// <summary>
    /// Executes one request and writes exactly one success or error response.
    /// </summary>
    /// <param name="request">The JSON-RPC request object.</param>
    /// <param name="cancellationToken">A cancellation token for request execution and response writing.</param>
    public async Task ExecuteAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = GetResponseId(request);
        RequestOutcome outcome;
        if (!TryDecodeEnvelope(request, out var method, out var parameters))
        {
            outcome = RequestOutcome.Error(-32600, "Invalid Request");
        }
        else
        {
            try
            {
                outcome = ExecuteRequest(method, parameters, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                outcome = RequestOutcome.Error(-32603, "Internal error");
            }
        }

        if (outcome.ErrorCode is int errorCode)
        {
            await transport.WriteErrorResponseAsync(
                id,
                errorCode,
                outcome.ErrorMessage!,
                cancellationToken);
            return;
        }

        await transport.WriteResponseAsync(id, outcome.Result, cancellationToken);
    }

    private RequestOutcome ExecuteRequest(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                return parameters is JsonObject
                    ? RequestOutcome.Success(VbaLspFeatureProjection.CreateInitializeResult(CapabilityContract))
                    : RequestOutcome.InvalidParams();
            case "shutdown":
                if (parameters is not null)
                {
                    return RequestOutcome.InvalidParams();
                }

                ShutdownRequested = true;
                return RequestOutcome.Success(null);
            case "textDocument/completion":
                return TryCreatePositionRequest(parameters, out var completionRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        completionRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateCompletionItems(
                            sourceIndex.GetCompletionResult(
                                completionRequest.Uri,
                                completionRequest.Line,
                                completionRequest.Character))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/documentSymbol":
                return TryCreateTextDocumentRequest(parameters, out var documentSymbolRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        documentSymbolRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateDocumentSymbols(
                            sourceIndex.GetDocumentDefinitions(documentSymbolRequest.Uri))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/definition":
                return TryCreatePositionRequest(parameters, out var definitionRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        definitionRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateLocation(
                            sourceIndex.ResolveDefinition(
                                definitionRequest.Uri,
                                definitionRequest.Line,
                                definitionRequest.Character))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/references":
                return TryCreatePositionRequest(parameters, out var referencesRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        referencesRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateLocations(
                            sourceIndex.FindReferences(
                                referencesRequest.Uri,
                                referencesRequest.Line,
                                referencesRequest.Character))))
                    : RequestOutcome.InvalidParams();
            case "workspace/symbol":
                if (!TryCreateWorkspaceSymbolQuery(parameters, out var query))
                {
                    return RequestOutcome.InvalidParams();
                }

                var symbols = workspace.CreateProjectSnapshots(cancellationToken)
                    .SelectMany(snapshot => snapshot.SourceIndex.GetWorkspaceSymbols(query))
                    .ToArray();
                return RequestOutcome.Success(VbaLspFeatureProjection.CreateWorkspaceSymbols(symbols));
            case "textDocument/hover":
                return TryCreatePositionRequest(parameters, out var hoverRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        hoverRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateHover(
                            sourceIndex.ResolveSourceDefinition(
                                hoverRequest.Uri,
                                hoverRequest.Line,
                                hoverRequest.Character))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/signatureHelp":
                return TryCreatePositionRequest(parameters, out var signatureRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        signatureRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateSignatureHelp(
                            sourceIndex.GetSignatureHelp(
                                signatureRequest.Uri,
                                signatureRequest.Line,
                                signatureRequest.Character))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/prepareRename":
                return TryCreatePositionRequest(parameters, out var prepareRenameRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        prepareRenameRequest,
                        cancellationToken,
                        sourceIndex => sourceIndex.PrepareRename(
                            prepareRenameRequest.Uri,
                            prepareRenameRequest.Line,
                            prepareRenameRequest.Character)))
                    : RequestOutcome.InvalidParams();
            case "textDocument/rename":
                return TryCreateRenameRequest(parameters, out var renameRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        renameRequest,
                        cancellationToken,
                        sourceIndex =>
                        {
                            var renamePlan = sourceIndex.CreateRenamePlan(
                                renameRequest.Uri,
                                renameRequest.Line,
                                renameRequest.Character,
                                renameRequest.NewName);
                            return VbaLspFeatureProjection.CreateWorkspaceEdit(renamePlan?.Changes);
                        }))
                    : RequestOutcome.InvalidParams();
            case "textDocument/formatting":
                return TryCreateFormattingRequest(parameters, out var formattingRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        formattingRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateFormattingEdits(
                            sourceIndex.FormatDocument(formattingRequest.Uri, formattingRequest.TabSize))))
                    : RequestOutcome.InvalidParams();
            case "textDocument/semanticTokens/full":
                return TryCreateTextDocumentRequest(parameters, out var semanticTokensRequest)
                    ? RequestOutcome.Success(WithSourceIndex(
                        semanticTokensRequest,
                        cancellationToken,
                        sourceIndex => VbaLspFeatureProjection.CreateSemanticTokens(
                            sourceIndex.GetSemanticTokenData(semanticTokensRequest.Uri))))
                    : RequestOutcome.InvalidParams();
            default:
                return RequestOutcome.Error(-32601, "Method not found");
        }
    }

    private TResult WithSourceIndex<TRequest, TResult>(
        TRequest request,
        CancellationToken cancellationToken,
        Func<VbaSourceIndex, TResult> createResult)
        where TRequest : ITextDocumentRequest
    {
        var snapshot = workspace.CreateProjectSnapshot(request.Uri, cancellationToken);
        return createResult(snapshot.SourceIndex);
    }

    private static bool TryDecodeEnvelope(
        JsonObject request,
        out string method,
        out JsonNode? parameters)
    {
        method = "";
        parameters = null;
        if (!request.TryGetPropertyValue("id", out var id)
            || !IsValidRequestId(id)
            || !TryGetString(request["jsonrpc"], out var jsonRpc)
            || !jsonRpc.Equals("2.0", StringComparison.Ordinal)
            || !TryGetString(request["method"], out method))
        {
            return false;
        }

        parameters = request["params"];
        return true;
    }

    private static JsonNode? GetResponseId(JsonObject request)
        => request.TryGetPropertyValue("id", out var id) && IsValidRequestId(id)
            ? id
            : null;

    private static bool IsValidRequestId(JsonNode? id)
        => id is null
            || id is JsonValue value
            && value.GetValueKind() is JsonValueKind.String or JsonValueKind.Number;

    private static bool TryCreateTextDocumentRequest(
        JsonNode? parameters,
        out TextDocumentRequest request)
    {
        request = default!;
        if (parameters is not JsonObject parameterObject
            || parameterObject["textDocument"] is not JsonObject textDocument
            || !TryGetString(textDocument["uri"], out var uri)
            || string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        request = new TextDocumentRequest(uri);
        return true;
    }

    private static bool TryCreatePositionRequest(
        JsonNode? parameters,
        out TextDocumentPositionRequest request)
    {
        request = default!;
        if (!TryCreateTextDocumentRequest(parameters, out var document)
            || parameters is not JsonObject parameterObject
            || parameterObject["position"] is not JsonObject position
            || !TryGetInt32(position["line"], out var line)
            || !TryGetInt32(position["character"], out var character)
            || line < 0
            || character < 0)
        {
            return false;
        }

        request = new TextDocumentPositionRequest(document.Uri, line, character);
        return true;
    }

    private static bool TryCreateWorkspaceSymbolQuery(JsonNode? parameters, out string query)
    {
        query = "";
        return parameters is JsonObject parameterObject
            && TryGetString(parameterObject["query"], out query);
    }

    private static bool TryCreateRenameRequest(JsonNode? parameters, out RenameRequest request)
    {
        request = default!;
        if (!TryCreatePositionRequest(parameters, out var position)
            || parameters is not JsonObject parameterObject
            || !TryGetString(parameterObject["newName"], out var newName)
            || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        request = new RenameRequest(position.Uri, position.Line, position.Character, newName);
        return true;
    }

    private static bool TryCreateFormattingRequest(JsonNode? parameters, out FormattingRequest request)
    {
        request = default!;
        if (!TryCreateTextDocumentRequest(parameters, out var document)
            || parameters is not JsonObject parameterObject
            || parameterObject["options"] is not JsonObject options
            || !TryGetInt32(options["tabSize"], out var tabSize)
            || tabSize <= 0)
        {
            return false;
        }

        request = new FormattingRequest(document.Uri, tabSize);
        return true;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value!);
    }

    private static bool TryGetInt32(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue
            && jsonValue.TryGetValue(out value);
    }

    private interface ITextDocumentRequest
    {
        string Uri { get; }
    }

    private sealed record TextDocumentRequest(string Uri) : ITextDocumentRequest;

    private sealed record TextDocumentPositionRequest(
        string Uri,
        int Line,
        int Character) : ITextDocumentRequest;

    private sealed record RenameRequest(
        string Uri,
        int Line,
        int Character,
        string NewName) : ITextDocumentRequest;

    private sealed record FormattingRequest(string Uri, int TabSize) : ITextDocumentRequest;

    private sealed record RequestOutcome(object? Result, int? ErrorCode, string? ErrorMessage)
    {
        public static RequestOutcome Success(object? result) => new(result, null, null);

        public static RequestOutcome Error(int code, string message) => new(null, code, message);

        public static RequestOutcome InvalidParams() => Error(-32602, "Invalid params");
    }
}

/// <summary>
/// Describes the capabilities and server identity advertised by request execution.
/// </summary>
internal sealed record VbaLspCapabilityContract(
    int TextDocumentSync,
    bool DefinitionProvider,
    bool ReferencesProvider,
    bool DocumentSymbolProvider,
    bool WorkspaceSymbolProvider,
    bool HoverProvider,
    bool DocumentFormattingProvider,
    bool RenamePrepareProvider,
    IReadOnlyList<string> SignatureHelpTriggerCharacters,
    IReadOnlyList<string> CompletionTriggerCharacters,
    IReadOnlyList<string> SemanticTokenTypes,
    IReadOnlyList<string> SemanticTokenModifiers,
    bool SemanticTokensFull,
    bool SemanticTokensRange,
    string ServerName,
    string ServerVersion);
