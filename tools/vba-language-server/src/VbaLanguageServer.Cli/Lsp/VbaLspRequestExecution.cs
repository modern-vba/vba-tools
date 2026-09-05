using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.BlockSkeletonInsertion;
using VbaLanguageServer.SourceModel;
using VbaTools.Syntax;
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
            ".", "_", " ", "(", ",", ":", ";", "+", "-", "*", "/", "\\", "^", "&", "=", "<", ">"
        ],
        SemanticTokenTypes: VbaSemanticTokenLegend.Types,
        SemanticTokenModifiers: VbaSemanticTokenLegend.Modifiers,
        SemanticTokensFull: true,
        SemanticTokensRange: false,
        ServerName: "vba-language-server",
        ServerVersion: "0.1.0");

    private readonly LspMessageTransport transport;
    private readonly IVbaInteractiveWorkspaceCapture workspace;
    private readonly IVbaLspRequestExecutionGate executionGate;
    private readonly VbaLspClientCapabilityState clientCapabilities;
    private int shutdownRequested;

    /// <summary>
    /// Creates a request executor over the transport and workspace boundaries.
    /// </summary>
    /// <param name="transport">The transport used to write the request response.</param>
    /// <param name="workspace">The boundary used to capture immutable language feature state.</param>
    public VbaLspRequestExecution(
        LspMessageTransport transport,
        IVbaInteractiveWorkspaceCapture workspace,
        IVbaLspRequestExecutionGate? executionGate = null,
        VbaLspClientCapabilityState? clientCapabilities = null)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.executionGate = executionGate ?? ImmediateVbaLspRequestExecutionGate.Instance;
        this.clientCapabilities = clientCapabilities ?? new VbaLspClientCapabilityState();
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
        finally
        {
            captured.Dispose();
        }

        releaseCancellationOwnership?.Invoke();
        if (outcome.ErrorCode is int errorCode)
        {
            await transport.WriteErrorResponseAsync(
                captured.ResponseId,
                errorCode,
                outcome.ErrorMessage!,
                outcome.ErrorData,
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
            "initialize" => parameters is JsonObject initializeParameters
                ? Captured(_ =>
                {
                    clientCapabilities.Update(initializeParameters);
                    return RequestOutcome.Success(
                        VbaLspFeatureProjection.CreateInitializeResult(CapabilityContract));
                })
                : Direct(RequestOutcome.InvalidParams()),
            "shutdown" => parameters is null
                ? Captured(_ =>
                {
                    Interlocked.Exchange(ref shutdownRequested, 1);
                    return RequestOutcome.Success(null);
                })
                : Direct(RequestOutcome.InvalidParams()),
            "textDocument/completion" =>
                CaptureCompletionRequest(
                    parameters,
                    cancellationToken,
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
                        return VbaLspFeatureProjection.CreateDefinitionLocations(
                            inventory.ResolveDefinitions(
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
                            inventory.ResolveHover(
                                request.Uri,
                                request.Line,
                                request.Character));
                    },
                    Captured,
                    Direct),
            "textDocument/signatureHelp" => CaptureSignatureHelpRequest(
                parameters,
                cancellationToken,
                Captured,
                Direct),
            "textDocument/prepareRename" =>
                CapturePrepareRenameRequest(
                    parameters,
                    cancellationToken,
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
            "vba/moduleIdentityMetadata" =>
                CaptureModuleIdentityMetadataRequest(
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

    private CapturedRequest CaptureCompletionRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateCompletionRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var result = VbaLspFeatureProjection.CreateCompletionItems(
                inventory.GetCompletionResult(
                    request.Uri,
                    request.Line,
                    request.Character,
                    request.Invocation));
            executionToken.ThrowIfCancellationRequested();
            return RequestOutcome.Success(result);
        });
    }

    private CapturedRequest CaptureSignatureHelpRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateSignatureHelpRequest(parameters, out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        var capabilities = clientCapabilities.Snapshot.SignatureHelp;
        var retriggerIdentity = capabilities.ContextSupport
            ? TryCreateRetriggerIdentity(parameters)
            : null;
        var inventory = CaptureSemanticInventory(request.Uri, cancellationToken);
        return captured(executionToken =>
        {
            executionToken.ThrowIfCancellationRequested();
            var result = VbaLspFeatureProjection.CreateSignatureHelp(
                inventory.GetSignatureHelp(
                    request.Uri,
                    request.Line,
                    request.Character,
                    retriggerIdentity),
                capabilities);
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

    private CapturedRequest CapturePrepareRenameRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
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
            var prepare = inventory.CreatePrepareRenameOutcome(
                request.Uri,
                request.Line,
                request.Character);
            executionToken.ThrowIfCancellationRequested();
            return prepare.Failure is null
                ? RequestOutcome.Success(prepare.Result)
                : RequestOutcome.Error(
                    -32803,
                    prepare.Failure.Message,
                    CreateRenameFailureData(prepare.Failure));
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

        var inventories =
            workspace.CaptureWorkspaceSemanticInventories(cancellationToken);
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

        var nameFailure = VbaSemanticInventory.ValidateRenameName(
            request.NewName);
        if (nameFailure is not null)
        {
            return direct(RequestOutcome.Error(
                -32803,
                nameFailure.Message,
                CreateRenameFailureData(nameFailure)));
        }

        var renameCapture = workspace.CaptureRenameProjectSnapshot(
            request.Uri,
            cancellationToken);
        return captured(executionToken =>
        {
            using (renameCapture)
            {
                executionToken.ThrowIfCancellationRequested();
                var requiresSourceTemplateIdentityFence =
                    renameCapture.SemanticInventory
                        .RequiresSourceTemplateIdentityFence(
                            request.Uri,
                            request.Line,
                            request.Character,
                            request.NewName,
                            renameCapture.ProjectIdentityRead);

                RequestOutcome? GetSourceTemplateChangeOutcome()
                {
                    if (!requiresSourceTemplateIdentityFence
                        || renameCapture.GetSourceTemplateChangeFailure()
                            is not { } failure)
                    {
                        return null;
                    }

                    return RequestOutcome.Error(
                        -32803,
                        failure.Message,
                        CreateRenameFailureData(failure));
                }

                if (renameCapture.SemanticInventory
                        .RequiresFileFollowingModuleRename(
                            request.Uri,
                            request.Line,
                            request.Character,
                            request.NewName,
                            renameCapture.ProjectIdentityRead)
                    && !clientCapabilities.Snapshot.WorkspaceEdit
                        .SupportsRenameFile)
                {
                    if (GetSourceTemplateChangeOutcome()
                        is { } capabilitySourceTemplateChangeOutcome)
                    {
                        return capabilitySourceTemplateChangeOutcome;
                    }

                    var failure = new VbaRenameFailure(
                        "clientCapabilityMissing",
                        "File-following module Rename requires ordered "
                        + "documentChanges and the rename resource operation.");
                    return RequestOutcome.Error(
                        -32803,
                        failure.Message,
                        CreateRenameFailureData(failure));
                }

                var rename = renameCapture.SemanticInventory.CreateRenameResult(
                    request.Uri,
                    request.Line,
                    request.Character,
                    request.NewName,
                    executionToken,
                    renameCapture.ProjectIdentityRead);
                executionToken.ThrowIfCancellationRequested();
                var sourceChangeFailure =
                    renameCapture.GetParticipatingSourceChangeFailure();
                if (sourceChangeFailure is not null)
                {
                    return RequestOutcome.Error(
                        -32803,
                        sourceChangeFailure.Message,
                        CreateRenameFailureData(sourceChangeFailure));
                }

                if ((rename.Plan is null || rename.Failure is not null)
                    && GetSourceTemplateChangeOutcome()
                        is { } incompleteOutcome)
                {
                    return incompleteOutcome;
                }

                if (rename.Failure?.Reason == "invalidName")
                {
                    return RequestOutcome.Error(
                        -32803,
                        rename.Failure.Message,
                        CreateRenameFailureData(rename.Failure));
                }

                if (rename.Plan is null && rename.Failure is null)
                {
                    return RequestOutcome.Success(null);
                }

                if (rename.Failure is not null)
                {
                    return RequestOutcome.Error(
                        -32803,
                        rename.Failure.Message,
                        CreateRenameFailureData(rename.Failure));
                }

                if (rename.Plan!.FileRenames.Count > 0
                    && !clientCapabilities.Snapshot.WorkspaceEdit.SupportsRenameFile)
                {
                    if (GetSourceTemplateChangeOutcome()
                        is { } plannedCapabilitySourceTemplateChangeOutcome)
                    {
                        return plannedCapabilitySourceTemplateChangeOutcome;
                    }

                    var failure = new VbaRenameFailure(
                        "clientCapabilityMissing",
                        "File-following module Rename requires ordered "
                        + "documentChanges and the rename resource operation.");
                    return RequestOutcome.Error(
                        -32803,
                        failure.Message,
                        CreateRenameFailureData(failure));
                }

                if (renameCapture.AnalysisFailureMessage is not null)
                {
                    if (GetSourceTemplateChangeOutcome()
                        is { } analysisSourceTemplateChangeOutcome)
                    {
                        return analysisSourceTemplateChangeOutcome;
                    }

                    var failure = new VbaRenameFailure(
                        "analysisIncomplete",
                        renameCapture.AnalysisFailureMessage);
                    return RequestOutcome.Error(
                        -32803,
                        failure.Message,
                        CreateRenameFailureData(failure));
                }

                var fileRenamePreflight =
                    renameCapture.PreflightFileRenames(rename.Plan);
                if (GetSourceTemplateChangeOutcome()
                    is { } postPreflightSourceTemplateChangeOutcome)
                {
                    return postPreflightSourceTemplateChangeOutcome;
                }

                if (fileRenamePreflight.Failure is not null)
                {
                    return RequestOutcome.Error(
                        -32803,
                        fileRenamePreflight.Failure.Message,
                        CreateRenameFailureData(fileRenamePreflight.Failure));
                }

                if (fileRenamePreflight.Plan.FileRenames.Count > 0
                    && !clientCapabilities.Snapshot.WorkspaceEdit
                        .SupportsRenameFile)
                {
                    var failure = new VbaRenameFailure(
                        "clientCapabilityMissing",
                        "File-following module Rename requires ordered "
                        + "documentChanges and the rename resource operation.");
                    return RequestOutcome.Error(
                        -32803,
                        failure.Message,
                        CreateRenameFailureData(failure));
                }

                var finalSourceChangeFailure =
                    renameCapture.GetParticipatingSourceChangeFailure();
                if (finalSourceChangeFailure is not null)
                {
                    return RequestOutcome.Error(
                        -32803,
                        finalSourceChangeFailure.Message,
                        CreateRenameFailureData(finalSourceChangeFailure));
                }

                if (GetSourceTemplateChangeOutcome()
                    is { } finalSourceTemplateChangeOutcome)
                {
                    return finalSourceTemplateChangeOutcome;
                }

                return RequestOutcome.Success(
                    VbaLspFeatureProjection.CreateWorkspaceEdit(
                        fileRenamePreflight.Plan));
            }
        }) with
        {
            Cleanup = renameCapture.Dispose
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateRenameFailureData(
        VbaRenameFailure failure)
    {
        var data = new Dictionary<string, object?>
        {
            ["reason"] = failure.Reason
        };
        if (failure.Conflicts is not null)
        {
            data["conflicts"] = failure.Conflicts
                .Select(conflict =>
                {
                    var projected = new Dictionary<string, object?>
                    {
                        ["collisionKind"] = conflict.CollisionKind,
                        ["name"] = conflict.Name
                    };
                    if (conflict.Uri is not null)
                    {
                        projected["uri"] = conflict.Uri;
                    }

                    if (conflict.Range is not null)
                    {
                        projected["range"] = conflict.Range;
                    }

                    if (conflict.ReferenceName is not null)
                    {
                        projected["referenceName"] = conflict.ReferenceName;
                    }

                    return projected;
                })
                .ToArray();
        }

        if (failure.Condition is not null)
        {
            data["condition"] = failure.Condition;
        }

        if (failure.Path is not null)
        {
            data["path"] = failure.Path;
        }

        if (failure.Guidance is not null)
        {
            data["guidance"] = failure.Guidance;
        }

        return data;
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

        var snapshot = workspace.CaptureExactDocumentSnapshot(
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

    private static CapturedRequest CaptureModuleIdentityMetadataRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        Func<Func<CancellationToken, RequestOutcome>, CapturedRequest> captured,
        Func<RequestOutcome, CapturedRequest> direct)
    {
        if (!TryCreateModuleIdentityMetadataRequest(
            parameters,
            cancellationToken,
            out var request))
        {
            return direct(RequestOutcome.InvalidParams());
        }

        cancellationToken.ThrowIfCancellationRequested();
        return captured(executionToken =>
        {
            var sources = new List<ModuleIdentityMetadataSourceResult>(request.Sources.Count);
            foreach (var source in request.Sources)
            {
                executionToken.ThrowIfCancellationRequested();
                var metadata = VbaModuleIdentityMetadataReader.Read(
                    source.Text,
                    VbaModuleIdentitySourceKind.ObjectModule);
                sources.Add(new ModuleIdentityMetadataSourceResult(
                    source.SourceUri,
                    source.Kind,
                    metadata.State switch
                    {
                        VbaModuleIdentityMetadataState.Missing => "missing",
                        VbaModuleIdentityMetadataState.Invalid => "invalid",
                        VbaModuleIdentityMetadataState.Authoritative => "authoritative",
                        _ => throw new InvalidOperationException(
                            $"Unknown ModuleIdentity metadata state '{metadata.State}'.")
                    },
                    metadata.Name));
            }

            return RequestOutcome.Success(new ModuleIdentityMetadataBatchResult(sources));
        });
    }

    private VbaSemanticInventory CaptureSemanticInventory(
        string uri,
        CancellationToken cancellationToken)
    {
        var inventory =
            workspace.CaptureProjectSemanticInventory(uri, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return inventory;
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

    private static bool TryCreateCompletionRequest(
        JsonNode? parameters,
        out CompletionRequest request)
    {
        request = default!;
        if (!TryCreatePositionRequest(parameters, out var position))
        {
            return false;
        }

        var invocation = VbaCompletionInvocation.Explicit;
        if (parameters is JsonObject parameterObject
            && parameterObject["context"] is JsonObject context)
        {
            if (!TryGetInt32(context["triggerKind"], out var triggerKind))
            {
                return false;
            }

            if (triggerKind == 2)
            {
                if (!TryGetString(
                        context["triggerCharacter"],
                        out var triggerCharacter)
                    || string.IsNullOrEmpty(triggerCharacter))
                {
                    return false;
                }

                invocation = new VbaCompletionInvocation(
                    VbaCompletionInvocationKind.TriggerCharacter,
                    triggerCharacter);
            }
            else if (triggerKind == 3)
            {
                invocation = new VbaCompletionInvocation(
                    VbaCompletionInvocationKind.Retrigger);
            }
            else if (triggerKind != 1)
            {
                return false;
            }
        }

        request = new CompletionRequest(
            position.Uri,
            position.Line,
            position.Character,
            invocation);
        return true;
    }

    private static bool TryCreateSignatureHelpRequest(
        JsonNode? parameters,
        out SignatureHelpRequest request)
    {
        request = default!;
        if (!TryCreatePositionRequest(parameters, out var position))
        {
            return false;
        }

        request = new SignatureHelpRequest(
            position.Uri,
            position.Line,
            position.Character);
        return true;
    }

    private static VbaSignaturePresentationIdentity? TryCreateRetriggerIdentity(
        JsonNode? parameters)
    {
        if (parameters is not JsonObject parameterObject
            || parameterObject["context"] is not JsonObject context
            || !TryGetBoolean(context["isRetrigger"], out var isRetrigger)
            || !isRetrigger
            || context["activeSignatureHelp"] is not JsonObject activeSignatureHelp
            || activeSignatureHelp["signatures"] is not JsonArray signatures)
        {
            return null;
        }

        var activeSignature = 0;
        if (activeSignatureHelp["activeSignature"] is { } activeSignatureNode
            && !TryGetInt32(activeSignatureNode, out activeSignature))
        {
            return null;
        }

        if (activeSignature < 0
            || activeSignature >= signatures.Count
            || signatures[activeSignature] is not JsonObject selectedSignature
            || !TryGetString(selectedSignature["label"], out var label))
        {
            return null;
        }

        var parameterLabels = new List<string>();
        if (selectedSignature["parameters"] is JsonArray parametersArray)
        {
            foreach (var parameter in parametersArray)
            {
                if (parameter is not JsonObject parameterObjectValue
                    || !TryGetString(parameterObjectValue["label"], out var parameterLabel))
                {
                    return null;
                }

                parameterLabels.Add(parameterLabel);
            }
        }
        else if (selectedSignature["parameters"] is not null)
        {
            return null;
        }

        return new VbaSignaturePresentationIdentity(
            label,
            Array.AsReadOnly(parameterLabels.ToArray()));
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
            || !TryGetString(parameterObject["newName"], out var newName))
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

    private static bool TryCreateModuleIdentityMetadataRequest(
        JsonNode? parameters,
        CancellationToken cancellationToken,
        out ModuleIdentityMetadataBatchRequest request)
    {
        request = default!;
        if (parameters is not JsonObject parameterObject
            || parameterObject["sources"] is not JsonArray sourceNodes)
        {
            return false;
        }

        var sources = new List<ModuleIdentityMetadataSourceRequest>(sourceNodes.Count);
        foreach (var sourceNode in sourceNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceNode is not JsonObject source
                || !TryGetString(source["sourceUri"], out var sourceUri)
                || string.IsNullOrWhiteSpace(sourceUri)
                || !TryGetString(source["kind"], out var kind)
                || kind is not "form" and not "document"
                || !TryGetString(source["text"], out var text))
            {
                return false;
            }

            sources.Add(new ModuleIdentityMetadataSourceRequest(
                sourceUri,
                kind,
                text));
        }

        request = new ModuleIdentityMetadataBatchRequest(sources);
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

    private sealed record CompletionRequest(
        string Uri,
        int Line,
        int Character,
        VbaCompletionInvocation Invocation) : ITextDocumentRequest;

    private sealed record SignatureHelpRequest(
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

    private sealed record ModuleIdentityMetadataBatchRequest(
        IReadOnlyList<ModuleIdentityMetadataSourceRequest> Sources);

    private sealed record ModuleIdentityMetadataSourceRequest(
        string SourceUri,
        string Kind,
        string Text);

    private sealed record ModuleIdentityMetadataBatchResult(
        IReadOnlyList<ModuleIdentityMetadataSourceResult> Sources);

    private sealed record ModuleIdentityMetadataSourceResult(
        string SourceUri,
        string Kind,
        string State,
        string? Name);

    internal sealed record CapturedRequest(
        JsonNode? ResponseId,
        VbaLspRequestId? RequestId,
        string Method,
        Func<CancellationToken, RequestOutcome> Execute,
        bool UseExecutionGate) : IDisposable
    {
        private Action? cleanup;

        public Action? Cleanup
        {
            init => cleanup = value;
        }

        public void Dispose()
            => Interlocked.Exchange(ref cleanup, null)?.Invoke();

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

    internal sealed record RequestOutcome(
        object? Result,
        int? ErrorCode,
        string? ErrorMessage,
        object? ErrorData)
    {
        public static RequestOutcome Success(object? result)
            => new(result, null, null, null);

        public static RequestOutcome Error(
            int code,
            string message,
            object? data = null)
            => new(null, code, message, data);

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
