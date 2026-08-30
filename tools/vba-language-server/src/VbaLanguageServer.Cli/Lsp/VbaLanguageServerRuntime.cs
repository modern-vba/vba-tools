using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using VbaTools.TypeLibRegistry;

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
    private readonly IVbaProjectReconciliationRuntimeLifecycle?
        projectReconciliationLifecycle;
    private readonly VbaDevReferenceListStartupState? vbaDevStartupState;
    private readonly IVbaHostClassProjectionSnapshotHandler?
        hostClassProjectionSnapshotHandler;
    private int startupWarningPublished;

    /// <summary>
    /// Creates a language-server runtime from transport, request, and lifecycle components.
    /// </summary>
    /// <param name="transport">The LSP transport used for JSON-RPC messages.</param>
    /// <param name="requestExecution">The boundary used for request handling.</param>
    /// <param name="documentLifecycle">The document lifecycle handler used for notifications.</param>
    /// <param name="catalogLifecycle">The optional background catalog lifecycle owner.</param>
    /// <param name="schedulerOptions">Optional scheduler options used by deterministic tests.</param>
    /// <param name="projectReconciliationLifecycle">The optional background project reconciliation owner.</param>
    /// <param name="vbaDevStartupState">The optional startup-validated VbaDev reference-list capability.</param>
    /// <param name="hostClassProjectionSnapshotHandler">The optional consumer-owned host-class snapshot handler.</param>
    public VbaLanguageServerRuntime(
        LspMessageTransport transport,
        VbaLspRequestExecution requestExecution,
        VbaDocumentLifecycle documentLifecycle,
        IReferenceCatalogRuntimeLifecycle? catalogLifecycle = null,
        VbaInteractiveWorkSchedulerOptions? schedulerOptions = null,
        IVbaProjectReconciliationRuntimeLifecycle?
            projectReconciliationLifecycle = null,
        VbaDevReferenceListStartupState? vbaDevStartupState = null,
        IVbaHostClassProjectionSnapshotHandler?
            hostClassProjectionSnapshotHandler = null)
    {
        this.transport = transport;
        this.requestExecution = requestExecution;
        this.documentLifecycle = documentLifecycle;
        this.catalogLifecycle = catalogLifecycle;
        this.schedulerOptions = schedulerOptions;
        this.projectReconciliationLifecycle = projectReconciliationLifecycle;
        this.vbaDevStartupState = vbaDevStartupState;
        this.hostClassProjectionSnapshotHandler =
            hostClassProjectionSnapshotHandler;
    }

    /// <summary>
    /// Creates the default stdio runtime with bundled reference catalogs and registry discovery.
    /// </summary>
    /// <param name="input">The JSON-RPC input stream.</param>
    /// <param name="output">The JSON-RPC output stream.</param>
    /// <returns>The configured language-server runtime.</returns>
    public static VbaLanguageServerRuntime CreateDefault(
        Stream input,
        Stream output,
        VbaDevReferenceListStartupState? vbaDevStartupState = null)
    {
        var transport = new LspMessageTransport(input, output);
        var referenceCatalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var registryDiscovery = BlockingReferenceCatalogDiscoveryHook.WrapIfConfigured(
            new TypeLibReferenceCatalogDiscovery(new RegistryTypeLibRegistryCatalogReader()));
        var catalogDiscovery = CreateReferenceCatalogDiscovery(
            registryDiscovery,
            vbaDevStartupState);
        var catalogRefreshService = new VbaProjectReferenceCatalogRefreshService(
            referenceCatalogCache,
            catalogDiscovery,
            VbaProjectReferenceCatalogPersistentStore.CreateDefault());
        var workspace = new VbaLanguageWorkspace(referenceCatalogCache);
        var clientCapabilities = new VbaLspClientCapabilityState();
        var requestExecution = new VbaLspRequestExecution(
            transport,
            workspace,
            BlockingVbaLspRequestExecutionGate.CreateFromEnvironment(),
            clientCapabilities);
        var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
            referenceCatalogCache,
            catalogRefreshService,
            workspace.ManifestWorkspace,
            transport);
        var documentLifecycle = new VbaDocumentLifecycle(
            transport,
            workspace,
            catalogRefresh,
            clientCapabilities);
        var projectReconciler =
            documentLifecycle.CreateProjectReconciler();
        return new VbaLanguageServerRuntime(
            transport,
            requestExecution,
            documentLifecycle,
            catalogRefresh,
            projectReconciliationLifecycle: projectReconciler,
            vbaDevStartupState: vbaDevStartupState,
            hostClassProjectionSnapshotHandler:
                new VbaHostClassProjectionSnapshotHandler(workspace));
    }

    internal static IVbaProjectReferenceCatalogDiscovery CreateReferenceCatalogDiscovery(
        IVbaProjectReferenceCatalogDiscovery registryDiscovery,
        VbaDevReferenceListStartupState? vbaDevStartupState)
    {
        ArgumentNullException.ThrowIfNull(registryDiscovery);
        return vbaDevStartupState?.ExecutablePath is { } executablePath
            ? new VbaDevReferenceListCatalogDiscoveryFactory(
                registryDiscovery,
                executablePath)
            : registryDiscovery;
    }

    /// <summary>
    /// Runs the request and notification loop until cancellation, EOF, or exit notification.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the message loop.</param>
    /// <returns>A task that completes when the runtime stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var responseLifetime = new CancellationTokenSource();
        var responseCancellation =
            new ResponseLifetimeCancellation(responseLifetime);
        using var hostCancellationRegistration = cancellationToken.Register(
            static state =>
                ((ResponseLifetimeCancellation)state!).Request(),
            responseCancellation);
        var scheduler = new VbaInteractiveWorkScheduler(
            VbaInteractiveWorkTimingFileSink.CreateFromEnvironment(),
            failureSink: _ => responseCancellation.Request(),
            options: schedulerOptions
                ?? VbaInteractiveWorkSchedulerOptions.CreateFromEnvironment());
        documentLifecycle.AttachScheduler(scheduler);
        catalogLifecycle?.AttachScheduler(scheduler);
        projectReconciliationLifecycle?.AttachScheduler(scheduler);
        var gracefulExit = false;
        var shutdownAdmitted = false;
        try
        {
            while (!responseLifetime.IsCancellationRequested)
            {
                var message = await transport.ReadMessageAsync(responseLifetime.Token);
                if (message is null)
                {
                    responseCancellation.Request();
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
                        responseCancellation.Request();
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

                if (method == VbaHostClassProjectionSnapshotHandler.Method
                    && hostClassProjectionSnapshotHandler is { } hostClassHandler)
                {
                    if (!hostClassHandler.TryParse(parameters, out var update))
                    {
                        continue;
                    }

                    if (update.CoalescingKey is not { } coalescingKey)
                    {
                        continue;
                    }

                    try
                    {
                        scheduler.AdmitCoalescibleAdvisoryMutation(
                            method,
                            coalescingKey,
                            update.Revision,
                            workCancellationToken =>
                            {
                                workCancellationToken.ThrowIfCancellationRequested();
                                _ = hostClassHandler.TryApply(update);
                                return Task.CompletedTask;
                            });
                    }
                    catch (VbaInteractiveWorkQueueFullException)
                    {
                        responseCancellation.Request();
                        return;
                    }
                    catch (ObjectDisposedException) when (!scheduler.IsAccepting)
                    {
                        return;
                    }

                    continue;
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
                    responseCancellation.Request();
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
            documentLifecycle.Stop();
            if (!gracefulExit)
            {
                responseCancellation.Request();
            }

            try
            {
                try
                {
                    if (projectReconciliationLifecycle is not null)
                    {
                        await projectReconciliationLifecycle.StopAsync();
                    }
                }
                finally
                {
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
            finally
            {
                hostCancellationRegistration.Dispose();
                await responseCancellation.ObserveAsync();
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
            or "workspace/didChangeWatchedFiles"
            or VbaHostClassProjectionSnapshotHandler.Method;

    private async Task HandleNotificationAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialized":
                await PublishStartupWarningAsync(cancellationToken);
                return;
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
            case VbaHostClassProjectionSnapshotHandler.Method:
                cancellationToken.ThrowIfCancellationRequested();
                if (hostClassProjectionSnapshotHandler is { } handler
                    && handler.TryParse(parameters, out var update))
                {
                    _ = handler.TryApply(update);
                }
                return;
            default:
                return;
        }
    }

    private Task PublishStartupWarningAsync(CancellationToken cancellationToken)
    {
        var warning = vbaDevStartupState?.WarningMessage;
        return warning is not null
            && Interlocked.Exchange(ref startupWarningPublished, 1) == 0
                ? transport.WriteLogMessageAsync(2, warning, cancellationToken)
                : Task.CompletedTask;
    }

    private sealed class ResponseLifetimeCancellation(
        CancellationTokenSource lifetime)
    {
        private readonly object gate = new();
        private Task? dispatch;

        public void Request()
        {
            lock (gate)
            {
                dispatch ??= lifetime.CancelAsync();
            }
        }

        public async Task ObserveAsync()
        {
            Task? cancellationDispatch;
            lock (gate)
            {
                cancellationDispatch = dispatch;
            }

            if (cancellationDispatch is null)
            {
                return;
            }

            try
            {
                await cancellationDispatch.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation callback failures must not escape shutdown or go unobserved.
            }
        }
    }

}
