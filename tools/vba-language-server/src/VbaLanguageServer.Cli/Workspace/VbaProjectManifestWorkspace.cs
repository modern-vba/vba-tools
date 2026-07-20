using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Describes whether a versioned manifest overlay update changed effective project state.
/// </summary>
internal sealed record VbaProjectManifestOverlayUpdate(
    bool Accepted,
    bool EffectiveChanged,
    VbaProjectManifestException? Error);

internal enum VbaProjectManifestReconciliationStatus
{
    Rejected,
    Observed,
    Applied,
    Invalid
}

internal sealed record VbaProjectManifestReconciliationUpdate(
    VbaProjectManifestReconciliationStatus Status,
    VbaProjectManifestException? Error = null,
    bool RetainedLastKnownGood = false);

internal sealed record VbaProjectManifestReconciliationTarget(
    string Uri,
    long CapturedRevision);

internal sealed record VbaProjectManifestReconciliationItemUpdate(
    string Uri,
    VbaProjectManifestReconciliationUpdate Update);

internal sealed record VbaProjectManifestAuthorityReplacementUpdate(
    bool Accepted,
    IReadOnlyList<VbaProjectManifestReconciliationItemUpdate>
        DeletedManifests,
    VbaProjectManifestReconciliationItemUpdate? ReloadedManifest);

internal sealed record VbaProjectManifestReconciliationCapture(
    long Revision,
    VbaProjectDiskManifestBaseline Baseline,
    bool HasOpenOverlay = false,
    string? OpenOverlayText = null,
    string? EffectiveManifestText = null);

/// <summary>
/// Captures manifest-authority overrides and the revision that owns them for
/// one resolved project scope.
/// </summary>
internal sealed record VbaProjectManifestBarrierSnapshot(
    long Revision,
    IReadOnlyDictionary<string, bool> Overrides)
{
    private static readonly IReadOnlyDictionary<string, long>
        EmptyReconciliationRevisions =
            new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, long> ReconciliationRevisions
        { get; init; } =
        EmptyReconciliationRevisions;
}

/// <summary>
/// Captures one resolution and its manifest barriers under the same manifest
/// workspace version fence.
/// </summary>
internal sealed record VbaProjectManifestResolutionCapture(
    VbaProjectResolution Resolution,
    VbaProjectManifestBarrierSnapshot Barriers);

/// <summary>
/// Supplies versioned project-manifest resolution to project snapshot construction.
/// </summary>
internal interface IVbaProjectManifestResolutionSource
{
    long Version { get; }

    long GetRevision(string authorityUri);

    VbaProjectResolution Resolve(string activeUri);

    VbaProjectManifestResolutionCapture CaptureResolution(string activeUri)
    {
        while (true)
        {
            var capturedVersion = Version;
            var resolution = Resolve(activeUri);
            var barriers = CaptureScopeBarriers(activeUri, resolution);
            if (Version == capturedVersion)
            {
                return new VbaProjectManifestResolutionCapture(
                    resolution,
                    barriers);
            }
        }
    }

    VbaProjectManifestBarrierSnapshot CaptureScopeBarriers(
        string activeUri,
        VbaProjectResolution resolution)
        => new(
            GetRevision(activeUri),
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase));

    VbaProjectManifestBarrierSnapshot CaptureDiskReconciliationBarriers(
        string activeUri,
        VbaProjectResolution resolution)
        => CaptureScopeBarriers(activeUri, resolution);

    long CaptureScopeBarrierRevision(
        string activeUri,
        VbaProjectResolution resolution)
        => CaptureScopeBarriers(activeUri, resolution).Revision;
}

/// <summary>
/// Tracks open project-manifest overlays and watched disk authority for language-server resolution.
/// </summary>
internal sealed class VbaProjectManifestWorkspace : IVbaProjectManifestResolutionSource
{
    private const string ManifestFileName = "vba-project.json";
    private readonly object gate = new();
    private readonly IVbaProjectFileSystem fileSystem;
    private readonly Dictionary<string, ManifestState> states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> reconciliationRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> effectiveScopeRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VbaProjectDiskManifestBaseline>
        reconciliationBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectiveManifest>
        lastKnownGoodDiskManifests = new(StringComparer.OrdinalIgnoreCase);
    private long version;
    private long retentionGeneration;

    public VbaProjectManifestWorkspace()
        : this(SystemVbaProjectFileSystem.Instance)
    {
    }

    internal VbaProjectManifestWorkspace(IVbaProjectFileSystem fileSystem)
    {
        this.fileSystem = fileSystem;
    }

    /// <summary>
    /// Gets the manifest-state version used by project snapshot caches.
    /// </summary>
    public long Version
    {
        get
        {
            lock (gate)
            {
                return version;
            }
        }
    }

    internal int RetainedStateCount
    {
        get
        {
            lock (gate)
            {
                return states.Count;
            }
        }
    }

    internal int RetainedEffectiveScopeRevisionCount
    {
        get
        {
            lock (gate)
            {
                return effectiveScopeRevisions.Count;
            }
        }
    }

    internal int RetainedReconciliationRevisionCount
    {
        get
        {
            lock (gate)
            {
                return reconciliationRevisions.Count;
            }
        }
    }

    internal int RetainedReconciliationBaselineCount
    {
        get
        {
            lock (gate)
            {
                return reconciliationBaselines.Count;
            }
        }
    }

    internal int RetainedLastKnownGoodCount
    {
        get
        {
            lock (gate)
            {
                return lastKnownGoodDiskManifests.Count;
            }
        }
    }

