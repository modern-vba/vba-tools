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
/// <param name="SemanticInventory">The query-shaped semantic inventory for editor features.</param>
public sealed record VbaProjectSnapshot(
    VbaProjectResolution Resolution,
    IReadOnlyDictionary<string, string> SourceDocuments,
    VbaProjectReferenceSelection? ReferenceSelection,
    VbaSemanticInventory SemanticInventory);

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed partial class VbaLanguageWorkspace : IVbaInteractiveWorkspaceCapture
{
    private readonly object gate = new();
    private readonly Dictionary<string, WorkspaceDocumentState> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AcceptedDocumentRevisionState> acceptedRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaSourceRevisionHistory sourceRevisionHistory = new();
    private readonly IVbaProjectDiskInventory diskInventory;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;
    private readonly VbaProjectSnapshotProvider snapshotProvider;
    private readonly IVbaDocumentAnalysisBuildObserver analysisBuildObserver;
    private VbaWorkspaceSnapshotState? workspaceSnapshotState;
    private long nextDocumentLifecycleEpoch;
    private long nextDocumentReservationToken;
    private long workspaceVersion;

    /// <summary>
    /// Creates a language workspace.
    /// </summary>
    /// <param name="referenceCatalogCache">The reference catalog cache used when building semantic inventories.</param>
    public VbaLanguageWorkspace(VbaProjectReferenceCatalogCache referenceCatalogCache)
        : this(
            referenceCatalogCache,
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance)
    {
    }

    internal VbaLanguageWorkspace(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver)
        : this(
            referenceCatalogCache,
            lifecycleObserver,
            NullVbaDocumentAnalysisBuildObserver.Instance)
    {
    }

    internal VbaLanguageWorkspace(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver,
        IVbaDocumentAnalysisBuildObserver analysisBuildObserver)
        : this(
            referenceCatalogCache,
            lifecycleObserver,
            analysisBuildObserver,
            NullVbaProjectSnapshotBuildObserver.Instance)
    {
    }

    internal VbaLanguageWorkspace(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver,
        IVbaDocumentAnalysisBuildObserver analysisBuildObserver,
        IVbaProjectSnapshotBuildObserver snapshotBuildObserver)
        : this(
            referenceCatalogCache,
            lifecycleObserver,
            analysisBuildObserver,
            snapshotBuildObserver,
            SystemVbaProjectFileSystem.Instance)
    {
    }

    internal VbaLanguageWorkspace(
        VbaProjectReferenceCatalogCache referenceCatalogCache,
        IVbaProjectReferenceCatalogLifecycleObserver lifecycleObserver,
        IVbaDocumentAnalysisBuildObserver analysisBuildObserver,
        IVbaProjectSnapshotBuildObserver snapshotBuildObserver,
        IVbaProjectFileSystem projectFileSystem,
        IVbaProjectReconciliationAuthorityLeaseObserver?
            reconciliationAuthorityLeaseObserver = null)
    {
        this.analysisBuildObserver = analysisBuildObserver;
        diskInventory =
            new VbaFileSystemProjectDiskInventory(projectFileSystem);
        diskDocumentCache = new VbaProjectSourceDocumentCache();
        ManifestWorkspace = new VbaProjectManifestWorkspace(projectFileSystem);
        snapshotProvider = new VbaProjectSnapshotProvider(
            referenceCatalogCache,
            diskInventory,
            diskDocumentCache,
            ManifestWorkspace,
            lifecycleObserver,
            snapshotBuildObserver,
            reconciliationAuthorityLeaseObserver);
    }

    /// <summary>
    /// Gets the focused manifest authority shared by snapshots, trace resolution, and lifecycle work.
    /// </summary>
    internal VbaProjectManifestWorkspace ManifestWorkspace { get; }

    /// <summary>
    /// Gets the disk inventory shared by cold snapshot capture and reconciliation.
    /// </summary>
    internal IVbaProjectDiskInventory DiskInventory => diskInventory;

    internal int RetainedSourceRevisionCount
    {
        get
        {
            lock (gate)
            {
                return sourceRevisionHistory.Count;
            }
        }
    }

    internal int RetainedProjectSnapshotSourceRevisionCount
        => snapshotProvider.RetainedSourceRevisionCount;

    internal int RetainedProjectSnapshotCount
        => snapshotProvider.RetainedProjectSnapshotCount;

    internal int RetainedProjectScopeInvalidationStateCount
        => snapshotProvider.RetainedScopeInvalidationStateCount;

    internal int RetainedReconciliationScopeCount
        => snapshotProvider.RetainedReconciliationScopeCount;

    internal int RetainedReconciliationAuthorityCount
        => snapshotProvider.RetainedReconciliationAuthorityCount;

    internal int RetainedProjectDiskDocumentCount
        => snapshotProvider.RetainedDiskDocumentCount;

    internal int RetainedManifestStateCount
        => ManifestWorkspace.RetainedStateCount;

    internal int RetainedManifestEffectiveRevisionCount
        => ManifestWorkspace.RetainedEffectiveScopeRevisionCount;

    internal int RetainedManifestReconciliationRevisionCount
        => ManifestWorkspace.RetainedReconciliationRevisionCount;

    internal int RetainedManifestReconciliationBaselineCount
        => ManifestWorkspace.RetainedReconciliationBaselineCount;

    internal int RetainedManifestLastKnownGoodCount
        => ManifestWorkspace.RetainedLastKnownGoodCount;

    /// <summary>
    /// Updates or adds an open document and parses its latest source text.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="text">The latest source text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    public void UpdateDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var existing = GetDocumentState(uri);
            var accepted = GetAcceptedRevisionState(uri);
            var continuesOpenLifecycle =
                accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer;
            var version = continuesOpenLifecycle
                ? (accepted!.Version ?? existing?.Version ?? -1) + 1
                : 0;
            if (RemoveExcludedSourceIdentity(uri))
            {
                MarkWorkspaceChanged(uri);
            }
            reservation = ReserveDocumentAnalysis(
                continuesOpenLifecycle
                    ? accepted!.Uri
                    : uri,
                WorkspaceDocumentAuthority.OpenBuffer,
                version,
                continuesOpenLifecycle
                    ? accepted!.LifecycleEpoch
                    : ++nextDocumentLifecycleEpoch,
                existing?.Analysis);
        }

        BuildAndCommitDocumentAnalysis(reservation, text, cancellationToken);
        WaitForAcceptedDocumentAnalysis(reservation, cancellationToken);
    }

    /// <summary>
    /// Opens a versioned client document and makes its text authoritative over disk state.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="version">The client document version.</param>
    /// <param name="text">The complete document text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    public void OpenDocument(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            if (RemoveExcludedSourceIdentity(uri))
            {
                MarkWorkspaceChanged(uri);
            }
            var existing = GetDocumentState(uri);
            reservation = ReserveDocumentAnalysis(
                uri,
                WorkspaceDocumentAuthority.OpenBuffer,
                version,
                ++nextDocumentLifecycleEpoch,
                existing?.Analysis);
        }

        BuildAndCommitDocumentAnalysis(
            reservation,
            text,
            cancellationToken);
    }

    /// <summary>
    /// Applies a client document change only when its version is newer than the open buffer.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="version">The client document version.</param>
    /// <param name="text">The complete document text.</param>
    /// <param name="cancellationToken">A cancellation token for the update.</param>
    /// <returns>True when the revision was reserved; false when it was stale or the document was not open.</returns>
    public bool ChangeDocument(
        string uri,
        int version,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(uri);
            var existing = GetDocumentState(uri);
            if (accepted?.Authority != WorkspaceDocumentAuthority.OpenBuffer
                || version <= accepted.Version)
            {
                return false;
            }

            reservation = ReserveDocumentAnalysis(
                accepted.Uri,
                WorkspaceDocumentAuthority.OpenBuffer,
                version,
                accepted.LifecycleEpoch,
                existing?.Analysis);
        }

        BuildAndCommitDocumentAnalysis(
            reservation,
            text,
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Reloads a watched disk source unless an open client buffer is authoritative.
    /// </summary>
    /// <param name="uri">The watched source URI.</param>
    /// <param name="text">The complete disk source text.</param>
    /// <param name="cancellationToken">A cancellation token for the reload.</param>
    /// <returns>True when disk text became the tracked source; false when an open buffer was preserved.</returns>
    public bool ReloadSourceDocument(
        string uri,
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        return ReloadSourceDocumentCore(
            uri,
            text,
            cancellationToken);
    }

    /// <summary>
    /// Reloads one watched source through the shared disk inventory.
    /// </summary>
    internal bool ReloadSourceDocumentFromDisk(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is null)
        {
            return false;
        }

        var manifestCapture = ManifestWorkspace.CaptureResolution(uri);
        var source = diskInventory.CaptureWatchedSource(
            manifestCapture.Resolution,
            uri,
            manifestCapture.Barriers.Overrides,
            cancellationToken);
        if (source is null)
        {
            return false;
        }

        diskDocumentCache.Invalidate(localPath);
        return ReloadSourceDocumentCore(
            uri,
            source.Text,
            cancellationToken);
    }

    private bool ReloadSourceDocumentCore(
        string uri,
        string text,
        CancellationToken cancellationToken)
    {
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var exclusionRemoved = RemoveExcludedSourceIdentity(uri);
            var accepted = GetAcceptedRevisionState(uri);
            var existing = GetDocumentState(uri);
            if (accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                if (exclusionRemoved)
                {
                    MarkWorkspaceChanged(uri);
                }

                return false;
            }

            if (exclusionRemoved)
            {
                MarkWorkspaceChanged(uri);
            }

            reservation = ReserveDocumentAnalysis(
                uri,
                WorkspaceDocumentAuthority.DiskWatcher,
                version: null,
                accepted?.Authority == WorkspaceDocumentAuthority.DiskWatcher
                    ? accepted.LifecycleEpoch
                    : ++nextDocumentLifecycleEpoch,
                existing?.Analysis);
        }

        return BuildAndCommitDocumentAnalysis(
            reservation,
            text,
            cancellationToken);
    }

    private bool ReloadReconciledSourceDocument(
        string uri,
        string text,
        long capturedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(uri);
            var existing = GetDocumentState(uri);
            if (GetSourceRevision(uri) > capturedWorkspaceRevision
                || accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            if (RemoveExcludedSourceIdentity(uri))
            {
                MarkWorkspaceChanged(uri);
            }

            reservation = ReserveDocumentAnalysis(
                uri,
                WorkspaceDocumentAuthority.DiskWatcher,
                version: null,
                accepted?.Authority == WorkspaceDocumentAuthority.DiskWatcher
                    ? accepted.LifecycleEpoch
                    : ++nextDocumentLifecycleEpoch,
                existing?.Analysis);
        }

        InvalidateDiskDocument(uri);
        return BuildAndCommitDocumentAnalysis(
            reservation,
            text,
            cancellationToken);
    }

    /// <summary>
    /// Closes an open client buffer so later snapshots can fall back to disk state.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the close.</param>
    /// <returns>True when an open buffer was removed.</returns>
    public bool CloseDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        IReadOnlyList<string>? remainingTrackedUris = null;
        lock (gate)
        {
            var revisionKey = FindAcceptedRevisionKey(uri);
            var documentKey = FindDocumentKey(uri);
            var hasOpenRevision = revisionKey is not null
                && acceptedRevisions[revisionKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documentKey is not null
                && documents[documentKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (!hasOpenRevision && !hasOpenDocument)
            {
                return false;
            }

            if (hasOpenRevision)
            {
                acceptedRevisions.Remove(revisionKey!);
                Monitor.PulseAll(gate);
            }

            if (hasOpenDocument)
            {
                documents.Remove(documentKey!);
                MarkWorkspaceChanged(uri);
                remainingTrackedUris = CaptureTrackedDocumentUris();
            }
        }

        if (remainingTrackedUris is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedUris);
        }

        return true;
    }

    /// <summary>
    /// Excludes a deleted disk source while preserving an equivalent open client buffer.
    /// </summary>
    /// <param name="uri">The deleted source URI.</param>
    /// <param name="cancellationToken">A cancellation token for the deletion.</param>
    /// <returns>True when no open buffer remains and diagnostics should be cleared.</returns>
    public bool DeleteSourceDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateDiskDocument(uri);
        IReadOnlyList<string>? remainingTrackedUris = null;
        lock (gate)
        {
            var exclusionAdded = AddExcludedSourceIdentity(uri);
            var revisionKey = FindAcceptedRevisionKey(uri);
            var documentKey = FindDocumentKey(uri);
            var hasOpenRevision = revisionKey is not null
                && acceptedRevisions[revisionKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documentKey is not null
                && documents[documentKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (hasOpenRevision || hasOpenDocument)
            {
                if (exclusionAdded)
                {
                    MarkWorkspaceChanged(uri);
                }

                return false;
            }

            if (revisionKey is not null)
            {
                acceptedRevisions.Remove(revisionKey);
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documentKey is not null
                && documents.Remove(documentKey);
            if (exclusionAdded || documentRemoved)
            {
                MarkWorkspaceChanged(uri);
            }

            if (documentRemoved)
            {
                remainingTrackedUris = CaptureTrackedDocumentUris();
            }
        }

        if (remainingTrackedUris is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedUris);
        }

        return true;
    }

    private bool DeleteReconciledSourceDocument(
        string uri,
        long capturedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string>? remainingTrackedUris = null;
        lock (gate)
        {
            var revisionKey = FindAcceptedRevisionKey(uri);
            var documentKey = FindDocumentKey(uri);
            var hasOpenRevision = revisionKey is not null
                && acceptedRevisions[revisionKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documentKey is not null
                && documents[documentKey].Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (GetSourceRevision(uri) > capturedWorkspaceRevision
                || hasOpenRevision
                || hasOpenDocument)
            {
                return false;
            }

            var exclusionAdded = AddExcludedSourceIdentity(uri);
            if (revisionKey is not null)
            {
                acceptedRevisions.Remove(revisionKey);
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documentKey is not null
                && documents.Remove(documentKey);
            if (exclusionAdded || documentRemoved)
            {
                MarkWorkspaceChanged(uri);
            }

            if (documentRemoved)
            {
                remainingTrackedUris = CaptureTrackedDocumentUris();
            }
        }

        InvalidateDiskDocument(uri);
        if (remainingTrackedUris is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedUris);
        }

        return true;
    }

    /// <summary>
    /// Removes any tracked document without excluding it from future disk inventory.
    /// </summary>
    /// <param name="uri">The document URI to remove.</param>
    /// <param name="cancellationToken">A cancellation token for the removal.</param>
    /// <returns>True when a tracked document was removed.</returns>
    public bool RemoveDocument(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string>? remainingTrackedUris = null;
        bool removed;
        lock (gate)
        {
            var revisionKey = FindAcceptedRevisionKey(uri);
            var documentKey = FindDocumentKey(uri);
            var revisionRemoved = revisionKey is not null
                && acceptedRevisions.Remove(revisionKey);
            if (revisionRemoved)
            {
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documentKey is not null
                && documents.Remove(documentKey);
            if (documentRemoved)
            {
                MarkWorkspaceChanged(uri);
                remainingTrackedUris = CaptureTrackedDocumentUris();
            }

            removed = revisionRemoved || documentRemoved;
        }

        if (remainingTrackedUris is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedUris);
        }

        return removed;
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
            var document = GetDocumentState(uri)?.Document;
            return document is not null
                ? document.SyntaxTree
                : null;
        }
    }

    /// <summary>
    /// Gets the effective tracked text for a source identity.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The tracked text, or null when the source is not tracked.</returns>
    public string? GetDocumentText(string uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return GetDocumentState(uri)?.Document.Text;
        }
    }

    /// <summary>
    /// Captures the immutable analysis currently committed for a tracked document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The committed analysis, or null when the document is not tracked.</returns>
    internal VbaDocumentAnalysis? GetDocumentAnalysis(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return GetDocumentState(uri)?.Analysis;
        }
    }

    /// <summary>
    /// Captures the latest publishable diagnostics analysis and its ownership revision.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The diagnostics snapshot, or null when the document is not tracked.</returns>
    internal VbaDocumentDiagnosticsSnapshot? GetDocumentDiagnosticsSnapshot(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var state = GetDocumentState(uri);
            var accepted = GetAcceptedRevisionState(uri);
            return state is not null
                && accepted is not null
                && !accepted.HasPendingBuild
                && accepted.Authority == state.Authority
                && accepted.Version == state.Version
                && accepted.LifecycleEpoch == state.LifecycleEpoch
                && accepted.ReservationToken == state.ReservationToken
                    ? new VbaDocumentDiagnosticsSnapshot(
                        state.Analysis,
                        state.Version,
                        state.LifecycleEpoch,
                        state.ReservationToken)
                    : null;
        }
    }

    /// <summary>
    /// Checks whether a captured diagnostics snapshot still owns the latest tracked revision.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="version">The captured client version, or null for disk-authoritative analysis.</param>
    /// <param name="lifecycleEpoch">The captured document lifecycle epoch.</param>
    /// <param name="reservationToken">The captured analysis reservation token.</param>
    /// <returns>True when the captured snapshot is still the latest publishable revision.</returns>
    internal bool IsLatestDiagnosticsSnapshot(
        string uri,
        int? version,
        long lifecycleEpoch,
        long reservationToken)
    {
        lock (gate)
        {
            var state = GetDocumentState(uri);
            var accepted = GetAcceptedRevisionState(uri);
            return state is not null
                && accepted is not null
                && !accepted.HasPendingBuild
                && accepted.Authority == state.Authority
                && accepted.Version == version
                && state.Version == version
                && accepted.LifecycleEpoch == lifecycleEpoch
                && state.LifecycleEpoch == lifecycleEpoch
                && accepted.ReservationToken == reservationToken
                && state.ReservationToken == reservationToken;
        }
    }

    /// <summary>
    /// Captures one exact-version open document without project, disk, or reference resolution.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="expectedVersion">The required client document version.</param>
    /// <param name="cancellationToken">A cancellation token for the lookup.</param>
    /// <returns>The immutable document snapshot, or null when the open version does not match.</returns>
    public VbaVersionedDocumentSnapshot? GetDocumentSnapshot(
        string uri,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var state = GetDocumentState(uri);
            var accepted = GetAcceptedRevisionState(uri);
            return state?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                && accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                && !accepted.HasPendingBuild
                && state.Version == expectedVersion
                && accepted.Version == expectedVersion
                && state.LifecycleEpoch == accepted.LifecycleEpoch
                && state.ReservationToken == accepted.ReservationToken
                    ? state.VersionedSnapshot
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
            return documents.Values
                .Select(state => state.Document.Uri)
                .ToArray();
        }
    }

    internal IReadOnlyList<string> GetOpenDocumentUris(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return documents.Values
                .Where(
                    state => state.Authority
                        == WorkspaceDocumentAuthority.OpenBuffer)
                .Select(state => state.Document.Uri)
                .ToArray();
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
        var capture = CaptureProjectSnapshotState(
            includeActiveUris: false);
        using var revisionCapture = capture.RevisionCapture;
        return snapshotProvider.CreateProjectSnapshot(
            activeUri,
            capture.WorkspaceState,
            cancellationToken);
    }

    /// <summary>
    /// Creates distinct project snapshots for all currently tracked document scopes.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for snapshot creation.</param>
    /// <returns>The distinct project snapshots.</returns>
    public IReadOnlyList<VbaProjectSnapshot> CreateProjectSnapshots(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capture = CaptureProjectSnapshotState(
            includeActiveUris: true);
        using var revisionCapture = capture.RevisionCapture;
        return snapshotProvider.CreateProjectSnapshots(
            capture.ActiveUris,
            capture.WorkspaceState,
            cancellationToken);
    }

    VbaSemanticInventory IVbaInteractiveWorkspaceCapture.CaptureProjectSemanticInventory(
        string activeUri,
        CancellationToken cancellationToken)
        => CreateProjectSnapshot(activeUri, cancellationToken).SemanticInventory;

    IReadOnlyList<VbaSemanticInventory>
        IVbaInteractiveWorkspaceCapture.CaptureWorkspaceSemanticInventories(
            CancellationToken cancellationToken)
        => CreateProjectSnapshots(cancellationToken)
            .Select(snapshot => snapshot.SemanticInventory)
            .ToArray();

    VbaVersionedDocumentSnapshot?
        IVbaInteractiveWorkspaceCapture.CaptureExactDocumentSnapshot(
            string uri,
            int expectedVersion,
            CancellationToken cancellationToken)
        => GetDocumentSnapshot(uri, expectedVersion, cancellationToken);

    private VbaWorkspaceSnapshotState CopyWorkspaceState()
    {
        lock (gate)
        {
            if (workspaceSnapshotState is not null)
            {
                return workspaceSnapshotState;
            }

            workspaceSnapshotState = new VbaWorkspaceSnapshotState(
                documents.Values
                    .Where(state => state.Authority == WorkspaceDocumentAuthority.OpenBuffer)
                    .ToDictionary(
                        state => state.Document.Uri,
                        state => state.Document,
                        StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(excludedSourceUris, StringComparer.OrdinalIgnoreCase),
                workspaceVersion);
            return workspaceSnapshotState;
        }
    }

    private WorkspaceProjectSnapshotCapture CaptureProjectSnapshotState(
        bool includeActiveUris)
    {
        lock (gate)
        {
            var workspaceState = CopyWorkspaceState();
            var revisionCapture =
                snapshotProvider.BeginSourceRevisionCapture(
                    workspaceState.Version);
            try
            {
                return new WorkspaceProjectSnapshotCapture(
                    workspaceState,
                    includeActiveUris
                        ? documents.Values
                            .Select(state => state.Document.Uri)
                            .ToArray()
                        : [],
                    revisionCapture);
            }
            catch
            {
                revisionCapture.Dispose();
                throw;
            }
        }
    }

    internal VbaProjectReconciliationCapture
        CaptureProjectReconciliation()
    {
        lock (gate)
        {
            var capturedWorkspaceRevision = workspaceVersion;
            var revisionCapture = sourceRevisionHistory.BeginCapture(
                capturedWorkspaceRevision);
            try
            {
                var openDocumentUris = documents.Values
                    .Where(
                        state => state.Authority
                            == WorkspaceDocumentAuthority.OpenBuffer)
                    .Select(state => state.Document.Uri)
                    .ToArray();
                var scopes = snapshotProvider
                    .CaptureReconciliationScopes(
                        capturedWorkspaceRevision)
                .Select(
                    scope =>
                    {
                        var manifestCandidates =
                            scope.ManifestCandidates
                            .Select(candidate =>
                            {
                                var manifestCapture = ManifestWorkspace
                                    .CaptureReconciliationState(candidate.Uri);
                                return candidate with
                                {
                                    CapturedRevision =
                                        manifestCapture.Revision,
                                    Baseline = manifestCapture.Baseline,
                                    HasOpenOverlay =
                                        manifestCapture.HasOpenOverlay,
                                    OpenOverlayText =
                                        manifestCapture.OpenOverlayText,
                                    EffectiveManifestText =
                                        manifestCapture
                                            .EffectiveManifestText
                                };
                            })
                            .ToArray();
                        var authorityManifestPath =
                            scope.Resolution.ManifestPath is null
                                ? null
                                : Path.GetFullPath(
                                    scope.Resolution.ManifestPath);
                        var activePath =
                            VbaProjectResolver.TryGetLocalPath(
                                scope.ActiveUri);
                        var observedManifestBarrierCandidates =
                            scope.ManifestBarriers.Overrides.Keys
                                .Concat(
                                    scope.ManifestBarriers
                                        .ReconciliationRevisions.Keys)
                                .Distinct(
                                    StringComparer.OrdinalIgnoreCase)
                                .Where(path =>
                                    IsManifestRelevantToScope(
                                        path,
                                        activePath,
                                        scope.Resolution)
                                    && (authorityManifestPath is null
                                        || !Path.GetFullPath(path).Equals(
                                            authorityManifestPath,
                                            StringComparison.OrdinalIgnoreCase)))
                                .Select(path =>
                                {
                                    var uri =
                                        new Uri(Path.GetFullPath(path))
                                            .AbsoluteUri;
                                    var manifestCapture = ManifestWorkspace
                                        .CaptureReconciliationState(uri);
                                    return new
                                        VbaProjectReconciliationManifestCandidate(
                                            uri,
                                            manifestCapture.Revision,
                                            manifestCapture.Baseline)
                                        {
                                            HasOpenOverlay =
                                                manifestCapture.HasOpenOverlay,
                                            OpenOverlayText =
                                                manifestCapture
                                                    .OpenOverlayText,
                                            EffectiveManifestText =
                                                manifestCapture
                                                    .EffectiveManifestText
                                        };
                                })
                                .ToArray();
                        var ownedSourceUris = scope.KnownSources
                            .Select(source => source.Uri)
                            .Append(scope.ActiveUri)
                            .ToArray();
                        var openSourceUris = openDocumentUris
                            .Where(
                                uri => ownedSourceUris.Any(
                                    ownedUri =>
                                        SameDocumentIdentity(
                                            ownedUri,
                                            uri)))
                            .ToArray();
                        return scope with
                        {
                            ManifestCandidates =
                                manifestCandidates,
                            ObservedManifestBarrierCandidates =
                                observedManifestBarrierCandidates,
                            OpenSourceUris = openSourceUris,
                            OpenDocumentUris = openDocumentUris
                        };
                    })
                .ToArray();
                return new VbaProjectReconciliationCapture(
                    scopes,
                    revisionCapture);
            }
            catch
            {
                revisionCapture.Dispose();
                throw;
            }
        }
    }

    private VbaDocumentAnalysis BuildDocumentAnalysis(
        string uri,
        string text,
        VbaDocumentAnalysis? previousAnalysis,
        int? clientVersion,
        CancellationToken cancellationToken)
    {
        analysisBuildObserver.BeforeBuild(
            new VbaDocumentAnalysisBuildContext(uri, clientVersion),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = VbaDocumentAnalysis.Create(
            uri,
            text,
            previousAnalysis,
            clientVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return analysis;
    }

    private bool BuildAndCommitDocumentAnalysis(
        DocumentAnalysisReservation reservation,
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            var analysis = BuildDocumentAnalysis(
                reservation.Uri,
                text,
                reservation.PreviousAnalysis,
                reservation.Version,
                cancellationToken);
            lock (gate)
            {
                return CommitDocumentAnalysis(reservation, analysis);
            }
        }
        catch
        {
            lock (gate)
            {
                AbandonDocumentAnalysis(reservation);
            }

            throw;
        }
    }

    private DocumentAnalysisReservation ReserveDocumentAnalysis(
        string uri,
        WorkspaceDocumentAuthority authority,
        int? version,
        long lifecycleEpoch,
        VbaDocumentAnalysis? previousAnalysis)
    {
        var existingKey = FindAcceptedRevisionKey(uri);
        if (existingKey is not null)
        {
            acceptedRevisions.Remove(existingKey);
        }

        var reservation = new DocumentAnalysisReservation(
            uri,
            authority,
            version,
            lifecycleEpoch,
            ++nextDocumentReservationToken,
            previousAnalysis);
        acceptedRevisions[uri] = new AcceptedDocumentRevisionState(
            reservation.Uri,
            reservation.Authority,
            reservation.Version,
            reservation.LifecycleEpoch,
            reservation.ReservationToken,
            HasPendingBuild: true);
        Monitor.PulseAll(gate);
        return reservation;
    }

    private bool CommitDocumentAnalysis(
        DocumentAnalysisReservation reservation,
        VbaDocumentAnalysis analysis)
    {
        var acceptedKey = FindAcceptedRevisionKey(reservation.Uri);
        if (acceptedKey is null)
        {
            return false;
        }

        var accepted = acceptedRevisions[acceptedKey];
        if (accepted.Authority != reservation.Authority
            || accepted.Version != reservation.Version
            || accepted.LifecycleEpoch != reservation.LifecycleEpoch
            || accepted.ReservationToken != reservation.ReservationToken
            || !accepted.HasPendingBuild)
        {
            return false;
        }

        StoreDocumentAnalysis(reservation, analysis);
        acceptedRevisions.Remove(acceptedKey);
        acceptedRevisions[analysis.Uri] = accepted with
        {
            Uri = analysis.Uri,
            HasPendingBuild = false
        };
        Monitor.PulseAll(gate);
        return true;
    }

    private void AbandonDocumentAnalysis(DocumentAnalysisReservation reservation)
    {
        var acceptedKey = FindAcceptedRevisionKey(reservation.Uri);
        if (acceptedKey is null)
        {
            return;
        }

        var accepted = acceptedRevisions[acceptedKey];
        if (accepted.LifecycleEpoch == reservation.LifecycleEpoch
            && accepted.ReservationToken == reservation.ReservationToken)
        {
            acceptedRevisions[acceptedKey] = accepted with { HasPendingBuild = false };
            Monitor.PulseAll(gate);
        }
    }

    private void WaitForAcceptedDocumentAnalysis(
        DocumentAnalysisReservation reservation,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var committed = GetDocumentState(reservation.Uri);
                if (committed?.LifecycleEpoch == reservation.LifecycleEpoch
                    && committed.ReservationToken >= reservation.ReservationToken)
                {
                    return;
                }

                var accepted = GetAcceptedRevisionState(reservation.Uri);
                if (accepted is null
                    || accepted.LifecycleEpoch != reservation.LifecycleEpoch
                    || !accepted.HasPendingBuild)
                {
                    return;
                }

                Monitor.Wait(gate, millisecondsTimeout: 50);
            }
        }
    }

    private void StoreDocumentAnalysis(
        DocumentAnalysisReservation reservation,
        VbaDocumentAnalysis analysis)
    {
        var existingKey = FindDocumentKey(analysis.Uri);
        if (existingKey is not null)
        {
            documents.Remove(existingKey);
        }

        var document = new VbaTrackedDocument(
            analysis.Uri,
            analysis.Text,
            analysis.SyntaxTree,
            analysis.SourceDocument);
        documents[analysis.Uri] = new WorkspaceDocumentState(
            document,
            analysis,
            reservation.Authority,
            reservation.Version,
            reservation.LifecycleEpoch,
            reservation.ReservationToken,
            reservation.Version is null
                ? null
                : VbaVersionedDocumentSnapshot.Create(analysis));
        MarkWorkspaceChanged(analysis.Uri);
    }

    private WorkspaceDocumentState? GetDocumentState(string uri)
    {
        var key = FindDocumentKey(uri);
        return key is null ? null : documents[key];
    }

    private AcceptedDocumentRevisionState? GetAcceptedRevisionState(string uri)
    {
        var key = FindAcceptedRevisionKey(uri);
        return key is null ? null : acceptedRevisions[key];
    }

    private string? FindDocumentKey(string uri)
    {
        if (documents.ContainsKey(uri))
        {
            return uri;
        }

        return documents.Keys.FirstOrDefault(candidate => SameDocumentIdentity(candidate, uri));
    }

    private string? FindAcceptedRevisionKey(string uri)
    {
        if (acceptedRevisions.ContainsKey(uri))
        {
            return uri;
        }

        return acceptedRevisions.Keys.FirstOrDefault(
            candidate => SameDocumentIdentity(candidate, uri));
    }

    private bool AddExcludedSourceIdentity(string uri)
    {
        if (excludedSourceUris.Any(candidate => SameDocumentIdentity(candidate, uri)))
        {
            return false;
        }

        return excludedSourceUris.Add(uri);
    }

    private bool RemoveExcludedSourceIdentity(string uri)
        => excludedSourceUris.RemoveWhere(candidate => SameDocumentIdentity(candidate, uri)) > 0;

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

    private static bool IsManifestRelevantToScope(
        string manifestPath,
        string? activePath,
        VbaProjectResolution resolution)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var manifestDirectory =
            Path.GetDirectoryName(fullManifestPath);
        return manifestDirectory is not null
            && (!string.IsNullOrWhiteSpace(resolution.RootPath)
                    && VbaProjectResolver.IsPathUnder(
                        fullManifestPath,
                        Path.GetFullPath(resolution.RootPath))
                || resolution.Kind == VbaProjectResolutionKind.AdHoc
                    && activePath is not null
                    && VbaProjectResolver.IsPathUnder(
                        activePath,
                        manifestDirectory));
    }

    private void InvalidateDiskDocument(string uri)
    {
        var localPath = VbaProjectResolver.TryGetLocalPath(uri);
        if (localPath is not null)
        {
            diskInventory.InvalidateSource(localPath);
            diskDocumentCache.Invalidate(localPath);
        }
    }

    private void MarkWorkspaceChanged(string uri)
    {
        workspaceVersion++;
        workspaceSnapshotState = null;
        sourceRevisionHistory.Record(uri, workspaceVersion);
        snapshotProvider.InvalidateSource(uri, workspaceVersion);
    }

    private IReadOnlyList<string> CaptureTrackedDocumentUris()
        => documents.Values
            .Select(state => state.Document.Uri)
            .ToArray();

    private void RetireInactiveProjectScopes(
        IReadOnlyList<string> remainingTrackedUris)
    {
        snapshotProvider.RetireInactiveScopes(
            remainingTrackedUris);
        ManifestWorkspace.RetireInactiveState(
            remainingTrackedUris,
            snapshotProvider.CaptureManifestRetentionScopes());
    }

    internal void RetireInactiveManifestState()
    {
        IReadOnlyList<string> trackedUris;
        lock (gate)
        {
            trackedUris = CaptureTrackedDocumentUris();
        }

        ManifestWorkspace.RetireInactiveState(
            trackedUris,
            snapshotProvider.CaptureManifestRetentionScopes());
    }

    private long GetSourceRevision(string uri)
    {
        return sourceRevisionHistory.GetRevision(uri);
    }

    private sealed record WorkspaceProjectSnapshotCapture(
        VbaWorkspaceSnapshotState WorkspaceState,
        IReadOnlyList<string> ActiveUris,
        IDisposable RevisionCapture);

    private enum WorkspaceDocumentAuthority
    {
        OpenBuffer,
        DiskWatcher
    }

    private sealed record WorkspaceDocumentState(
        VbaTrackedDocument Document,
        VbaDocumentAnalysis Analysis,
        WorkspaceDocumentAuthority Authority,
        int? Version,
        long LifecycleEpoch,
        long ReservationToken,
        VbaVersionedDocumentSnapshot? VersionedSnapshot);

    private sealed record AcceptedDocumentRevisionState(
        string Uri,
        WorkspaceDocumentAuthority Authority,
        int? Version,
        long LifecycleEpoch,
        long ReservationToken,
        bool HasPendingBuild);

    private sealed record DocumentAnalysisReservation(
        string Uri,
        WorkspaceDocumentAuthority Authority,
        int? Version,
        long LifecycleEpoch,
        long ReservationToken,
        VbaDocumentAnalysis? PreviousAnalysis);

}
