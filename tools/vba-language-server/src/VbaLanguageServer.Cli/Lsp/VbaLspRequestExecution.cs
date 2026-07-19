using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
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
        SignatureHelpRetriggerCharacters: ["="],
        CompletionTriggerCharacters:
        [
            ".", " ", "(", ",", ":", ";", "+", "-", "*", "/", "\\", "^", "&", "=", "<", ">"
        ],
        SemanticTokenTypes: VbaSourceIndex.SemanticTokenTypes,
        SemanticTokenModifiers: VbaSourceIndex.SemanticTokenModifiers,
        SemanticTokensFull: true,
        SemanticTokensRange: false,
        ServerName: "vba-language-server",
        ServerVersion: "0.1.0");

    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaLspRequestExecutionGate executionGate;
    private int shutdownRequested;

    /// <summary>
    /// Creates a request executor over the transport and workspace boundaries.
    /// </summary>
    /// <param name="transport">The transport used to write the request response.</param>
    /// <param name="workspace">The workspace used to create language feature snapshots.</param>
    public VbaLspRequestExecution(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        IVbaLspRequestExecutionGate? executionGate = null)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.executionGate = executionGate ?? ImmediateVbaLspRequestExecutionGate.Instance;
    }

    /// <summary>
    /// Gets whether a valid shutdown request has been handled.
    /// </summary>
    public bool ShutdownRequested => Volatile.Read(ref shutdownRequested) != 0;

    /// <summary>
    /// Captures one request's immutable document or project state on the ordered lane.
    /// </summary>
    public CapturedRequest Capture(
        JsonObject request,
        CancellationToken requestCancellationToken)
    {
        var id = GetResponseId(request);
        if (!TryDecodeEnvelope(request, out var method, out var parameters))
        {
            return CapturedRequest.Direct(
                id,
                "<invalid-request>",
                requestId: null,
                RequestOutcome.Error(-32600, "Invalid Request"),
                useExecutionGate: false);
        }

        var requestId = VbaLspRequestId.TryCreate(id, out var parsedRequestId)
            ? parsedRequestId
            : (VbaLspRequestId?)null;
        try
        {
            requestCancellationToken.ThrowIfCancellationRequested();
            return CaptureRequest(
                id,
                requestId,
                method,
                parameters,
                requestCancellationToken);
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            return CapturedRequest.Direct(
                id,
                method,
                requestId,
                RequestOutcome.Error(-32800, "Request cancelled"),
                useExecutionGate: false);
        }
        catch (Exception)
        {
            return CapturedRequest.Direct(
                id,
                method,
                requestId,
                RequestOutcome.Error(-32603, "Internal error"),
                useExecutionGate: false);
        }
    }

    /// <summary>
    /// Executes one request and writes exactly one success or error response.
    /// </summary>
    public Task ExecuteAsync(
        JsonObject request,
        CancellationToken requestCancellationToken,
        CancellationToken responseCancellationToken,
        Action? releaseCancellationOwnership = null)
    {
        var captured = Capture(request, requestCancellationToken);
        return ExecuteAsync(
            captured,
            requestCancellationToken,
            responseCancellationToken,
            releaseCancellationOwnership);
    }

    /// <summary>
    /// Executes a previously captured request without consulting mutable workspace state.
    /// </summary>
    public async Task ExecuteAsync(
        CapturedRequest captured,
        CancellationToken requestCancellationToken,
        CancellationToken responseCancellationToken,
        Action? releaseCancellationOwnership = null)
    {
        RequestOutcome outcome;
        try
        {
            requestCancellationToken.ThrowIfCancellationRequested();
            if (captured.UseExecutionGate)
            {
                await executionGate.WaitAsync(
                    captured.RequestId,
                    captured.Method,
                    requestCancellationToken);
            }

            requestCancellationToken.ThrowIfCancellationRequested();
            outcome = await Task.Run(
                () =>
                {
                    requestCancellationToken.ThrowIfCancellationRequested();
                    var result = captured.Execute(requestCancellationToken);
                    requestCancellationToken.ThrowIfCancellationRequested();
                    return result;
                },
                requestCancellationToken);
        }
        catch (OperationCanceledException)
            when (requestCancellationToken.IsCancellationRequested
                && !responseCancellationToken.IsCancellationRequested)
        {
            outcome = RequestOutcome.Error(-32800, "Request cancelled");
        }
        catch (OperationCanceledException)
            when (responseCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            outcome = RequestOutcome.Error(-32603, "Internal error");
        }

        releaseCancellationOwnership?.Invoke();
        if (outcome.ErrorCode is int errorCode)
        {
            await transport.WriteErrorResponseAsync(
                captured.ResponseId,
                errorCode,
                outcome.ErrorMessage!,
                responseCancellationToken);
            return;
        }

        await transport.WriteResponseAsync(
            captured.ResponseId,
            outcome.Result,
            responseCancellationToken);
    }

    private CapturedRequest CaptureRequest(
        JsonNode? responseId,
        VbaLspRequestId? requestId,
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        CapturedRequest Captured(Func<CancellationToken, RequestOutcome> execute)
            => new(responseId, requestId, method, execute, UseExecutionGate: true);

        CapturedRequest Direct(RequestOutcome outcome)
            => Captured(_ => outcome);

        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "initialize" => parameters is JsonObject
                ? Direct(RequestOutcome.Success(
                    VbaLspFeatureProjection.CreateInitializeResult(CapabilityContract)))
                : Direct(RequestOutcome.InvalidParams()),
            "shutdown" => parameters is null
                ? Captured(_ =>
                {
                    Interlocked.Exchange(ref shutdownRequested, 1);
                    return RequestOutcome.Success(null);
                })
                : Direct(RequestOutcome.InvalidParams()),
            "textDocument/completion" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateCompletionItems(
                            inventory.GetCompletionResult(
                                request.Uri,
                                request.Line,
                                request.Character));
                    },
                    Captured,
                    Direct),
            "textDocument/documentSymbol" =>
                CaptureTextDocumentRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateDocumentSymbols(
                            inventory.GetDocumentDefinitions(request.Uri));
                    },
                    Captured,
                    Direct),
            "textDocument/definition" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateLocation(
                            inventory.ResolveDefinition(
                                request.Uri,
                                request.Line,
                                request.Character));
                    },
                    Captured,
                    Direct),
            "textDocument/references" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateLocations(
                            inventory.FindReferences(
                                request.Uri,
                                request.Line,
                                request.Character,
                                token));
                    },
                    Captured,
                    Direct),
            "workspace/symbol" =>
                CaptureWorkspaceSymbolRequest(
                    parameters,
                    cancellationToken,
                    Captured,
                    Direct),
            "textDocument/hover" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateHover(
                            inventory.ResolveSourceDefinition(
                                request.Uri,
                                request.Line,
                                request.Character));
                    },
                    Captured,
                    Direct),
            "textDocument/signatureHelp" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateSignatureHelp(
                            inventory.GetSignatureHelp(
                                request.Uri,
                                request.Line,
                                request.Character));
                    },
                    Captured,
                    Direct),
            "textDocument/prepareRename" =>
                CapturePositionRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return inventory.PrepareRename(
                            request.Uri,
                            request.Line,
                            request.Character);
                    },
                    Captured,
                    Direct),
            "textDocument/rename" =>
                CaptureRenameRequest(
                    parameters,
                    cancellationToken,
                    Captured,
                    Direct),
            "textDocument/formatting" =>
                CaptureFormattingRequest(
                    parameters,
                    cancellationToken,
                    Captured,
                    Direct),
            "vba/blockSkeletonInsertion" =>
                CaptureBlockSkeletonRequest(
                    parameters,
                    cancellationToken,
                    Captured,
                    Direct),
            "textDocument/semanticTokens/full" =>
                CaptureTextDocumentRequest(
                    parameters,
                    cancellationToken,
                    (request, inventory, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return VbaLspFeatureProjection.CreateSemanticTokens(
                            inventory.GetSemanticTokenData(request.Uri, token));
                    },
                    Captured,
                    Direct),
            _ => Direct(RequestOutcome.Error(-32601, "Method not found"))
        };
    }

    private CapturedRequest CapturePositionRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<TextDocumentPositionRequest, VbaSemanticInventory, CancellationToken, object?> createResult,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreatePositionRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var result = createResult(request, inventory, executionToken);
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(result);
        });
    }

    private CapturedRequest CaptureTextDocumentRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<TextDocumentRequest, VbaSemanticInventory, CancellationToken, object?> createResult,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateTextDocumentRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var result = createResult(request, inventory, executionToken);
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(result);
        });
    }

    private CapturedRequest CaptureWorkspaceSymbolRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateWorkspaceSymbolQuery(parameters, out var query))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventories = workspace.CreateProjectSnapshots(cancellationToken)
            .Select(snapshot => snapshot.SemanticInventory)
            .ToArray();
        return captured(executionToken =>
        {
            var symbols = new List<VbaWorkspaceSymbol>();
            foreach (var inventory in inventories)
            {
                executionToken.ThrowIfCancellationRequested();
                symbols.AddRange(inventory.GetWorkspaceSymbols(query));
            }

            return RequestOutcome.Success(
                VbaLspFeatureProjection.CreateWorkspaceSymbols(symbols));
        });
    }

    private CapturedRequest CaptureRenameRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateRenameRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var renamePlan = inventory.CreateRenamePlan(
                request.Uri,
                request.Line,
                request.Character,
                request.NewName,
                executionToken);
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(
                VbaLspFeatureProjection.CreateWorkspaceEdit(renamePlan?.Changes));
        });
    }

    private CapturedRequest CaptureFormattingRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateFormattingRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var edits = inventory.FormatDocument(
                request.Uri,
                request.IndentationStyle,
                executionToken);
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(
                VbaLspFeatureProjection.CreateFormattingEdits(edits));
        });
    }

    private CapturedRequest CaptureBlockSkeletonRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateBlockSkeletonInsertionRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var snapshot = workspace.GetDocumentSnapshot(
            request.DocumentUri,
            request.DocumentVersion,
            cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var plan = snapshot is null
                ? null
                : BlockSkeletonInsertionPlanner.CreatePlan(
                    snapshot,
                    request.Position,
                    request.IndentationStyle);
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(plan);
        });
    }

    private VbaSemanticInventory CaptureSemanticInventory(
        string uri,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.CreateProjectSnapshot(uri, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot.SemanticInventory;
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

        var indentSize = tabSize;
        if (options["indentSize"] is { } indentSizeNode
            && (!TryGetInt32(indentSizeNode, out indentSize) || indentSize <= 0))
        {
            return false;
        }

        var insertSpaces = true;
        if (options["insertSpaces"] is { } insertSpacesNode
            && !TryGetBoolean(insertSpacesNode, out insertSpaces))
        {
            return false;
        }

        request = new FormattingRequest(
            document.Uri,
            VbaIndentationStyle.FromEditorOptions(insertSpaces, indentSize));
        return true;
    }

    private static bool TryCreateBlockSkeletonInsertionRequest(
        JsonNode? parameters,
        out BlockSkeletonInsertionRequest request)
    {
        request = default!;
        if (parameters is not JsonObject parameterObject
            || !TryGetString(parameterObject["documentUri"], out var documentUri)
            || string.IsNullOrWhiteSpace(documentUri)
            || !TryGetInt32(parameterObject["documentVersion"], out var documentVersion)
            || documentVersion < 0
            || parameterObject["position"] is not JsonObject position
            || !TryGetInt32(position["line"], out var line)
            || !TryGetInt32(position["character"], out var character)
            || line < 0
            || character < 0
            || parameterObject["options"] is not JsonObject options
            || !TryGetBoolean(options["insertSpaces"], out var insertSpaces)
            || !TryGetInt32(options["tabSize"], out var tabSize)
            || tabSize <= 0)
        {
            return false;
        }

        var indentSize = tabSize;
        if (options["indentSize"] is { } indentSizeNode
            && (!TryGetInt32(indentSizeNode, out indentSize) || indentSize <= 0))
        {
            return false;
        }

        request = new BlockSkeletonInsertionRequest(
            documentUri,
            documentVersion,
            new BlockSkeletonInsertionPosition(line, character),
            VbaIndentationStyle.FromEditorOptions(insertSpaces, indentSize));
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

    private static bool TryGetBoolean(JsonNode? node, out bool value)
    {
        value = false;
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

    private sealed record FormattingRequest(
        string Uri,
        VbaIndentationStyle IndentationStyle) : ITextDocumentRequest;

    private sealed record BlockSkeletonInsertionRequest(
        string DocumentUri,
        int DocumentVersion,
        BlockSkeletonInsertionPosition Position,
        VbaIndentationStyle IndentationStyle);

    internal sealed record CapturedRequest(
        JsonNode? ResponseId,
        VbaLspRequestId? RequestId,
        string Method,
        Func<CancellationToken, RequestOutcome> Execute,
        bool UseExecutionGate)
    {
        public static CapturedRequest Direct(
            JsonNode? responseId,
            string method,
            VbaLspRequestId? requestId,
            RequestOutcome outcome,
            bool useExecutionGate)
            => new(
                responseId,
                requestId,
                method,
                _ => outcome,
                useExecutionGate);
    }

    internal sealed record RequestOutcome(object? Result, int? ErrorCode, string? ErrorMessage)
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
    IReadOnlyList<string> SignatureHelpRetriggerCharacters,
    IReadOnlyList<string> CompletionTriggerCharacters,
    IReadOnlyList<string> SemanticTokenTypes,
    IReadOnlyList<string> SemanticTokenModifiers,
    bool SemanticTokensFull,
    bool SemanticTokensRange,
    string ServerName,
    string ServerVersion);
