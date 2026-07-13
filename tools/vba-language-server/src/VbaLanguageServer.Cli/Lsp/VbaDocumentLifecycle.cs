using System.Text.Json.Nodes;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Handles LSP document lifecycle notifications and diagnostic publication.
/// </summary>
internal sealed class VbaDocumentLifecycle
{
    private readonly VbaDocumentChangePipeline pipeline;

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
        pipeline = new VbaDocumentChangePipeline(
            workspace,
            catalogRefresh,
            new VbaDiagnosticsPublisher(transport, workspace));
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
            await pipeline.ApplyAsync(
                new VbaDocumentChange(VbaDocumentChangeKind.Opened, uri, text),
                cancellationToken);
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
            await pipeline.ApplyAsync(
                new VbaDocumentChange(VbaDocumentChangeKind.Changed, uri, text),
                cancellationToken);
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

        await pipeline.ApplyAsync(
            new VbaDocumentChange(VbaDocumentChangeKind.Closed, uri),
            cancellationToken);
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
                    await pipeline.ApplyAsync(
                        new VbaDocumentChange(VbaDocumentChangeKind.SourceFileDeleted, uri),
                        cancellationToken);
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
        if (VbaDocumentChangePipeline.IsVbaSourcePath(localPath))
        {
            await pipeline.ApplyAsync(
                new VbaDocumentChange(VbaDocumentChangeKind.SourceFileChanged, uri, text),
                cancellationToken);
            return;
        }

        if (IsProjectManifestPath(localPath))
        {
            await pipeline.ApplyAsync(
                new VbaDocumentChange(VbaDocumentChangeKind.ProjectManifestChanged, uri, text),
                cancellationToken);
        }
    }

    private static bool IsProjectManifestPath(string path)
        => Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase);
}
