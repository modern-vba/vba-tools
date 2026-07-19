using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Runs the stdio JSON-RPC message loop for the VBA language server.
/// </summary>
internal sealed class VbaLanguageServerRuntime
{
    private readonly LspMessageTransport transport;
    private readonly VbaLspRequestExecution requestExecution;
    private readonly VbaDocumentLifecycle documentLifecycle;
    private readonly IReferenceCatalogRuntimeLifecycle? catalogLifecycle;
    private readonly VbaInteractiveWorkSchedulerOptions? schedulerOptions;

    /// <summary>
    /// Creates a language-server runtime from transport, request, and lifecycle components.
    /// </summary>
    /// <param name="transport">The LSP transport used for JSON-RPC messages.</param>
    /// <param name="requestExecution">The boundary used for request handling.</param>
    /// <param name="documentLifecycle">The document lifecycle handler used for notifications.</param>
    /// <param name="catalogLifecycle">The optional background catalog lifecycle owner.</param>
    /// <param name="schedulerOptions">Optional scheduler options used by deterministic tests.</param>
    public VbaLanguageServerRuntime(
        LspMessageTransport transport,
        VbaLspRequestExecution requestExecution,
        VbaDocumentLifecycle documentLifecycle,
        IReferenceCatalogRuntimeLifecycle? catalogLifecycle = null,
        VbaInteractiveWorkSchedulerOptions? schedulerOptions = null)
    {
        this.transport = transport;
        this.requestExecution = requestExecution;
        this.documentLifecycle = documentLifecycle;
        this.catalogLifecycle = catalogLifecycle;
        this.schedulerOptions = schedulerOptions;
    }

