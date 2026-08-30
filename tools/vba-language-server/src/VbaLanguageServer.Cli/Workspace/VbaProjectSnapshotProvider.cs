using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;

namespace VbaLanguageServer.Workspace;

internal interface IVbaProjectSnapshotBuildObserver
{
    void BeforeCapture(string activeUri, CancellationToken cancellationToken)
    {
    }

    void BeforeBuildProjectSnapshot(
        string activeUri,
        CancellationToken cancellationToken)
    {
    }

    void BeforeBuildSemanticInventory(
        string activeUri,
        CancellationToken cancellationToken)
    {
    }

    void BeforeStore(long workspaceVersion, CancellationToken cancellationToken);
}

internal sealed class NullVbaProjectSnapshotBuildObserver
    : IVbaProjectSnapshotBuildObserver
{
    public static NullVbaProjectSnapshotBuildObserver Instance { get; } = new();

    private NullVbaProjectSnapshotBuildObserver()
    {
    }

    public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
    {
    }
}

/// <summary>
/// Represents the document state used to create a project snapshot.
/// </summary>
/// <param name="DocumentsByIdentity">The tracked workspace documents keyed by structural identity.</param>
/// <param name="ExcludedSourceIdentities">The source identities excluded from disk inventory.</param>
/// <param name="Version">The workspace document-state version.</param>
internal sealed record VbaWorkspaceSnapshotState(
    IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
        DocumentsByIdentity,
    IReadOnlySet<VbaDocumentIdentity> ExcludedSourceIdentities,
    long Version);

internal sealed record VbaProjectManifestRetentionScope(
    VbaIdentifiedDocument ActiveDocument,
    string RootPath)
{
    public string ActiveUri => ActiveDocument.Uri;
}

internal interface IVbaProjectReconciliationAuthorityLeaseObserver
{
    void AuthorityLeaseAcquired(
        VbaProjectAuthorityIdentity authorityKey,
        long authorityGeneration);
}

internal sealed class NullVbaProjectReconciliationAuthorityLeaseObserver
    : IVbaProjectReconciliationAuthorityLeaseObserver
{
    public static
        NullVbaProjectReconciliationAuthorityLeaseObserver Instance
    { get; } = new();

    private NullVbaProjectReconciliationAuthorityLeaseObserver()
    {
    }

    public void AuthorityLeaseAcquired(
        VbaProjectAuthorityIdentity authorityKey,
        long authorityGeneration)
    {
    }
}

