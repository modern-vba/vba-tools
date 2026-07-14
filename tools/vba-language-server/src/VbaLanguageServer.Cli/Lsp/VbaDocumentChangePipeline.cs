using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Identifies document and watched-file changes handled by the language server.
/// </summary>
internal enum VbaDocumentChangeKind
{
    /// <summary>
    /// A text document was opened by the client.
    /// </summary>
    Opened,

    /// <summary>
    /// A text document was changed by the client.
    /// </summary>
    Changed,

    /// <summary>
    /// A text document was closed by the client.
    /// </summary>
    Closed,

    /// <summary>
    /// A watched VBA source file was created or changed.
    /// </summary>
    SourceFileChanged,

    /// <summary>
    /// A watched VBA source file was deleted.
    /// </summary>
    SourceFileDeleted,

    /// <summary>
    /// A project manifest was created or changed.
    /// </summary>
    ProjectManifestChanged
}

/// <summary>
/// Represents a typed document lifecycle event after LSP JSON parameters have been decoded.
/// </summary>
/// <param name="Kind">The lifecycle event kind.</param>
/// <param name="Uri">The document or file URI.</param>
/// <param name="Text">The complete document text when the event carries content.</param>
internal sealed record VbaDocumentChange(
    VbaDocumentChangeKind Kind,
    string Uri,
    string? Text = null);

/// <summary>
/// Applies document changes in the required workspace, diagnostics, and reference-catalog order.
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
    /// <param name="change">The typed document change.</param>
    /// <param name="cancellationToken">A cancellation token for pipeline work.</param>
    public async Task ApplyAsync(VbaDocumentChange change, CancellationToken cancellationToken)
    {
        switch (change.Kind)
        {
            case VbaDocumentChangeKind.Opened:
            case VbaDocumentChangeKind.Changed:
                await ApplyTextDocumentChangeAsync(change, cancellationToken);
                return;
            case VbaDocumentChangeKind.Closed:
                await ApplyClosedDocumentAsync(change.Uri, cancellationToken);
                return;
            case VbaDocumentChangeKind.SourceFileChanged:
                await ApplyChangedSourceFileAsync(change, cancellationToken);
                return;
            case VbaDocumentChangeKind.SourceFileDeleted:
                await ApplyDeletedSourceFileAsync(change.Uri, cancellationToken);
                return;
            case VbaDocumentChangeKind.ProjectManifestChanged:
                await ApplyProjectManifestChangeAsync(change, cancellationToken);
                return;
            default:
                return;
        }
    }

    private async Task ApplyTextDocumentChangeAsync(
        VbaDocumentChange change,
        CancellationToken cancellationToken)
    {
        if (change.Text is null)
        {
            return;
        }

        if (IsVbaSourceUri(change.Uri))
        {
            workspace.UpdateDocument(change.Uri, change.Text, cancellationToken);
            await catalogRefresh.ApplyDocumentChangeAsync(
                new ReferenceCatalogDocumentChange(
                    change.Uri,
                    change.Text,
                    PreloadPersistedCatalogs: true,
                    PublishReferenceTrace: change.Kind == VbaDocumentChangeKind.Opened,
                    RefreshInBackground: true),
                token => diagnosticsPublisher.PublishTrackedDiagnosticsAsync(change.Uri, token),
                cancellationToken);
            return;
        }

        await catalogRefresh.ApplyDocumentChangeAsync(
            new ReferenceCatalogDocumentChange(
                change.Uri,
                change.Text,
                PreloadPersistedCatalogs: false,
                PublishReferenceTrace: false,
                RefreshInBackground: true),
            _ => Task.CompletedTask,
            cancellationToken);
    }

    private async Task ApplyClosedDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        if (workspace.RemoveDocument(uri, cancellationToken))
        {
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }
    }

    private async Task ApplyChangedSourceFileAsync(
        VbaDocumentChange change,
        CancellationToken cancellationToken)
    {
        if (change.Text is null)
        {
            return;
        }

        workspace.UpdateDocument(change.Uri, change.Text, cancellationToken);
        await catalogRefresh.ApplyDocumentChangeAsync(
            new ReferenceCatalogDocumentChange(
                change.Uri,
                change.Text,
                PreloadPersistedCatalogs: false,
                PublishReferenceTrace: true,
                RefreshInBackground: true),
            token => diagnosticsPublisher.PublishTrackedDiagnosticsAsync(change.Uri, token),
            cancellationToken);
    }

    private async Task ApplyDeletedSourceFileAsync(string uri, CancellationToken cancellationToken)
    {
        if (workspace.RemoveSourceDocument(uri, cancellationToken))
        {
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }
    }

    private async Task ApplyProjectManifestChangeAsync(
        VbaDocumentChange change,
        CancellationToken cancellationToken)
    {
        if (change.Text is null)
        {
            return;
        }

        await catalogRefresh.ApplyProjectManifestChangeAsync(
            change.Uri,
            change.Text,
            workspace.GetDocumentUris(cancellationToken),
            cancellationToken);
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
}
