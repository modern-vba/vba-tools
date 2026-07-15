using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Represents one valid document or watched-file change decoded from LSP parameters.
/// </summary>
/// <param name="Uri">The document or watched-file URI.</param>
internal abstract record VbaDocumentChange(string Uri);

/// <summary>
/// Represents a newly opened versioned client document.
/// </summary>
internal sealed record VbaTextDocumentOpenedChange(
    string Uri,
    int Version,
    string Text) : VbaDocumentChange(Uri);

/// <summary>
/// Represents a complete-text change to an open client document.
/// </summary>
internal sealed record VbaTextDocumentChangedChange(
    string Uri,
    int Version,
    string Text) : VbaDocumentChange(Uri);

/// <summary>
/// Represents a closed client document.
/// </summary>
internal sealed record VbaTextDocumentClosedChange(string Uri) : VbaDocumentChange(Uri);

/// <summary>
/// Represents a created or changed watched file that must be reloaded from disk.
/// </summary>
internal sealed record VbaWatchedFileReloadChange(string Uri) : VbaDocumentChange(Uri);

/// <summary>
/// Represents a deleted watched file.
/// </summary>
internal sealed record VbaWatchedFileDeletedChange(string Uri) : VbaDocumentChange(Uri);

/// <summary>
/// Applies document changes in the required workspace, diagnostics, trace, and refresh order.
/// </summary>
internal sealed class VbaDocumentChangePipeline
{
    private readonly VbaLanguageWorkspace workspace;
    private readonly ReferenceCatalogRefreshCoordinator catalogRefresh;
    private readonly VbaDiagnosticsPublisher diagnosticsPublisher;

    /// <summary>
    /// Creates a document change pipeline.
    /// </summary>
    /// <param name="workspace">The workspace to update.</param>
    /// <param name="catalogRefresh">The catalog refresh coordinator.</param>
    /// <param name="diagnosticsPublisher">The diagnostics publisher.</param>
    public VbaDocumentChangePipeline(
        VbaLanguageWorkspace workspace,
        ReferenceCatalogRefreshCoordinator catalogRefresh,
        VbaDiagnosticsPublisher diagnosticsPublisher)
    {
        this.workspace = workspace;
        this.catalogRefresh = catalogRefresh;
        this.diagnosticsPublisher = diagnosticsPublisher;
    }

    /// <summary>
    /// Applies one decoded document change.
    /// </summary>
    /// <param name="change">The discriminated document change.</param>
    /// <param name="cancellationToken">A cancellation token for pipeline work.</param>
    public async Task ApplyAsync(VbaDocumentChange change, CancellationToken cancellationToken)
    {
        switch (change)
        {
            case VbaTextDocumentOpenedChange opened:
                await ApplyOpenedDocumentAsync(opened, cancellationToken);
                return;
            case VbaTextDocumentChangedChange changed:
                await ApplyChangedDocumentAsync(changed, cancellationToken);
                return;
            case VbaTextDocumentClosedChange closed:
                await ApplyClosedDocumentAsync(closed.Uri, cancellationToken);
                return;
            case VbaWatchedFileReloadChange reload:
                await ApplyWatchedFileReloadAsync(reload.Uri, cancellationToken);
                return;
            case VbaWatchedFileDeletedChange deleted:
                await ApplyWatchedFileDeletedAsync(deleted.Uri, cancellationToken);
                return;
        }
    }

    private async Task ApplyOpenedDocumentAsync(
        VbaTextDocumentOpenedChange change,
        CancellationToken cancellationToken)
    {
        if (!IsVbaSourceUri(change.Uri))
        {
            catalogRefresh.RefreshReferenceCatalogsInBackground(
                change.Uri,
                change.Text,
                cancellationToken);
            return;
        }

        workspace.OpenDocument(change.Uri, change.Version, change.Text, cancellationToken);
        await ApplyAuthoritativeSourceTextAsync(
            change.Uri,
            change.Text,
            preloadPersistedCatalogs: true,
            publishReferenceTrace: true,
            cancellationToken);
    }

    private async Task ApplyChangedDocumentAsync(
        VbaTextDocumentChangedChange change,
        CancellationToken cancellationToken)
    {
        if (!IsVbaSourceUri(change.Uri))
        {
            catalogRefresh.RefreshReferenceCatalogsInBackground(
                change.Uri,
                change.Text,
                cancellationToken);
            return;
        }

        if (workspace.ChangeDocument(change.Uri, change.Version, change.Text, cancellationToken) is null)
        {
            return;
        }

        await ApplyAuthoritativeSourceTextAsync(
            change.Uri,
            change.Text,
            preloadPersistedCatalogs: true,
            publishReferenceTrace: false,
            cancellationToken);
    }

    private async Task ApplyClosedDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        if (workspace.CloseDocument(uri, cancellationToken))
        {
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }
    }

    private async Task ApplyWatchedFileReloadAsync(string uri, CancellationToken cancellationToken)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null || !File.Exists(localPath))
        {
            return;
        }

        var isSource = IsVbaSourcePath(localPath);
        var isManifest = IsProjectManifestPath(localPath);
        if (!isSource && !isManifest)
        {
            return;
        }

        var text = await VbaSourceFileTextReader.ReadAllTextAsync(localPath, cancellationToken);
        if (isSource)
        {
            if (!workspace.ReloadSourceDocument(uri, text, cancellationToken))
            {
                return;
            }

            await ApplyAuthoritativeSourceTextAsync(
                uri,
                text,
                preloadPersistedCatalogs: false,
                publishReferenceTrace: true,
                cancellationToken);
            return;
        }

        catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
        foreach (var documentUri in workspace.GetDocumentUris(cancellationToken))
        {
            await catalogRefresh.PublishReferenceSelectionTraceAsync(documentUri, cancellationToken);
        }
    }

    private async Task ApplyWatchedFileDeletedAsync(string uri, CancellationToken cancellationToken)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null || !IsVbaSourcePath(localPath))
        {
            return;
        }

        if (workspace.DeleteSourceDocument(uri, cancellationToken))
        {
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }
    }

    private async Task ApplyAuthoritativeSourceTextAsync(
        string uri,
        string fallbackText,
        bool preloadPersistedCatalogs,
        bool publishReferenceTrace,
        CancellationToken cancellationToken)
    {
        var text = workspace.GetDocumentText(uri, cancellationToken) ?? fallbackText;
        if (preloadPersistedCatalogs)
        {
            await catalogRefresh.PreloadReferenceCatalogsAsync(uri, text, cancellationToken);
        }

        await diagnosticsPublisher.PublishTrackedDiagnosticsAsync(uri, cancellationToken);
        if (publishReferenceTrace)
        {
            await catalogRefresh.PublishReferenceSelectionTraceAsync(uri, cancellationToken);
        }

        catalogRefresh.RefreshReferenceCatalogsInBackground(uri, text, cancellationToken);
    }

    private static bool IsVbaSourceUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null && IsVbaSourcePath(localPath);
    }

    internal static bool IsVbaSourcePath(string path)
        => path.EndsWith(".bas", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".cls", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".frm", StringComparison.OrdinalIgnoreCase);

    internal static bool IsProjectManifestPath(string path)
        => Path.GetFileName(path).Equals("vba-project.json", StringComparison.OrdinalIgnoreCase);
}