    internal void RetireInactiveState(
        IReadOnlyList<string> activeUris,
        IReadOnlyList<VbaProjectManifestRetentionScope> activeScopes)
    {
        var activePaths = activeUris
            .Concat(activeScopes.Select(scope => scope.ActiveUri))
            .Select(VbaProjectResolver.TryGetLocalPath)
            .Where(path => path is not null)
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var activeRoots = activeScopes
            .Select(scope => scope.RootPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (gate)
        {
            var retainedPaths = states
                .Where(pair => pair.Value.OpenManifest is not null)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var manifestPath in states.Keys
                .Concat(effectiveScopeRevisions.Keys)
                .Concat(reconciliationRevisions.Keys)
                .Concat(reconciliationBaselines.Keys)
                .Concat(lastKnownGoodDiskManifests.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (IsRetainedManifestPath(
                    manifestPath,
                    activePaths,
                    activeRoots))
                {
                    retainedPaths.Add(manifestPath);
                }
            }

            var removed = RemoveInactive(states, retainedPaths);
            removed |= RemoveInactive(
                effectiveScopeRevisions,
                retainedPaths);
            removed |= RemoveInactive(
                reconciliationRevisions,
                retainedPaths);
            removed |= RemoveInactive(
                reconciliationBaselines,
                retainedPaths);
            removed |= RemoveInactive(
                lastKnownGoodDiskManifests,
                retainedPaths);
            if (removed)
            {
                version++;
                retentionGeneration++;
            }
        }
    }

    public long GetRevision(string authorityUri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(authorityUri);
        if (localPath is null)
        {
            return 0;
        }

        lock (gate)
        {
            if (Path.GetFileName(localPath).Equals(
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return effectiveScopeRevisions.TryGetValue(
                    Path.GetFullPath(localPath),
                    out var manifestRevision)
                        ? manifestRevision
                        : 0;
            }

            var activeDirectory =
                Path.GetDirectoryName(localPath) ?? Directory.GetCurrentDirectory();
            var revision = 0L;
            for (var directory = new DirectoryInfo(activeDirectory);
                directory is not null;
                directory = directory.Parent)
            {
                if (effectiveScopeRevisions.TryGetValue(
                        Path.Combine(directory.FullName, ManifestFileName),
                        out var candidateRevision))
                {
                    revision = Math.Max(revision, candidateRevision);
                }
            }

            return revision;
        }
    }

    public VbaProjectManifestResolutionCapture CaptureResolution(
        string activeUri)
    {
        while (true)
        {
            long capturedVersion;
            long capturedRetentionGeneration;
            Dictionary<string, ManifestState> stateSnapshot;
            Dictionary<string, long> effectiveRevisionSnapshot;
            Dictionary<string, long> reconciliationRevisionSnapshot;
            Dictionary<string, EffectiveManifest> lastKnownGoodSnapshot;
            lock (gate)
            {
                capturedVersion = version;
                capturedRetentionGeneration = retentionGeneration;
                stateSnapshot = new Dictionary<string, ManifestState>(
                    states,
                    StringComparer.OrdinalIgnoreCase);
                effectiveRevisionSnapshot = new Dictionary<string, long>(
                    effectiveScopeRevisions,
                    StringComparer.OrdinalIgnoreCase);
                reconciliationRevisionSnapshot =
                    new Dictionary<string, long>(
                        reconciliationRevisions,
                        StringComparer.OrdinalIgnoreCase);
                lastKnownGoodSnapshot =
                    new Dictionary<string, EffectiveManifest>(
                        lastKnownGoodDiskManifests,
                        StringComparer.OrdinalIgnoreCase);
            }

            var resolution = Resolve(
                activeUri,
                capturedVersion,
                capturedRetentionGeneration,
                stateSnapshot,
                reconciliationRevisionSnapshot,
                lastKnownGoodSnapshot);
            var barriers = CreateBarrierSnapshot(
                activeUri,
                resolution,
                stateSnapshot,
                effectiveRevisionSnapshot,
                reconciliationRevisionSnapshot,
                includeReconciliationRevisions: false);
            lock (gate)
            {
                if (version == capturedVersion)
                {
                    return new VbaProjectManifestResolutionCapture(
                        resolution,
                        barriers);
                }
            }
        }
    }

    public VbaProjectManifestBarrierSnapshot CaptureScopeBarriers(
        string activeUri,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return CreateBarrierSnapshot(
                activeUri,
                resolution,
                states,
                effectiveScopeRevisions,
                reconciliationRevisions,
                includeReconciliationRevisions: false);
        }
    }

    public VbaProjectManifestBarrierSnapshot
        CaptureDiskReconciliationBarriers(
            string activeUri,
            VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return CreateBarrierSnapshot(
                activeUri,
                resolution,
                states,
                effectiveScopeRevisions,
                reconciliationRevisions,
                includeReconciliationRevisions: true);
        }
    }

