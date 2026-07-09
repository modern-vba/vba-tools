using System.Text.Json.Nodes;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal sealed class VbaLanguageServerRuntime
{
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly VbaLanguageFeatureService features;
    private readonly ReferenceCatalogRefreshCoordinator catalogRefresh;
    private bool shutdownRequested;

    public VbaLanguageServerRuntime(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        VbaLanguageFeatureService features,
        ReferenceCatalogRefreshCoordinator catalogRefresh)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.features = features;
        this.catalogRefresh = catalogRefresh;
    }

    public static VbaLanguageServerRuntime CreateDefault(Stream input, Stream output)
    {
        var transport = new LspMessageTransport(input, output);
        var referenceCatalogCache = new VbaProjectReferenceCatalogCache(
            VbaProjectReferenceCatalogSet.CreateBundled());
        var catalogRefreshService = new VbaProjectReferenceCatalogRefreshService(
            referenceCatalogCache,
            new TypeLibReferenceCatalogDiscovery(new RegistryTypeLibRegistryReader()));
        var workspace = new VbaLanguageWorkspace(referenceCatalogCache);
        var features = new VbaLanguageFeatureService(workspace);
        var catalogRefresh = new ReferenceCatalogRefreshCoordinator(
            referenceCatalogCache,
            catalogRefreshService,
            transport);
        return new VbaLanguageServerRuntime(transport, workspace, features, catalogRefresh);
    }

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
                    features.CreateCompletionItems(parameters),
                    cancellationToken);
                return;
            case "textDocument/documentSymbol":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateDocumentSymbols(parameters),
                    cancellationToken);
                return;
            case "textDocument/definition":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateDefinitionLocation(parameters),
                    cancellationToken);
                return;
            case "textDocument/references":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateReferenceLocations(parameters),
                    cancellationToken);
                return;
            case "workspace/symbol":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateWorkspaceSymbols(parameters),
                    cancellationToken);
                return;
            case "textDocument/hover":
                await transport.WriteResponseAsync(idNode, features.CreateHover(parameters), cancellationToken);
                return;
            case "textDocument/signatureHelp":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateSignatureHelp(parameters),
                    cancellationToken);
                return;
            case "textDocument/prepareRename":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreatePrepareRename(parameters),
                    cancellationToken);
                return;
            case "textDocument/rename":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateRenameEdit(parameters),
                    cancellationToken);
                return;
            case "textDocument/formatting":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateFormattingEdits(parameters),
                    cancellationToken);
                return;
            case "textDocument/semanticTokens/full":
                await transport.WriteResponseAsync(
                    idNode,
                    features.CreateSemanticTokens(parameters),
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
                await RecordOpenedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didChange":
                await RecordChangedDocumentAsync(parameters, cancellationToken);
                return;
            default:
                return;
        }
    }

    private async Task RecordOpenedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var text = textDocument?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            workspace.UpdateDocument(uri, text);
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
            await catalogRefresh.PublishReferenceSelectionTraceAsync(uri, cancellationToken);
            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    private async Task RecordChangedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var changes = parameters?["contentChanges"]?.AsArray();
        var text = changes?.LastOrDefault()?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            workspace.UpdateDocument(uri, text);
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    private Task PublishDiagnosticsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics = VbaLanguageFeatureService.CreateDiagnostics(uri, text)
            },
            cancellationToken);
    }
}