/// <summary>
/// Represents the stable cache identity for one project snapshot scope.
/// </summary>
internal sealed class VbaProjectSnapshotIdentity
    : IEquatable<VbaProjectSnapshotIdentity>,
      IComparable<VbaProjectSnapshotIdentity>
{
    private readonly VbaProjectAuthorityIdentity? authority;
    private readonly VbaDocumentIdentity? indeterminateDocument;
    private readonly VbaProjectResolutionKind resolutionKind;
    private readonly string indeterminateManifestPath;
    private readonly string indeterminateDocumentName;
    private readonly string sourceRootPath;
    private readonly ReferenceSelectionFingerprint? referenceSelection;
    private readonly string sourceTemplatePath;
    private readonly string[] commonModuleFiles;

    private VbaProjectSnapshotIdentity(
        VbaProjectAuthorityIdentity? authority,
        VbaDocumentIdentity? indeterminateDocument,
        VbaProjectResolutionKind resolutionKind,
        string indeterminateManifestPath,
        string indeterminateDocumentName,
        string sourceRootPath,
        ReferenceSelectionFingerprint? referenceSelection,
        string sourceTemplatePath,
        string[] commonModuleFiles)
    {
        this.authority = authority;
        this.indeterminateDocument = indeterminateDocument;
        this.resolutionKind = resolutionKind;
        this.indeterminateManifestPath = indeterminateManifestPath;
        this.indeterminateDocumentName = indeterminateDocumentName;
        this.sourceRootPath = sourceRootPath;
        this.referenceSelection = referenceSelection;
        this.sourceTemplatePath = sourceTemplatePath;
        this.commonModuleFiles = commonModuleFiles;
    }

    public static VbaProjectSnapshotIdentity Create(
        VbaDocumentIdentity activeDocumentIdentity,
        VbaProjectResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var hasAuthority = VbaProjectIdentityModel.TryIdentifyAuthority(
            resolution,
            out var identifiedAuthority);
        return new VbaProjectSnapshotIdentity(
            hasAuthority ? identifiedAuthority : null,
            !hasAuthority ? activeDocumentIdentity : null,
            resolution.Kind,
            hasAuthority
                ? ""
                : CreatePathFact(resolution.ManifestPath),
            hasAuthority
                ? ""
                : NormalizeToken(resolution.DocumentName),
            resolution.RootIdentity is { } rootIdentity
                ? NormalizeToken(rootIdentity.CanonicalPath)
                : CreatePathFact(resolution.RootPath),
            ReferenceSelectionFingerprint.TryCreate(
                resolution,
                out var selectionFingerprint)
                    ? selectionFingerprint
                    : null,
            CreatePathFact(resolution.SourceTemplatePath),
            resolution.InstalledCommonModuleEntries
                .Select(module => NormalizeToken(module.ModuleFile))
                .OrderBy(moduleFile => moduleFile, StringComparer.Ordinal)
                .ToArray());
    }

    public bool Equals(VbaProjectSnapshotIdentity? other)
        => other is not null
            && Nullable.Equals(authority, other.authority)
            && Nullable.Equals(
                indeterminateDocument,
                other.indeterminateDocument)
            && resolutionKind == other.resolutionKind
            && indeterminateManifestPath.Equals(
                other.indeterminateManifestPath,
                StringComparison.Ordinal)
            && indeterminateDocumentName.Equals(
                other.indeterminateDocumentName,
                StringComparison.Ordinal)
            && sourceRootPath.Equals(
                other.sourceRootPath,
                StringComparison.Ordinal)
            && Nullable.Equals(
                referenceSelection,
                other.referenceSelection)
            && sourceTemplatePath.Equals(
                other.sourceTemplatePath,
                StringComparison.Ordinal)
            && commonModuleFiles.SequenceEqual(
                other.commonModuleFiles,
                StringComparer.Ordinal);

    public override bool Equals(object? obj)
        => obj is VbaProjectSnapshotIdentity other
            && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(authority);
        hash.Add(indeterminateDocument);
        hash.Add(resolutionKind);
        hash.Add(indeterminateManifestPath, StringComparer.Ordinal);
        hash.Add(indeterminateDocumentName, StringComparer.Ordinal);
        hash.Add(sourceRootPath, StringComparer.Ordinal);
        hash.Add(referenceSelection);
        hash.Add(sourceTemplatePath, StringComparer.Ordinal);
        foreach (var commonModuleFile in commonModuleFiles)
        {
            hash.Add(commonModuleFile, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(VbaProjectSnapshotIdentity? other)
    {
        if (other is null)
        {
            return 1;
        }

        var comparison = CompareNullable(authority, other.authority);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNullable(
            indeterminateDocument,
            other.indeterminateDocument);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = resolutionKind.CompareTo(other.resolutionKind);
        if (comparison != 0)
        {
            return comparison;
        }

        foreach (var pair in new[]
        {
            (indeterminateManifestPath, other.indeterminateManifestPath),
            (indeterminateDocumentName, other.indeterminateDocumentName),
            (sourceRootPath, other.sourceRootPath)
        })
        {
            comparison = StringComparer.Ordinal.Compare(pair.Item1, pair.Item2);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        comparison = CompareNullable(
            referenceSelection,
            other.referenceSelection);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            sourceTemplatePath,
            other.sourceTemplatePath);
        return comparison != 0
            ? comparison
            : CompareStrings(commonModuleFiles, other.commonModuleFiles);
    }

    public override string ToString() => CreateDiagnosticValue();

    private string CreateDiagnosticValue()
        => string.Join(
            "\u001e",
            authority?.ToString()
                ?? indeterminateDocument?.ToString()
                ?? "",
            resolutionKind,
            indeterminateManifestPath,
            indeterminateDocumentName,
            sourceRootPath,
            referenceSelection?.ToString() ?? "",
            sourceTemplatePath,
            string.Join("\u001f", commonModuleFiles));

    private static string CreatePathFact(string? path)
        => VbaProjectIdentityModel.TryNormalizeSnapshotPath(
            path,
            out var canonicalPath)
                ? NormalizeToken(canonicalPath)
                : string.Join(
                    "\u001f",
                    "UNRESOLVED-PATH",
                    NormalizeToken(path));

    private static int CompareNullable<T>(T? left, T? right)
        where T : struct, IComparable<T>
        => left.HasValue
            ? right.HasValue
                ? left.Value.CompareTo(right.Value)
                : 1
            : right.HasValue
                ? -1
                : 0;

    private static int CompareStrings(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var sharedLength = Math.Min(left.Count, right.Count);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = StringComparer.Ordinal.Compare(
                left[index],
                right[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static string NormalizeToken(string? value)
        => value?.Trim().ToUpperInvariant() ?? "";
}

/// <summary>
/// Creates and caches immutable project snapshots from workspace state.
/// </summary>
internal sealed class VbaProjectSnapshotProvider
{
    internal sealed class ProjectSnapshotOwnership
    {
        internal ProjectSnapshotOwnership(
            VbaProjectSnapshotIdentity cacheIdentity,
            string activeUri,
            VbaDocumentIdentity activeDocumentIdentity,
            VbaProjectResolution resolution,
            long workspaceVersion,
            long manifestVersion,
            long hostClassProjectionRevision,
            long fullInvalidationGeneration,
            long scopeInvalidationGeneration,
            object scopeIdentity,
            IReadOnlyList<VbaDocumentIdentity> sourceIdentities)
        {
            CacheIdentity = cacheIdentity;
            ActiveUri = activeUri;
            ActiveDocumentIdentity = activeDocumentIdentity;
            Resolution = resolution;
            WorkspaceVersion = workspaceVersion;
            ManifestVersion = manifestVersion;
            HostClassProjectionRevision = hostClassProjectionRevision;
            FullInvalidationGeneration = fullInvalidationGeneration;
            ScopeInvalidationGeneration = scopeInvalidationGeneration;
            ScopeIdentity = scopeIdentity;
            SourceIdentities = sourceIdentities;
        }

        internal VbaProjectSnapshotIdentity CacheIdentity { get; }

        internal string ActiveUri { get; }

        internal VbaDocumentIdentity ActiveDocumentIdentity { get; }

        internal VbaProjectResolution Resolution { get; }

        internal long WorkspaceVersion { get; }

        internal long ManifestVersion { get; }

        internal long HostClassProjectionRevision { get; }

        internal long FullInvalidationGeneration { get; }

        internal long ScopeInvalidationGeneration { get; }

        internal object ScopeIdentity { get; }

        internal IReadOnlyList<VbaDocumentIdentity> SourceIdentities { get; }
    }

    private readonly object gate = new();
    private readonly Dictionary<VbaProjectSnapshotIdentity, CachedProjectSnapshot>
        cache = new();
    private readonly Dictionary<VbaProjectAuthorityIdentity, ReconciliationBaseline>
        reconciliationBaselines = new();
    private readonly Dictionary<VbaDocumentIdentity, VbaProjectAuthorityIdentity>
        reconciliationAuthoritiesByActiveUri = new();
    private readonly Dictionary<VbaDocumentIdentity, CachedManifestResolution>
        manifestResolutionCache = new();
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly IVbaProjectManifestResolutionSource manifestResolutionSource;
    private readonly IVbaProjectDiskInventory diskInventory;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;
    private readonly VbaProjectSnapshotBuilder snapshotBuilder;
    private readonly IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver;
    private readonly IVbaProjectSnapshotBuildObserver buildObserver;
    private readonly VbaHostClassProjectionSnapshotStore hostClassProjectionStore;
    private readonly IVbaProjectReconciliationAuthorityLeaseObserver
        reconciliationAuthorityLeaseObserver;
    private readonly Dictionary<
        VbaProjectSnapshotIdentity,
        ProjectScopeInvalidationState> scopeInvalidationStates = new();
    private readonly Dictionary<
        VbaProjectSnapshotIdentity,
        WarmProjectScopeSeed> scopeAuthoritySeeds = new();
    private readonly VbaSourceRevisionHistory sourceRevisionHistory = new();
    private ProjectScopeAuthorityLookup scopeAuthorityLookup =
        ProjectScopeAuthorityLookup.Empty;
    private long fullInvalidationGeneration;
    private long nextReconciliationGeneration;

    public VbaProjectSnapshotProvider(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        IVbaProjectDiskInventory diskInventory,
        VbaProjectSourceDocumentCache diskDocumentCache,
        IVbaProjectManifestResolutionSource manifestResolutionSource,
        IVbaProjectReferenceCatalogLifecycleObserver? lifecycleObserver = null,
        IVbaProjectSnapshotBuildObserver? buildObserver = null,
        IVbaProjectReconciliationAuthorityLeaseObserver?
            reconciliationAuthorityLeaseObserver = null,
        VbaHostClassProjectionSnapshotStore? hostClassProjectionStore = null)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        this.diskInventory = diskInventory;
        this.diskDocumentCache = diskDocumentCache;
        this.manifestResolutionSource = manifestResolutionSource;
        this.lifecycleObserver =
            lifecycleObserver ?? NullVbaProjectReferenceCatalogLifecycleObserver.Instance;
        this.buildObserver = buildObserver ?? NullVbaProjectSnapshotBuildObserver.Instance;
        this.reconciliationAuthorityLeaseObserver =
            reconciliationAuthorityLeaseObserver
            ?? NullVbaProjectReconciliationAuthorityLeaseObserver.Instance;
        this.hostClassProjectionStore =
            hostClassProjectionStore ?? new VbaHostClassProjectionSnapshotStore();
        snapshotBuilder = new VbaProjectSnapshotBuilder(
            diskInventory,
            diskDocumentCache);
    }

    public VbaProjectSnapshot CreateProjectSnapshot(
        VbaIdentifiedDocument activeDocument,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        using var revisionCapture =
            sourceRevisionHistory.BeginCapture(workspaceState.Version);
        cancellationToken.ThrowIfCancellationRequested();
        var authorityLookup = CaptureScopeAuthorityLookup(
            cancellationToken);
        var capture = CaptureKnownProjectScope(
                activeDocument,
                authorityLookup,
                cancellationToken,
                out var supersededCacheIdentity)
            ?? CaptureProjectScope(
                activeDocument,
                cancellationToken,
                supersededCacheIdentity);
        return CreateProjectSnapshot(capture, workspaceState, cancellationToken);
    }

    private CapturedProjectScopeAuthorityLookup CaptureScopeAuthorityLookup(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return new CapturedProjectScopeAuthorityLookup(
                scopeAuthorityLookup);
        }
    }

    private ProjectScopeCapture? CaptureKnownProjectScope(
        VbaIdentifiedDocument activeDocument,
        CapturedProjectScopeAuthorityLookup authorityLookup,
        CancellationToken cancellationToken,
        out VbaProjectSnapshotIdentity? supersededCacheIdentity)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seed = authorityLookup.Lookup.Resolve(activeDocument.Identity);
        if (seed is null)
        {
            supersededCacheIdentity = null;
            return null;
        }

        var manifestBarriers =
            manifestResolutionSource.CaptureScopeBarriers(
            activeDocument,
            seed.Resolution);
        if (seed.ManifestVersion != manifestBarriers.Revision)
        {
            supersededCacheIdentity = seed.CacheIdentity;
            return null;
        }

        supersededCacheIdentity = null;
        return new ProjectScopeCapture(
            activeDocument,
            seed.Resolution,
            manifestBarriers,
            referenceCatalogCache.CaptureSelectionState(
                seed.Resolution.ReferenceEntries,
                VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                    seed.Resolution,
                    out var seedCatalogScope)
                        ? seedCatalogScope
                        : null),
            hostClassProjectionStore.CaptureSelectionState(seed.Resolution),
            seed.CacheIdentity,
            SupersededCacheIdentity: null);
    }

    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots(
        IReadOnlyList<VbaIdentifiedDocument> activeDocuments,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        using var revisionCapture =
            sourceRevisionHistory.BeginCapture(workspaceState.Version);
        var authorityLookup = CaptureScopeAuthorityLookup(
            cancellationToken);
        var captures = new Dictionary<
            VbaProjectSnapshotIdentity,
            ProjectScopeCapture>();
        foreach (var activeDocument in activeDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = CaptureKnownProjectScope(
                    activeDocument,
                    authorityLookup,
                    cancellationToken,
                    out var supersededCacheIdentity)
                ?? CaptureProjectScope(
                    activeDocument,
                    cancellationToken,
                    supersededCacheIdentity);
            captures.TryAdd(capture.CacheIdentity, capture);
        }

        return captures.Values
            .Select(capture => CreateProjectSnapshot(
                capture,
                workspaceState,
                cancellationToken))
            .ToArray();
    }

    private ProjectScopeCapture CaptureProjectScope(
        VbaIdentifiedDocument activeDocument,
        CancellationToken cancellationToken,
        VbaProjectSnapshotIdentity? supersededCacheIdentity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestCapture = ResolveCurrentManifest(activeDocument);
        var resolution = manifestCapture.Resolution;
        var referenceCatalogState = referenceCatalogCache.CaptureSelectionState(
            resolution.ReferenceEntries,
            VbaProjectReferenceCatalogScopeIdentity.TryCreate(
                resolution,
                out var catalogScope)
                    ? catalogScope
                    : null);
        var cacheIdentity = VbaProjectSnapshotIdentity.Create(
            activeDocument.Identity,
            resolution);
        return new ProjectScopeCapture(
            activeDocument,
            resolution,
            manifestCapture.Barriers,
            referenceCatalogState,
            hostClassProjectionStore.CaptureSelectionState(resolution),
            cacheIdentity,
            supersededCacheIdentity);
    }

    private VbaProjectSnapshot CreateProjectSnapshot(
        ProjectScopeCapture capture,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        buildObserver.BeforeCapture(capture.ActiveUri, cancellationToken);
        var capturedInvalidation = CaptureInvalidation(
            capture.CacheIdentity,
            capture.ActiveDocument,
            capture.Resolution);
        try
        {
            if (TryGetCachedSnapshot(
                capture.CacheIdentity,
                capture.ManifestBarriers.Revision,
                capture.ReferenceCatalogState.Revision,
                capture.HostClassProjectionState.Revision,
                cancellationToken,
                out var cachedSnapshot))
            {
                return cachedSnapshot;
            }

            buildObserver.BeforeBuildProjectSnapshot(
                capture.ActiveUri,
                cancellationToken);
            var inventorySnapshot = snapshotBuilder.CreateInventorySnapshot(
                capture.ActiveDocument,
                capture.Resolution,
                workspaceState.DocumentsByIdentity,
                workspaceState.ExcludedSourceIdentities,
                capture.ManifestBarriers.Overrides,
                cancellationToken);
            var sourceIdentities =
                inventorySnapshot.DocumentsByIdentity.Keys.ToArray();
            RegisterScopeSources(
                capture.CacheIdentity,
                capturedInvalidation,
                sourceIdentities);

            buildObserver.BeforeBuildSemanticInventory(
                capture.ActiveUri,
                cancellationToken);
            var snapshot = snapshotBuilder.BuildSnapshot(
                capture.Resolution,
                inventorySnapshot.Documents,
                inventorySnapshot.DiskSources,
                inventorySnapshot.Failures,
                inventorySnapshot.ExistingOpenSourceIdentities,
                capture.ReferenceCatalogState.CatalogSet,
                capture.ReferenceCatalogState.Sources,
                capture.HostClassProjectionState.Snapshot,
                capture.ReferenceCatalogState.Identities,
                capture.ReferenceCatalogState.AuthoritativeProjectNames) with
            {
                ManifestBarrierOverrides =
                    capture.ManifestBarriers.Overrides,
                DiagnosticsOwnership = new ProjectSnapshotOwnership(
                    capture.CacheIdentity,
                    capture.ActiveUri,
                    capturedInvalidation.State.ActiveDocumentIdentity,
                    capture.Resolution,
                    workspaceState.Version,
                    capture.ManifestBarriers.Revision,
                    capture.HostClassProjectionState.Revision,
                    capturedInvalidation.FullGeneration,
                    capturedInvalidation.ScopeGeneration,
                    capturedInvalidation.State,
                    sourceIdentities)
            };
            buildObserver.BeforeStore(workspaceState.Version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StoreCachedSnapshot(
                capture.CacheIdentity,
                workspaceState.Version,
                capture.ManifestBarriers.Revision,
                capture.ReferenceCatalogState.Revision,
                capture.HostClassProjectionState.Revision,
                capture.SupersededCacheIdentity,
                inventorySnapshot.DiskSources,
                snapshot,
                sourceIdentities,
                inventorySnapshot.DocumentsByIdentity,
                workspaceState.DocumentsByIdentity
                    .Select(pair => new VbaIdentifiedDocument(
                        pair.Key,
                        pair.Value.Uri))
                    .ToArray());
            return snapshot;
        }
        finally
        {
            ReleaseInvalidationBuild(
                capture.CacheIdentity,
                capturedInvalidation);
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            fullInvalidationGeneration++;
            cache.Clear();
            scopeAuthoritySeeds.Clear();
            scopeAuthorityLookup = ProjectScopeAuthorityLookup.Empty;
        }
    }

    internal bool IsCurrentProjectSnapshot(
        ProjectSnapshotOwnership? ownership)
    {
        if (ownership is null)
        {
            return false;
        }

        lock (gate)
        {
            return IsCurrentProjectSnapshotCore(ownership);
        }
    }

    public bool TryApplyHostClassProjectionSnapshot(
        VbaHostClassProjectionSnapshotUpdate update,
        IReadOnlyList<VbaIdentifiedDocument> activeDocuments)
    {
        var matchingScopes = activeDocuments
            .Select(activeDocument => new
            {
                ActiveDocument = activeDocument,
                Resolution = ResolveCurrentManifest(
                    activeDocument).Resolution
            })
            .Where(scope => VbaHostClassProjectionSnapshotStore.Matches(
                scope.Resolution,
                update.Context))
            .Select(scope => new
            {
                scope.ActiveDocument,
                scope.Resolution,
                CacheIdentity = VbaProjectSnapshotIdentity.Create(
                    scope.ActiveDocument.Identity,
                    scope.Resolution)
            })
            .GroupBy(scope => scope.CacheIdentity)
            .Select(group => group.First())
            .ToArray();
        if (matchingScopes.Length == 0)
        {
            var matchesEffectiveManifest =
                manifestResolutionSource.TryResolveManifestDocument(
                    update.Context.Project,
                    update.Context.Document,
                    out var manifestResolution)
                && VbaHostClassProjectionSnapshotStore.Matches(
                    manifestResolution,
                    update.Context);
            lock (gate)
            {
                if (!(matchesEffectiveManifest
                        ? hostClassProjectionStore.TryApply(update)
                        : hostClassProjectionStore.TryApplyRetainedClear(update)))
                {
                    return false;
                }

                foreach (var (key, invalidationState) in
                    scopeInvalidationStates)
                {
                    if (!VbaHostClassProjectionSnapshotStore.Matches(
                        invalidationState.Resolution,
                        update.Context))
                    {
                        continue;
                    }

                    invalidationState.Generation++;
                    cache.Remove(key);
                }

                return true;
            }
        }

        lock (gate)
        {
            if (!hostClassProjectionStore.TryApply(update))
            {
                return false;
            }

            foreach (var scope in matchingScopes)
            {
                cache.Remove(scope.CacheIdentity);
                if (scopeInvalidationStates.TryGetValue(
                    scope.CacheIdentity,
                    out var invalidationState))
                {
                    invalidationState.Generation++;
                }
            }

            return true;
        }
    }

    public void InvalidateSource(
        VbaIdentifiedDocument source,
        long sourceRevision)
    {
        lock (gate)
        {
            sourceRevisionHistory.Record(
                source,
                sourceRevision);
            foreach (var (key, state) in scopeInvalidationStates)
            {
                if (!BelongsToScope(state, source))
                {
                    continue;
                }

                state.Generation++;
                cache.Remove(key);
            }
        }
    }

    public int RetainedSourceRevisionCount
    {
        get
        {
            lock (gate)
            {
                return sourceRevisionHistory.Count;
            }
        }
    }

    public int RetainedProjectSnapshotCount
    {
        get
        {
            lock (gate)
            {
                return cache.Count;
            }
        }
    }

    public int RetainedScopeInvalidationStateCount
    {
        get
        {
            lock (gate)
            {
                return scopeInvalidationStates.Count;
            }
        }
    }

    public int RetainedReconciliationScopeCount
    {
        get
        {
            lock (gate)
            {
                return reconciliationBaselines.Count;
            }
        }
    }

    public int RetainedReconciliationAuthorityCount
    {
        get
        {
            lock (gate)
            {
                return reconciliationAuthoritiesByActiveUri.Count;
            }
        }
    }

    public int RetainedDiskDocumentCount
        => diskDocumentCache.Count;

    public IDisposable BeginSourceRevisionCapture(long workspaceVersion)
    {
        lock (gate)
        {
            return sourceRevisionHistory.BeginCapture(workspaceVersion);
        }
    }

    public void RetireInactiveScopes(
        IReadOnlyList<VbaIdentifiedDocument> remainingTrackedDocuments)
    {
        var diskIdentitiesToInvalidate =
            new HashSet<VbaDocumentIdentity>();
        lock (gate)
        {
            var remainingDocuments = remainingTrackedDocuments
                .DistinctBy(document => document.Identity)
                .OrderBy(
                    document => document.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var remainingDocumentIdentities = remainingDocuments
                .Select(document => document.Identity)
                .ToHashSet();
            var cacheAnchors = new Dictionary<
                VbaProjectSnapshotIdentity,
                VbaIdentifiedDocument>();
            var reconciliationAnchors =
                new Dictionary<
                    VbaProjectAuthorityIdentity,
                    VbaIdentifiedDocument>();
            var staleAuthorityDocuments = new HashSet<VbaDocumentIdentity>();
            var preferredScopes = remainingDocuments.ToDictionary(
                document => document.Identity,
                document =>
                {
                    var seed = scopeAuthorityLookup.Resolve(document.Identity);
                    if (seed is not null
                        && manifestResolutionSource.CaptureScopeBarriers(
                                document,
                                seed.Resolution)
                            .Revision != seed.ManifestVersion)
                    {
                        staleAuthorityDocuments.Add(document.Identity);
                        return null;
                    }

                    return seed is not null
                        && VbaProjectIdentityModel.TryIdentifyAuthority(
                            seed.Resolution,
                            out var authority)
                                ? new PreferredRetirementScope(
                                    seed.CacheIdentity,
                                    authority)
                                : null;
                });
            foreach (var cacheIdentity in scopeInvalidationStates.Keys
                .Concat(cache.Keys)
                .Concat(scopeAuthoritySeeds.Keys)
                .Distinct())
            {
                var anchorDocument = remainingDocuments.FirstOrDefault(
                    document => staleAuthorityDocuments.Contains(
                            document.Identity)
                        ? false
                        : preferredScopes[document.Identity] is { } preferred
                        ? preferred.CacheIdentity.Equals(
                            cacheIdentity)
                        : scopeInvalidationStates.TryGetValue(
                            cacheIdentity,
                            out var scopeState)
                            ? BelongsToScope(scopeState, document)
                            : scopeAuthoritySeeds.TryGetValue(
                                cacheIdentity,
                                out var seed)
                                && BelongsToScope(seed, document));
                if (anchorDocument is not null)
                {
                    cacheAnchors[cacheIdentity] = anchorDocument;
                }
            }

            foreach (var (authorityKey, baseline) in
                reconciliationBaselines)
            {
                var anchorDocument = remainingDocuments.FirstOrDefault(
                    document => staleAuthorityDocuments.Contains(
                            document.Identity)
                        ? false
                        : preferredScopes[document.Identity] is { } preferred
                        ? preferred.ReconciliationAuthorityKey.Equals(
                            authorityKey)
                        : BelongsToScope(baseline, document));
                if (anchorDocument is not null)
                {
                    reconciliationAnchors[authorityKey] =
                        anchorDocument;
                }
            }

            var retiredCacheIdentities = scopeInvalidationStates.Keys
                .Concat(cache.Keys)
                .Concat(scopeAuthoritySeeds.Keys)
                .Distinct()
                .Where(key => !cacheAnchors.ContainsKey(key))
                .ToArray();
            var retiredReconciliationKeys = reconciliationBaselines.Keys
                .Where(key => !reconciliationAnchors.ContainsKey(key))
                .ToArray();

            foreach (var cacheIdentity in retiredCacheIdentities)
            {
                if (cache.TryGetValue(cacheIdentity, out var cached))
                {
                    foreach (var sourceIdentity in
                        cached.DiskSourceIdentities)
                    {
                        diskIdentitiesToInvalidate.Add(sourceIdentity);
                    }
                }

                if (scopeInvalidationStates.TryGetValue(
                    cacheIdentity,
                    out var scopeState))
                {
                    foreach (var sourceIdentity in
                        scopeState.SourceIdentities)
                    {
                        diskIdentitiesToInvalidate.Add(sourceIdentity);
                    }
                }

                cache.Remove(cacheIdentity);
                scopeInvalidationStates.Remove(cacheIdentity);
                scopeAuthoritySeeds.Remove(cacheIdentity);
            }

            foreach (var (cacheIdentity, anchorDocument) in cacheAnchors)
            {
                if (scopeInvalidationStates.TryGetValue(
                    cacheIdentity,
                    out var scopeState))
                {
                    scopeState.ActiveUri = anchorDocument.Uri;
                    scopeState.ActiveDocumentIdentity =
                        anchorDocument.Identity;
                }

                if (scopeAuthoritySeeds.TryGetValue(
                    cacheIdentity,
                    out var seed))
                {
                    scopeAuthoritySeeds[cacheIdentity] = seed with
                    {
                        ActiveUri = anchorDocument.Uri,
                        ActiveDocumentIdentity = anchorDocument.Identity
                    };
                }
            }

            foreach (var authorityKey in retiredReconciliationKeys)
            {
                if (reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
                {
                    foreach (var source in baseline.KnownSources)
                    {
                        diskIdentitiesToInvalidate.Add(
                            source.DocumentIdentity);
                    }
                }

                reconciliationBaselines.Remove(authorityKey);
            }

            foreach (var (authorityKey, anchorDocument) in
                reconciliationAnchors)
            {
                if (reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
                {
                    var reanchored =
                        baseline.ActiveDocumentIdentity
                            != anchorDocument.Identity;
                    reconciliationBaselines[authorityKey] = baseline with
                    {
                        ActiveUri = anchorDocument.Uri,
                        ActiveDocumentIdentity = anchorDocument.Identity,
                        Generation = reanchored
                            ? ++nextReconciliationGeneration
                            : baseline.Generation
                    };
                }
            }

            reconciliationAuthoritiesByActiveUri.Clear();
            foreach (var (authorityKey, anchorDocument) in
                reconciliationAnchors)
            {
                if (reconciliationBaselines.ContainsKey(authorityKey))
                {
                    reconciliationAuthoritiesByActiveUri[
                        anchorDocument.Identity] = authorityKey;
                }
            }

            foreach (var activeIdentity in manifestResolutionCache.Keys
                .Where(candidate =>
                    !remainingDocumentIdentities.Contains(candidate))
                .ToArray())
            {
                manifestResolutionCache.Remove(activeIdentity);
            }

            RebuildScopeAuthorityLookup();
        }

        foreach (var diskIdentity in diskIdentitiesToInvalidate)
        {
            diskInventory.InvalidateSource(diskIdentity);
            diskDocumentCache.Invalidate(diskIdentity);
        }
    }

    public IReadOnlyList<VbaProjectManifestRetentionScope>
        CaptureManifestRetentionScopes()
    {
        lock (gate)
        {
            return reconciliationBaselines.Values
                .Select(
                    baseline => new VbaProjectManifestRetentionScope(
                        new VbaIdentifiedDocument(
                            baseline.ActiveDocumentIdentity,
                            baseline.ActiveUri),
                        baseline.Resolution.RootPath))
                .Concat(
                    scopeAuthoritySeeds.Values.Select(
                        seed => new VbaProjectManifestRetentionScope(
                            new VbaIdentifiedDocument(
                                seed.ActiveDocumentIdentity,
                                seed.ActiveUri),
                            seed.Resolution.RootPath)))
                .Distinct()
                .ToArray();
        }
    }

    public IReadOnlyList<VbaProjectReconciliationScope>
        CaptureReconciliationScopes(long capturedWorkspaceRevision)
    {
        lock (gate)
        {
            var scopes = new List<VbaProjectReconciliationScope>();
            foreach (var (authorityKey, baseline) in reconciliationBaselines)
            {
                scopes.Add(
                    new VbaProjectReconciliationScope(
                        authorityKey,
                        new VbaIdentifiedDocument(
                            baseline.ActiveDocumentIdentity,
                            baseline.ActiveUri),
                        baseline.Resolution,
                        capturedWorkspaceRevision,
                        GetManifestCandidateDocuments(
                                new VbaIdentifiedDocument(
                                    baseline.ActiveDocumentIdentity,
                                    baseline.ActiveUri),
                                baseline.Resolution)
                            .Select(document => new VbaProjectReconciliationManifestCandidate(
                                document.Identity,
                                document.Uri,
                                0,
                                new VbaProjectDiskManifestBaseline(
                                    Exists: false,
                                    Text: null)))
                            .ToArray(),
                        baseline.KnownSources)
                    {
                        ManifestBarriers =
                            manifestResolutionSource
                                .CaptureDiskReconciliationBarriers(
                                new VbaIdentifiedDocument(
                                    baseline.ActiveDocumentIdentity,
                                    baseline.ActiveUri),
                                baseline.Resolution),
                        AuthorityGeneration = baseline.Generation
                    });
            }

            return scopes;
        }
    }

    public bool IsReconciliationScopeCurrent(
        VbaProjectAuthorityIdentity authorityKey,
        long capturedManifestBarrierRevision,
        long capturedAuthorityGeneration)
    {
        lock (gate)
        {
            return reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline)
                && baseline.Generation == capturedAuthorityGeneration
                && manifestResolutionSource.CaptureScopeBarrierRevision(
                        new VbaIdentifiedDocument(
                            baseline.ActiveDocumentIdentity,
                            baseline.ActiveUri),
                        baseline.Resolution)
                    == capturedManifestBarrierRevision;
        }
    }

    public void CommitReconciledSourceBaseline(
        VbaProjectAuthorityIdentity authorityKey,
        VbaProjectDiskKnownSource source)
    {
        lock (gate)
        {
            if (!reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
            {
                return;
            }

            var knownSources = baseline.KnownSources
                .Where(known => known.DocumentIdentity
                    != source.DocumentIdentity)
                .Append(source)
                .OrderBy(
                    known => known.FullPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            reconciliationBaselines[authorityKey] = baseline with
            {
                KnownSources = knownSources
            };
        }
    }

    public void CommitDeletedReconciledSourceBaseline(
        VbaProjectAuthorityIdentity authorityKey,
        VbaDocumentIdentity documentIdentity)
    {
        lock (gate)
        {
            if (!reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
            {
                return;
            }

            reconciliationBaselines[authorityKey] = baseline with
            {
                KnownSources = baseline.KnownSources
                    .Where(known => known.DocumentIdentity
                        != documentIdentity)
                    .ToArray()
            };
        }
    }

    public void ReleaseReconciledSourceOwnership(
        VbaProjectAuthorityIdentity authorityKey,
        VbaDocumentIdentity documentIdentity)
    {
        lock (gate)
        {
            if (!reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
            {
                return;
            }

            reconciliationBaselines[authorityKey] = baseline with
            {
                KnownSources = baseline.KnownSources
                    .Where(known => known.DocumentIdentity
                        != documentIdentity)
                    .ToArray()
            };

            foreach (var (cacheIdentity, scopeState) in
                scopeInvalidationStates.ToArray())
            {
                if (!VbaProjectIdentityModel.TryIdentifyAuthority(
                        scopeState.Resolution,
                        out var scopeAuthority)
                    || scopeAuthority != authorityKey)
                {
                    continue;
                }

                scopeState.Generation++;
                scopeState.SourceIdentities.Remove(documentIdentity);
                cache.Remove(cacheIdentity);
                if (scopeAuthoritySeeds.TryGetValue(
                        cacheIdentity,
                        out var seed))
                {
                    scopeAuthoritySeeds[cacheIdentity] = seed with
                    {
                        SourceIdentities = seed.SourceIdentities
                            .Where(sourceIdentity =>
                                sourceIdentity != documentIdentity)
                            .ToArray()
                    };
                }
            }

            RebuildScopeAuthorityLookup();
        }
    }

    public bool TryUseReconciliationAuthority<TResult>(
        VbaProjectAuthorityIdentity authorityKey,
        long capturedManifestBarrierRevision,
        long capturedAuthorityGeneration,
        Func<ReconciliationAuthorityLease, TResult> commit,
        out TResult result)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (gate)
        {
            if (!TryGetCurrentReconciliationBaseline(
                    authorityKey,
                    capturedManifestBarrierRevision,
                    capturedAuthorityGeneration,
                    out var baseline))
            {
                result = default!;
                return false;
            }

            reconciliationAuthorityLeaseObserver.AuthorityLeaseAcquired(
                authorityKey,
                capturedAuthorityGeneration);
            if (!TryGetCurrentReconciliationBaseline(
                    authorityKey,
                    capturedManifestBarrierRevision,
                    capturedAuthorityGeneration,
                    out baseline))
            {
                result = default!;
                return false;
            }

            var lease = new ReconciliationAuthorityLease(
                this,
                authorityKey,
                baseline);
            try
            {
                result = commit(lease);
                return true;
            }
            finally
            {
                lease.Release();
            }
        }
    }

    private bool TryGetCurrentReconciliationBaseline(
        VbaProjectAuthorityIdentity authorityKey,
        long capturedManifestBarrierRevision,
        long capturedAuthorityGeneration,
        out ReconciliationBaseline baseline)
        => reconciliationBaselines.TryGetValue(
                authorityKey,
                out baseline!)
            && baseline.Generation == capturedAuthorityGeneration
            && manifestResolutionSource.CaptureScopeBarrierRevision(
                    new VbaIdentifiedDocument(
                        baseline.ActiveDocumentIdentity,
                        baseline.ActiveUri),
                    baseline.Resolution)
                == capturedManifestBarrierRevision;

    private void CommitReconciledManifestScopeLocked(
        VbaProjectAuthorityIdentity authorityKey,
        ReconciliationBaseline baseline,
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution,
        bool retainPreviousAuthority,
        IReadOnlyList<VbaDocumentIdentity> retainedPreviousSourceIdentities,
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
    {
        if (!Monitor.IsEntered(gate))
        {
            throw new InvalidOperationException(
                "Reconciliation authority commits require an active lease.");
        }

        _ = TransferReconciliationScope(
            authorityKey,
            baseline,
            activeDocument,
            resolution,
            replacementKnownSources: null,
            retainPreviousAuthority,
            retainedPreviousSourceIdentities,
            trackedDocuments);

        var previousCacheIdentity =
            VbaProjectSnapshotIdentity.Create(
                baseline.ActiveDocumentIdentity,
                baseline.Resolution);
        var committedCacheIdentity =
            VbaProjectSnapshotIdentity.Create(
                activeDocument.Identity,
                resolution);
        if (!previousCacheIdentity.Equals(committedCacheIdentity))
        {
            cache.Remove(previousCacheIdentity);
            scopeInvalidationStates.Remove(previousCacheIdentity);
            scopeAuthoritySeeds.Remove(previousCacheIdentity);
            RebuildScopeAuthorityLookup();
        }
    }

    internal sealed class ReconciliationAuthorityLease
    {
        private readonly VbaProjectSnapshotProvider owner;
        private readonly VbaProjectAuthorityIdentity authorityKey;
        private readonly ReconciliationBaseline baseline;
        private bool active = true;
        private bool authorityCommitted;

        internal ReconciliationAuthorityLease(
            VbaProjectSnapshotProvider owner,
            VbaProjectAuthorityIdentity authorityKey,
            ReconciliationBaseline baseline)
        {
            this.owner = owner;
            this.authorityKey = authorityKey;
            this.baseline = baseline;
        }

        public void CommitManifestScope(
            VbaIdentifiedDocument activeDocument,
            VbaProjectResolution resolution,
            bool retainPreviousAuthority,
            IReadOnlyList<VbaDocumentIdentity> retainedPreviousSourceIdentities,
            IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
        {
            ObjectDisposedException.ThrowIf(!active, this);
            if (authorityCommitted)
            {
                throw new InvalidOperationException(
                    "A reconciliation authority lease can commit only once.");
            }

            owner.CommitReconciledManifestScopeLocked(
                authorityKey,
                baseline,
                activeDocument,
                resolution,
                retainPreviousAuthority,
                retainedPreviousSourceIdentities,
                trackedDocuments);
            authorityCommitted = true;
        }

        internal void Release()
            => active = false;
    }

    private string? TransferReconciliationScope(
        VbaProjectAuthorityIdentity previousAuthorityKey,
        ReconciliationBaseline previousBaseline,
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution,
        IReadOnlyList<VbaProjectDiskKnownSource>? replacementKnownSources,
        bool retainPreviousAuthority,
        IReadOnlyList<VbaDocumentIdentity>? retainedPreviousSourceIdentities,
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
    {
        var relation = VbaProjectIdentityModel.Relate(
            activeDocument.Identity,
            previousBaseline.Resolution,
            resolution);
        if (relation.Kind is VbaProjectAuthorityRelationKind.Indeterminate
            or VbaProjectAuthorityRelationKind.Unrelated
            || relation.PreviousAuthority is not { } previousAuthority
            || relation.CurrentAuthority is not { } committedAuthorityKey)
        {
            return null;
        }

        if (previousAuthorityKey != previousAuthority)
        {
            return null;
        }

        var sameSourceOwnershipBoundary =
            relation.Ownership.SameSourceOwnershipBoundary == true;
        if (retainPreviousAuthority
            != (relation.Kind
                == VbaProjectAuthorityRelationKind.RetainPrevious))
        {
            return null;
        }

        RemoveReconciliationAuthorityMapping(activeDocument.Identity);
        if (previousAuthorityKey == committedAuthorityKey)
        {
            reconciliationBaselines[previousAuthorityKey] =
                previousBaseline with
                {
                    ActiveUri = activeDocument.Uri,
                    ActiveDocumentIdentity = activeDocument.Identity,
                    Resolution = resolution,
                    KnownSources = sameSourceOwnershipBoundary
                        ? previousBaseline.KnownSources
                        : replacementKnownSources ?? [],
                    Generation = sameSourceOwnershipBoundary
                        ? previousBaseline.Generation
                        : ++nextReconciliationGeneration
                };
            reconciliationAuthoritiesByActiveUri[
                relation.SubjectDocument] = previousAuthorityKey;
            return null;
        }

        reconciliationBaselines.TryGetValue(
            committedAuthorityKey,
            out var existing);
        reconciliationBaselines[committedAuthorityKey] =
            new ReconciliationBaseline(
                activeDocument.Uri,
                activeDocument.Identity,
                resolution,
                replacementKnownSources
                    ?? existing?.KnownSources
                    ?? [],
                ++nextReconciliationGeneration);

        var previousAnchor = retainPreviousAuthority
            ? trackedDocuments.FirstOrDefault(
                document => document.Identity != activeDocument.Identity
                    && BelongsToScope(previousBaseline, document)
                    && VbaProjectIdentityModel
                        .OwnsTransferredProjectDocument(
                            resolution,
                            document.Identity) == false)
            : null;
        if (previousAnchor is null)
        {
            reconciliationBaselines.Remove(previousAuthorityKey);
            RemoveReconciliationAuthorityMappings(previousAuthorityKey);
        }
        else
        {
            var retainedSources = retainedPreviousSourceIdentities?.ToHashSet();
            reconciliationBaselines[previousAuthorityKey] =
                previousBaseline with
                {
                    ActiveUri = previousAnchor.Uri,
                    ActiveDocumentIdentity = previousAnchor.Identity,
                    KnownSources = previousBaseline.KnownSources
                        .Where(
                            source => retainedSources is null
                                ? VbaProjectIdentityModel
                                    .OwnsTransferredProjectDocument(
                                        resolution,
                                        source.DocumentIdentity) == false
                                : retainedSources.Contains(
                                    source.DocumentIdentity))
                        .ToArray()
                };
            reconciliationAuthoritiesByActiveUri[
                previousAnchor.Identity] = previousAuthorityKey;
        }

        reconciliationAuthoritiesByActiveUri[
            relation.SubjectDocument] = committedAuthorityKey;
        return previousAnchor?.Uri;
    }

    private void RemoveReconciliationAuthorityMapping(
        VbaDocumentIdentity activeDocumentIdentity)
        => reconciliationAuthoritiesByActiveUri.Remove(activeDocumentIdentity);

    private void RemoveReconciliationAuthorityMappings(
        VbaProjectAuthorityIdentity authorityKey)
    {
        foreach (var mappedActiveUri in
            reconciliationAuthoritiesByActiveUri
                .Where(pair => pair.Value == authorityKey)
                .Select(pair => pair.Key)
                .ToArray())
        {
            reconciliationAuthoritiesByActiveUri.Remove(mappedActiveUri);
        }
    }

    private static IReadOnlyList<VbaIdentifiedDocument>
        GetManifestCandidateDocuments(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
    {
        if (!string.IsNullOrWhiteSpace(resolution.ManifestPath))
        {
            var currentManifestPath =
                Path.GetFullPath(resolution.ManifestPath);
            var manifestActivePath =
                activeDocument.Identity.IsLocalFile
                    ? activeDocument.Identity.CanonicalValue
                    : null;
            var currentManifestDirectory =
                Path.GetDirectoryName(currentManifestPath);
            if (manifestActivePath is null
                || currentManifestDirectory is null
                || !VbaProjectResolver.IsPathUnder(
                    manifestActivePath,
                    currentManifestDirectory))
            {
                return [IdentifyManifestDocument(currentManifestPath)];
            }

            var manifestActiveDirectory =
                Path.GetDirectoryName(manifestActivePath)
                ?? Directory.GetCurrentDirectory();
            var manifestCandidates =
                new List<VbaIdentifiedDocument>();
            for (var directory = new DirectoryInfo(manifestActiveDirectory);
                directory is not null;
                directory = directory.Parent)
            {
                manifestCandidates.Add(IdentifyManifestDocument(
                    Path.Combine(
                        directory.FullName,
                        "vba-project.json")));
            }

            return manifestCandidates;
        }

        var activePath = activeDocument.Identity.IsLocalFile
            ? activeDocument.Identity.CanonicalValue
            : null;
        if (activePath is null)
        {
            return [];
        }

        var activeDirectory =
            Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        var candidates = new List<VbaIdentifiedDocument>();
        for (var directory = new DirectoryInfo(activeDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            candidates.Add(IdentifyManifestDocument(
                Path.Combine(directory.FullName, "vba-project.json")));
        }

        return candidates;
    }

    private static VbaIdentifiedDocument IdentifyManifestDocument(
        string manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath);
        return VbaProjectIdentityModel.TryIdentifyLocalDocumentPath(
                fullPath,
                out var documentIdentity)
            ? new VbaIdentifiedDocument(
                documentIdentity,
                new Uri(fullPath).AbsoluteUri)
            : throw new InvalidOperationException(
                "A reconciliation manifest has no document identity.");
    }

    private VbaProjectManifestResolutionCapture ResolveCurrentManifest(
        VbaIdentifiedDocument activeDocument)
    {
        CachedManifestResolution? cached;
        lock (gate)
        {
            cached = manifestResolutionCache.TryGetValue(
                    activeDocument.Identity,
                    out var existing)
                        ? existing
                        : null;
        }

        if (cached is not null)
        {
            var barriers =
                manifestResolutionSource.CaptureScopeBarriers(
                    activeDocument,
                    cached.Resolution);
            if (cached.Version == barriers.Revision)
            {
                return new VbaProjectManifestResolutionCapture(
                    cached.Resolution,
                    barriers);
            }
        }

        lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
            VbaProjectReferenceCatalogLifecycleOperation.ProjectSnapshotManifestResolve,
            DocumentIdentity: activeDocument.Identity));
        var capture =
            manifestResolutionSource.CaptureResolution(activeDocument.Uri);
        lock (gate)
        {
            manifestResolutionCache[activeDocument.Identity] =
                new CachedManifestResolution(
                    capture.Barriers.Revision,
                    capture.Resolution);
        }

        return capture;
    }

    private bool TryGetCachedSnapshot(
        VbaProjectSnapshotIdentity cacheIdentity,
        long expectedManifestVersion,
        long expectedReferenceCatalogRevision,
        long expectedHostClassProjectionRevision,
        CancellationToken cancellationToken,
        out VbaProjectSnapshot snapshot)
    {
        var catalogInvalidatedScope = false;
        lock (gate)
        {
            if (cache.TryGetValue(cacheIdentity, out var cached)
                && cached.ManifestVersion == expectedManifestVersion
                && cached.HostClassProjectionRevision
                    == expectedHostClassProjectionRevision)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cached.ReferenceCatalogRevision == expectedReferenceCatalogRevision)
                {
                    snapshot = cached.Snapshot;
                    return true;
                }

                catalogInvalidatedScope =
                    cached.ReferenceCatalogRevision != expectedReferenceCatalogRevision;
            }
        }

        if (catalogInvalidatedScope)
        {
            lifecycleObserver.Record(new VbaProjectReferenceCatalogLifecycleEvent(
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation));
        }

        snapshot = default!;
        return false;
    }

    private void StoreCachedSnapshot(
        VbaProjectSnapshotIdentity cacheIdentity,
        long snapshotWorkspaceVersion,
        long snapshotManifestVersion,
        long snapshotReferenceCatalogRevision,
        long snapshotHostClassProjectionRevision,
        VbaProjectSnapshotIdentity? supersededCacheIdentity,
        IReadOnlyList<VbaProjectDiskSource> diskSources,
        VbaProjectSnapshot snapshot,
        IReadOnlyList<VbaDocumentIdentity> sourceIdentities,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
            sourceDocumentsByIdentity,
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
    {
        lock (gate)
        {
            if (snapshot.DiagnosticsOwnership is null
                || !IsCurrentProjectSnapshotCore(
                    snapshot.DiagnosticsOwnership))
            {
                return;
            }

            var scopeState = scopeInvalidationStates[cacheIdentity];

            if (cache.TryGetValue(cacheIdentity, out var current)
                && (current.WorkspaceVersion > snapshotWorkspaceVersion
                    || current.ManifestVersion > snapshotManifestVersion
                    || current.ReferenceCatalogRevision > snapshotReferenceCatalogRevision
                    || current.HostClassProjectionRevision
                        > snapshotHostClassProjectionRevision))
            {
                return;
            }

            cache[cacheIdentity] = new CachedProjectSnapshot(
                snapshotWorkspaceVersion,
                snapshotManifestVersion,
                snapshotReferenceCatalogRevision,
                snapshotHostClassProjectionRevision,
                diskSources
                    .Select(source => source.DocumentIdentity)
                    .ToArray(),
                snapshot);
            scopeState.IsMaterialized = true;
            scopeAuthoritySeeds[cacheIdentity] =
                new WarmProjectScopeSeed(
                    cacheIdentity,
                    scopeState.ActiveUri,
                    scopeState.ActiveDocumentIdentity,
                    scopeState.Resolution,
                    snapshotManifestVersion,
                    sourceIdentities);
            _ = RegisterReconciliationScope(
                new VbaIdentifiedDocument(
                    scopeState.ActiveDocumentIdentity,
                    scopeState.ActiveUri),
                scopeState.Resolution,
                diskSources,
                sourceDocumentsByIdentity,
                snapshot.ExistingOpenSourceIdentities,
                trackedDocuments);
            if (supersededCacheIdentity is not null
                && !supersededCacheIdentity.Equals(cacheIdentity))
            {
                cache.Remove(supersededCacheIdentity);
                scopeInvalidationStates.Remove(
                    supersededCacheIdentity);
                scopeAuthoritySeeds.Remove(
                    supersededCacheIdentity);
            }

            RebuildScopeAuthorityLookup();
        }
    }

    private bool IsCurrentProjectSnapshotCore(
        ProjectSnapshotOwnership ownership)
    {
        return fullInvalidationGeneration
                == ownership.FullInvalidationGeneration
            && scopeInvalidationStates.TryGetValue(
                ownership.CacheIdentity,
                out var scopeState)
            && ReferenceEquals(
                scopeState,
                ownership.ScopeIdentity)
            && scopeState.Generation
                == ownership.ScopeInvalidationGeneration
            && manifestResolutionSource.CaptureScopeBarriers(
                    new VbaIdentifiedDocument(
                        ownership.ActiveDocumentIdentity,
                        ownership.ActiveUri),
                    ownership.Resolution)
                .Revision == ownership.ManifestVersion
            && hostClassProjectionStore
                .CaptureSelectionState(ownership.Resolution)
                .Revision == ownership.HostClassProjectionRevision
            && !HasSourceChangedSince(
                ownership.Resolution,
                ownership.ActiveDocumentIdentity,
                ownership.WorkspaceVersion,
                sourceRevisionHistory,
                ownership.SourceIdentities);
    }

    private string? RegisterReconciliationScope(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution,
        IReadOnlyList<VbaProjectDiskSource> diskSources,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
            sourceDocumentsByIdentity,
        IReadOnlySet<VbaDocumentIdentity> existingOpenSourceIdentities,
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
    {
        if (!VbaProjectIdentityModel.TryIdentifyAuthority(
                resolution,
                out var authorityKey))
        {
            return null;
        }

        var previousAuthorityKey =
            reconciliationAuthoritiesByActiveUri.TryGetValue(
                activeDocument.Identity,
                out var mappedAuthorityKey)
                    ? mappedAuthorityKey
                    : authorityKey;
        var knownSources = CreateKnownSources(
            diskSources,
            sourceDocumentsByIdentity,
            existingOpenSourceIdentities,
            trackedDocuments);
        if (!reconciliationBaselines.TryGetValue(
                previousAuthorityKey,
                out var previousBaseline))
        {
            RemoveReconciliationAuthorityMapping(activeDocument.Identity);
            reconciliationAuthoritiesByActiveUri[activeDocument.Identity] =
                authorityKey;
            reconciliationBaselines[authorityKey] =
                new ReconciliationBaseline(
                    activeDocument.Uri,
                    activeDocument.Identity,
                    resolution,
                    knownSources,
                    ++nextReconciliationGeneration);
            return null;
        }

        return TransferReconciliationScope(
            previousAuthorityKey,
            previousBaseline,
            activeDocument,
            resolution,
            replacementKnownSources: knownSources,
            retainPreviousAuthority:
                VbaProjectIdentityModel.Relate(
                    activeDocument.Identity,
                    previousBaseline.Resolution,
                    resolution).Kind
                    == VbaProjectAuthorityRelationKind.RetainPrevious,
            retainedPreviousSourceIdentities: null,
            trackedDocuments);
    }

    private static IReadOnlyList<VbaProjectDiskKnownSource> CreateKnownSources(
        IReadOnlyList<VbaProjectDiskSource> diskSources,
        IReadOnlyDictionary<VbaDocumentIdentity, VbaTrackedDocument>
            sourceDocumentsByIdentity,
        IReadOnlySet<VbaDocumentIdentity> existingOpenSourceIdentities,
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments)
    {
        var knownSources = diskSources
            .Select(
                source => new VbaProjectDiskKnownSource(
                    source.DocumentIdentity,
                    source.Uri,
                    source.FullPath,
                    source.Text,
                    source.ContentIdentity))
            .ToList();
        var knownIdentities = knownSources
            .Select(source => source.DocumentIdentity)
            .ToHashSet();
        foreach (var trackedDocument in trackedDocuments)
        {
            if (!sourceDocumentsByIdentity.TryGetValue(
                    trackedDocument.Identity,
                    out var matchingSource))
            {
                continue;
            }

            var localPath = VbaProjectResolver.TryGetLocalPath(
                matchingSource.Uri);
            if (localPath is null)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(localPath);
            if (!existingOpenSourceIdentities.Contains(
                    trackedDocument.Identity)
                || !knownIdentities.Add(trackedDocument.Identity))
            {
                continue;
            }

            knownSources.Add(
                new VbaProjectDiskKnownSource(
                    trackedDocument.Identity,
                    matchingSource.Uri,
                    fullPath,
                    matchingSource.Text,
                    VbaProjectDiskContentIdentity.FromText(
                        matchingSource.Text)));
        }

        return knownSources.ToArray();
    }

    private void RebuildScopeAuthorityLookup()
    {
        scopeAuthorityLookup = ProjectScopeAuthorityLookup.Create(
            scopeAuthoritySeeds.Values);
    }

    private sealed record CachedProjectSnapshot(
        long WorkspaceVersion,
        long ManifestVersion,
        long ReferenceCatalogRevision,
        long HostClassProjectionRevision,
        IReadOnlyList<VbaDocumentIdentity> DiskSourceIdentities,
        VbaProjectSnapshot Snapshot);

    internal sealed record ReconciliationBaseline(
        string ActiveUri,
        VbaDocumentIdentity ActiveDocumentIdentity,
        VbaProjectResolution Resolution,
        IReadOnlyList<VbaProjectDiskKnownSource> KnownSources,
        long Generation);

    private CapturedProjectScopeInvalidation CaptureInvalidation(
        VbaProjectSnapshotIdentity cacheIdentity,
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            if (!scopeInvalidationStates.TryGetValue(
                    cacheIdentity,
                    out var scopeState))
            {
                scopeState = new ProjectScopeInvalidationState(
                    activeDocument.Uri,
                    activeDocument.Identity,
                    resolution);
                scopeInvalidationStates.Add(cacheIdentity, scopeState);
            }
            else
            {
                scopeState.ActiveUri = activeDocument.Uri;
                scopeState.ActiveDocumentIdentity = activeDocument.Identity;
                scopeState.Resolution = resolution;
            }

            scopeState.PendingBuilds++;
            return new CapturedProjectScopeInvalidation(
                fullInvalidationGeneration,
                scopeState.Generation,
                scopeState);
        }
    }

    private void ReleaseInvalidationBuild(
        VbaProjectSnapshotIdentity cacheIdentity,
        CapturedProjectScopeInvalidation capturedInvalidation)
    {
        var diskIdentitiesToInvalidate =
            new HashSet<VbaDocumentIdentity>();
        lock (gate)
        {
            var capturedState = capturedInvalidation.State;
            capturedState.PendingBuilds--;
            if (capturedState.PendingBuilds != 0)
            {
                return;
            }

            var isCurrentState =
                scopeInvalidationStates.TryGetValue(
                    cacheIdentity,
                    out var currentState)
                && ReferenceEquals(currentState, capturedState);
            if (isCurrentState
                && (capturedState.IsMaterialized
                    || cache.ContainsKey(cacheIdentity)))
            {
                return;
            }

            if (isCurrentState)
            {
                scopeInvalidationStates.Remove(cacheIdentity);
                scopeAuthoritySeeds.Remove(cacheIdentity);
            }

            foreach (var sourceIdentity in capturedState.SourceIdentities)
            {
                diskIdentitiesToInvalidate.Add(sourceIdentity);
            }

            if (isCurrentState)
            {
                RebuildScopeAuthorityLookup();
            }
        }

        foreach (var diskIdentity in diskIdentitiesToInvalidate)
        {
            diskInventory.InvalidateSource(diskIdentity);
            diskDocumentCache.Invalidate(diskIdentity);
        }
    }

    private void RegisterScopeSources(
        VbaProjectSnapshotIdentity cacheIdentity,
        CapturedProjectScopeInvalidation capturedInvalidation,
        IEnumerable<VbaDocumentIdentity> sourceIdentities)
    {
        lock (gate)
        {
            foreach (var sourceIdentity in sourceIdentities)
            {
                capturedInvalidation.State.SourceIdentities.Add(
                    sourceIdentity);
            }
        }
    }

    private static bool HasSourceChangedSince(
        VbaProjectResolution resolution,
        VbaDocumentIdentity activeDocumentIdentity,
        long workspaceVersion,
        VbaSourceRevisionHistory sourceRevisions,
        IEnumerable<VbaDocumentIdentity> sourceIdentities)
    {
        var knownSourceIdentities = sourceIdentities.ToHashSet();
        foreach (var sourceRevision in sourceRevisions.CaptureEntries())
        {
            if (sourceRevision.Revision > workspaceVersion
                && (resolution.ContainsUri(sourceRevision.Uri)
                || activeDocumentIdentity
                    == sourceRevision.DocumentIdentity
                || knownSourceIdentities.Contains(
                    sourceRevision.DocumentIdentity)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BelongsToScope(
        ProjectScopeInvalidationState scopeState,
        VbaIdentifiedDocument document)
        => scopeState.Resolution.ContainsUri(document.Uri)
            || scopeState.ActiveDocumentIdentity == document.Identity
            || scopeState.SourceIdentities.Contains(document.Identity);

    private static bool BelongsToScope(
        WarmProjectScopeSeed seed,
        VbaIdentifiedDocument document)
        => seed.Resolution.ContainsUri(document.Uri)
            || seed.ActiveDocumentIdentity == document.Identity
            || seed.SourceIdentities.Contains(document.Identity);

    private static bool BelongsToScope(
        ReconciliationBaseline baseline,
        VbaIdentifiedDocument document)
        => baseline.Resolution.ContainsUri(document.Uri)
            || baseline.ActiveDocumentIdentity == document.Identity
            || baseline.KnownSources.Any(
                source => source.DocumentIdentity == document.Identity);

    private sealed record CachedManifestResolution(
        long Version,
        VbaProjectResolution Resolution);

    private sealed class ProjectScopeInvalidationState
    {
        public ProjectScopeInvalidationState(
            string activeUri,
            VbaDocumentIdentity activeDocumentIdentity,
            VbaProjectResolution resolution)
        {
            ActiveUri = activeUri;
            ActiveDocumentIdentity = activeDocumentIdentity;
            Resolution = resolution;
        }

        public string ActiveUri { get; set; }

        public VbaDocumentIdentity ActiveDocumentIdentity { get; set; }

        public VbaProjectResolution Resolution { get; set; }

        public HashSet<VbaDocumentIdentity> SourceIdentities { get; } =
            new();

        public long Generation { get; set; }

        public int PendingBuilds { get; set; }

        public bool IsMaterialized { get; set; }
    }

    private sealed record CapturedProjectScopeInvalidation(
        long FullGeneration,
        long ScopeGeneration,
        ProjectScopeInvalidationState State);

    private sealed record ProjectScopeCapture(
        VbaIdentifiedDocument ActiveDocument,
        VbaProjectResolution Resolution,
        VbaProjectManifestBarrierSnapshot ManifestBarriers,
        VbaProjectReferenceCatalogSelectionState ReferenceCatalogState,
        VbaHostClassProjectionSelectionState HostClassProjectionState,
        VbaProjectSnapshotIdentity CacheIdentity,
        VbaProjectSnapshotIdentity? SupersededCacheIdentity)
    {
        public string ActiveUri => ActiveDocument.Uri;
    }

    private sealed record CapturedProjectScopeAuthorityLookup(
        ProjectScopeAuthorityLookup Lookup);

    private sealed record PreferredRetirementScope(
        VbaProjectSnapshotIdentity CacheIdentity,
        VbaProjectAuthorityIdentity ReconciliationAuthorityKey);

    private sealed record WarmProjectScopeSeed(
        VbaProjectSnapshotIdentity CacheIdentity,
        string ActiveUri,
        VbaDocumentIdentity ActiveDocumentIdentity,
        VbaProjectResolution Resolution,
        long ManifestVersion,
        IReadOnlyList<VbaDocumentIdentity> SourceIdentities);

    private sealed class ProjectScopeAuthorityLookup
    {
        private readonly IReadOnlyDictionary<
            VbaDocumentIdentity,
            WarmProjectScopeSeed>
            exactAuthorities;

        private ProjectScopeAuthorityLookup(
            IReadOnlyDictionary<
                VbaDocumentIdentity,
                WarmProjectScopeSeed> exactAuthorities)
        {
            this.exactAuthorities = exactAuthorities;
        }

        public static ProjectScopeAuthorityLookup Empty { get; } =
            new(
                new Dictionary<
                    VbaDocumentIdentity,
                    WarmProjectScopeSeed>());

        public static ProjectScopeAuthorityLookup Create(
            IEnumerable<WarmProjectScopeSeed> seeds)
        {
            var exact = new Dictionary<
                VbaDocumentIdentity,
                WarmProjectScopeSeed>();
            foreach (var seed in seeds)
            {
                AddPreferred(
                    exact,
                    seed.ActiveDocumentIdentity,
                    seed);

                foreach (var sourceIdentity in seed.SourceIdentities)
                {
                    AddPreferred(exact, sourceIdentity, seed);
                }
            }

            return new ProjectScopeAuthorityLookup(exact);
        }

        public WarmProjectScopeSeed? Resolve(
            VbaDocumentIdentity documentIdentity)
        {
            if (exactAuthorities.TryGetValue(
                documentIdentity,
                out var exact))
            {
                return exact;
            }

            return null;
        }

        private static void AddPreferred(
            Dictionary<VbaDocumentIdentity, WarmProjectScopeSeed> authorities,
            VbaDocumentIdentity identity,
            WarmProjectScopeSeed seed)
        {
            if (!authorities.TryGetValue(identity, out var current)
                || IsMoreSpecific(seed, current))
            {
                authorities[identity] = seed;
            }
        }

        private static bool IsMoreSpecific(
            WarmProjectScopeSeed candidate,
            WarmProjectScopeSeed current)
        {
            var candidateRoot =
                VbaProjectIdentityModel.TryNormalizeSnapshotPath(
                    candidate.Resolution.RootPath,
                    out var normalizedCandidateRoot)
                        ? normalizedCandidateRoot
                        : "";
            var currentRoot =
                VbaProjectIdentityModel.TryNormalizeSnapshotPath(
                    current.Resolution.RootPath,
                    out var normalizedCurrentRoot)
                        ? normalizedCurrentRoot
                        : "";
            if (candidateRoot.Length != currentRoot.Length)
            {
                return candidateRoot.Length > currentRoot.Length;
            }

            if (candidate.Resolution.Kind != current.Resolution.Kind)
            {
                return candidate.Resolution.Kind
                    == VbaProjectResolutionKind.ManifestDocument;
            }

            return candidate.CacheIdentity.CompareTo(
                    current.CacheIdentity)
                < 0;
        }
    }
}