    /// <summary>
    /// Creates the default stdio runtime with bundled reference catalogs and registry discovery.
    /// </summary>
    /// <param name="input">The JSON-RPC input stream.</param>
    /// <param name="output">The JSON-RPC output stream.</param>
    /// <returns>The configured language-server runtime.</returns>
    public static VbaLanguageServerRuntime CreateDefault(Stream input, Stream output)
    {
        var transport = new LspMessageTransport(input, output);
        var referenceCatalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var catalogDiscovery = BlockingReferenceCatalogDiscoveryHook.WrapIfConfigured(
            new TypeLibReferenceCatalogDiscovery(new RegistryTypeLibRegistryReader()));
        var catalogRefreshService = new VbaProjectReferenceCatalogRefreshService(
            referenceCatalogCache,
            catalogDiscovery,
            VbaProjectReferenceCatalogPersistentStore.CreateDefault());
        var workspace = new VbaLanguageWorkspace(referenceCatalogCache);
        var requestExecution = new VbaLspRequestExecution(
            transport,
            workspace,
            BlockingVbaLspRequestExecutionGate.CreateFromEnvironment());
        var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
            referenceCatalogCache,
            catalogRefreshService,
            workspace.ManifestWorkspace,
            transport);
        var documentLifecycle = new VbaDocumentLifecycle(transport, workspace, catalogRefresh);
        return new VbaLanguageServerRuntime(
            transport,
            requestExecution,
            documentLifecycle,
            catalogRefresh);
    }

    /// <summary>
    /// Runs the request and notification loop until cancellation, EOF, or exit notification.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the message loop.</param>
    /// <returns>A task that completes when the runtime stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var responseLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scheduler = new VbaInteractiveWorkScheduler(
            VbaInteractiveWorkTimingFileSink.CreateFromEnvironment(),
            failureSink: _ => responseLifetime.Cancel(),
            options: schedulerOptions
                ?? VbaInteractiveWorkSchedulerOptions.CreateFromEnvironment());
        documentLifecycle.AttachScheduler(scheduler);
        catalogLifecycle?.AttachScheduler(scheduler);
        var gracefulExit = false;
        var shutdownAdmitted = false;
        try
        {
            while (!responseLifetime.IsCancellationRequested)
            {
                var message = await transport.ReadMessageAsync(responseLifetime.Token);
                if (message is null)
                {
                    responseLifetime.Cancel();
                    return;
                }

                if (!TryGetNotification(message, out var method, out var parameters))
                {
                    var requestMethod = GetRequestMethod(message);
                    var requestId = VbaLspRequestId.TryCreate(
                        message["id"],
                        out var parsedRequestId)
                            ? parsedRequestId
                            : (VbaLspRequestId?)null;
                    try
                    {
                        scheduler.AdmitRequest(
                            requestId,
                            requestMethod,
                            requestCancellationToken =>
                                requestExecution.Capture(
                                    message,
                                    requestCancellationToken),
                            (
                                capturedRequest,
                                requestCancellationToken,
                                releaseCancellationOwnership) =>
                                requestExecution.ExecuteAsync(
                                    capturedRequest,
                                    requestCancellationToken,
                                    responseLifetime.Token,
                                    releaseCancellationOwnership));
                    }
                    catch (VbaDuplicateRequestIdException)
                    {
                        try
                        {
                            await scheduler.AdmitRequiredBarrierAsync(
                                "<duplicate-request>",
                                _ => transport.WriteErrorResponseAsync(
                                    message["id"],
                                    -32600,
                                    "Duplicate request id",
                                    responseLifetime.Token),
                                responseLifetime.Token);
                        }
                        catch (ObjectDisposedException) when (!scheduler.IsAccepting)
                        {
                            return;
                        }

                        continue;
                    }
                    catch (VbaInteractiveWorkQueueFullException)
                    {
                        await transport.WriteErrorResponseAsync(
                            requestId is null ? null : message["id"],
                            -32000,
                            "Server busy",
                            responseLifetime.Token);
                        continue;
                    }
                    catch (ObjectDisposedException) when (!scheduler.IsAccepting)
                    {
                        return;
                    }

                    shutdownAdmitted |= IsValidShutdownAdmission(
                        message,
                        requestMethod);
                    continue;
                }

                if (method == "$/cancelRequest")
                {
                    if (TryGetCancellationRequestId(parameters, out var cancelledRequestId))
                    {
                        scheduler.TryCancel(cancelledRequestId);
                    }

                    continue;
                }

                if (method == "exit")
                {
                    if (!shutdownAdmitted && !requestExecution.ShutdownRequested)
                    {
                        Environment.ExitCode = 1;
                        responseLifetime.Cancel();
                        return;
                    }

                    VbaInteractiveWorkAdmission exit;
                    try
                    {
                        exit = await scheduler.AdmitRequiredBarrierAsync(
                            "exit",
                            _ =>
                            {
                                Environment.ExitCode =
                                    requestExecution.ShutdownRequested ? 0 : 1;
                                return Task.CompletedTask;
                            },
                            responseLifetime.Token);
                    }
                    catch (ObjectDisposedException) when (!scheduler.IsAccepting)
                    {
                        return;
                    }

                    await exit.Completion;
                    gracefulExit = true;
                    return;
                }

                Func<CancellationToken, Task> executeNotification =
                    workCancellationToken => HandleNotificationAsync(
                        method,
                        parameters,
                        workCancellationToken);
                try
                {
                    if (IsWorkspaceMutationNotification(method))
                    {
                        if (method == "textDocument/didChange"
                            && TryGetTextDocumentUri(parameters, out var changedDocumentUri))
                        {
                            scheduler.AdmitCoalescibleMutation(
                                method,
                                changedDocumentUri,
                                executeNotification);
                        }
                        else
                        {
                            scheduler.AdmitMutation(method, executeNotification);
                        }
                    }
                    else
                    {
                        scheduler.AdmitBarrier(method, executeNotification);
                    }
                }
                catch (VbaInteractiveWorkQueueFullException)
                {
                    responseLifetime.Cancel();
                    return;
                }
                catch (ObjectDisposedException) when (!scheduler.IsAccepting)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (responseLifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (!gracefulExit)
            {
                responseLifetime.Cancel();
            }

            try
            {
                if (catalogLifecycle is not null)
                {
                    await catalogLifecycle.StopAsync();
                }
            }
            finally
            {
                await scheduler.StopAsync(
                    gracefulExit
                        ? VbaInteractiveStopReason.Complete
                        : VbaInteractiveStopReason.Abort);
            }
        }
    }

    private static bool TryGetNotification(
        JsonObject message,
        out string method,
        out JsonNode? parameters)
    {
        method = "";
        parameters = null;
        if (message.ContainsKey("id")
            || message["jsonrpc"] is not JsonValue jsonRpcNode
            || !jsonRpcNode.TryGetValue<string>(out var jsonRpc)
            || !jsonRpc.Equals("2.0", StringComparison.Ordinal)
            || message["method"] is not JsonValue methodNode
            || !methodNode.TryGetValue(out method!)
            || message.TryGetPropertyValue("params", out var parameterNode)
            && parameterNode is not null and not JsonObject and not JsonArray)
        {
            return false;
        }

        parameters = message["params"];
        return true;
    }

    private static bool TryGetCancellationRequestId(
        JsonNode? parameters,
        out VbaLspRequestId requestId)
    {
        requestId = default;
        return parameters is JsonObject parameterObject
            && VbaLspRequestId.TryCreate(parameterObject["id"], out requestId);
    }

    private static bool TryGetTextDocumentUri(JsonNode? parameters, out string uri)
    {
        uri = "";
        return parameters is JsonObject parameterObject
            && parameterObject["textDocument"] is JsonObject textDocument
            && textDocument["uri"] is JsonValue uriNode
            && uriNode.TryGetValue(out uri!)
            && !string.IsNullOrWhiteSpace(uri);
    }

    private static string GetRequestMethod(JsonObject message)
        => message["method"] is JsonValue methodNode
            && methodNode.TryGetValue<string>(out var method)
                ? method
                : "<invalid-request>";

    private static bool IsValidShutdownAdmission(
        JsonObject message,
        string method)
        => method == "shutdown"
            && message.TryGetPropertyValue("id", out var id)
            && (id is null || VbaLspRequestId.TryCreate(id, out _))
            && message["params"] is null
            && message["jsonrpc"] is JsonValue jsonRpcNode
            && jsonRpcNode.TryGetValue<string>(out var jsonRpc)
            && jsonRpc == "2.0";

    private static bool IsWorkspaceMutationNotification(string method)
        => method is "textDocument/didOpen"
            or "textDocument/didChange"
            or "textDocument/didClose"
            or "workspace/didChangeWatchedFiles";

    private async Task HandleNotificationAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                await documentLifecycle.RecordOpenedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didChange":
                await documentLifecycle.RecordChangedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didClose":
                await documentLifecycle.RecordClosedDocumentAsync(parameters, cancellationToken);
                return;
            case "workspace/didChangeWatchedFiles":
                await documentLifecycle.RecordWatchedFilesChangedAsync(parameters, cancellationToken);
                return;
            default:
                return;
        }
    }

}
