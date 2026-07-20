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
/// <param name="Documents">The tracked workspace documents.</param>
/// <param name="ExcludedSourceUris">The source URIs excluded from disk inventory.</param>
/// <param name="Version">The workspace document-state version.</param>
internal sealed record VbaWorkspaceSnapshotState(
    IReadOnlyDictionary<string, VbaTrackedDocument> Documents,
    IReadOnlySet<string> ExcludedSourceUris,
    long Version);

internal sealed record VbaProjectManifestRetentionScope(
    string ActiveUri,
    string RootPath);

/// <summary>
/// Represents the stable cache identity for one project snapshot scope.
/// </summary>
internal sealed record VbaProjectSnapshotIdentity(string Key)
{
    public static VbaProjectSnapshotIdentity Create(string activeUri, VbaProjectResolution resolution)
        => new(string.Join(
            "\u001e",
            resolution.Kind.ToString(),
            resolution.Kind == VbaProjectResolutionKind.AdHoc
                && string.IsNullOrWhiteSpace(resolution.RootPath)
                ? activeUri
                : "",
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
    private readonly Dictionary<string, ReconciliationBaseline> reconciliationBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> reconciliationAuthoritiesByActiveUri =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedManifestResolution> manifestResolutionCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaProjectReferenceCatalogCache referenceCatalogCache;
    private readonly IVbaProjectManifestResolutionSource manifestResolutionSource;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;
    private readonly VbaProjectSnapshotBuilder snapshotBuilder;
    private readonly IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver;
    private readonly IVbaProjectSnapshotBuildObserver buildObserver;
    private readonly Dictionary<string, ProjectScopeInvalidationState> scopeInvalidationStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WarmProjectScopeSeed> scopeAuthoritySeeds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaSourceRevisionHistory sourceRevisionHistory = new();
    private ProjectScopeAuthorityLookup scopeAuthorityLookup =
        ProjectScopeAuthorityLookup.Empty;
    private long fullInvalidationGeneration;
    private long nextReconciliationGeneration;

    public VbaProjectSnapshotProvider(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        VbaProjectSourceDocumentCache diskDocumentCache,
        IVbaProjectManifestResolutionSource manifestResolutionSource,
        IVbaProjectReferenceCatalogLifecycleObserver? lifecycleObserver = null,
        IVbaProjectSnapshotBuildObserver? buildObserver = null)
    {
        this.referenceCatalogCache = referenceCatalogCache;
        this.diskDocumentCache = diskDocumentCache;
        this.manifestResolutionSource = manifestResolutionSource;
        this.lifecycleObserver =
            lifecycleObserver ?? NullVbaProjectReferenceCatalogLifecycleObserver.Instance;
        this.buildObserver = buildObserver ?? NullVbaProjectSnapshotBuildObserver.Instance;
        snapshotBuilder = new VbaProjectSnapshotBuilder(diskDocumentCache);
    }

    public VbaProjectSnapshot CreateProjectSnapshot(
        string activeUri,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        using var revisionCapture =
            sourceRevisionHistory.BeginCapture(workspaceState.Version);
        cancellationToken.ThrowIfCancellationRequested();
        var authorityLookup = CaptureScopeAuthorityLookup(
            cancellationToken);
        var capture = CaptureKnownProjectScope(
                activeUri,
                authorityLookup,
                cancellationToken,
                out var supersededCacheIdentity)
            ?? CaptureProjectScope(
                activeUri,
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
        string activeUri,
        CapturedProjectScopeAuthorityLookup authorityLookup,
        CancellationToken cancellationToken,
        out VbaProjectSnapshotIdentity? supersededCacheIdentity)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seed = authorityLookup.Lookup.Resolve(activeUri);
        if (seed is null)
        {
            supersededCacheIdentity = null;
            return null;
        }

        var manifestBarriers =
            manifestResolutionSource.CaptureScopeBarriers(
            activeUri,
            seed.Resolution);
        if (seed.ManifestVersion != manifestBarriers.Revision)
        {
            supersededCacheIdentity =
                new VbaProjectSnapshotIdentity(seed.CacheKey);
            return null;
        }

        supersededCacheIdentity = null;
        return new ProjectScopeCapture(
            activeUri,
            seed.Resolution,
            manifestBarriers,
            referenceCatalogCache.CaptureSelectionState(
                seed.Resolution.ReferenceEntries),
            new VbaProjectSnapshotIdentity(seed.CacheKey),
            SupersededCacheIdentity: null);
    }

    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots(
        IReadOnlyList<string> activeUris,
        VbaWorkspaceSnapshotState workspaceState,
        CancellationToken cancellationToken)
    {
        using var revisionCapture =
            sourceRevisionHistory.BeginCapture(workspaceState.Version);
        var authorityLookup = CaptureScopeAuthorityLookup(
            cancellationToken);
        var captures = new Dictionary<string, ProjectScopeCapture>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var activeUri in activeUris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var capture = CaptureKnownProjectScope(
                    activeUri,
                    authorityLookup,
                    cancellationToken,
                    out var supersededCacheIdentity)
                ?? CaptureProjectScope(
                    activeUri,
                    cancellationToken,
                    supersededCacheIdentity);
            captures.TryAdd(capture.CacheIdentity.Key, capture);
        }

        return captures.Values
            .Select(capture => CreateProjectSnapshot(
                capture,
                workspaceState,
                cancellationToken))
            .ToArray();
    }

    private ProjectScopeCapture CaptureProjectScope(
        string activeUri,
        CancellationToken cancellationToken,
        VbaProjectSnapshotIdentity? supersededCacheIdentity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestCapture = ResolveCurrentManifest(activeUri);
        var resolution = manifestCapture.Resolution;
        var referenceCatalogState = referenceCatalogCache.CaptureSelectionState(
            resolution.ReferenceEntries);
        var cacheIdentity = VbaProjectSnapshotIdentity.Create(activeUri, resolution);
        return new ProjectScopeCapture(
            activeUri,
            resolution,
            manifestCapture.Barriers,
            referenceCatalogState,
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
            capture.ActiveUri,
            capture.Resolution);
        try
        {
            if (TryGetCachedSnapshot(
                capture.CacheIdentity,
                capture.ManifestBarriers.Revision,
                capture.ReferenceCatalogState.Revision,
                cancellationToken,
                out var cachedSnapshot))
            {
                return cachedSnapshot;
            }

            buildObserver.BeforeBuildProjectSnapshot(
                capture.ActiveUri,
                cancellationToken);
            var inventorySnapshot = snapshotBuilder.CreateInventorySnapshot(
                capture.ActiveUri,
                capture.Resolution,
                workspaceState.Documents,
                workspaceState.ExcludedSourceUris,
                capture.ManifestBarriers.Overrides,
                cancellationToken);
            RegisterScopeSources(
                capture.CacheIdentity,
                capturedInvalidation,
                inventorySnapshot.Documents.Keys);

            buildObserver.BeforeBuildSemanticInventory(
                capture.ActiveUri,
                cancellationToken);
            var snapshot = snapshotBuilder.BuildSnapshot(
                capture.Resolution,
                inventorySnapshot.Documents,
                capture.ReferenceCatalogState.CatalogSet);
            buildObserver.BeforeStore(workspaceState.Version, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StoreCachedSnapshot(
                capture.CacheIdentity,
                workspaceState.Version,
                capture.ManifestBarriers.Revision,
                capture.ReferenceCatalogState.Revision,
                capturedInvalidation,
                capture.SupersededCacheIdentity,
                inventorySnapshot.Documents.Keys,
                inventorySnapshot.SourceFiles,
                snapshot,
                workspaceState.Documents.Keys.ToArray());
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

    public void InvalidateSource(string uri, long sourceRevision)
    {
        lock (gate)
        {
            sourceRevisionHistory.Record(uri, sourceRevision);
            foreach (var (key, state) in scopeInvalidationStates)
            {
                if (!BelongsToScope(state, uri))
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
        IReadOnlyList<string> remainingTrackedUris)
    {
        var diskPathsToInvalidate = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            var remainingUris = remainingTrackedUris
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(uri => uri, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cacheAnchors = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var reconciliationAnchors = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var staleAuthorityUris = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var preferredScopes = remainingUris.ToDictionary(
                uri => uri,
                uri =>
                {
                    var seed = scopeAuthorityLookup.Resolve(uri);
                    if (seed is not null
                        && manifestResolutionSource.CaptureScopeBarriers(
                                uri,
                                seed.Resolution)
                            .Revision != seed.ManifestVersion)
                    {
                        staleAuthorityUris.Add(uri);
                        return null;
                    }

                    return seed is null
                        ? null
                        : new PreferredRetirementScope(
                            seed.CacheKey,
                            CreateReconciliationAuthorityKey(
                                uri,
                                seed.Resolution));
                },
                StringComparer.OrdinalIgnoreCase);
            foreach (var cacheKey in scopeInvalidationStates.Keys
                .Concat(cache.Keys)
                .Concat(scopeAuthoritySeeds.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var anchorUri = remainingUris.FirstOrDefault(
                    uri => staleAuthorityUris.Contains(uri)
                        ? false
                        : preferredScopes[uri] is { } preferred
                        ? preferred.CacheKey.Equals(
                            cacheKey,
                            StringComparison.OrdinalIgnoreCase)
                        : scopeInvalidationStates.TryGetValue(
                            cacheKey,
                            out var scopeState)
                            ? BelongsToScope(scopeState, uri)
                            : scopeAuthoritySeeds.TryGetValue(
                                cacheKey,
                                out var seed)
                                && BelongsToScope(seed, uri));
                if (anchorUri is not null)
                {
                    cacheAnchors[cacheKey] = anchorUri;
                }
            }

            foreach (var (authorityKey, baseline) in
                reconciliationBaselines)
            {
                var anchorUri = remainingUris.FirstOrDefault(
                    uri => staleAuthorityUris.Contains(uri)
                        ? false
                        : preferredScopes[uri] is { } preferred
                        ? preferred.ReconciliationAuthorityKey.Equals(
                            authorityKey,
                            StringComparison.OrdinalIgnoreCase)
                        : BelongsToScope(baseline, uri));
                if (anchorUri is not null)
                {
                    reconciliationAnchors[authorityKey] = anchorUri;
                }
            }

            var retiredCacheKeys = scopeInvalidationStates.Keys
                .Concat(cache.Keys)
                .Concat(scopeAuthoritySeeds.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(key => !cacheAnchors.ContainsKey(key))
                .ToArray();
            var retiredReconciliationKeys = reconciliationBaselines.Keys
                .Where(key => !reconciliationAnchors.ContainsKey(key))
                .ToArray();

            foreach (var cacheKey in retiredCacheKeys)
            {
                if (cache.TryGetValue(cacheKey, out var cached))
                {
                    foreach (var sourceFile in cached.SourceFiles)
                    {
                        diskPathsToInvalidate.Add(sourceFile.FullPath);
                    }
                }

                if (scopeInvalidationStates.TryGetValue(
                    cacheKey,
                    out var scopeState))
                {
                    foreach (var sourceUri in scopeState.SourceUris)
                    {
                        var sourcePath =
                            VbaProjectResolver.TryGetLocalPath(sourceUri);
                        if (sourcePath is not null)
                        {
                            diskPathsToInvalidate.Add(sourcePath);
                        }
                    }
                }

                cache.Remove(cacheKey);
                scopeInvalidationStates.Remove(cacheKey);
                scopeAuthoritySeeds.Remove(cacheKey);
            }

            foreach (var (cacheKey, anchorUri) in cacheAnchors)
            {
                if (scopeInvalidationStates.TryGetValue(
                    cacheKey,
                    out var scopeState))
                {
                    scopeState.ActiveUri = anchorUri;
                }

                if (scopeAuthoritySeeds.TryGetValue(
                    cacheKey,
                    out var seed))
                {
                    scopeAuthoritySeeds[cacheKey] = seed with
                    {
                        ActiveUri = anchorUri
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
                        diskPathsToInvalidate.Add(source.FullPath);
                    }
                }

                reconciliationBaselines.Remove(authorityKey);
            }

            foreach (var (authorityKey, anchorUri) in
                reconciliationAnchors)
            {
                if (reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
                {
                    var reanchored =
                        !SameDocumentIdentity(
                            baseline.ActiveUri,
                            anchorUri);
                    reconciliationBaselines[authorityKey] = baseline with
                    {
                        ActiveUri = anchorUri,
                        Generation = reanchored
                            ? ++nextReconciliationGeneration
                            : baseline.Generation
                    };
                }
            }

            reconciliationAuthoritiesByActiveUri.Clear();
            foreach (var (authorityKey, anchorUri) in
                reconciliationAnchors)
            {
                if (reconciliationBaselines.ContainsKey(authorityKey))
                {
                    reconciliationAuthoritiesByActiveUri[anchorUri] =
                        authorityKey;
                }
            }

            foreach (var activeUri in manifestResolutionCache.Keys
                .Where(
                    candidate => !remainingUris.Any(
                        uri => SameDocumentIdentity(candidate, uri)))
                .ToArray())
            {
                manifestResolutionCache.Remove(activeUri);
            }

            RebuildScopeAuthorityLookup();
        }

        foreach (var diskPath in diskPathsToInvalidate)
        {
            diskDocumentCache.Invalidate(diskPath);
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
                        baseline.ActiveUri,
                        baseline.Resolution.RootPath))
                .Concat(
                    scopeAuthoritySeeds.Values.Select(
                        seed => new VbaProjectManifestRetentionScope(
                            seed.ActiveUri,
                            seed.Resolution.RootPath)))
                .Distinct()
                .ToArray();
        }
    }

    public IReadOnlyList<VbaProjectDiskReconciliationScope>
        CaptureDiskReconciliationScopes(long capturedWorkspaceRevision)
    {
        lock (gate)
        {
            var scopes = new List<VbaProjectDiskReconciliationScope>();
            foreach (var (authorityKey, baseline) in reconciliationBaselines)
            {
                scopes.Add(
                    new VbaProjectDiskReconciliationScope(
                        authorityKey,
                        baseline.ActiveUri,
                        baseline.Resolution,
                        capturedWorkspaceRevision,
                        GetManifestCandidateUris(
                                baseline.ActiveUri,
                                baseline.Resolution)
                            .Select(uri => new VbaProjectDiskManifestCandidate(
                                uri,
                                CapturedRevision: 0,
                                new VbaProjectDiskManifestBaseline(
                                    Exists: false,
                                    Text: null)))
                            .ToArray(),
                        baseline.KnownSources)
                    {
                        ManifestBarriers =
                            manifestResolutionSource
                                .CaptureDiskReconciliationBarriers(
                                baseline.ActiveUri,
                                baseline.Resolution),
                        AuthorityGeneration = baseline.Generation
                    });
            }

            return scopes;
        }
    }

    public bool IsReconciliationScopeCurrent(
        string authorityKey,
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
                        baseline.ActiveUri,
                        baseline.Resolution)
                    == capturedManifestBarrierRevision;
        }
    }

    public void CommitReconciledSourceBaseline(
        string authorityKey,
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
                .Where(known => !SameDocumentIdentity(known.Uri, source.Uri))
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
        string authorityKey,
        string uri)
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
                    .Where(known => !SameDocumentIdentity(known.Uri, uri))
                    .ToArray()
            };
        }
    }

    public void ReleaseReconciledSourceOwnership(
        string authorityKey,
        string uri)
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
                    .Where(known => !SameDocumentIdentity(known.Uri, uri))
                    .ToArray()
            };

            foreach (var (cacheKey, scopeState) in
                scopeInvalidationStates.ToArray())
            {
                if (!CreateReconciliationAuthorityKey(
                        scopeState.ActiveUri,
                        scopeState.Resolution)
                    .Equals(
                        authorityKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scopeState.Generation++;
                scopeState.SourceUris.RemoveWhere(
                    sourceUri => SameDocumentIdentity(sourceUri, uri));
                cache.Remove(cacheKey);
                if (scopeAuthoritySeeds.TryGetValue(
                        cacheKey,
                        out var seed))
                {
                    scopeAuthoritySeeds[cacheKey] = seed with
                    {
                        SourceUris = seed.SourceUris
                            .Where(
                                sourceUri => !SameDocumentIdentity(
                                    sourceUri,
                                    uri))
                            .ToArray()
                    };
                }
            }

            RebuildScopeAuthorityLookup();
        }
    }

    public void CommitReconciledManifestScope(
        string authorityKey,
        string activeUri,
        VbaProjectResolution resolution,
        bool retainPreviousAuthority,
        IReadOnlyList<string> retainedPreviousSourceUris,
        IReadOnlyList<string> trackedUris)
    {
        lock (gate)
        {
            if (!reconciliationBaselines.TryGetValue(
                    authorityKey,
                    out var baseline))
            {
                return;
            }

            _ = TransferReconciliationScope(
                authorityKey,
                baseline,
                activeUri,
                resolution,
                replacementKnownSources: null,
                retainPreviousAuthority,
                retainedPreviousSourceUris,
                trackedUris);

            var previousCacheIdentity =
                VbaProjectSnapshotIdentity.Create(
                    baseline.ActiveUri,
                    baseline.Resolution);
            var committedCacheIdentity =
                VbaProjectSnapshotIdentity.Create(
                    activeUri,
                    resolution);
            if (!previousCacheIdentity.Key.Equals(
                    committedCacheIdentity.Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                cache.Remove(previousCacheIdentity.Key);
                scopeInvalidationStates.Remove(
                    previousCacheIdentity.Key);
                scopeAuthoritySeeds.Remove(
                    previousCacheIdentity.Key);
                RebuildScopeAuthorityLookup();
            }
        }
    }

    private string? TransferReconciliationScope(
        string previousAuthorityKey,
        ReconciliationBaseline previousBaseline,
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyList<VbaProjectDiskKnownSource>? replacementKnownSources,
        bool retainPreviousAuthority,
        IReadOnlyList<string>? retainedPreviousSourceUris,
        IReadOnlyList<string> trackedUris)
    {
        var committedAuthorityKey = CreateReconciliationAuthorityKey(
            activeUri,
            resolution);
        var sameSourceOwnershipBoundary =
            HasSameSourceOwnershipBoundary(
                previousBaseline.Resolution,
                resolution);
        RemoveReconciliationAuthorityMapping(activeUri);
        if (previousAuthorityKey.Equals(
                committedAuthorityKey,
                StringComparison.OrdinalIgnoreCase))
        {
            reconciliationBaselines[previousAuthorityKey] =
                previousBaseline with
                {
                    ActiveUri = activeUri,
                    Resolution = resolution,
                    KnownSources = sameSourceOwnershipBoundary
                        ? previousBaseline.KnownSources
                        : replacementKnownSources ?? [],
                    Generation = sameSourceOwnershipBoundary
                        ? previousBaseline.Generation
                        : ++nextReconciliationGeneration
                };
            reconciliationAuthoritiesByActiveUri[activeUri] =
                previousAuthorityKey;
            return null;
        }

        reconciliationBaselines.TryGetValue(
            committedAuthorityKey,
            out var existing);
        reconciliationBaselines[committedAuthorityKey] =
            new ReconciliationBaseline(
                activeUri,
                resolution,
                replacementKnownSources
                    ?? existing?.KnownSources
                    ?? [],
                ++nextReconciliationGeneration);

        var previousAnchor = retainPreviousAuthority
            ? trackedUris.FirstOrDefault(
                uri => !SameDocumentIdentity(uri, activeUri)
                    && BelongsToScope(previousBaseline, uri)
                    && !BelongsToTransferredProject(
                        resolution,
                        uri))
            : null;
        if (previousAnchor is null)
        {
            reconciliationBaselines.Remove(previousAuthorityKey);
            RemoveReconciliationAuthorityMappings(previousAuthorityKey);
        }
        else
        {
            var retainedSources = retainedPreviousSourceUris?.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            reconciliationBaselines[previousAuthorityKey] =
                previousBaseline with
                {
                    ActiveUri = previousAnchor,
                    KnownSources = previousBaseline.KnownSources
                        .Where(
                            source => retainedSources is null
                                ? !BelongsToTransferredProject(
                                    resolution,
                                    source.Uri)
                                : retainedSources.Contains(source.Uri))
                        .ToArray()
                };
            reconciliationAuthoritiesByActiveUri[previousAnchor] =
                previousAuthorityKey;
        }

        reconciliationAuthoritiesByActiveUri[activeUri] =
            committedAuthorityKey;
        return previousAnchor;
    }

    private void RemoveReconciliationAuthorityMapping(string activeUri)
    {
        foreach (var mappedActiveUri in
            reconciliationAuthoritiesByActiveUri.Keys
                .Where(
                    candidate =>
                        SameDocumentIdentity(candidate, activeUri))
                .ToArray())
        {
            reconciliationAuthoritiesByActiveUri.Remove(mappedActiveUri);
        }
    }

    private void RemoveReconciliationAuthorityMappings(string authorityKey)
    {
        foreach (var mappedActiveUri in
            reconciliationAuthoritiesByActiveUri
                .Where(pair => pair.Value.Equals(
                    authorityKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray())
        {
            reconciliationAuthoritiesByActiveUri.Remove(mappedActiveUri);
        }
    }

    private static bool ShouldRetainPreviousAuthority(
        VbaProjectResolution previous,
        VbaProjectResolution current)
    {
        if (previous.Kind
                != VbaProjectResolutionKind.ManifestDocument
            || current.Kind
                != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrWhiteSpace(previous.ManifestPath)
            || string.IsNullOrWhiteSpace(current.ManifestPath)
            || previous.ManifestPath.Equals(
                current.ManifestPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return VbaProjectResolver.IsPathUnder(
            current.ManifestPath,
            previous.RootPath);
    }

    private static bool BelongsToTransferredProject(
        VbaProjectResolution resolution,
        string uri)
    {
        if (resolution.Kind
                != VbaProjectResolutionKind.ManifestDocument
            || string.IsNullOrWhiteSpace(resolution.ManifestPath))
        {
            return resolution.ContainsUri(uri);
        }

        var path = VbaProjectResolver.TryGetLocalPath(uri);
        var manifestDirectory = Path.GetDirectoryName(
            resolution.ManifestPath);
        return path is not null
            && manifestDirectory is not null
            && VbaProjectResolver.IsPathUnder(
                path,
                manifestDirectory);
    }

    private static bool HasSameSourceOwnershipBoundary(
        VbaProjectResolution left,
        VbaProjectResolution right)
        => left.Kind == right.Kind
            && NormalizeAuthorityPath(left.RootPath).Equals(
                NormalizeAuthorityPath(right.RootPath),
                StringComparison.OrdinalIgnoreCase)
            && NormalizeAuthorityPath(left.ManifestPath ?? "").Equals(
                NormalizeAuthorityPath(right.ManifestPath ?? ""),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.DocumentName,
                right.DocumentName,
                StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> GetManifestCandidateUris(
        string activeUri,
        VbaProjectResolution resolution)
    {
        if (!string.IsNullOrWhiteSpace(resolution.ManifestPath))
        {
            var currentManifestPath =
                Path.GetFullPath(resolution.ManifestPath);
            var currentManifestUri =
                new Uri(currentManifestPath).AbsoluteUri;
            var manifestActivePath =
                VbaProjectResolver.TryGetLocalPath(activeUri);
            var currentManifestDirectory =
                Path.GetDirectoryName(currentManifestPath);
            if (manifestActivePath is null
                || currentManifestDirectory is null
                || !VbaProjectResolver.IsPathUnder(
                    manifestActivePath,
                    currentManifestDirectory))
            {
                return [currentManifestUri];
            }

            var manifestActiveDirectory =
                Path.GetDirectoryName(manifestActivePath)
                ?? Directory.GetCurrentDirectory();
            var manifestCandidates = new List<string>();
            for (var directory = new DirectoryInfo(manifestActiveDirectory);
                directory is not null;
                directory = directory.Parent)
            {
                manifestCandidates.Add(
                    new Uri(Path.Combine(
                        directory.FullName,
                        "vba-project.json")).AbsoluteUri);
            }

            return manifestCandidates;
        }

        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            return [];
        }

        var activeDirectory =
            Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        var candidates = new List<string>();
        for (var directory = new DirectoryInfo(activeDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            candidates.Add(
                new Uri(Path.Combine(directory.FullName, "vba-project.json"))
                    .AbsoluteUri);
        }

        return candidates;
    }

    private VbaProjectManifestResolutionCapture ResolveCurrentManifest(
        string activeUri)
    {
        CachedManifestResolution? cached;
        lock (gate)
        {
            manifestResolutionCache.TryGetValue(activeUri, out cached);
        }

        if (cached is not null)
        {
            var barriers =
                manifestResolutionSource.CaptureScopeBarriers(
                    activeUri,
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
            ScopeKey: activeUri));
        var capture =
            manifestResolutionSource.CaptureResolution(activeUri);
        lock (gate)
        {
            manifestResolutionCache[activeUri] =
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
        CancellationToken cancellationToken,
        out VbaProjectSnapshot snapshot)
    {
        var catalogInvalidatedScope = false;
        lock (gate)
        {
            if (cache.TryGetValue(cacheIdentity.Key, out var cached)
                && cached.ManifestVersion == expectedManifestVersion)
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
                VbaProjectReferenceCatalogLifecycleOperation.ProjectScopeInvalidation,
                ScopeKey: cacheIdentity.Key));
        }

        snapshot = default!;
        return false;
    }

    private void StoreCachedSnapshot(
        VbaProjectSnapshotIdentity cacheIdentity,
        long snapshotWorkspaceVersion,
        long snapshotManifestVersion,
        long snapshotReferenceCatalogRevision,
        CapturedProjectScopeInvalidation capturedInvalidation,
        VbaProjectSnapshotIdentity? supersededCacheIdentity,
        IEnumerable<string> sourceUris,
        IReadOnlyList<VbaProjectSourceFileState> sourceFiles,
        VbaProjectSnapshot snapshot,
        IReadOnlyList<string> trackedUris)
    {
        lock (gate)
        {
            if (fullInvalidationGeneration != capturedInvalidation.FullGeneration
                || !scopeInvalidationStates.TryGetValue(
                    cacheIdentity.Key,
                    out var scopeState)
                || !ReferenceEquals(
                    scopeState,
                    capturedInvalidation.State)
                || scopeState.Generation != capturedInvalidation.ScopeGeneration
                || manifestResolutionSource.CaptureScopeBarriers(
                        scopeState.ActiveUri,
                        scopeState.Resolution)
                    .Revision != snapshotManifestVersion
                || HasSourceChangedSince(
                    scopeState.Resolution,
                    scopeState.ActiveUri,
                    snapshotWorkspaceVersion,
                    sourceRevisionHistory,
                    sourceUris))
            {
                return;
            }

            if (cache.TryGetValue(cacheIdentity.Key, out var current)
                && (current.WorkspaceVersion > snapshotWorkspaceVersion
                    || current.ManifestVersion > snapshotManifestVersion
                    || current.ReferenceCatalogRevision > snapshotReferenceCatalogRevision))
            {
                return;
            }

            cache[cacheIdentity.Key] = new CachedProjectSnapshot(
                snapshotWorkspaceVersion,
                snapshotManifestVersion,
                snapshotReferenceCatalogRevision,
                sourceFiles,
                snapshot);
            scopeState.IsMaterialized = true;
            scopeAuthoritySeeds[cacheIdentity.Key] =
                new WarmProjectScopeSeed(
                    cacheIdentity.Key,
                    scopeState.ActiveUri,
                    scopeState.Resolution,
                    snapshotManifestVersion,
                    snapshot.SourceDocuments.Keys.ToArray());
            _ = RegisterReconciliationScope(
                scopeState.ActiveUri,
                scopeState.Resolution,
                sourceFiles,
                snapshot,
                trackedUris);
            if (supersededCacheIdentity is not null
                && !supersededCacheIdentity.Key.Equals(
                    cacheIdentity.Key,
                    StringComparison.OrdinalIgnoreCase))
            {
                cache.Remove(supersededCacheIdentity.Key);
                scopeInvalidationStates.Remove(
                    supersededCacheIdentity.Key);
                scopeAuthoritySeeds.Remove(
                    supersededCacheIdentity.Key);
            }

            RebuildScopeAuthorityLookup();
        }
    }

    private string? RegisterReconciliationScope(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyList<VbaProjectSourceFileState> sourceFiles,
        VbaProjectSnapshot snapshot,
        IReadOnlyList<string> trackedUris)
    {
        var authorityKey = CreateReconciliationAuthorityKey(
            activeUri,
            resolution);
        var existingActiveUri = reconciliationAuthoritiesByActiveUri.Keys
            .FirstOrDefault(
                candidate => SameDocumentIdentity(candidate, activeUri));
        var previousAuthorityKey = existingActiveUri is not null
            && reconciliationAuthoritiesByActiveUri.TryGetValue(
                existingActiveUri,
                out var mappedAuthorityKey)
                ? mappedAuthorityKey
                : authorityKey;
        var knownSources = CreateKnownSources(sourceFiles, snapshot);
        if (!reconciliationBaselines.TryGetValue(
                previousAuthorityKey,
                out var previousBaseline))
        {
            RemoveReconciliationAuthorityMapping(activeUri);
            reconciliationAuthoritiesByActiveUri[activeUri] = authorityKey;
            reconciliationBaselines[authorityKey] =
                new ReconciliationBaseline(
                    activeUri,
                    resolution,
                    knownSources,
                    ++nextReconciliationGeneration);
            return null;
        }

        return TransferReconciliationScope(
            previousAuthorityKey,
            previousBaseline,
            activeUri,
            resolution,
            replacementKnownSources: knownSources,
            retainPreviousAuthority:
                ShouldRetainPreviousAuthority(
                    previousBaseline.Resolution,
                    resolution),
            retainedPreviousSourceUris: null,
            trackedUris);
    }

    private static IReadOnlyList<VbaProjectDiskKnownSource> CreateKnownSources(
        IReadOnlyList<VbaProjectSourceFileState> sourceFiles,
        VbaProjectSnapshot snapshot)
    {
        var knownSources = new List<VbaProjectDiskKnownSource>();
        foreach (var sourceFile in sourceFiles)
        {
            var fullPath = Path.GetFullPath(sourceFile.FullPath);
            var uri = new Uri(fullPath).AbsoluteUri;
            if (!snapshot.SourceDocuments.TryGetValue(uri, out var sourceText))
            {
                continue;
            }

            knownSources.Add(new VbaProjectDiskKnownSource(
                uri,
                fullPath,
                sourceText));
        }

        return knownSources;
    }

    private static string CreateReconciliationAuthorityKey(
        string activeUri,
        VbaProjectResolution resolution)
    {
        if (resolution.Kind == VbaProjectResolutionKind.ManifestDocument
            && !string.IsNullOrWhiteSpace(resolution.ManifestPath))
        {
            return string.Join(
                "\u001e",
                "manifest",
                NormalizeAuthorityPath(resolution.ManifestPath),
                resolution.DocumentName ?? "");
        }

        var rootAuthority = NormalizeAuthorityPath(resolution.RootPath);
        return string.Join(
            "\u001e",
            "ad-hoc",
            string.IsNullOrWhiteSpace(rootAuthority)
                ? activeUri
                : rootAuthority);
    }

    private static string NormalizeAuthorityPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException)
        {
            return path;
        }
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
        IReadOnlyList<VbaProjectSourceFileState> SourceFiles,
        VbaProjectSnapshot Snapshot);

    private sealed record ReconciliationBaseline(
        string ActiveUri,
        VbaProjectResolution Resolution,
        IReadOnlyList<VbaProjectDiskKnownSource> KnownSources,
        long Generation);

    private CapturedProjectScopeInvalidation CaptureInvalidation(
        VbaProjectSnapshotIdentity cacheIdentity,
        string activeUri,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            if (!scopeInvalidationStates.TryGetValue(
                    cacheIdentity.Key,
                    out var scopeState))
            {
                scopeState = new ProjectScopeInvalidationState(
                    activeUri,
                    resolution);
                scopeInvalidationStates.Add(cacheIdentity.Key, scopeState);
            }
            else
            {
                scopeState.ActiveUri = activeUri;
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
        var diskPathsToInvalidate = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
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
                    cacheIdentity.Key,
                    out var currentState)
                && ReferenceEquals(currentState, capturedState);
            if (isCurrentState
                && (capturedState.IsMaterialized
                    || cache.ContainsKey(cacheIdentity.Key)))
            {
                return;
            }

            if (isCurrentState)
            {
                scopeInvalidationStates.Remove(cacheIdentity.Key);
                scopeAuthoritySeeds.Remove(cacheIdentity.Key);
            }

            foreach (var sourceUri in capturedState.SourceUris)
            {
                var sourcePath =
                    VbaProjectResolver.TryGetLocalPath(sourceUri);
                if (sourcePath is not null)
                {
                    diskPathsToInvalidate.Add(sourcePath);
                }
            }

            if (isCurrentState)
            {
                RebuildScopeAuthorityLookup();
            }
        }

        foreach (var diskPath in diskPathsToInvalidate)
        {
            diskDocumentCache.Invalidate(diskPath);
        }
    }

    private void RegisterScopeSources(
        VbaProjectSnapshotIdentity cacheIdentity,
        CapturedProjectScopeInvalidation capturedInvalidation,
        IEnumerable<string> sourceUris)
    {
        lock (gate)
        {
            foreach (var sourceUri in sourceUris)
            {
                capturedInvalidation.State.SourceUris.Add(sourceUri);
            }
        }
    }

    private static bool HasSourceChangedSince(
        VbaProjectResolution resolution,
        string activeUri,
        long workspaceVersion,
        VbaSourceRevisionHistory sourceRevisions,
        IEnumerable<string> sourceUris)
    {
        var knownSourceUris = sourceUris.ToArray();
        foreach (var (sourceUri, sourceRevision) in
            sourceRevisions.CaptureEntries())
        {
            if (sourceRevision > workspaceVersion
                && (resolution.ContainsUri(sourceUri)
                || SameDocumentIdentity(activeUri, sourceUri)
                || knownSourceUris.Any(
                    knownSourceUri => SameDocumentIdentity(
                        knownSourceUri,
                        sourceUri))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool BelongsToScope(
        ProjectScopeInvalidationState scopeState,
        string uri)
        => scopeState.Resolution.ContainsUri(uri)
            || SameDocumentIdentity(scopeState.ActiveUri, uri)
            || scopeState.SourceUris.Any(
                sourceUri => SameDocumentIdentity(sourceUri, uri));

    private static bool BelongsToScope(
        WarmProjectScopeSeed seed,
        string uri)
        => seed.Resolution.ContainsUri(uri)
            || SameDocumentIdentity(seed.ActiveUri, uri)
            || seed.SourceUris.Any(
                sourceUri => SameDocumentIdentity(sourceUri, uri));

    private static bool BelongsToScope(
        ReconciliationBaseline baseline,
        string uri)
        => baseline.Resolution.ContainsUri(uri)
            || SameDocumentIdentity(baseline.ActiveUri, uri)
            || baseline.KnownSources.Any(
                source => SameDocumentIdentity(source.Uri, uri));

    private static bool SameDocumentIdentity(string leftUri, string rightUri)
    {
        if (leftUri.Equals(rightUri, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftPath = VbaProjectResolver.TryGetLocalPath(leftUri);
        var rightPath = VbaProjectResolver.TryGetLocalPath(rightUri);
        return leftPath is not null
            && rightPath is not null
            && leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CachedManifestResolution(
        long Version,
        VbaProjectResolution Resolution);

    private sealed class ProjectScopeInvalidationState
    {
        public ProjectScopeInvalidationState(
            string activeUri,
            VbaProjectResolution resolution)
        {
            ActiveUri = activeUri;
            Resolution = resolution;
        }

        public string ActiveUri { get; set; }

        public VbaProjectResolution Resolution { get; set; }

        public HashSet<string> SourceUris { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public long Generation { get; set; }

        public int PendingBuilds { get; set; }

        public bool IsMaterialized { get; set; }
    }

    private sealed record CapturedProjectScopeInvalidation(
        long FullGeneration,
        long ScopeGeneration,
        ProjectScopeInvalidationState State);

    private sealed record ProjectScopeCapture(
        string ActiveUri,
        VbaProjectResolution Resolution,
        VbaProjectManifestBarrierSnapshot ManifestBarriers,
        VbaProjectReferenceCatalogSelectionState ReferenceCatalogState,
        VbaProjectSnapshotIdentity CacheIdentity,
        VbaProjectSnapshotIdentity? SupersededCacheIdentity);

    private sealed record CapturedProjectScopeAuthorityLookup(
        ProjectScopeAuthorityLookup Lookup);

    private sealed record PreferredRetirementScope(
        string CacheKey,
        string ReconciliationAuthorityKey);

    private sealed record WarmProjectScopeSeed(
        string CacheKey,
        string ActiveUri,
        VbaProjectResolution Resolution,
        long ManifestVersion,
        IReadOnlyList<string> SourceUris);

    private sealed class ProjectScopeAuthorityLookup
    {
        private static readonly StringComparer PathComparer =
            StringComparer.OrdinalIgnoreCase;
        private readonly IReadOnlyDictionary<string, WarmProjectScopeSeed>
            exactAuthorities;

        private ProjectScopeAuthorityLookup(
            IReadOnlyDictionary<string, WarmProjectScopeSeed> exactAuthorities)
        {
            this.exactAuthorities = exactAuthorities;
        }

        public static ProjectScopeAuthorityLookup Empty { get; } =
            new(
                new Dictionary<string, WarmProjectScopeSeed>(PathComparer));

        public static ProjectScopeAuthorityLookup Create(
            IEnumerable<WarmProjectScopeSeed> seeds)
        {
            var exact = new Dictionary<string, WarmProjectScopeSeed>(
                PathComparer);
            foreach (var seed in seeds)
            {
                AddPreferred(
                    exact,
                    NormalizeUri(seed.ActiveUri),
                    seed);
                foreach (var sourceUri in seed.SourceUris)
                {
                    AddPreferred(
                        exact,
                        NormalizeUri(sourceUri),
                        seed);
                }
            }

            return new ProjectScopeAuthorityLookup(exact);
        }

        public WarmProjectScopeSeed? Resolve(string activeUri)
        {
            var path = NormalizeUri(activeUri);
            if (!string.IsNullOrEmpty(path)
                && exactAuthorities.TryGetValue(path, out var exact))
            {
                return exact;
            }

            return null;
        }

        private static void AddPreferred(
            Dictionary<string, WarmProjectScopeSeed> authorities,
            string key,
            WarmProjectScopeSeed seed)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!authorities.TryGetValue(key, out var current)
                || IsMoreSpecific(seed, current))
            {
                authorities[key] = seed;
            }
        }

        private static bool IsMoreSpecific(
            WarmProjectScopeSeed candidate,
            WarmProjectScopeSeed current)
        {
            var candidateRoot = NormalizePath(candidate.Resolution.RootPath);
            var currentRoot = NormalizePath(current.Resolution.RootPath);
            if (candidateRoot.Length != currentRoot.Length)
            {
                return candidateRoot.Length > currentRoot.Length;
            }

            if (candidate.Resolution.Kind != current.Resolution.Kind)
            {
                return candidate.Resolution.Kind
                    == VbaProjectResolutionKind.ManifestDocument;
            }

            return string.Compare(
                    candidate.CacheKey,
                    current.CacheKey,
                    StringComparison.OrdinalIgnoreCase)
                < 0;
        }

        private static string NormalizeUri(string uri)
            => VbaProjectResolver.TryGetLocalPath(uri) is { } localPath
                ? NormalizePath(localPath)
                : "";

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (PathTooLongException)
            {
            }

            return "";
        }
    }
}
