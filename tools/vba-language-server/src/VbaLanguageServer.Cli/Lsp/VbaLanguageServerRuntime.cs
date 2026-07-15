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

    /// <summary>
    /// Creates a language-server runtime from transport, request, and lifecycle components.
    /// </summary>
    /// <param name="transport">The LSP transport used for JSON-RPC messages.</param>
    /// <param name="requestExecution">The boundary used for request handling.</param>
    /// <param name="documentLifecycle">The document lifecycle handler used for notifications.</param>
    public VbaLanguageServerRuntime(
        LspMessageTransport transport,
        VbaLspRequestExecution requestExecution,
        VbaDocumentLifecycle documentLifecycle)
    {
        this.transport = transport;
        this.requestExecution = requestExecution;
        this.documentLifecycle = documentLifecycle;
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
        var requestExecution = new VbaLspRequestExecution(transport, workspace);
        var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
            referenceCatalogCache,
            catalogRefreshService,
            transport);
        var documentLifecycle = new VbaDocumentLifecycle(transport, workspace, catalogRefresh);
        return new VbaLanguageServerRuntime(transport, requestExecution, documentLifecycle);
    }

    /// <summary>
    /// Runs the request and notification loop until cancellation, EOF, or exit notification.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the message loop.</param>
    /// <returns>A task that completes when the runtime stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await transport.ReadMessageAsync(cancellationToken);
            if (message is null)
            {
                return;
            }

            if (!TryGetNotification(message, out var method, out var parameters))
            {
                await requestExecution.ExecuteAsync(message, cancellationToken);
                continue;
            }

            if (method == "exit")
            {
                Environment.ExitCode = requestExecution.ShutdownRequested ? 0 : 1;
                return;
            }

            await HandleNotificationAsync(method, parameters, cancellationToken);
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
