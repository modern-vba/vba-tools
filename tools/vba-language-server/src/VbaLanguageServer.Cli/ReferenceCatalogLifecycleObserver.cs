namespace VbaLanguageServer.SourceModel;

internal enum VbaProjectReferenceCatalogLifecycleOperation
{
    ProjectSelectionResolve,
    ProjectSnapshotManifestResolve,
    PersistedPreload,
    Discovery,
    Commit,
    ExplicitRetry,
    ProjectScopeInvalidation
}

internal sealed record VbaProjectReferenceCatalogLifecycleEvent(
    VbaProjectReferenceCatalogLifecycleOperation Operation,
    string? ScopeKey = null,
    string? ReferenceName = null);

internal interface IVbaProjectReferenceCatalogLifecycleObserver
{
    void Record(VbaProjectReferenceCatalogLifecycleEvent lifecycleEvent);
}

internal sealed class NullVbaProjectReferenceCatalogLifecycleObserver
    : IVbaProjectReferenceCatalogLifecycleObserver
{
    public static NullVbaProjectReferenceCatalogLifecycleObserver Instance { get; } = new();

    private NullVbaProjectReferenceCatalogLifecycleObserver()
    {
    }

    public void Record(VbaProjectReferenceCatalogLifecycleEvent lifecycleEvent)
    {
    }
}
