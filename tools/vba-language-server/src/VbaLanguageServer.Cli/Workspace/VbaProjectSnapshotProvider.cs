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
        var inventorySnapshot = snapshotBuilder.CreateInventorySnapshot(
            activeUri,
            resolution,
            workspaceState.Documents,
            workspaceState.ExcludedSourceUris,
            cancellationToken);

        var cacheKey = CreateSnapshotCacheKey(activeUri, resolution);
        if (TryGetCachedSnapshot(
            cacheKey,
            workspaceState.Version,
            referenceCatalogState.Version,
            inventorySnapshot.Stamp,
            out var cachedSnapshot))
        {
            return cachedSnapshot;
        }

        var snapshot = snapshotBuilder.BuildSnapshot(
            resolution,
            inventorySnapshot.Documents,
            referenceCatalogState.CatalogSet);
        StoreCachedSnapshot(
            cacheKey,
            workspaceState.Version,
            referenceCatalogState.Version,
            inventorySnapshot.Stamp,
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
        string cacheKey,
        long expectedWorkspaceVersion,
        long expectedReferenceCatalogVersion,
        string expectedInventoryStamp,
        out VbaProjectSnapshot snapshot)
    {
        lock (gate)
        {
            if (cache.TryGetValue(cacheKey, out var cached)
                && cached.WorkspaceVersion == expectedWorkspaceVersion
                && cached.ReferenceCatalogVersion == expectedReferenceCatalogVersion
                && cached.InventoryStamp.Equals(expectedInventoryStamp, StringComparison.Ordinal))
            {
                snapshot = cached.Snapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    private void StoreCachedSnapshot(
        string cacheKey,
        long snapshotWorkspaceVersion,
        long snapshotReferenceCatalogVersion,
        string snapshotInventoryStamp,
        VbaProjectSnapshot snapshot)
    {
        lock (gate)
        {
            cache[cacheKey] = new CachedProjectSnapshot(
                snapshotWorkspaceVersion,
                snapshotReferenceCatalogVersion,
                snapshotInventoryStamp,
                snapshot);
        }
    }

    private static string CreateSnapshotCacheKey(string activeUri, VbaProjectResolution resolution)
        => string.Join(
            "\u001e",
            activeUri,
            resolution.Kind.ToString(),
            resolution.RootPath,
            resolution.ManifestPath ?? "",
            resolution.DocumentName ?? "",
            resolution.DocumentKind ?? "",
            string.Join("\u001f", resolution.ReferenceEntries.Select(reference => reference.Name)));

    private sealed record CachedProjectSnapshot(
        long WorkspaceVersion,
        long ReferenceCatalogVersion,
        string InventoryStamp,
        VbaProjectSnapshot Snapshot);
}
