using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Coordinates the best currently available reference catalogs and background refresh policy.
/// </summary>
public sealed class VbaProjectReferenceCatalogAvailability
{
    private readonly VbaProjectReferenceCatalogCache cache;
    private readonly VbaProjectReferenceCatalogRefreshService refreshService;

    /// <summary>
    /// Creates a reference catalog availability module.
    /// </summary>
    /// <param name="cache">The current in-memory catalog cache.</param>
    /// <param name="refreshService">The refresh service used for persisted and generated catalogs.</param>
    public VbaProjectReferenceCatalogAvailability(
        VbaProjectReferenceCatalogCache cache,
        VbaProjectReferenceCatalogRefreshService refreshService)
    {
        this.cache = cache;
        this.refreshService = refreshService;
    }

    /// <summary>
    /// Gets the best currently available catalog set.
    /// </summary>
    public VbaProjectReferenceCatalogSet Current => cache.Current;

    /// <summary>
    /// Gets a versioned snapshot of the best currently available catalog set.
    /// </summary>
    public VbaProjectReferenceCatalogCacheState State => cache.State;

    /// <summary>
    /// Gets the active source for a reference catalog.
    /// </summary>
    /// <param name="referenceName">The human-visible reference name.</param>
    /// <returns>The catalog source, or unavailable when no catalog exists.</returns>
    public VbaProjectReferenceCatalogSource GetCatalogSource(string referenceName)
        => cache.GetCatalogSource(referenceName);

    /// <summary>
    /// Loads persisted catalogs synchronously so editor requests can use the best available metadata immediately.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <returns>The preload results.</returns>
    public IReadOnlyList<VbaProjectReferenceCatalogRefreshResult> PreloadPersistedCatalogs(
        VbaProjectReferenceSelection selection)
        => refreshService.PreloadPersistedCatalogs(selection);

    /// <summary>
    /// Refreshes catalogs that are missing or stale while preserving the best available catalog in the cache.
    /// </summary>
    /// <param name="selection">The active reference selection.</param>
    /// <param name="cancellationToken">A cancellation token for refresh work.</param>
    /// <returns>The refresh results.</returns>
    public Task<IReadOnlyList<VbaProjectReferenceCatalogRefreshResult>> RefreshAsync(
        VbaProjectReferenceSelection selection,
        CancellationToken cancellationToken = default)
        => refreshService.RefreshAsync(selection, cancellationToken);
}
