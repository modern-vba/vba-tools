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
    VbaIdentifiedDocument Document,
    long CapturedRevision)
{
    public VbaDocumentIdentity DocumentIdentity => Document.Identity;

    public string Uri => Document.Uri;
}

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
    IReadOnlyDictionary<VbaDocumentIdentity, bool> Overrides)
{
    private static readonly IReadOnlyDictionary<VbaDocumentIdentity, long>
        EmptyReconciliationRevisions =
            new Dictionary<VbaDocumentIdentity, long>();

    public IReadOnlyDictionary<VbaDocumentIdentity, long>
        ReconciliationRevisions
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

    long GetRevision(VbaIdentifiedDocument authorityDocument);

    VbaProjectResolution Resolve(string activeUri);

    bool TryResolveManifestDocument(
        string projectPath,
        string documentName,
        out VbaProjectResolution resolution)
    {
        resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            "");
        return false;
    }

    VbaProjectManifestResolutionCapture CaptureResolution(string activeUri)
    {
        while (true)
        {
            var capturedVersion = Version;
            var resolution = Resolve(activeUri);
            if (!VbaProjectIdentityModel.TryIdentifyDocument(
                    activeUri,
                    out var activeDocumentIdentity))
            {
                throw new InvalidOperationException(
                    "A manifest resolution has no active document identity.");
            }

            var barriers = CaptureScopeBarriers(
                new VbaIdentifiedDocument(
                    activeDocumentIdentity,
                    activeUri),
                resolution);
            if (Version == capturedVersion)
            {
                return new VbaProjectManifestResolutionCapture(
                    resolution,
                    barriers);
            }
        }
    }

    VbaProjectManifestBarrierSnapshot CaptureScopeBarriers(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
        => new(
            GetRevision(activeDocument),
            new Dictionary<VbaDocumentIdentity, bool>());

    VbaProjectManifestBarrierSnapshot CaptureDiskReconciliationBarriers(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
        => CaptureScopeBarriers(activeDocument, resolution);

    long CaptureScopeBarrierRevision(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
        => CaptureScopeBarriers(activeDocument, resolution).Revision;
}

/// <summary>
/// Tracks open project-manifest overlays and watched disk authority for language-server resolution.
/// </summary>
internal sealed class VbaProjectManifestWorkspace : IVbaProjectManifestResolutionSource
{
    private const string ManifestFileName = "vba-project.json";
    private readonly object gate = new();
    private readonly IVbaProjectFileSystem fileSystem;
    private readonly Dictionary<VbaDocumentIdentity, ManifestState> states =
        new();
    private readonly Dictionary<VbaDocumentIdentity, long>
        reconciliationRevisions = new();
    private readonly Dictionary<VbaDocumentIdentity, long>
        effectiveScopeRevisions = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        VbaProjectDiskManifestBaseline> reconciliationBaselines = new();
    private readonly Dictionary<VbaDocumentIdentity, EffectiveManifest>
        lastKnownGoodDiskManifests = new();
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
        IReadOnlyList<VbaIdentifiedDocument> activeDocuments,
        IReadOnlyList<VbaProjectManifestRetentionScope> activeScopes)
    {
        var activePaths = activeDocuments
            .Concat(activeScopes.Select(scope => scope.ActiveDocument))
            .Where(document => document.Identity.IsLocalFile)
            .Select(document => document.Identity.CanonicalValue)
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
            var retainedDocuments = states
                .Where(pair => pair.Value.OpenManifest is not null)
                .Select(pair => pair.Key)
                .ToHashSet();
            foreach (var manifestDocument in states.Keys
                .Concat(effectiveScopeRevisions.Keys)
                .Concat(reconciliationRevisions.Keys)
                .Concat(reconciliationBaselines.Keys)
                .Concat(lastKnownGoodDiskManifests.Keys)
                .Distinct())
            {
                if (IsRetainedManifestPath(
                    manifestDocument.CanonicalValue,
                    activePaths,
                    activeRoots))
                {
                    retainedDocuments.Add(manifestDocument);
                }
            }

            var removed = RemoveInactive(states, retainedDocuments);
            removed |= RemoveInactive(
                effectiveScopeRevisions,
                retainedDocuments);
            removed |= RemoveInactive(
                reconciliationRevisions,
                retainedDocuments);
            removed |= RemoveInactive(
                reconciliationBaselines,
                retainedDocuments);
            removed |= RemoveInactive(
                lastKnownGoodDiskManifests,
                retainedDocuments);
            if (removed)
            {
                version++;
                retentionGeneration++;
            }
        }
    }

    public long GetRevision(VbaIdentifiedDocument authorityDocument)
    {
        if (!authorityDocument.Identity.IsLocalFile)
        {
            return 0;
        }

        var localPath = authorityDocument.Identity.CanonicalValue;

        lock (gate)
        {
            if (Path.GetFileName(localPath).Equals(
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return effectiveScopeRevisions.TryGetValue(
                    IdentifyManifestDocument(localPath),
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
                        IdentifyManifestDocument(
                            Path.Combine(
                                directory.FullName,
                                ManifestFileName)),
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
        => CaptureResolution(activeUri, CancellationToken.None);

    internal VbaProjectManifestResolutionCapture CaptureResolution(
        string activeUri,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long capturedVersion;
            long capturedRetentionGeneration;
            Dictionary<VbaDocumentIdentity, ManifestState> stateSnapshot;
            Dictionary<VbaDocumentIdentity, long>
                effectiveRevisionSnapshot;
            Dictionary<VbaDocumentIdentity, long>
                reconciliationRevisionSnapshot;
            Dictionary<VbaDocumentIdentity, EffectiveManifest>
                lastKnownGoodSnapshot;
            lock (gate)
            {
                capturedVersion = version;
                capturedRetentionGeneration = retentionGeneration;
                stateSnapshot = new Dictionary<
                    VbaDocumentIdentity,
                    ManifestState>(states);
                effectiveRevisionSnapshot = new Dictionary<
                    VbaDocumentIdentity,
                    long>(effectiveScopeRevisions);
                reconciliationRevisionSnapshot =
                    new Dictionary<VbaDocumentIdentity, long>(
                        reconciliationRevisions);
                lastKnownGoodSnapshot =
                    new Dictionary<
                        VbaDocumentIdentity,
                        EffectiveManifest>(lastKnownGoodDiskManifests);
            }

            var resolution = Resolve(
                activeUri,
                capturedVersion,
                capturedRetentionGeneration,
                stateSnapshot,
                reconciliationRevisionSnapshot,
                lastKnownGoodSnapshot,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return CreateBarrierSnapshot(
                activeDocument.Uri,
                resolution,
                states,
                effectiveScopeRevisions,
                reconciliationRevisions,
                includeReconciliationRevisions: false);
        }
    }

    public VbaProjectManifestBarrierSnapshot
        CaptureDiskReconciliationBarriers(
            VbaIdentifiedDocument activeDocument,
            VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return CreateBarrierSnapshot(
                activeDocument.Uri,
                resolution,
                states,
                effectiveScopeRevisions,
                reconciliationRevisions,
                includeReconciliationRevisions: true);
        }
    }

    public long CaptureScopeBarrierRevision(
        VbaIdentifiedDocument activeDocument,
        VbaProjectResolution resolution)
    {
        lock (gate)
        {
            return GetScopeBarrierRevision(
                activeDocument.Uri,
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
        var manifestIdentity = IdentifyManifestDocument(manifestPath);

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
                manifestIdentity,
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
            states.TryGetValue(manifestIdentity, out var existing);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestIdentity,
                out var lastKnownGood);
            var effectiveManifest =
                overlayManifest
                ?? existing?.OpenManifest?.EffectiveManifest
                ?? existing?.ReconciledDiskManifest
                ?? lastKnownGood
                ?? (version == diskFallbackVersion
                    && GetReconciliationRevisionLocked(manifestIdentity)
                        == diskFallbackReconciliationRevision
                    ? diskFallback
                    : null);

            states[manifestIdentity] = new ManifestState(
                new OpenManifestState(documentVersion, effectiveManifest),
                existing?.DiskDeleted == true,
                existing?.ReconciledDiskManifest,
                existing?.DiskInvalid == true,
                existing?.DiskValidationError);
            version++;
            MarkEffectiveScopeChanged(manifestIdentity);
            MarkReconciliationChanged(manifestIdentity);
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
        var manifestIdentity = IdentifyManifestDocument(manifestPath);

        var overlayIsValid = TryCreateEffectiveManifest(
            manifestPath,
            uri,
            text,
            out var overlayManifest,
            out var error);
        lock (gate)
        {
            if (!states.TryGetValue(manifestIdentity, out var existing)
                || existing.OpenManifest is null
                || documentVersion <= existing.OpenManifest.Version)
            {
                return new VbaProjectManifestOverlayUpdate(false, false, null);
            }

            states[manifestIdentity] = existing with
            {
                OpenManifest = new OpenManifestState(
                    documentVersion,
                    overlayManifest ?? existing.OpenManifest.EffectiveManifest)
            };
            version++;
            MarkEffectiveScopeChanged(manifestIdentity);
            MarkReconciliationChanged(manifestIdentity);
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
        var manifestIdentity = IdentifyManifestDocument(manifestPath);

        lock (gate)
        {
            if (!states.TryGetValue(manifestIdentity, out var existing)
                || existing.OpenManifest is null)
            {
                return false;
            }

            if (existing.DiskDeleted
                || existing.DiskInvalid
                || existing.ReconciledDiskManifest is not null)
            {
                states[manifestIdentity] = existing with
                {
                    OpenManifest = null
                };
            }
            else
            {
                states.Remove(manifestIdentity);
            }

            version++;
            MarkEffectiveScopeChanged(manifestIdentity);
            MarkReconciliationChanged(manifestIdentity);
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
        var manifestIdentity = IdentifyManifestDocument(manifestPath);

        lock (gate)
        {
            MarkReconciliationChanged(manifestIdentity);
            reconciliationBaselines.Remove(manifestIdentity);
            if (states.TryGetValue(manifestIdentity, out var existing)
                && existing.OpenManifest is not null)
            {
                if (existing.DiskDeleted
                    || existing.DiskInvalid
                    || existing.ReconciledDiskManifest is not null)
                {
                    states[manifestIdentity] = existing with
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
                        MarkEffectiveScopeChanged(manifestIdentity);
                    }
                }

                return false;
            }

            states.Remove(manifestIdentity);
            version++;
            MarkEffectiveScopeChanged(manifestIdentity);
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
        var manifestIdentity = IdentifyManifestDocument(manifestPath);

        lock (gate)
        {
            states.TryGetValue(manifestIdentity, out var existing);
            if (existing?.DiskDeleted == true)
            {
                return false;
            }

            states[manifestIdentity] = new ManifestState(
                existing?.OpenManifest,
                DiskDeleted: true,
                ReconciledDiskManifest: null);
            reconciliationBaselines[manifestIdentity] =
                new VbaProjectDiskManifestBaseline(
                    Exists: false,
                    Text: null);
            lastKnownGoodDiskManifests.Remove(manifestIdentity);
            version++;
            MarkEffectiveScopeChanged(manifestIdentity);
            MarkReconciliationChanged(manifestIdentity);
            return existing?.OpenManifest is null;
        }
    }

    public long GetReconciliationRevision(
        VbaIdentifiedDocument manifestDocument)
        => CaptureReconciliationState(manifestDocument).Revision;

    public VbaProjectDiskManifestBaseline GetReconciliationBaseline(
        VbaIdentifiedDocument manifestDocument)
        => CaptureReconciliationState(manifestDocument).Baseline;

    public VbaProjectManifestReconciliationCapture
        CaptureReconciliationState(
            VbaIdentifiedDocument manifestDocument)
    {
        if (!TryGetManifestPath(
                manifestDocument,
                out var manifestPath))
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
                manifestDocument.Identity,
                out var capturedRevision)
                    ? capturedRevision
                    : 0;
            var baseline = reconciliationBaselines.TryGetValue(
                manifestDocument.Identity,
                out var capturedBaseline)
                    ? capturedBaseline
                    : new VbaProjectDiskManifestBaseline(
                        Exists: false,
                        Text: null);
            states.TryGetValue(manifestDocument.Identity, out var state);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestDocument.Identity,
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
        VbaIdentifiedDocument manifestDocument,
        string text,
        long capturedRevision)
    {
        if (!TryGetManifestPath(
                manifestDocument,
                out var manifestPath))
        {
            return new(
                VbaProjectManifestReconciliationStatus.Rejected);
        }

        var isValid = TryCreateEffectiveManifest(
            manifestPath,
            manifestDocument.Uri,
            text,
            out var effectiveManifest,
            out var error);
        lock (gate)
        {
            reconciliationRevisions.TryGetValue(
                manifestDocument.Identity,
                out var currentRevision);
            if (currentRevision != capturedRevision)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Rejected);
            }

            return ReloadReconciledManifestLocked(
                manifestDocument.Identity,
                text,
                isValid ? effectiveManifest : null,
                error);
        }
    }

    public VbaProjectManifestReconciliationUpdate DeleteReconciledManifest(
        VbaIdentifiedDocument manifestDocument,
        long capturedRevision)
    {
        if (!TryGetManifestPath(
                manifestDocument,
                out var manifestPath))
        {
            return new(
                VbaProjectManifestReconciliationStatus.Rejected);
        }

        lock (gate)
        {
            reconciliationRevisions.TryGetValue(
                manifestDocument.Identity,
                out var currentRevision);
            states.TryGetValue(manifestDocument.Identity, out var existing);
            if (currentRevision != capturedRevision)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Rejected);
            }

            return DeleteReconciledManifestLocked(
                manifestDocument.Identity,
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
            VbaDocumentIdentity Identity,
            string Path)>(deletedManifests.Count);
        foreach (var target in deletedManifests)
        {
            if (!TryGetManifestPath(
                    target.Document,
                    out var manifestPath))
            {
                return RejectedAuthorityReplacement();
            }

            deletedTargets.Add((
                target,
                target.DocumentIdentity,
                manifestPath));
        }

        string? reloadPath = null;
        VbaDocumentIdentity? reloadIdentity = null;
        EffectiveManifest? reloadManifest = null;
        VbaProjectManifestException? reloadError = null;
        if (reloadedManifest is not null)
        {
            if (reloadedText is null
                || !TryGetManifestPath(
                    reloadedManifest.Document,
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
            reloadIdentity = reloadedManifest.DocumentIdentity;
        }

        lock (gate)
        {
            if (deletedTargets.Any(
                    target =>
                        GetReconciliationRevisionLocked(target.Identity)
                        != target.Target.CapturedRevision)
                || reloadedManifest is not null
                    && GetReconciliationRevisionLocked(
                        reloadIdentity!.Value)
                        != reloadedManifest.CapturedRevision)
            {
                return RejectedAuthorityReplacement();
            }

            var deletedUpdates =
                new List<VbaProjectManifestReconciliationItemUpdate>(
                    deletedTargets.Count);
            foreach (var (target, identity, _) in deletedTargets)
            {
                states.TryGetValue(identity, out var existing);
                deletedUpdates.Add(new(
                    target.Uri,
                    DeleteReconciledManifestLocked(
                        identity,
                        existing)));
            }

            VbaProjectManifestReconciliationItemUpdate? reloadUpdate =
                null;
            if (reloadedManifest is not null)
            {
                reloadUpdate = new(
                    reloadedManifest.Uri,
                    ReloadReconciledManifestLocked(
                        reloadIdentity!.Value,
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
            VbaDocumentIdentity manifestDocument,
            string text,
            EffectiveManifest? effectiveManifest,
            VbaProjectManifestException? error)
    {
        states.TryGetValue(manifestDocument, out var existing);
        if (effectiveManifest is null)
        {
            lastKnownGoodDiskManifests.TryGetValue(
                manifestDocument,
                out var lastKnownGood);
            reconciliationBaselines[manifestDocument] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: text);
            MarkReconciliationChanged(manifestDocument);
            if (existing?.OpenManifest is not null)
            {
                states[manifestDocument] = existing with
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

            states[manifestDocument] = new ManifestState(
                OpenManifest: null,
                DiskDeleted: false,
                ReconciledDiskManifest: lastKnownGood,
                DiskInvalid: lastKnownGood is null,
                DiskValidationError: error);
            if (lastKnownGood is null)
            {
                version++;
                MarkEffectiveScopeChanged(manifestDocument);
            }

            return new(
                VbaProjectManifestReconciliationStatus.Invalid,
                error,
                RetainedLastKnownGood: lastKnownGood is not null);
        }

        if (existing?.OpenManifest is not null)
        {
            states[manifestDocument] = existing with
            {
                DiskDeleted = false,
                ReconciledDiskManifest = effectiveManifest,
                DiskInvalid = false,
                DiskValidationError = null
            };
            reconciliationBaselines[manifestDocument] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: effectiveManifest.Text);
            lastKnownGoodDiskManifests[manifestDocument] =
                effectiveManifest;
            MarkReconciliationChanged(manifestDocument);
            return new(
                VbaProjectManifestReconciliationStatus.Observed);
        }

        states[manifestDocument] = new ManifestState(
            OpenManifest: null,
            DiskDeleted: false,
            ReconciledDiskManifest: effectiveManifest,
            DiskInvalid: false,
            DiskValidationError: null);
        reconciliationBaselines[manifestDocument] =
            new VbaProjectDiskManifestBaseline(
                Exists: true,
                Text: effectiveManifest.Text);
        lastKnownGoodDiskManifests[manifestDocument] = effectiveManifest;
        version++;
        MarkEffectiveScopeChanged(manifestDocument);
        MarkReconciliationChanged(manifestDocument);
        return new(
            VbaProjectManifestReconciliationStatus.Applied);
    }

    private VbaProjectManifestReconciliationUpdate
        DeleteReconciledManifestLocked(
            VbaDocumentIdentity manifestDocument,
            ManifestState? existing)
    {
        if (existing?.OpenManifest is not null)
        {
            if (existing.DiskDeleted)
            {
                return new(
                    VbaProjectManifestReconciliationStatus.Observed);
            }

            states[manifestDocument] = existing with
            {
                DiskDeleted = true,
                ReconciledDiskManifest = null,
                DiskInvalid = false,
                DiskValidationError = null
            };
            reconciliationBaselines[manifestDocument] =
                new VbaProjectDiskManifestBaseline(
                    Exists: false,
                    Text: null);
            lastKnownGoodDiskManifests.Remove(manifestDocument);
            MarkReconciliationChanged(manifestDocument);
            return new(
                VbaProjectManifestReconciliationStatus.Observed);
        }

        states[manifestDocument] = new ManifestState(
            OpenManifest: null,
            DiskDeleted: true,
            ReconciledDiskManifest: null,
            DiskInvalid: false,
            DiskValidationError: null);
        reconciliationBaselines[manifestDocument] =
            new VbaProjectDiskManifestBaseline(
                Exists: false,
                Text: null);
        lastKnownGoodDiskManifests.Remove(manifestDocument);
        version++;
        MarkEffectiveScopeChanged(manifestDocument);
        MarkReconciliationChanged(manifestDocument);
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
        var activeIdentity = VbaProjectResolver.ResolvePathIdentity(activePath);
        var effectiveManifest = CreateEffectiveManifest(
            manifestPath,
            manifestUri,
            text);
        foreach (var (documentName, document) in effectiveManifest.Manifest.Documents)
        {
            var sourceRoot = effectiveManifest.SourceRoots[documentName];
            var sourceRootIdentity =
                effectiveManifest.SourceRootIdentities[documentName];
            if (FileSystemPathIdentityRelations.SameOrDescendant(
                    activeIdentity,
                    sourceRootIdentity))
            {
                return new VbaProjectResolution(
                    VbaProjectResolutionKind.ManifestDocument,
                    sourceRoot,
                    manifestPath,
                    documentName,
                    document.Kind,
                    document.References ?? [],
                    VbaProjectResolver.ResolveManifestPath(
                        Path.GetDirectoryName(manifestPath)!,
                        document.TemplatePath),
                    document.CommonModules ?? [])
                {
                    RootIdentity = sourceRootIdentity
                };
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

        var manifestIdentity = IdentifyManifestDocument(manifestPath);
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
                manifestIdentity,
                out capturedReconciliationRevision);
            states.TryGetValue(manifestIdentity, out state);
            lastKnownGoodDiskManifests.TryGetValue(
                manifestIdentity,
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
                    CancellationToken.None,
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
                    && GetReconciliationRevisionLocked(manifestIdentity)
                        == capturedReconciliationRevision
                    && !lastKnownGoodDiskManifests.ContainsKey(
                        manifestIdentity)
                    && (!states.TryGetValue(
                            manifestIdentity,
                            out var currentState)
                        || currentState.OpenManifest is null))
                {
                    states[manifestIdentity] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: null,
                        DiskInvalid: true,
                        DiskValidationError: ex);
                    version++;
                    MarkEffectiveScopeChanged(manifestIdentity);
                    MarkReconciliationChanged(manifestIdentity);
                }
            }

            error = ex;
            return false;
        }
    }

    public bool TryResolveManifestDocument(
        string projectPath,
        string documentName,
        out VbaProjectResolution resolution)
    {
        resolution = new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            "");
        string manifestPath;
        string manifestUri;
        try
        {
            manifestPath = Path.Combine(
                Path.GetFullPath(projectPath),
                ManifestFileName);
            manifestUri = new Uri(manifestPath).AbsoluteUri;
        }
        catch (Exception ex) when (
            ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or UriFormatException)
        {
            return false;
        }

        if (!TryGetEffectiveManifest(
                manifestUri,
                out var effectiveUri,
                out var text,
                out _))
        {
            return false;
        }

        var effectiveManifest = CreateEffectiveManifest(
            manifestPath,
            effectiveUri,
            text);
        foreach (var (candidateName, document) in
            effectiveManifest.Manifest.Documents)
        {
            if (!candidateName.Equals(
                    documentName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var sourceRoot = effectiveManifest.SourceRoots[candidateName];
            var sourceRootIdentity =
                effectiveManifest.SourceRootIdentities[candidateName];
            resolution = new VbaProjectResolution(
                VbaProjectResolutionKind.ManifestDocument,
                sourceRoot,
                manifestPath,
                candidateName,
                document.Kind,
                document.References ?? [],
                VbaProjectResolver.ResolveManifestPath(
                    Path.GetDirectoryName(manifestPath)!,
                    document.TemplatePath),
                document.CommonModules ?? [])
            {
                RootIdentity = sourceRootIdentity
            };
            return true;
        }

        return false;
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
        var activeIdentity = VbaProjectResolver.ResolvePathIdentity(activePath);
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
                var manifestDocument =
                    IdentifyManifestDocument(manifestPath);
                states.TryGetValue(
                    manifestDocument,
                    out var state);
                lastKnownGoodDiskManifests.TryGetValue(
                    manifestDocument,
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
                    var sourceRootIdentity =
                        effectiveManifest.SourceRootIdentities[documentName];
                    if (FileSystemPathIdentityRelations.SameOrDescendant(
                            activeIdentity,
                            sourceRootIdentity))
                    {
                        resolution = new VbaProjectResolution(
                            VbaProjectResolutionKind.ManifestDocument,
                            sourceRoot,
                            manifestPath,
                            documentName,
                            document.Kind,
                            document.References ?? [],
                            VbaProjectResolver.ResolveManifestPath(
                                Path.GetDirectoryName(manifestPath)!,
                                document.TemplatePath),
                            document.CommonModules ?? [])
                        {
                            RootIdentity = sourceRootIdentity
                        };
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
        IReadOnlyDictionary<VbaDocumentIdentity, ManifestState>
            stateSnapshot,
        IReadOnlyDictionary<VbaDocumentIdentity, long> revisionSnapshot,
        IReadOnlyDictionary<VbaDocumentIdentity, EffectiveManifest>
            lastKnownGoodSnapshot,
        CancellationToken cancellationToken)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        if (activePath is null)
        {
            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, "");
        }

        var activeDirectory = Path.GetDirectoryName(activePath) ?? Directory.GetCurrentDirectory();
        var activeIdentity = VbaProjectResolver.ResolvePathIdentity(activePath);
        for (var directory = new DirectoryInfo(activeDirectory); directory is not null; directory = directory.Parent)
        {
            var manifestPath = Path.Combine(directory.FullName, ManifestFileName);
            var manifestDocument = IdentifyManifestDocument(manifestPath);
            stateSnapshot.TryGetValue(manifestDocument, out var state);
            lastKnownGoodSnapshot.TryGetValue(
                manifestDocument,
                out var lastKnownGood);
            revisionSnapshot.TryGetValue(
                manifestDocument,
                out var capturedReconciliationRevision);
            if (!TryReadEffectiveManifest(
                    manifestPath,
                    state,
                    lastKnownGood,
                    capturedVersion,
                    capturedRetentionGeneration,
                    capturedReconciliationRevision,
                    cancellationToken,
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
                var sourceRootIdentity =
                    effectiveManifest.SourceRootIdentities[documentName];
                if (FileSystemPathIdentityRelations.SameOrDescendant(
                        activeIdentity,
                        sourceRootIdentity))
                {
                    return new VbaProjectResolution(
                        VbaProjectResolutionKind.ManifestDocument,
                        sourceRoot,
                        manifestPath,
                        documentName,
                        document.Kind,
                        document.References ?? [],
                        VbaProjectResolver.ResolveManifestPath(
                            Path.GetDirectoryName(manifestPath)!,
                            document.TemplatePath),
                        document.CommonModules ?? [])
                    {
                        RootIdentity = sourceRootIdentity
                    };
                }
            }

            return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
        }

        return new VbaProjectResolution(VbaProjectResolutionKind.AdHoc, activeDirectory);
    }

    private static VbaProjectManifestBarrierSnapshot CreateBarrierSnapshot(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<VbaDocumentIdentity, ManifestState>
            stateSnapshot,
        IReadOnlyDictionary<VbaDocumentIdentity, long>
            effectiveRevisionSnapshot,
        IReadOnlyDictionary<VbaDocumentIdentity, long>
            reconciliationRevisionSnapshot,
        bool includeReconciliationRevisions)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        var overrides = new Dictionary<VbaDocumentIdentity, bool>();
        foreach (var (manifestDocument, state) in stateSnapshot)
        {
            var manifestPath = manifestDocument.CanonicalValue;
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

            overrides[manifestDocument] =
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
                        pair.Key.CanonicalValue,
                        activePath,
                        resolution.RootPath))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value)
        };
    }

    private static VbaDocumentIdentity IdentifyManifestDocument(
        string manifestPath)
        => VbaProjectIdentityModel.TryIdentifyLocalDocumentPath(
            manifestPath,
            out var identity)
                ? identity
                : throw new InvalidOperationException(
                    "A manifest workspace path has no document identity.");

    private static long GetScopeBarrierRevision(
        string activeUri,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<VbaDocumentIdentity, long>
            effectiveRevisionSnapshot)
        => GetScopeBarrierRevision(
            VbaProjectResolver.TryGetLocalPath(activeUri),
            resolution.RootPath,
            effectiveRevisionSnapshot);

    private static long GetScopeBarrierRevision(
        string? activePath,
        string rootPath,
        IReadOnlyDictionary<VbaDocumentIdentity, long>
            effectiveRevisionSnapshot)
    {
        var revision = 0L;
        foreach (var (manifestDocument, candidateRevision) in
            effectiveRevisionSnapshot)
        {
            if (candidateRevision > revision
                && IsManifestRelevantToScope(
                    manifestDocument.CanonicalValue,
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
        Dictionary<VbaDocumentIdentity, TValue> values,
        IReadOnlySet<VbaDocumentIdentity> retainedDocuments)
    {
        var removed = false;
        foreach (var document in values.Keys
            .Where(document => !retainedDocuments.Contains(document))
            .ToArray())
        {
            removed |= values.Remove(document);
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
        CancellationToken cancellationToken,
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
            cancellationToken.ThrowIfCancellationRequested();
            string? observedText = null;
            try
            {
                effectiveManifest = ReadDiskManifest(
                    manifestPath,
                    capturedVersion,
                    capturedRetentionGeneration,
                    capturedReconciliationRevision,
                    cancellationToken,
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
                CancellationToken.None,
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
        CancellationToken cancellationToken,
        out string text)
    {
        try
        {
            text = fileSystem.ReadManifestText(
                manifestPath,
                cancellationToken);
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
        var manifestDocument = IdentifyManifestDocument(manifestPath);

        lock (gate)
        {
            if (version != capturedVersion
                || retentionGeneration
                    != capturedRetentionGeneration
                || GetReconciliationRevisionLocked(manifestDocument)
                    != capturedReconciliationRevision)
            {
                return false;
            }

            var baselineChanged =
                !reconciliationBaselines.TryGetValue(
                    manifestDocument,
                    out var baseline)
                || !baseline.Exists
                || !string.Equals(
                    baseline.Text,
                    text,
                    StringComparison.Ordinal);
            if (baselineChanged)
            {
                reconciliationBaselines[manifestDocument] =
                    new VbaProjectDiskManifestBaseline(
                        Exists: true,
                        Text: text);
            }

            states.TryGetValue(manifestDocument, out var state);
            var effectiveChanged = false;
            if (state?.OpenManifest is null)
            {
                lastKnownGoodDiskManifests.TryGetValue(
                    manifestDocument,
                    out var currentLastKnownGood);
                if (hasLastKnownGood
                    && currentLastKnownGood is not null)
                {
                    states[manifestDocument] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: currentLastKnownGood,
                        DiskInvalid: false,
                        DiskValidationError: validationError);
                }
                else
                {
                    states[manifestDocument] = new ManifestState(
                        OpenManifest: null,
                        DiskDeleted: false,
                        ReconciledDiskManifest: null,
                        DiskInvalid: true,
                        DiskValidationError: validationError);
                    version++;
                    MarkEffectiveScopeChanged(manifestDocument);
                    effectiveChanged = true;
                }
            }

            if (baselineChanged || effectiveChanged)
            {
                MarkReconciliationChanged(manifestDocument);
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
        var sourceRootIdentities =
            DocumentSourceSetIsolationValidator.ResolveAndValidate(
                manifest,
                manifestPath,
                uri);
        var manifestDirectory = Path.GetDirectoryName(
                Path.GetFullPath(manifestPath))
            ?? throw new VbaProjectManifestException(
                $"Project manifest path has no parent directory: {uri}");
        var sourceRoots = manifest.Documents.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                var normalizedPath = pair.Value.SourcePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar);
                return Path.GetFullPath(
                    Path.IsPathRooted(normalizedPath)
                        ? normalizedPath
                        : Path.Combine(manifestDirectory, normalizedPath));
            },
            StringComparer.OrdinalIgnoreCase);

        return new EffectiveManifest(
            uri,
            text,
            manifest,
            sourceRoots,
            sourceRootIdentities);
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

    private static bool TryGetManifestPath(
        VbaIdentifiedDocument manifestDocument,
        out string manifestPath)
    {
        manifestPath = "";
        if (!manifestDocument.Identity.IsLocalFile
            || !Path.GetFileName(
                    manifestDocument.Identity.CanonicalValue)
                .Equals(
                    ManifestFileName,
                    StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifestPath = manifestDocument.Identity.CanonicalValue;
        return true;
    }

    private void MarkReconciliationChanged(
        VbaDocumentIdentity manifestDocument)
    {
        reconciliationRevisions.TryGetValue(
            manifestDocument,
            out var previous);
        reconciliationRevisions[manifestDocument] = previous + 1;
    }

    private void MarkEffectiveScopeChanged(
        VbaDocumentIdentity manifestDocument)
        => effectiveScopeRevisions[manifestDocument] = version;

    private void SeedReconciliationBaseline(
        string manifestPath,
        EffectiveManifest effectiveManifest,
        long capturedVersion,
        long capturedRetentionGeneration,
        long capturedReconciliationRevision)
    {
        var manifestDocument = IdentifyManifestDocument(manifestPath);
        lock (gate)
        {
            if (version != capturedVersion
                || retentionGeneration
                    != capturedRetentionGeneration
                || GetReconciliationRevisionLocked(manifestDocument)
                    != capturedReconciliationRevision)
            {
                return;
            }

            var baselineChanged =
                !reconciliationBaselines.TryGetValue(
                    manifestDocument,
                    out var baseline)
                || !baseline.Exists
                || !string.Equals(
                    baseline.Text,
                    effectiveManifest.Text,
                    StringComparison.Ordinal);
            reconciliationBaselines[manifestDocument] =
                new VbaProjectDiskManifestBaseline(
                    Exists: true,
                    Text: effectiveManifest.Text);
            lastKnownGoodDiskManifests[manifestDocument] =
                effectiveManifest;
            if (baselineChanged)
            {
                MarkReconciliationChanged(manifestDocument);
            }
        }
    }

    private long GetReconciliationRevisionLocked(
        VbaDocumentIdentity manifestDocument)
        => reconciliationRevisions.TryGetValue(
            manifestDocument,
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
        IReadOnlyDictionary<string, string> SourceRoots,
        IReadOnlyDictionary<string, FileSystemPathIdentity> SourceRootIdentities);
}
