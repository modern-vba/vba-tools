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
    private readonly VbaLanguageFeatureService features;
    private readonly VbaDocumentLifecycle documentLifecycle;
    private bool shutdownRequested;

    /// <summary>
    /// Creates a language-server runtime from transport, feature, and lifecycle components.
    /// </summary>
    /// <param name="transport">The LSP transport used for JSON-RPC messages.</param>
    /// <param name="features">The feature service used for request handling.</param>
    /// <param name="documentLifecycle">The document lifecycle handler used for notifications.</param>
    public VbaLanguageServerRuntime(
        LspMessageTransport transport,
        VbaLanguageFeatureService features,
        VbaDocumentLifecycle documentLifecycle)
    {
        this.transport = transport;
        this.features = features;
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
        var features = new VbaLanguageFeatureService(workspace);
        var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
            referenceCatalogCache,
            catalogRefreshService,
            transport);
        var documentLifecycle = new VbaDocumentLifecycle(transport, workspace, catalogRefresh);
        return new VbaLanguageServerRuntime(transport, features, documentLifecycle);
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

            if (!message.TryGetPropertyValue("method", out var methodNode))
            {
                continue;
            }

            var method = methodNode?.GetValue<string>();
            var hasId = message.TryGetPropertyValue("id", out var idNode);

            if (hasId)
            {
                await HandleRequestAsync(idNode, method, message["params"], cancellationToken);
                continue;
            }

            if (method == "exit")
            {
                Environment.ExitCode = shutdownRequested ? 0 : 1;
                return;
            }

            await HandleNotificationAsync(method, message["params"], cancellationToken);
        }
    }

    private async Task HandleRequestAsync(
        JsonNode? idNode,
        string? method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await transport.WriteResponseAsync(
                    idNode,
                    VbaLanguageFeatureService.CreateInitializeResult(),
                    cancellationToken);
                return;
            case "shutdown":
                shutdownRequested = true;
                await transport.WriteResponseAsync(idNode, null, cancellationToken);
                return;
            case "textDocument/completion":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateCompletionItems(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/documentSymbol":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateDocumentSymbols(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/definition":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateDefinitionLocation(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/references":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateReferenceLocations(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "workspace/symbol":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateWorkspaceSymbols(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/hover":
                await transport.WriteResponseAsync(idNode, features.CreateHover(parameters, cancellationToken), cancellationToken);
                return;
            case "textDocument/signatureHelp":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateSignatureHelp(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/prepareRename":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreatePrepareRename(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/rename":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateRenameEdit(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/formatting":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateFormattingEdits(parameters, cancellationToken),
                    cancellationToken);
                return;
            case "textDocument/semanticTokens/full":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateSemanticTokens(parameters, cancellationToken),
                    cancellationToken);
                return;
            default:
                await transport.WriteErrorResponseAsync(
                    idNode,
                    -32601,
                    $"Method not found: {method}",
                    cancellationToken);
                return;
        }
    }

    private async Task HandleNotificationAsync(
        string? method,
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
