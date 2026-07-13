using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents an immutable snapshot of one resolved VBA project scope.
/// </summary>
/// <param name="Resolution">The project boundary resolution.</param>
/// <param name="SourceDocuments">The source text documents included in the scope, keyed by URI.</param>
/// <param name="ReferenceSelection">The active reference selection for the scope.</param>
/// <param name="SourceIndex">The source index built from the scoped documents and reference catalogs.</param>
public sealed record VbaProjectSnapshot(
    VbaProjectResolution Resolution,
    IReadOnlyDictionary<string, string> SourceDocuments,
    VbaProjectReferenceSelection? ReferenceSelection,
    VbaSourceIndex SourceIndex);

/// <summary>
/// Represents one document tracked in workspace memory.
/// </summary>
/// <param name="Uri">The document URI.</param>
/// <param name="Text">The latest document text.</param>
/// <param name="SyntaxTree">The latest parsed syntax tree.</param>
/// <param name="LastParseUpdateKind">The last parse update granularity.</param>
/// <param name="SourceDocument">The projected source document, when already available.</param>
public sealed record VbaTrackedDocument(
    string Uri,
    string Text,
    VbaSyntaxTree SyntaxTree,
    VbaSyntaxTreeParseUpdateKind LastParseUpdateKind,
    VbaSourceDocument? SourceDocument = null);

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed class VbaLanguageWorkspace
{
    private readonly object gate = new();
    private readonly Dictionary<string, VbaTrackedDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedProjectSnapshot> projectSnapshotCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache = new();
    private readonly VbaProjectSnapshotBuilder snapshotBuilder;
    private long workspaceVersion;

    /// <summary>
    /// Creates a language workspace.
    /// </summary>
    /// <param name="referenceCatalogCache">The reference catalog cache used when building source indexes.</param>
    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        snapshotBuilder = new VbaProjectSnapshotBuilder(diskDocumentCache);
    }

    /// <summary>
    /// Updates or adds an open document and parses its latest source text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="text">The latest source text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    /// <returns>The parse update kind for the new syntax tree.</returns>
    public VbaSyntaxTreeParseUpdateKind UpdateDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            documents.TryGetValue(uri, out var previousDocument);
            var parseResult = VbaSyntaxTree.ParseOrUpdate(uri, text, previousDocument?.SyntaxTree);
            excludedSourceUris.Remove(uri);
            documents[uri] = new VbaTrackedDocument(
                uri,
                text,
                parseResult.SyntaxTree,
                parseResult.UpdateKind,
                VbaSourceIndex.CreateDocument(uri, parseResult.SyntaxTree));
            workspaceVersion++;
            projectSnapshotCache.Clear();
            return parseResult.UpdateKind;
        }
    }

    /// <summary>
    /// Removes a source document and excludes it from disk inventory snapshots until reopened.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when an open tracked document was removed.</returns>
    public bool RemoveSourceDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            excludedSourceUris.Add(uri);
            var removed = documents.Remove(uri);
            workspaceVersion++;
            projectSnapshotCache.Clear();
            return removed;
        }
    }

    /// <summary>
    /// Removes a tracked document without excluding it from future disk inventory.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when an open tracked document was removed.</returns>
    public bool RemoveDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var removed = documents.Remove(uri);
            if (removed)
            {
                workspaceVersion++;
                projectSnapshotCache.Clear();
            }

            return removed;
        }
    }

    /// <summary>
    /// Gets the latest syntax tree for a tracked document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The syntax tree, or null when the document is not tracked.</returns>
    public VbaSyntaxTree? GetDocumentSyntaxTree(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.TryGetValue(uri, out var document)
                ? document.SyntaxTree
                : null;
        }
    }

    /// <summary>
    /// Gets the URIs of currently tracked documents.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The tracked document URIs.</returns>
    public IReadOnlyList<string> GetDocumentUris(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Keys.ToArray();
        }
    }

    /// <summary>
    /// Creates a project snapshot for the scope containing an active document.
    /// </summary>
    /// <param name="activeUri">The active document URI.</param>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The resolved project snapshot.</returns>
    public VbaProjectSnapshot CreateProjectSnapshot(
        string activeUri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolution = VbaProjectResolver.Resolve(activeUri);
        var workspaceState = CopyWorkspaceState();
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

    /// <summary>
    /// Creates distinct project snapshots for all currently tracked document scopes.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The distinct project snapshots.</returns>
    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots(CancellationToken cancellationToken = default)
    {
        var snapshots = new List<VbaProjectSnapshot>();
        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var uri in GetDocumentUris(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = CreateProjectSnapshot(uri, cancellationToken);
            var scopeKey = string.Join(
                "|",
                snapshot.SourceDocuments.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            if (seenScopes.Add(scopeKey))
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private WorkspaceState CopyWorkspaceState()
    {
        lock (gate)
        {
            return new WorkspaceState(
                new Dictionary<string, VbaTrackedDocument>(documents, StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(excludedSourceUris, StringComparer.OrdinalIgnoreCase),
                workspaceVersion);
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
            if (projectSnapshotCache.TryGetValue(cacheKey, out var cached)
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
            projectSnapshotCache[cacheKey] = new CachedProjectSnapshot(
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

    private sealed record WorkspaceState(
        IReadOnlyDictionary<string, VbaTrackedDocument> Documents,
        IReadOnlySet<string> ExcludedSourceUris,
        long Version);

    private sealed record CachedProjectSnapshot(
        long WorkspaceVersion,
        long ReferenceCatalogVersion,
        string InventoryStamp,
        VbaProjectSnapshot Snapshot);
}
