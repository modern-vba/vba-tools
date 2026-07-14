using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents the document state used to create a project snapshot.
/// </summary>
/// <param name="Documents">The tracked workspace documents.</param>
/// <param name="ExcludedSourceUris">The source URIs excluded from disk inventory.</param>
/// <param name="Version">The workspace document-state version.</param>
internal sealed record VbaWorkspaceSnapshotState(
    IReadOnlyDictionary<string, VbaTrackedDocument> Documents,
    IReadOnlySet<string> ExcludedSourceUris,
    long Version);

/// <summary>
/// Represents the stable cache identity for one project snapshot scope.
/// </summary>
internal sealed record VbaProjectSnapshotIdentity(string Key)
{
    public static VbaProjectSnapshotIdentity Create(string activeUri, VbaProjectResolution resolution)
        => new(string.Join(
            "\u001e",
            activeUri,
            resolution.Kind.ToString(),
            resolution.RootPath,
            resolution.ManifestPath ?? "",
            resolution.DocumentName ?? "",
            resolution.DocumentKind ?? "",
            string.Join("\u001f", resolution.ReferenceEntries.Select(reference => reference.Name))));
}

/// <summary>
/// Creates and caches immutable project snapshots from workspace state.
/// </summary>
internal sealed class VbaProjectSnapshotProvider
{
    private readonly object gate = new();
    private readonly Dictionary<string, CachedProjectSnapshot> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectSnapshotBuilder snapshotBuilder;

    public VbaProjectSnapshotProvider(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        VbaProjectSourceDocumentCache diskDocumentCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        snapshotBuilder = new VbaProjectSnapshotBuilder(diskDocumentCache);
    }

    public VbaProjectSnapshot CreateProjectSnapshot(
        string activeUri,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = VbaProjectResolver.Resolve(activeUri);
        var referenceCatalogState = referenceCatalogCache.State;
        var cacheIdentity = VbaProjectSnapshotIdentity.Create(activeUri, resolution);
        if (TryGetCachedSnapshot(
            cacheIdentity,
            workspaceState.Version,
            referenceCatalogState.Version,
            cancellationToken,
            out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        var inventorySnapshot = snapshotBuilder.CreateInventorySnapshot(
            activeUri,
            resolution,
            workspaceState.Documents,
            workspaceState.ExcludedSourceUris,
            cancellationToken);

        var snapshot = snapshotBuilder.BuildSnapshot(
            resolution,
            inventorySnapshot.Documents,
            referenceCatalogState.CatalogSet);
        StoreCachedSnapshot(
            cacheIdentity,
            workspaceState.Version,
            referenceCatalogState.Version,
            inventorySnapshot.SourceFiles,
            snapshot);
        return snapshot;
    }

    public void Invalidate()
    {
        lock (gate)
        {
            cache.Clear();
        }
    }

    private bool TryGetCachedSnapshot(
        VbaProjectSnapshotIdentity cacheIdentity,
        long expectedWorkspaceVersion,
        long expectedReferenceCatalogVersion,
        CancellationToken cancellationToken,
        out VbaProjectSnapshot snapshot)
    {
        lock (gate)
        {
            if (cache.TryGetValue(cacheIdentity.Key, out var cached)
                && cached.WorkspaceVersion == expectedWorkspaceVersion
                && cached.ReferenceCatalogVersion == expectedReferenceCatalogVersion
                && AreKnownSourcesCurrent(cached.SourceFiles, cancellationToken))
            {
                snapshot = cached.Snapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    private void StoreCachedSnapshot(
        VbaProjectSnapshotIdentity cacheIdentity,
        long snapshotWorkspaceVersion,
        long snapshotReferenceCatalogVersion,
        IReadOnlyList<VbaProjectSourceFileState> sourceFiles,
        VbaProjectSnapshot snapshot)
    {
        lock (gate)
        {
            cache[cacheIdentity.Key] = new CachedProjectSnapshot(
                snapshotWorkspaceVersion,
                snapshotReferenceCatalogVersion,
                sourceFiles,
                snapshot);
        }
    }

    private static bool AreKnownSourcesCurrent(
        IReadOnlyList<VbaProjectSourceFileState> sourceFiles,
        CancellationToken cancellationToken)
    {
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceFile.IsCurrent())
            {
                return false;
            }
        }

        return true;
    }

    private sealed record CachedProjectSnapshot(
        long WorkspaceVersion,
        long ReferenceCatalogVersion,
        IReadOnlyList<VbaProjectSourceFileState> SourceFiles,
        VbaProjectSnapshot Snapshot);
}
