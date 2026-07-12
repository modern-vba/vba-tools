using System.Text.Json.Nodes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Handles LSP document lifecycle notifications and diagnostic publication.
/// </summary>
internal sealed class VbaDocumentLifecycle
{
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly ReferenceCatalogRefreshCoordinator catalogRefresh;

    /// <summary>
    /// Creates a document lifecycle handler.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that tracks open documents.</param>
    /// <param name="catalogRefresh">The coordinator used for reference trace and refresh work.</param>
    public VbaDocumentLifecycle(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        ReferenceCatalogRefreshCoordinator catalogRefresh)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.catalogRefresh = catalogRefresh;
    }

    /// <summary>
    /// Records a textDocument/didOpen notification and publishes diagnostics for VBA sources.
    /// </summary>
    /// <param name="parameters">The LSP notification parameters.</param>
    /// <param name="cancellationToken">A cancellation token for lifecycle work.</param>
    public async Task RecordOpenedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var text = textDocument?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            if (IsVbaSourceUri(uri))
            {
                workspace.UpdateDocument(uri, text, cancellationToken);
                await catalogRefresh.PreloadReferenceCatalogsAsync(uri, text, cancellationToken);
                await PublishTrackedDiagnosticsAsync(uri, cancellationToken);
                await catalogRefresh.PublishReferenceSelectionTraceAsync(uri, cancellationToken);
            }

            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    /// <summary>
    /// Records a textDocument/didChange notification and publishes diagnostics for VBA sources.
    /// </summary>
    /// <param name="parameters">The LSP notification parameters.</param>
    /// <param name="cancellationToken">A cancellation token for lifecycle work.</param>
    public async Task RecordChangedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
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
                await catalogRefresh.PreloadReferenceCatalogsAsync(uri, text, cancellationToken);
                await PublishTrackedDiagnosticsAsync(uri, cancellationToken);
            }

            catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        }
    }

    /// <summary>
    /// Records a textDocument/didClose notification and clears diagnostics for removed tracked documents.
    /// </summary>
    /// <param name="parameters">The LSP notification parameters.</param>
    /// <param name="cancellationToken">A cancellation token for lifecycle work.</param>
    public async Task RecordClosedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
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

    /// <summary>
    /// Records workspace file changes by reloading changed sources and excluding deleted source files.
    /// </summary>
    /// <param name="parameters">The LSP notification parameters.</param>
    /// <param name="cancellationToken">A cancellation token for lifecycle work.</param>
    public async Task RecordWatchedFilesChangedAsync(JsonNode? parameters, CancellationToken cancellationToken)
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
                    if (workspace.RemoveSourceDocument(uri, cancellationToken))
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
