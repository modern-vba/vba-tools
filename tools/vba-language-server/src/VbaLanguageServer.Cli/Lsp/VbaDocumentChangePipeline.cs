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
            var affectedTrackedSources =
                CaptureTrackedSourcesOwnedByManifest(
                    change.Uri,
                    cancellationToken);
            await ApplyManifestOverlayUpdateAsync(
                change.Uri,
                manifestWorkspace.OpenManifest(change.Uri, change.Version, change.Text),
                affectedTrackedSources,
                cancellationToken);
            workspace.RetireInactiveManifestState();
            return;
        }

        if (!IsVbaSourceUri(change.Uri))
        {
            return;
        }

        workspace.OpenDocument(change.Uri, change.Version, change.Text, cancellationToken);
        catalogLifecycle.ActivateProject(change.Uri);
        await ApplyAuthoritativeSourceTextAsync(change.Uri, cancellationToken);
    }

    private async Task ApplyChangedDocumentAsync(
        VbaTextDocumentChangedChange change,
        CancellationToken cancellationToken)
    {
        if (IsProjectManifestUri(change.Uri))
        {
            var affectedTrackedSources =
                CaptureTrackedSourcesOwnedByManifest(
                    change.Uri,
                    cancellationToken);
            await ApplyManifestOverlayUpdateAsync(
                change.Uri,
                manifestWorkspace.ChangeManifest(change.Uri, change.Version, change.Text),
                affectedTrackedSources,
                cancellationToken);
            workspace.RetireInactiveManifestState();
            return;
        }

        if (!IsVbaSourceUri(change.Uri))
        {
            return;
        }

        if (!workspace.ChangeDocument(change.Uri, change.Version, change.Text, cancellationToken))
        {
            return;
        }

        await ApplyAuthoritativeSourceTextAsync(change.Uri, cancellationToken);
    }

    private async Task ApplyClosedDocumentAsync(string uri, CancellationToken cancellationToken)
    {
        if (IsProjectManifestUri(uri))
        {
            var affectedOpenSources =
                CaptureOpenSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            var affectedTrackedSources =
                CaptureTrackedSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            if (manifestWorkspace.CloseManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(
                    uri,
                    affectedTrackedSources,
                    cancellationToken);
                ReactivateTransferredOpenSourceCatalogs(
                    uri,
                    affectedOpenSources,
                    cancellationToken);
            }

            workspace.RetireInactiveManifestState();
            return;
        }

        if (!IsVbaSourceUri(uri))
        {
            return;
        }

        var affectedProjectUris = workspace
            .CreateProjectSnapshot(uri, cancellationToken)
            .SourceDocuments
            .Keys
            .ToArray();
        if (workspace.CloseDocument(uri, cancellationToken))
        {
            diagnosticsPublisher.CancelProjectValidationsForDocuments([uri]);
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
            var remainingUri = affectedProjectUris.FirstOrDefault(candidate =>
                !VbaProjectIdentityModel.SameDocument(candidate, uri)
                && workspace.GetDocumentAnalysis(candidate, cancellationToken) is not null);
            if (remainingUri is not null)
            {
                await diagnosticsPublisher.PublishProjectDiagnosticsAsync(
                    remainingUri,
                    cancellationToken);
            }
        }
    }

    private async Task ApplyWatchedFileReloadAsync(string uri, CancellationToken cancellationToken)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null)
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
            var affectedOpenSources =
                CaptureOpenSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            var affectedTrackedSources =
                CaptureTrackedSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            if (manifestWorkspace.ReloadManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(
                    uri,
                    affectedTrackedSources,
                    cancellationToken);
                ReactivateTransferredOpenSourceCatalogs(
                    uri,
                    affectedOpenSources,
                    cancellationToken);
            }

            workspace.RetireInactiveManifestState();
            return;
        }

        if (!workspace.ReloadSourceDocumentFromDisk(
                uri,
                cancellationToken))
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
            var affectedOpenSources =
                CaptureOpenSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            var affectedTrackedSources =
                CaptureTrackedSourcesOwnedByManifest(
                    uri,
                    cancellationToken);
            if (manifestWorkspace.DeleteManifest(uri))
            {
                await ApplyEffectiveManifestStateAsync(
                    uri,
                    affectedTrackedSources,
                    cancellationToken);
                ReactivateTransferredOpenSourceCatalogs(
                    uri,
                    affectedOpenSources,
                    cancellationToken);
            }

            workspace.RetireInactiveManifestState();
            return;
        }

        if (!IsVbaSourcePath(localPath))
        {
            return;
        }

        var affectedProjectUris = workspace
            .CreateProjectSnapshot(uri, cancellationToken)
            .SourceDocuments
            .Keys
            .ToArray();
        if (workspace.DeleteSourceDocument(uri, cancellationToken))
        {
            diagnosticsPublisher.CancelProjectValidationsForDocuments([uri]);
            await diagnosticsPublisher.PublishEmptyDiagnosticsAsync(uri, cancellationToken);
            var remainingUri = affectedProjectUris.FirstOrDefault(candidate =>
                !VbaProjectIdentityModel.SameDocument(candidate, uri)
                && workspace.GetDocumentAnalysis(candidate, cancellationToken) is not null);
            if (remainingUri is not null)
            {
                await diagnosticsPublisher.PublishProjectDiagnosticsAsync(
                    remainingUri,
                    cancellationToken);
            }
        }
    }

    private async Task ApplyAuthoritativeSourceTextAsync(
        string uri,
        CancellationToken cancellationToken)
    {
        diagnosticsPublisher.InvalidateProjectValidationsForDocuments([uri]);
        await diagnosticsPublisher.PublishProjectDiagnosticsAsync(uri, cancellationToken);
    }

    private async Task ApplyEffectiveManifestStateAsync(
        string uri,
        IReadOnlyList<string> previouslyAffectedSourceUris,
        CancellationToken cancellationToken)
    {
        diagnosticsPublisher.CancelProjectValidationsForDocuments(
            previouslyAffectedSourceUris);
        if (manifestWorkspace.TryGetEffectiveManifest(
            uri,
            out var effectiveUri,
            out var text,
            out var error))
        {
            await diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
                uri,
                error,
                cancellationToken: cancellationToken);
            await ApplyManifestTextAsync(effectiveUri, text, cancellationToken);
            await PublishAffectedManifestProjectDiagnosticsAsync(
                uri,
                previouslyAffectedSourceUris,
                cancellationToken);
            return;
        }

        await diagnosticsPublisher.PublishManifestValidationDiagnosticAsync(
            uri,
            error,
            cancellationToken);
        catalogLifecycle.DeactivateManifest(uri);
        await PublishAffectedManifestProjectDiagnosticsAsync(
            uri,
            previouslyAffectedSourceUris,
            cancellationToken);
    }

    private async Task ApplyManifestOverlayUpdateAsync(
        string uri,
        VbaProjectManifestOverlayUpdate update,
        IReadOnlyList<string> previouslyAffectedSourceUris,
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
            await ApplyEffectiveManifestStateAsync(
                uri,
                previouslyAffectedSourceUris,
                cancellationToken);
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

    private async Task PublishAffectedManifestProjectDiagnosticsAsync(
        string manifestUri,
        IReadOnlyList<string> previouslyAffectedSourceUris,
        CancellationToken cancellationToken)
    {
        var remainingUris = new Dictionary<VbaDocumentIdentity, string>();
        foreach (var uri in previouslyAffectedSourceUris
            .Concat(CaptureTrackedSourcesOwnedByManifest(
                manifestUri,
                cancellationToken))
            .Where(IsVbaSourceUri))
        {
            if (VbaProjectIdentityModel.TryIdentifyDocument(
                    uri,
                    out var identity))
            {
                remainingUris.TryAdd(identity, uri);
            }
        }

        while (remainingUris.Count > 0)
        {
            var active = remainingUris
                .OrderBy(
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Value, StringComparer.Ordinal)
                .First();
            var activeUri = active.Value;
            var projectUris = workspace
                .CreateProjectSnapshot(activeUri, cancellationToken)
                .SourceDocuments
                .Keys;
            foreach (var projectUri in projectUris)
            {
                if (VbaProjectIdentityModel.TryIdentifyDocument(
                        projectUri,
                        out var projectIdentity))
                {
                    remainingUris.Remove(projectIdentity);
                }
            }

            remainingUris.Remove(active.Key);
            await diagnosticsPublisher.PublishProjectDiagnosticsAsync(
                activeUri,
                cancellationToken);
        }
    }

    private IReadOnlyList<string> CaptureTrackedSourcesOwnedByManifest(
        string manifestUri,
        CancellationToken cancellationToken)
    {
        return !VbaProjectIdentityModel.TryIdentifyDocument(
                manifestUri,
                out var manifestDocument)
                || !manifestDocument.IsLocalFile
            ? []
            : workspace.GetDocumentUris(cancellationToken)
                .Where(sourceUri =>
                    VbaProjectIdentityModel.TryIdentifyAuthority(
                        manifestWorkspace
                            .CaptureResolution(sourceUri)
                            .Resolution,
                        out var authority)
                    && authority.UsesManifest(manifestDocument))
                .OrderBy(uri => uri, StringComparer.OrdinalIgnoreCase)
                .ThenBy(uri => uri, StringComparer.Ordinal)
                .ToArray();
    }

    private IReadOnlyList<string> CaptureOpenSourcesOwnedByManifest(
        string manifestUri,
        CancellationToken cancellationToken)
    {
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                manifestUri,
                out var manifestDocument)
            || !manifestDocument.IsLocalFile)
        {
            return [];
        }

        var manifestPath =
            VbaProjectResolver.TryGetLocalPath(manifestUri);
        if (manifestPath is null)
        {
            return [];
        }

        var manifestDirectory =
            Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        return workspace.GetOpenDocumentUris(cancellationToken)
            .Where(sourceUri =>
            {
                var resolution = manifestWorkspace
                    .CaptureResolution(sourceUri)
                    .Resolution;
                if (VbaProjectIdentityModel.TryIdentifyAuthority(
                        resolution,
                        out var authority)
                    && authority.UsesManifest(manifestDocument))
                {
                    return true;
                }

                var sourcePath =
                    VbaProjectResolver.TryGetLocalPath(sourceUri);
                return resolution.Kind
                        == VbaProjectResolutionKind.AdHoc
                    && manifestDirectory is not null
                    && sourcePath is not null
                    && VbaProjectResolver.IsPathUnder(
                        sourcePath,
                        manifestDirectory);
            })
            .ToArray();
    }

    private void ReactivateTransferredOpenSourceCatalogs(
        string previousManifestUri,
        IReadOnlyList<string> sourceUris,
        CancellationToken cancellationToken)
    {
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                previousManifestUri,
                out var previousManifestDocument)
            || !previousManifestDocument.IsLocalFile)
        {
            return;
        }

        var activatedScopes = new HashSet<VbaProjectAuthorityIdentity>();
        foreach (var sourceUri in sourceUris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = manifestWorkspace
                .CaptureResolution(sourceUri)
                .Resolution;
            if (resolution.Kind
                    != VbaProjectResolutionKind.ManifestDocument
                || !VbaProjectIdentityModel.TryIdentifyAuthority(
                    resolution,
                    out var authority)
                || authority.UsesManifest(previousManifestDocument))
            {
                continue;
            }

            if (activatedScopes.Add(authority))
            {
                catalogLifecycle.ActivateProject(sourceUri);
            }
        }
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