    public long CaptureScopeBarrierRevision(
        string activeUri,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return GetScopeBarrierRevision(
                activeUri,
                resolution,
                effectiveScopeRevisions);
        }
    }

    /// <summary>
    /// Opens a versioned manifest overlay that takes precedence over disk state.
    /// </summary>
    public VbaProjectManifestOverlayUpdate OpenManifest(string uri, int documentVersion, string text)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new VbaProjectManifestOverlayUpdate(false, false, null);
        }

        var overlayIsValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var overlayManifest,
            out var error);
        long diskFallbackVersion;
        long diskFallbackRetentionGeneration;
        long diskFallbackReconciliationRevision;
        lock (gate)
        {
            diskFallbackVersion = version;
            diskFallbackRetentionGeneration = retentionGeneration;
            reconciliationRevisions.TryGetValue(
                manifestPath,
                out diskFallbackReconciliationRevision);
        }

        var diskFallback = overlayIsValid
            ? null
            : TryReadValidDiskManifest(
                manifestPath,
                diskFallbackVersion,
                diskFallbackRetentionGeneration,
                diskFallbackReconciliationRevision);
        lock (gate)
        {
            states.TryGetValue(manifestPath, out var existing);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestPath,
                out var lastKnownGood);
            var effectiveManifest =
                overlayManifest
                ?? existing?.OpenManifest?.EffectiveManifest
                ?? existing?.ReconciledDiskManifest
                ?? lastKnownGood
                ?? (version == diskFallbackVersion
                    && GetReconciliationRevisionLocked(manifestPath)
                        == diskFallbackReconciliationRevision
                    ? diskFallback
                    : null);

            states[manifestPath] = new ManifestState(
                new OpenManifestState(documentVersion, effectiveManifest),
                existing?.DiskDeleted == true,
                existing?.ReconciledDiskManifest,
                existing?.DiskInvalid == true,
                existing?.DiskValidationError);
            version++;
            MarkEffectiveScopeChanged(manifestPath);
            MarkReconciliationChanged(manifestPath);
            return new VbaProjectManifestOverlayUpdate(
                Accepted: true,
                EffectiveChanged: overlayIsValid,
                Error: error);
        }
    }

    /// <summary>
    /// Changes an open manifest only when the incoming version is newer.
    /// </summary>
    public VbaProjectManifestOverlayUpdate ChangeManifest(string uri, int documentVersion, string text)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new VbaProjectManifestOverlayUpdate(false, false, null);
        }

        var overlayIsValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var overlayManifest,
            out var error);
        lock (gate)
        {
            if (!states.TryGetValue(manifestPath, out var existing)
                || existing.OpenManifest is null
                || documentVersion <= existing.OpenManifest.Version)
            {
                return new VbaProjectManifestOverlayUpdate(false, false, null);
            }

            states[manifestPath] = existing with
            {
                OpenManifest = new OpenManifestState(
                    documentVersion,
                    overlayManifest ?? existing.OpenManifest.EffectiveManifest)
            };
            version++;
            MarkEffectiveScopeChanged(manifestPath);
            MarkReconciliationChanged(manifestPath);
            return new VbaProjectManifestOverlayUpdate(
                Accepted: true,
                EffectiveChanged: overlayIsValid,
                Error: error);
        }
    }

    /// <summary>
    /// Closes an open manifest overlay and restores effective disk or deletion state.
    /// </summary>
    public bool CloseManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            if (!states.TryGetValue(manifestPath, out var existing)
                || existing.OpenManifest is null)
            {
                return false;
            }

            if (existing.DiskDeleted
                || existing.DiskInvalid
                || existing.ReconciledDiskManifest is not null)
            {
                states[manifestPath] = existing with { OpenManifest = null };
            }
            else
            {
                states.Remove(manifestPath);
            }

            version++;
            MarkEffectiveScopeChanged(manifestPath);
            MarkReconciliationChanged(manifestPath);
            return true;
        }
    }

    /// <summary>
    /// Records a watched manifest create or change without replacing an open overlay.
    /// </summary>
    /// <returns>True when disk state is authoritative and should be processed.</returns>
    public bool ReloadManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            MarkReconciliationChanged(manifestPath);
            reconciliationBaselines.Remove(manifestPath);
            if (states.TryGetValue(manifestPath, out var existing)
                && existing.OpenManifest is not null)
            {
                if (existing.DiskDeleted
                    || existing.DiskInvalid
                    || existing.ReconciledDiskManifest is not null)
                {
                    states[manifestPath] = existing with
                    {
                        DiskDeleted = false,
                        ReconciledDiskManifest = null,
                        DiskInvalid = false,
                        DiskValidationError = null
                    };
                    if (existing.DiskDeleted
                        || existing.DiskInvalid)
                    {
                        version++;
                        MarkEffectiveScopeChanged(manifestPath);
                    }
                }

                return false;
            }

            states.Remove(manifestPath);
            version++;
            MarkEffectiveScopeChanged(manifestPath);
            return true;
        }
    }

    /// <summary>
    /// Records a watched manifest deletion without removing an open overlay.
    /// </summary>
    /// <returns>True when the effective manifest was deleted; false when an overlay remains or state was unchanged.</returns>
    public bool DeleteManifest(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        lock (gate)
        {
            states.TryGetValue(manifestPath, out var existing);
            if (existing?.DiskDeleted == true)
            {
                return false;
            }

            states[manifestPath] = new ManifestState(
                existing?.OpenManifest,
                DiskDeleted: true,
                ReconciledDiskManifest: null);
            reconciliationBaselines[manifestPath] =
                new VbaProjectDiskManifestBaseline(
                    Exists: false,
                    Text: null);
            lastKnownGoodDiskManifests.Remove(manifestPath);
            version++;
            MarkEffectiveScopeChanged(manifestPath);
            MarkReconciliationChanged(manifestPath);
            return existing?.OpenManifest is null;
        }
    }

    public long GetReconciliationRevision(string uri)
        => CaptureReconciliationState(uri).Revision;

    public VbaProjectDiskManifestBaseline GetReconciliationBaseline(string uri)
        => CaptureReconciliationState(uri).Baseline;

    public VbaProjectManifestReconciliationCapture
        CaptureReconciliationState(string uri)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new VbaProjectManifestReconciliationCapture(
                Revision: 0,
                new VbaProjectDiskManifestBaseline(
                    Exists: false,
                    Text: null));
        }

        lock (gate)
        {
            var revision = reconciliationRevisions.TryGetValue(
                manifestPath,
                out var capturedRevision)
                    ? capturedRevision
                    : 0;
            var baseline = reconciliationBaselines.TryGetValue(
                manifestPath,
                out var capturedBaseline)
                    ? capturedBaseline
                    : new VbaProjectDiskManifestBaseline(
                        Exists: false,
                        Text: null);
            states.TryGetValue(manifestPath, out var state);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestPath,
                out var lastKnownGood);
            var effectiveManifest =
                state?.OpenManifest?.EffectiveManifest
                ?? state?.ReconciledDiskManifest
                ?? lastKnownGood;
            return new VbaProjectManifestReconciliationCapture(
                revision,
                baseline,
                state?.OpenManifest is not null,
                state?.OpenManifest?.EffectiveManifest?.Text,
                effectiveManifest?.Text);
        }
    }

    public VbaProjectManifestReconciliationUpdate ReloadReconciledManifest(
        string uri,
        string text,
        long capturedRevision)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new(
                VbaProjectManifestReconciliationStatus.Rejected);
        }

        var isValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var effectiveManifest,
            out var error);
        lock (gate)
        {
            reconciliationRevisions.TryGetValue(
                manifestPath,
                out var currentRevision);
            if (currentRevision != capturedRevision)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Rejected);
            }

            return ReloadReconciledManifestLocked(
                manifestPath,
                text,
                isValid ? effectiveManifest : null,
                error);
        }
    }

    public VbaProjectManifestReconciliationUpdate DeleteReconciledManifest(
        string uri,
        long capturedRevision)
    {
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return new(
                VbaProjectManifestReconciliationStatus.Rejected);
        }

        lock (gate)
        {
            reconciliationRevisions.TryGetValue(
                manifestPath,
                out var currentRevision);
            states.TryGetValue(manifestPath, out var existing);
            if (currentRevision != capturedRevision)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Rejected);
            }

            return DeleteReconciledManifestLocked(
                manifestPath,
                existing);
        }
    }

    public VbaProjectManifestAuthorityReplacementUpdate
        ReplaceDeletedReconciledManifestAuthority(
            IReadOnlyList<VbaProjectManifestReconciliationTarget>
                deletedManifests,
            VbaProjectManifestReconciliationTarget? reloadedManifest,
            string? reloadedText)
    {
        var deletedTargets = new List<(
            VbaProjectManifestReconciliationTarget Target,
            string Path)>(deletedManifests.Count);
        foreach (var target in deletedManifests)
        {
            if (!TryGetManifestPath(target.Uri, out var manifestPath))
            {
                return RejectedAuthorityReplacement();
            }

            deletedTargets.Add((target, manifestPath));
        }

        string? reloadPath = null;
        EffectiveManifest? reloadManifest = null;
        VbaProjectManifestException? reloadError = null;
        if (reloadedManifest is not null)
        {
            if (reloadedText is null
                || !TryGetManifestPath(
                    reloadedManifest.Uri,
                    out reloadPath))
            {
                return RejectedAuthorityReplacement();
            }

            _ = TryCreateEffectiveManifest(
                reloadPath,
                reloadedManifest.Uri,
                reloadedText,
                out reloadManifest,
                out reloadError);
        }

        lock (gate)
        {
            if (deletedTargets.Any(
                    target =>
                        GetReconciliationRevisionLocked(target.Path)
                        != target.Target.CapturedRevision)
                || reloadedManifest is not null
                    && GetReconciliationRevisionLocked(reloadPath!)
                        != reloadedManifest.CapturedRevision)
            {
                return RejectedAuthorityReplacement();
            }

            var deletedUpdates =
                new List<VbaProjectManifestReconciliationItemUpdate>(
                    deletedTargets.Count);
            foreach (var (target, path) in deletedTargets)
            {
                states.TryGetValue(path, out var existing);
                deletedUpdates.Add(new(
                    target.Uri,
                    DeleteReconciledManifestLocked(path, existing)));
            }

            VbaProjectManifestReconciliationItemUpdate? reloadUpdate =
                null;
            if (reloadedManifest is not null)
            {
                reloadUpdate = new(
                    reloadedManifest.Uri,
                    ReloadReconciledManifestLocked(
                        reloadPath!,
                        reloadedText!,
                        reloadManifest,
                        reloadError));
            }

            return new(
                Accepted: true,
                deletedUpdates,
                reloadUpdate);
        }
    }

    private VbaProjectManifestReconciliationUpdate
        ReloadReconciledManifestLocked(
            string manifestPath,
            string text,
            EffectiveManifest? effectiveManifest,
            VbaProjectManifestException? error)
    {
        states.TryGetValue(manifestPath, out var existing);
        if (effectiveManifest is null)
        {
            lastKnownGoodDiskManifests.TryGetValue(
                manifestPath,
                out var lastKnownGood);
            reconciliationBaselines[manifestPath] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: text);
            MarkReconciliationChanged(manifestPath);
            if (existing?.OpenManifest is not null)
            {
                states[manifestPath] = existing with
                {
                    DiskDeleted = false,
                    ReconciledDiskManifest = lastKnownGood,
                    DiskInvalid = lastKnownGood is null,
                    DiskValidationError = error
                };
                return new(
                    VbaProjectManifestReconciliationStatus.Observed,
                    error);
            }

            states[manifestPath] = new ManifestState(
                OpenManifest: null,
                DiskDeleted: false,
                ReconciledDiskManifest: lastKnownGood,
                DiskInvalid: lastKnownGood is null,
                DiskValidationError: error);
            if (lastKnownGood is null)
            {
                version++;
                MarkEffectiveScopeChanged(manifestPath);
            }

            return new(
                VbaProjectManifestReconciliationStatus.Invalid,
                error,
                RetainedLastKnownGood: lastKnownGood is not null);
        }

        if (existing?.OpenManifest is not null)
        {
            states[manifestPath] = existing with
            {
                DiskDeleted = false,
                ReconciledDiskManifest = effectiveManifest,
                DiskInvalid = false,
                DiskValidationError = null
            };
            reconciliationBaselines[manifestPath] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: effectiveManifest.Text);
            lastKnownGoodDiskManifests[manifestPath] =
                effectiveManifest;
            MarkReconciliationChanged(manifestPath);
            return new(
                VbaProjectManifestReconciliationStatus.Observed);
        }

        states[manifestPath] = new ManifestState(
            OpenManifest: null,
            DiskDeleted: false,
            ReconciledDiskManifest: effectiveManifest,
            DiskInvalid: false,
            DiskValidationError: null);
        reconciliationBaselines[manifestPath] =
            new VbaProjectDiskManifestBaseline(
                Exists: true,
                Text: effectiveManifest.Text);
        lastKnownGoodDiskManifests[manifestPath] = effectiveManifest;
        version++;
        MarkEffectiveScopeChanged(manifestPath);
        MarkReconciliationChanged(manifestPath);
        return new(
            VbaProjectManifestReconciliationStatus.Applied);
    }

    private VbaProjectManifestReconciliationUpdate
        DeleteReconciledManifestLocked(
            string manifestPath,
            ManifestState? existing)
    {
        if (existing?.OpenManifest is not null)
        {
            if (existing.DiskDeleted)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Observed);
            }

            states[manifestPath] = existing with
            {
                DiskDeleted = true,
                ReconciledDiskManifest = null,
                DiskInvalid = false,
                DiskValidationError = null
            };
            reconciliationBaselines[manifestPath] =
                new VbaProjectDiskManifestBaseline(
                    Exists: false,
                    Text: null);
            lastKnownGoodDiskManifests.Remove(manifestPath);
            MarkReconciliationChanged(manifestPath);
            return new(
                VbaProjectManifestReconciliationStatus.Observed);
        }

        states[manifestPath] = new ManifestState(
            OpenManifest: null,
            DiskDeleted: true,
            ReconciledDiskManifest: null,
            DiskInvalid: false,
            DiskValidationError: null);
        reconciliationBaselines[manifestPath] =
            new VbaProjectDiskManifestBaseline(
                Exists: false,
                Text: null);
        lastKnownGoodDiskManifests.Remove(manifestPath);
        version++;
        MarkEffectiveScopeChanged(manifestPath);
        MarkReconciliationChanged(manifestPath);
        return new(
            VbaProjectManifestReconciliationStatus.Applied);
    }

    private static VbaProjectManifestAuthorityReplacementUpdate
        RejectedAuthorityReplacement()
        => new(
            Accepted: false,
            DeletedManifests: [],
            ReloadedManifest: null);

    internal static VbaProjectResolution ResolveManifestText(
        string activeUri,
        string manifestUri,
        string text)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        var manifestPath = VbaProjectResolver.TryGetLocalPath(manifestUri);
        if (activePath is null || manifestPath is null)
        {
            return new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                "");
        }

        var activeDirectory =
            Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        var effectiveManifest = CreateEffectiveManifest(
            manifestPath,
            manifestUri,
            text);
        foreach (var (documentName, document) in effectiveManifest.Manifest.Documents)
        {
            var sourceRoot = effectiveManifest.SourceRoots[documentName];
            if (VbaProjectResolver.IsPathUnder(activePath, sourceRoot))
            {
                return new VbaProjectResolution(
                    VbaProjectResolutionKind.ManifestDocument,
                    sourceRoot,
                    manifestPath,
                    documentName,
                    document.Kind,
                    document.References ?? []);
            }
        }

        return new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            activeDirectory);
    }

    /// <summary>
    /// Gets the effective open or disk manifest text for one manifest URI.
    /// </summary>
    public bool TryGetEffectiveManifest(
        string uri,
        out string effectiveUri,
        out string text,
        out VbaProjectManifestException? error)
    {
        effectiveUri = "";
        text = "";
        error = null;
        if (!TryGetManifestPath(uri, out var manifestPath))
        {
            return false;
        }

        ManifestState? state;
        EffectiveManifest? lastKnownGood;
        long capturedVersion;
        long capturedRetentionGeneration;
        long capturedReconciliationRevision;
        lock (gate)
        {
            capturedVersion = version;
            capturedRetentionGeneration = retentionGeneration;
            reconciliationRevisions.TryGetValue(
                manifestPath,
                out capturedReconciliationRevision);
            states.TryGetValue(manifestPath, out state);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestPath,
                out lastKnownGood);
        }

        try
        {
            if (!TryReadEffectiveManifest(
                    manifestPath,
                    state,
                    lastKnownGood,
                    capturedVersion,
                    capturedRetentionGeneration,
                    capturedReconciliationRevision,
                    out var effectiveManifest,
                    out var validationError,
                    out _))
            {
                error = validationError;
                return false;
            }

            effectiveUri = effectiveManifest.Uri;
            text = effectiveManifest.Text;
            error = validationError;
            return true;
        }
        catch (VbaProjectManifestException ex)
        {
            lock (gate)
            {
                if (version == capturedVersion
                    && retentionGeneration
                        == capturedRetentionGeneration
                    && GetReconciliationRevisionLocked(manifestPath)
                        == capturedReconciliationRevision
                    && !lastKnownGoodDiskManifests.ContainsKey(
                        manifestPath)
                    && (!states.TryGetValue(
                            manifestPath,
                            out var currentState)
                        || currentState.OpenManifest is null))
                {
                    states[manifestPath] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: null,
                        DiskInvalid: true,
                        DiskValidationError: ex);
                    version++;
                    MarkEffectiveScopeChanged(manifestPath);
                    MarkReconciliationChanged(manifestPath);
                }
            }

            error = ex;
            return false;
        }
    }

    /// <summary>
    /// Resolves a source URI against effective manifest overlays and watched deletion state.
    /// </summary>
    public VbaProjectResolution Resolve(string activeUri)
        => CaptureResolution(activeUri).Resolution;

    internal bool TryResolveKnownState(
        string activeUri,
        out VbaProjectResolution resolution)
    {
        var activePath =
            VbaProjectResolver.TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            resolution = new VbaProjectResolution(
                VbaProjectResolutionKind.AdHoc,
                "");
            return true;
        }

        var activeDirectory =
            Path.GetDirectoryName(activePath)
            ?? Directory.GetCurrentDirectory();
        var sawKnownManifestState = false;
        lock (gate)
        {
            for (var directory = new DirectoryInfo(activeDirectory);
                directory is not null;
                directory = directory.Parent)
            {
                var manifestPath = Path.Combine(
                    directory.FullName,
                    ManifestFileName);
                states.TryGetValue(
                    manifestPath,
                    out var state);
                lastKnownGoodDiskManifests.TryGetValue(
                    manifestPath,
                    out var lastKnownGood);
                sawKnownManifestState |=
                    state is not null
                    || lastKnownGood is not null;
                EffectiveManifest? effectiveManifest;
                if (state?.OpenManifest is not null)
                {
                    effectiveManifest =
                        state.OpenManifest.EffectiveManifest;
                }
                else if (state?.ReconciledDiskManifest is not null)
                {
                    effectiveManifest =
                        state.ReconciledDiskManifest;
                }
                else if (state?.DiskDeleted == true
                    || state?.DiskInvalid == true)
                {
                    effectiveManifest = null;
                }
                else
                {
                    effectiveManifest = lastKnownGood;
                }
                if (effectiveManifest is null)
                {
                    continue;
                }

                foreach (var (documentName, document) in
                    effectiveManifest.Manifest.Documents)
                {
                    var sourceRoot =
                        effectiveManifest.SourceRoots[documentName];
                    if (VbaProjectResolver.IsPathUnder(
                        activePath,
                        sourceRoot))
                    {
                        resolution = new VbaProjectResolution(
                            VbaProjectResolutionKind.ManifestDocument,
                            sourceRoot,
                            manifestPath,
                            documentName,
                            document.Kind,
                            document.References ?? []);
                        return true;
                    }
                }

                resolution = new VbaProjectResolution(
                    VbaProjectResolutionKind.AdHoc,
                    activeDirectory);
                return true;
            }
        }

        resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            activeDirectory);
        return sawKnownManifestState;
    }

    private VbaProjectResolution Resolve(
        string activeUri,
        long capturedVersion,
        long capturedRetentionGeneration,
        IReadOnlyDictionary<string, ManifestState> stateSnapshot,
        IReadOnlyDictionary<string, long> revisionSnapshot,
        IReadOnlyDictionary<string, EffectiveManifest>
            lastKnownGoodSnapshot)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, "");
        }

        var activeDirectory = Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        for (var directory = new DirectoryInfo(activeDirectory); directory is not null; directory = directory.Parent)
        {
            var manifestPath = Path.Combine(directory.FullName, ManifestFileName);
            stateSnapshot.TryGetValue(manifestPath, out var state);
            lastKnownGoodSnapshot.TryGetValue(
                manifestPath,
                out var lastKnownGood);
            revisionSnapshot.TryGetValue(
                manifestPath,
                out var capturedReconciliationRevision);
            if (!TryReadEffectiveManifest(
                    manifestPath,
                    state,
                    lastKnownGood,
                    capturedVersion,
                    capturedRetentionGeneration,
                    capturedReconciliationRevision,
                    out var effectiveManifest,
                    out var validationError,
                    out var recordedNewInvalidManifest))
            {
                if (recordedNewInvalidManifest
                    && validationError is not null)
                {
                    throw validationError;
                }

                continue;
            }

            foreach (var (documentName, document) in effectiveManifest.Manifest.Documents)
            {
                var sourceRoot = effectiveManifest.SourceRoots[documentName];
                if (VbaProjectResolver.IsPathUnder(activePath, sourceRoot))
                {
                    return new VbaProjectResolution(
                        VbaProjectResolutionKind.ManifestDocument,
                        sourceRoot,
                        manifestPath,
                        documentName,
                        document.Kind,
                        document.References ?? []);
                }
            }

            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
        }

        return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
    }

    private static VbaProjectManifestBarrierSnapshot CreateBarrierSnapshot(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, ManifestState> stateSnapshot,
        IReadOnlyDictionary<string, long> effectiveRevisionSnapshot,
        IReadOnlyDictionary<string, long> reconciliationRevisionSnapshot,
        bool includeReconciliationRevisions)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        var overrides = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (manifestPath, state) in stateSnapshot)
        {
            if (!IsManifestWithinScope(
                    manifestPath,
                    resolution.RootPath)
                && (resolution.Kind
                        != VbaProjectResolutionKind.AdHoc
                    || !IsActivePathUnderManifest(
                        manifestPath,
                        activePath)))
            {
                continue;
            }

            overrides[Path.GetFullPath(manifestPath)] =
                state.OpenManifest is not null
                    ? state.OpenManifest.EffectiveManifest is not null
                    : state.ReconciledDiskManifest is not null
                        || !state.DiskDeleted
                            && !state.DiskInvalid;
        }

        var revision = GetScopeBarrierRevision(
            activePath,
            resolution.RootPath,
            effectiveRevisionSnapshot);
        var snapshot = new VbaProjectManifestBarrierSnapshot(
            revision,
            overrides);
        if (!includeReconciliationRevisions)
        {
            return snapshot;
        }

        return snapshot with
        {
            ReconciliationRevisions =
                reconciliationRevisionSnapshot
                .Where(
                    pair => IsManifestRelevantToScope(
                        pair.Key,
                        activePath,
                        resolution.RootPath))
                .ToDictionary(
                    pair => Path.GetFullPath(pair.Key),
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)
        };
    }

    private static long GetScopeBarrierRevision(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, long> effectiveRevisionSnapshot)
        => GetScopeBarrierRevision(
            VbaProjectResolver.TryGetLocalPath(activeUri),
            resolution.RootPath,
            effectiveRevisionSnapshot);

    private static long GetScopeBarrierRevision(
        string? activePath,
        string rootPath,
        IReadOnlyDictionary<string, long> effectiveRevisionSnapshot)
    {
        var revision = 0L;
        foreach (var (manifestPath, candidateRevision) in
            effectiveRevisionSnapshot)
        {
            if (candidateRevision > revision
                && IsManifestRelevantToScope(
                    manifestPath,
                    activePath,
                    rootPath))
            {
                revision = candidateRevision;
            }
        }

        return revision;
    }

    private static bool IsManifestRelevantToScope(
        string manifestPath,
        string? activePath,
        string rootPath)
    {
        var manifestDirectory = Path.GetDirectoryName(manifestPath);
        return manifestDirectory is not null
            && (activePath is not null
                    && VbaProjectResolver.IsPathUnder(
                        activePath,
                        manifestDirectory)
                || IsManifestWithinScope(manifestPath, rootPath));
    }

    private static bool IsActivePathUnderManifest(
        string manifestPath,
        string? activePath)
    {
        var manifestDirectory =
            Path.GetDirectoryName(manifestPath);
        return activePath is not null
            && manifestDirectory is not null
            && VbaProjectResolver.IsPathUnder(
                activePath,
                manifestDirectory);
    }

    private static bool IsManifestWithinScope(
        string manifestPath,
        string rootPath)
        => !string.IsNullOrWhiteSpace(rootPath)
            && VbaProjectResolver.IsPathUnder(
                Path.GetFullPath(manifestPath),
                Path.GetFullPath(rootPath));

    private static bool IsRetainedManifestPath(
        string manifestPath,
        IReadOnlyList<string> activePaths,
        IReadOnlyList<string> activeRoots)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifestDirectory =
            Path.GetDirectoryName(fullManifestPath);
        return manifestDirectory is not null
            && (activePaths.Any(
                    activePath => VbaProjectResolver.IsPathUnder(
                        activePath,
                        manifestDirectory))
                || activeRoots.Any(
                    root => VbaProjectResolver.IsPathUnder(
                        fullManifestPath,
                        root)));
    }

    private static bool RemoveInactive<TValue>(
        Dictionary<string, TValue> values,
        IReadOnlySet<string> retainedPaths)
    {
        var removed = false;
        foreach (var path in values.Keys
            .Where(path => !retainedPaths.Contains(path))
            .ToArray())
        {
            removed |= values.Remove(path);
        }

        return removed;
    }

    private bool TryReadEffectiveManifest(
        string manifestPath,
        ManifestState? state,
        EffectiveManifest? lastKnownGood,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision,
        out EffectiveManifest effectiveManifest,
        out VbaProjectManifestException? validationError,
        out bool recordedNewInvalidManifest)
    {
        validationError = null;
        recordedNewInvalidManifest = false;
        if (state?.OpenManifest is not null)
        {
            effectiveManifest = state.OpenManifest.EffectiveManifest!;
            return state.OpenManifest.EffectiveManifest is not null;
        }

        if (state?.ReconciledDiskManifest is not null)
        {
            effectiveManifest = state.ReconciledDiskManifest;
            validationError = state.DiskValidationError;
            return true;
        }

        if (state?.DiskDeleted == true)
        {
            effectiveManifest = default!;
            return false;
        }

        if (state?.DiskInvalid == true)
        {
            effectiveManifest = default!;
            validationError = state.DiskValidationError;
            return false;
        }

        if (fileSystem.FileExists(manifestPath))
        {
            string? observedText = null;
            try
            {
                effectiveManifest = ReadDiskManifest(
                    manifestPath,
                    capturedVersion,
                    capturedRetentionGeneration,
                    capturedReconciliationRevision,
                    out observedText);
                return true;
            }
            catch (VbaProjectManifestException ex)
            {
                recordedNewInvalidManifest =
                    RecordInvalidDiskManifest(
                        manifestPath,
                        observedText,
                        capturedVersion,
                        capturedRetentionGeneration,
                        capturedReconciliationRevision,
                        hasLastKnownGood: lastKnownGood is not null,
                        validationError: ex);
                validationError = ex;
                effectiveManifest = lastKnownGood!;
                return lastKnownGood is not null;
            }
        }

        effectiveManifest = lastKnownGood!;
        return lastKnownGood is not null;
    }

    private EffectiveManifest? TryReadValidDiskManifest(
        string manifestPath,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision)
    {
        if (!fileSystem.FileExists(manifestPath))
        {
            return null;
        }

        try
        {
            return ReadDiskManifest(
                manifestPath,
                capturedVersion,
                capturedRetentionGeneration,
                capturedReconciliationRevision,
                out _);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private EffectiveManifest ReadDiskManifest(
        string manifestPath,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision,
        out string text)
    {
        try
        {
            text = fileSystem.ReadManifestText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VbaProjectManifestException(
                $"Project manifest could not be read: {manifestPath}",
                ex);
        }

        var effectiveManifest = CreateEffectiveManifest(
            manifestPath,
            new Uri(manifestPath).AbsoluteUri,
            text);
        SeedReconciliationBaseline(
            manifestPath,
            effectiveManifest,
            capturedVersion,
            capturedRetentionGeneration,
            capturedReconciliationRevision);
        return effectiveManifest;
    }

    private bool RecordInvalidDiskManifest(
        string manifestPath,
        string? text,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision,
        bool hasLastKnownGood,
        VbaProjectManifestException validationError)
    {
        if (text is null)
        {
            return false;
        }

        lock (gate)
        {
            if (version != capturedVersion
                || retentionGeneration
                    != capturedRetentionGeneration
                || GetReconciliationRevisionLocked(manifestPath)
                    != capturedReconciliationRevision)
            {
                return false;
            }

            var baselineChanged =
                !reconciliationBaselines.TryGetValue(
                    manifestPath,
                    out var baseline)
                || !baseline.Exists
                || !string.Equals(
                    baseline.Text,
                    text,
                    StringComparison.Ordinal);
            if (baselineChanged)
            {
                reconciliationBaselines[manifestPath] =
                    new VbaProjectDiskManifestBaseline(
                        Exists: true,
                        Text: text);
            }

            states.TryGetValue(manifestPath, out var state);
            var effectiveChanged = false;
            if (state?.OpenManifest is null)
            {
                lastKnownGoodDiskManifests.TryGetValue(
                    manifestPath,
                    out var currentLastKnownGood);
                if (hasLastKnownGood
                    && currentLastKnownGood is not null)
                {
                    states[manifestPath] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: currentLastKnownGood,
                        DiskInvalid: false,
                        DiskValidationError: validationError);
                }
                else
                {
                    states[manifestPath] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: null,
                        DiskInvalid: true,
                        DiskValidationError: validationError);
                    version++;
                    MarkEffectiveScopeChanged(manifestPath);
                    effectiveChanged = true;
                }
            }

            if (baselineChanged || effectiveChanged)
            {
                MarkReconciliationChanged(manifestPath);
            }

            return effectiveChanged;
        }
    }

    private static bool TryCreateEffectiveManifest(
        string manifestPath,
        string uri,
        string text,
        out EffectiveManifest? effectiveManifest,
        out VbaProjectManifestException? error)
    {
        try
        {
            effectiveManifest = CreateEffectiveManifest(manifestPath, uri, text);
            error = null;
            return true;
        }
        catch (VbaProjectManifestException ex)
        {
            effectiveManifest = null;
            error = ex;
            return false;
        }
    }

    private static EffectiveManifest CreateEffectiveManifest(
        string manifestPath,
        string uri,
        string text)
    {
        var manifest = ProjectManifestReader.Parse(text, uri);
        var manifestDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var sourceRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (documentName, document) in manifest.Documents)
        {
            try
            {
                sourceRoots[documentName] = Path.GetFullPath(
                    Path.Combine(manifestDirectory, document.SourcePath));
            }
            catch (Exception ex) when (ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                throw new VbaProjectManifestException(
                    $"Document '{documentName}' has an invalid sourcePath in project manifest: {uri}",
                    ex);
            }
        }

        return new EffectiveManifest(uri, text, manifest, sourceRoots);
    }

    private static bool TryGetManifestPath(string uri, out string manifestPath)
    {
        manifestPath = "";
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null
            || !Path.GetFileName(localPath).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifestPath = Path.GetFullPath(localPath);
        return true;
    }

    private void MarkReconciliationChanged(string manifestPath)
    {
        reconciliationRevisions.TryGetValue(
            manifestPath,
            out var previous);
        reconciliationRevisions[manifestPath] = previous + 1;
    }

    private void MarkEffectiveScopeChanged(string manifestPath)
        => effectiveScopeRevisions[manifestPath] = version;

    private void SeedReconciliationBaseline(
        string manifestPath,
        EffectiveManifest effectiveManifest,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision)
    {
        lock (gate)
        {
            if (version != capturedVersion
                || retentionGeneration
                    != capturedRetentionGeneration
                || GetReconciliationRevisionLocked(manifestPath)
                    != capturedReconciliationRevision)
            {
                return;
            }

            var baselineChanged =
                !reconciliationBaselines.TryGetValue(
                    manifestPath,
                    out var baseline)
                || !baseline.Exists
                || !string.Equals(
                    baseline.Text,
                    effectiveManifest.Text,
                    StringComparison.Ordinal);
            reconciliationBaselines[manifestPath] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: effectiveManifest.Text);
            lastKnownGoodDiskManifests[manifestPath] =
                effectiveManifest;
            if (baselineChanged)
            {
                MarkReconciliationChanged(manifestPath);
            }
        }
    }

    private long GetReconciliationRevisionLocked(string manifestPath)
        => reconciliationRevisions.TryGetValue(
            manifestPath,
            out var revision)
                ? revision
                : 0;

    private sealed record ManifestState(
        OpenManifestState? OpenManifest,
        bool DiskDeleted,
        EffectiveManifest? ReconciledDiskManifest = null,
        bool DiskInvalid = false,
        VbaProjectManifestException? DiskValidationError = null);

    private sealed record OpenManifestState(
        int Version,
        EffectiveManifest? EffectiveManifest);

    private sealed record EffectiveManifest(
        string Uri,
        string Text,
        ProjectManifest Manifest,
        IReadOnlyDictionary<string, string> SourceRoots);
}
