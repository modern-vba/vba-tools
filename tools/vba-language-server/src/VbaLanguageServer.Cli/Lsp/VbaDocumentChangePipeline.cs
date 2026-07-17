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
    private readonly VbaProjectManifestWorkspace manifestWorkspace;
    private readonly IReferenceCatalogLifecycle catalogLifecycle;
    private readonly VbaDiagnosticsPublisher diagnosticsPublisher;

    /// <summary>
    /// Creates a document change pipeline.
    /// </summary>
    /// <param name="workspace">The workspace to update.</param>
    /// <param name="catalogLifecycle">The reference catalog lifecycle boundary.</param>
    /// <param name="diagnosticsPublisher">The diagnostics publisher.</param>
    public VbaDocumentChangePipeline(
        VbaLanguageWorkspace workspace,
        IReferenceCatalogLifecycle catalogLifecycle,
        VbaDiagnosticsPublisher diagnosticsPublisher)
    {
        this.workspace = workspace;
        manifestWorkspace = workspace.ManifestWorkspace;
        this.catalogLifecycle = catalogLifecycle;
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
        if (IsProjectManifestUri(change.Uri))
        {
            await ApplyManifestOverlayUpdateAsync(
                change.Uri,
                manifestWorkspace.OpenManifest(change.Uri, change.Version, change.Text),
                cancellationToken);
            return;
        }

        if (!IsVbaSourceUri(change.Uri))
        {
            return;
        }

        workspace.OpenDocument(change.Uri, change.Version, change.Text, cancellationToken);
        await ApplyAuthoritativeSourceTextAsync(change.Uri, cancellationToken);
        catalogLifecycle.ActivateProject(change.Uri);
    }

    private async Task ApplyChangedDocumentAsync(
        VbaTextDocumentChangedChange change,
        CancellationToken cancellationToken)
    {
        if (IsProjectManifestUri(change.Uri))
        {
            await ApplyManifestOverlayUpdateAsync(
                change.Uri,
                manifestWorkspace.ChangeManifest(change.Uri, change.Version, change.Text),
                cancellationToken);
            return;
        }

        if (!IsVbaSourceUri(change.Uri))
        {
            return;
        }

        if (workspace.ChangeDocument(change.Uri, change.Version, change.Text, cancellationToken) is null)
        {
            return;
        }

        await ApplyAuthoritativeSourceTextAsync(change.Uri, cancellationToken);
    }

    private async Task ApplyClosedDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        if (IsProjectManifestUri(uri))
        {
            if (manifestWorkspace.CloseManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(uri, cancellationToken);
            }

            return;
        }

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

        if (isManifest)
        {
            if (manifestWorkspace.ReloadManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(uri, cancellationToken);
            }

            return;
        }

        var text = await VbaSourceFileTextReader.ReadAllTextAsync(localPath, cancellationToken);
        if (!workspace.ReloadSourceDocument(uri, text, cancellationToken))
        {
            return;
        }

        await ApplyAuthoritativeSourceTextAsync(uri, cancellationToken);
    }

    private async Task ApplyWatchedFileDeletedAsync(string uri, CancellationToken cancellationToken)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null)
        {
            return;
        }

        if (IsProjectManifestPath(localPath))
        {
            if (manifestWorkspace.DeleteManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(uri, cancellationToken);
            }

            return;
        }

        if (!IsVbaSourcePath(localPath))
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
        CancellationToken cancellationToken)
    {
        await diagnosticsPublisher.PublishTrackedDiagnosticsAsync(uri, cancellationToken);
    }

    private async Task ApplyEffectiveManifestStateAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        if (manifestWorkspace.TryGetEffectiveManifest(
            uri,
            out var effectiveUri,
            out var text,
            out var error))
        {
            await diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
                uri,
                error: null,
                cancellationToken: cancellationToken);
            await ApplyManifestTextAsync(effectiveUri, text, cancellationToken);
            return;
        }

        await diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error,
            cancellationToken);
        catalogLifecycle.DeactivateManifest(uri);
    }

    private async Task ApplyManifestOverlayUpdateAsync(
        string uri,
        VbaProjectManifestOverlayUpdate update,
        CancellationToken cancellationToken)
    {
        if (!update.Accepted)
        {
            return;
        }

        if (update.Error is not null)
        {
            await diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
                uri,
                update.Error,
                cancellationToken);
            return;
        }

        if (update.EffectiveChanged)
        {
            await ApplyEffectiveManifestStateAsync(uri, cancellationToken);
        }
    }

    private Task ApplyManifestTextAsync(
        string uri,
        string text,
        CancellationToken cancellationToken)
    {
        catalogLifecycle.ApplyManifestSelectionChange(uri, text);
        return Task.CompletedTask;
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

    private static bool IsProjectManifestUri(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        return localPath is not null && IsProjectManifestPath(localPath);
    }
}
