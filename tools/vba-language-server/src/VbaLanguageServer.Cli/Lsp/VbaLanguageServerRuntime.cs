using System.Text.Json.Nodes;
using VbaLanguageServer.ProjectModel;
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
                await RecordOpenedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didChange":
                await RecordChangedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didClose":
                await RecordClosedDocumentAsync(parameters, cancellationToken);
                return;
            case "workspace/didChangeWatchedFiles":
                await RecordWatchedFilesChangedAsync(parameters, cancellationToken);
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
            if (IsVbaSourceUri(uri))
            {
                workspace.UpdateDocument(uri, text, cancellationToken);
                await PublishTrackedDiagnosticsAsync(uri, cancellationToken);
                await catalogRefresh.PublishReferenceSelectionTraceAsync(uri, cancellationToken);
            }

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
            if (IsVbaSourceUri(uri))
            {
                workspace.UpdateDocument(uri, text, cancellationToken);
                await PublishTrackedDiagnosticsAsync(uri, cancellationToken);
            }

            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    private async Task RecordClosedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return;
        }

        if (workspace.RemoveDocument(uri, cancellationToken))
        {
            await PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }
    }

    private async Task RecordWatchedFilesChangedAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var changes = parameters?["changes"]?.AsArray();
        if (changes is null)
        {
            return;
        }

        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = change?["uri"]?.GetValue<string>();
            var type = change?["type"]?.GetValue<int>();
            if (string.IsNullOrEmpty(uri) || type is null)
            {
                continue;
            }

            switch (type.Value)
            {
                case 1:
                case 2:
                    await ReloadChangedFileAsync(uri, cancellationToken);
                    break;
                case 3:
                    if (workspace.RemoveDocument(uri, cancellationToken))
                    {
                        await PublishEmptyDiagnosticsAsync(uri, cancellationToken);
                    }

                    break;
            }
        }
    }

    private async Task ReloadChangedFileAsync(string uri, CancellationToken cancellationToken)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null || !File.Exists(localPath))
        {
            return;
        }

        var text = await File.ReadAllTextAsync(localPath, cancellationToken);
        if (IsVbaSourcePath(localPath))
        {
            workspace.UpdateDocument(uri, text, cancellationToken);
            await PublishTrackedDiagnosticsAsync(uri, cancellationToken);
            await catalogRefresh.PublishReferenceSelectionTraceAsync(uri, cancellationToken);
            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
            return;
        }

        if (IsProjectManifestPath(localPath))
        {
            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
            foreach (var documentUri in workspace.GetDocumentUris(cancellationToken))
            {
                await catalogRefresh.PublishReferenceSelectionTraceAsync(documentUri, cancellationToken);
            }
        }
    }

    private Task PublishTrackedDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        var syntaxTree = workspace.GetDocumentSyntaxTree(uri, cancellationToken);
        if (syntaxTree is null)
        {
            return PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }

        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics = VbaLanguageFeatureService.CreateDiagnostics(uri, syntaxTree)
            },
            cancellationToken);
    }

    private Task PublishEmptyDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics = Array.Empty<object>()
            },
            cancellationToken);
    }

    private static bool IsVbaSourceUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null && IsVbaSourcePath(localPath);
    }

    private static bool IsVbaSourcePath(string path)
        => path.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".frm", StringComparison.OrdinalIgnoreCase);

    private static bool IsProjectManifestPath(string path)
        => Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase);
}
