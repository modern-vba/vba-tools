using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using System.Security.Cryptography;

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
    VbaSemanticInventory SemanticInventory)
{
    internal IReadOnlyList<VbaProjectDiskSourceFailure>
        DiskSourceFailures { get; init; } = [];

    internal IReadOnlyList<VbaProjectDiskSource>
        DiskSources { get; init; } = [];

    internal IReadOnlySet<string> ExistingOpenSourcePaths { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyDictionary<string, bool> ManifestBarrierOverrides
        { get; init; } = new Dictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

    internal VbaProjectSnapshotProvider.ProjectSnapshotOwnership?
        DiagnosticsOwnership { get; init; }
}

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed partial class VbaLanguageWorkspace : IVbaInteractiveWorkspaceCapture
{
    private readonly object gate = new();
    private readonly Dictionary<string, WorkspaceDocumentState> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AcceptedDocumentRevisionState> acceptedRevisions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VbaProjectDiskSourceFailure>
        diskSourceFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedSourceUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly VbaSourceRevisionHistory sourceRevisionHistory = new();
    private readonly VbaSourceRevisionHistory renameSourceRevisionHistory =
        new(retainOnlyWhileCapturesActive: true);
    private readonly IVbaProjectDiskInventory diskInventory;
    private readonly IVbaProjectFileSystem projectFileSystem;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;
    private readonly VbaProjectSnapshotProvider snapshotProvider;
    private readonly VbaHostClassProjectionSnapshotStore hostClassProjectionStore;
    private readonly IVbaDocumentAnalysisBuildObserver analysisBuildObserver;
    private VbaWorkspaceSnapshotState? workspaceSnapshotState;
    private long nextDocumentLifecycleEpoch;
    private long nextDocumentReservationToken;
    private long workspaceVersion;
    private long renameSourceVersion;

    internal event Action<string>? DiskSourceDiagnosticsChanged;

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
        DiskSourceDecoding sourceDecoding)
        : this(
            referenceCatalogCache,
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            NullVbaProjectSnapshotBuildObserver.Instance,
            SystemVbaProjectFileSystem.Instance,
            reconciliationAuthorityLeaseObserver: null,
            sourceDecoding: sourceDecoding)
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
            reconciliationAuthorityLeaseObserver = null,
        DiskSourceDecoding? sourceDecoding = null)
    {
        this.analysisBuildObserver = analysisBuildObserver;
        this.projectFileSystem = projectFileSystem;
        diskInventory =
            new VbaFileSystemProjectDiskInventory(
                projectFileSystem,
                sourceDecoding ?? DiskSourceDecoding.ForCurrentProcess);
        diskDocumentCache = new VbaProjectSourceDocumentCache();
        ManifestWorkspace = new VbaProjectManifestWorkspace(projectFileSystem);
        hostClassProjectionStore = new VbaHostClassProjectionSnapshotStore();
        snapshotProvider = new VbaProjectSnapshotProvider(
            referenceCatalogCache,
            diskInventory,
            diskDocumentCache,
            ManifestWorkspace,
            lifecycleObserver,
            snapshotBuildObserver,
            reconciliationAuthorityLeaseObserver,
            hostClassProjectionStore);
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

    internal int RetainedRenameSourceRevisionCount
    {
        get
        {
            lock (gate)
            {
                return renameSourceRevisionHistory.Count;
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
        lock (gate)
        {
            if (GetAcceptedRevisionState(uri)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer
                || GetDocumentState(uri)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }
        }

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
            out var failure,
            cancellationToken);
        if (source is null)
        {
            return failure is not null
                && RecordDiskSourceFailure(failure);
        }

        ClearDiskSourceFailure(uri);
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

        ClearDiskSourceFailure(uri);
        InvalidateDiskDocument(uri);
        return BuildAndCommitDocumentAnalysis(
            reservation,
            text,
            cancellationToken);
    }

    private bool CommitReconciledDiskSourceFailure(
        VbaProjectDiskSourceFailure failure,
        long capturedWorkspaceRevision)
    {
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(failure.Uri);
            var existing = GetDocumentState(failure.Uri);
            if (GetSourceRevision(failure.Uri) > capturedWorkspaceRevision
                || accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            if (!RecordDiskSourceFailure(failure))
            {
                return false;
            }
        }

        InvalidateDiskDocument(failure.Uri);
        return true;
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
            var failureRemoved = ClearDiskSourceFailure(uri);
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
                if (exclusionAdded || failureRemoved)
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
            if (exclusionAdded || documentRemoved || failureRemoved)
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

            var failureRemoved = ClearDiskSourceFailure(uri);
            var exclusionAdded = AddExcludedSourceIdentity(uri);
            if (revisionKey is not null)
            {
                acceptedRevisions.Remove(revisionKey);
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documentKey is not null
                && documents.Remove(documentKey);
            if (exclusionAdded || documentRemoved || failureRemoved)
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
            if (state is null
                || accepted is null
                || accepted.HasPendingBuild
                || accepted.Authority != state.Authority
                || accepted.Version != state.Version
                || accepted.LifecycleEpoch != state.LifecycleEpoch
                || accepted.ReservationToken != state.ReservationToken)
            {
                return null;
            }

            var ownership = new VbaDocumentDiagnosticsOwnership(
                state.Analysis.Uri,
                state.Version,
                state.LifecycleEpoch,
                state.ReservationToken);
            return new VbaDocumentDiagnosticsSnapshot(
                state.Analysis,
                state.Version,
                state.LifecycleEpoch,
                state.ReservationToken,
                [],
                [ownership],
                ProjectSnapshotOwnership: null);
        }
    }

    /// <summary>
    /// Captures diagnostics for every tracked source in the active project snapshot.
    /// </summary>
    internal IReadOnlyList<VbaDocumentDiagnosticsSnapshot>?
        GetProjectDiagnosticsSnapshots(
            string activeUri,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var projectSnapshot = CreateProjectSnapshot(activeUri, cancellationToken);
        var sourceTemplateFingerprint =
            CaptureDiagnosticsSourceTemplateFingerprint(projectSnapshot);
        lock (gate)
        {
            var captured = new List<(
                WorkspaceDocumentState State,
                AcceptedDocumentRevisionState Accepted)>();
            foreach (var (sourceUri, sourceText) in projectSnapshot.SourceDocuments)
            {
                var state = GetDocumentState(sourceUri);
                if (state is null)
                {
                    continue;
                }

                var accepted = GetAcceptedRevisionState(sourceUri);
                if (accepted is null
                    || accepted.HasPendingBuild
                    || accepted.Authority != state.Authority
                    || accepted.Version != state.Version
                    || accepted.LifecycleEpoch != state.LifecycleEpoch
                    || accepted.ReservationToken != state.ReservationToken
                    || !string.Equals(
                        state.Analysis.Text,
                        sourceText,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                captured.Add((state, accepted));
            }

            if (captured.Count == 0)
            {
                return [];
            }

            var ownership = captured
                .Select(item => new VbaDocumentDiagnosticsOwnership(
                    item.State.Analysis.Uri,
                    item.State.Version,
                    item.State.LifecycleEpoch,
                    item.State.ReservationToken))
                .ToArray();
            return captured
                .Select(item => new VbaDocumentDiagnosticsSnapshot(
                    item.State.Analysis,
                    item.State.Version,
                    item.State.LifecycleEpoch,
                    item.State.ReservationToken,
                    projectSnapshot.SemanticInventory
                        .GetProjectValidationDiagnostics(
                            item.State.Analysis.Uri,
                            sourceTemplateFingerprint),
                    ownership,
                    projectSnapshot.DiagnosticsOwnership)
                {
                    SourceTemplateFingerprint = sourceTemplateFingerprint
                })
                .ToArray();
        }
    }

    private string? CaptureDiagnosticsSourceTemplateFingerprint(
        VbaProjectSnapshot projectSnapshot)
    {
        if (projectSnapshot.Resolution.Kind
                != VbaProjectResolutionKind.ManifestDocument
            || projectSnapshot.Resolution.SourceTemplatePath is not { }
                sourceTemplatePath)
        {
            return null;
        }

        try
        {
            return CaptureRenameFileEvidence(sourceTemplatePath).ContentDigest;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal VbaProjectDiskSourceFailure? GetDiskSourceFailure(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (GetAcceptedRevisionState(uri)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer
                || GetDocumentState(uri)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return null;
            }

            var key = FindDiskSourceFailureKey(uri);
            return key is null ? null : diskSourceFailures[key];
        }
    }

    internal bool IsCurrentDiskSourceFailure(
        VbaProjectDiskSourceFailure failure)
    {
        lock (gate)
        {
            var key = FindDiskSourceFailureKey(failure.Uri);
            return key is not null
                && diskSourceFailures[key] == failure
                && GetAcceptedRevisionState(failure.Uri)?.Authority
                    != WorkspaceDocumentAuthority.OpenBuffer
                && GetDocumentState(failure.Uri)?.Authority
                    != WorkspaceDocumentAuthority.OpenBuffer;
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

    internal bool AreLatestDiagnosticsSnapshots(
        IReadOnlyList<VbaDocumentDiagnosticsOwnership> ownership,
        VbaProjectSnapshotProvider.ProjectSnapshotOwnership?
            projectSnapshotOwnership,
        string? sourceTemplateFingerprint = null)
    {
        if (!snapshotProvider.IsCurrentProjectSnapshot(
                projectSnapshotOwnership))
        {
            return false;
        }

        bool documentsAreCurrent;
        lock (gate)
        {
            documentsAreCurrent = ownership.Count > 0
                && ownership.All(item =>
                {
                    var state = GetDocumentState(item.Uri);
                    var accepted = GetAcceptedRevisionState(item.Uri);
                    return state is not null
                        && accepted is not null
                        && !accepted.HasPendingBuild
                        && accepted.Authority == state.Authority
                        && accepted.Version == item.ClientVersion
                        && state.Version == item.ClientVersion
                        && accepted.LifecycleEpoch == item.LifecycleEpoch
                        && state.LifecycleEpoch == item.LifecycleEpoch
                        && accepted.ReservationToken == item.ReservationToken
                        && state.ReservationToken == item.ReservationToken;
                });
        }

        return documentsAreCurrent
            && snapshotProvider.IsCurrentProjectSnapshot(
                projectSnapshotOwnership)
            && IsCurrentDiagnosticsSourceTemplateFingerprint(
                projectSnapshotOwnership,
                sourceTemplateFingerprint);
    }

    private bool IsCurrentDiagnosticsSourceTemplateFingerprint(
        VbaProjectSnapshotProvider.ProjectSnapshotOwnership?
            projectSnapshotOwnership,
        string? sourceTemplateFingerprint)
    {
        if (sourceTemplateFingerprint is null)
        {
            return true;
        }

        if (projectSnapshotOwnership?.Resolution.SourceTemplatePath
                is not { } sourceTemplatePath)
        {
            return false;
        }

        try
        {
            return CaptureRenameFileEvidence(sourceTemplatePath)
                .ContentDigest?.Equals(
                    sourceTemplateFingerprint,
                    StringComparison.Ordinal) == true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return false;
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
        var snapshot = snapshotProvider.CreateProjectSnapshot(
            activeUri,
            capture.WorkspaceState,
            cancellationToken);
        ApplyColdDiskSourceDiagnostics(snapshot);
        return snapshot;
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
        var snapshots = snapshotProvider.CreateProjectSnapshots(
            capture.ActiveUris,
            capture.WorkspaceState,
            cancellationToken);
        foreach (var snapshot in snapshots)
        {
            ApplyColdDiskSourceDiagnostics(snapshot);
        }

        return snapshots;
    }

    internal bool TryApplyHostClassProjectionSnapshot(
        VbaHostClassProjectionSnapshotUpdate update)
    {
        IReadOnlyList<string> activeUris;
        lock (gate)
        {
            activeUris = documents.Values
                .Select(state => state.Document.Uri)
                .ToArray();
        }

        return snapshotProvider.TryApplyHostClassProjectionSnapshot(
            update,
            activeUris);
    }

    VbaSemanticInventory IVbaInteractiveWorkspaceCapture.CaptureProjectSemanticInventory(
        string activeUri,
        CancellationToken cancellationToken)
        => CreateProjectSnapshot(activeUri, cancellationToken).SemanticInventory;

    VbaRenameProjectSnapshotCapture
        IVbaInteractiveWorkspaceCapture.CaptureRenameProjectSnapshot(
            string activeUri,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceProjectSnapshotCapture capture;
        IDisposable workspaceRevisionLease;
        IDisposable renameRevisionLease;
        long capturedRenameSourceVersion;
        lock (gate)
        {
            capture = CaptureProjectSnapshotState(includeActiveUris: false);
            workspaceRevisionLease = sourceRevisionHistory.BeginCapture(
                capture.WorkspaceState.Version);
            capturedRenameSourceVersion = renameSourceVersion;
            renameRevisionLease = renameSourceRevisionHistory.BeginCapture(
                capturedRenameSourceVersion);
        }

        try
        {
            using (capture.RevisionCapture)
            {
                var snapshot = snapshotProvider.CreateProjectSnapshot(
                    activeUri,
                    capture.WorkspaceState,
                    cancellationToken);
                ApplyColdDiskSourceDiagnostics(snapshot);
                var sourceUris = snapshot.SourceDocuments.Keys.ToArray();
                var fileEvidence = CaptureRenameFileEvidence(
                    snapshot,
                    sourceUris);
                var sourceTemplateFingerprint =
                    snapshot.Resolution.Kind
                        == VbaProjectResolutionKind.ManifestDocument
                    && snapshot.Resolution.SourceTemplatePath is { } sourceTemplatePath
                        ? CaptureRenameFileEvidence(sourceTemplatePath)
                            .ContentDigest
                        : null;
                return new VbaRenameProjectSnapshotCapture(
                    snapshot.SemanticInventory,
                    () => GetRenameSourceChangeFailureSince(
                        snapshot.Resolution,
                        activeUri,
                        capturedRenameSourceVersion,
                        sourceUris,
                        snapshot.ManifestBarrierOverrides),
                    new CombinedRevisionLease(
                        workspaceRevisionLease,
                        renameRevisionLease),
                    plan => PreflightRenameFileOperations(
                        plan,
                        fileEvidence,
                        snapshot.Resolution),
                    snapshot.DiskSourceFailures.Count == 0
                        ? null
                        : "Rename cannot prove binding preservation because "
                            + "one or more project sources could not be read.",
                    sourceTemplateFingerprint);
            }
        }
        catch
        {
            capture.RevisionCapture.Dispose();
            workspaceRevisionLease.Dispose();
            renameRevisionLease.Dispose();
            throw;
        }
    }

    private IReadOnlyDictionary<string, VbaRenameFileEvidence>
        CaptureRenameFileEvidence(
            VbaProjectSnapshot snapshot,
            IEnumerable<string> sourceUris)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceUri in sourceUris)
        {
            if (VbaProjectResolver.TryGetLocalPath(sourceUri) is not { }
                sourcePath)
            {
                continue;
            }

            paths.Add(sourcePath);
            if (Path.GetExtension(sourcePath).Equals(
                ".frm",
                StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(Path.ChangeExtension(sourcePath, ".frx"));
            }
        }

        if (projectFileSystem.DirectoryExists(snapshot.Resolution.RootPath))
        {
            var searchOption = snapshot.Resolution.Kind
                == VbaProjectResolutionKind.ManifestDocument
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;
            foreach (var candidate in projectFileSystem.EnumerateSourceFiles(
                snapshot.Resolution.RootPath,
                "*",
                searchOption))
            {
                if (Path.GetExtension(candidate).Equals(
                    ".frx",
                    StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(Path.GetFullPath(candidate));
                }
            }
        }

        var snapshotEvidence = snapshot.DiskSources.ToDictionary(
            source => Path.GetFullPath(source.FullPath),
            source => new VbaRenameFileEvidence(
                Path.GetFullPath(source.FullPath),
                Exists: true,
                source.Metadata,
                source.RawContentDigest,
                ReadFailure: null),
            StringComparer.OrdinalIgnoreCase);
        return paths.ToDictionary(
            path => path,
            path => snapshotEvidence.TryGetValue(path, out var evidence)
                ? evidence
                : CaptureRenameFileEvidence(path),
            StringComparer.OrdinalIgnoreCase);
    }

    private VbaRenameFileEvidence CaptureRenameFileEvidence(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!projectFileSystem.TryGetSourceMetadata(fullPath, out var metadata))
        {
            return new VbaRenameFileEvidence(
                fullPath,
                Exists: false,
                Metadata: null,
                ContentDigest: null,
                ReadFailure: null);
        }

        try
        {
            var bytes = projectFileSystem.ReadSourceBytes(fullPath);
            if (!projectFileSystem.TryGetSourceMetadata(
                fullPath,
                out var loadedMetadata)
                || loadedMetadata != metadata)
            {
                return new VbaRenameFileEvidence(
                    fullPath,
                    Exists: true,
                    Metadata: loadedMetadata,
                    ContentDigest: null,
                    ReadFailure: null);
            }

            return new VbaRenameFileEvidence(
                fullPath,
                Exists: true,
                Metadata: loadedMetadata,
                ContentDigest: Convert.ToHexString(SHA256.HashData(bytes)),
                ReadFailure: null);
        }
        catch (FileNotFoundException)
        {
            return new VbaRenameFileEvidence(
                fullPath,
                Exists: false,
                Metadata: null,
                ContentDigest: null,
                ReadFailure: null);
        }
        catch (DirectoryNotFoundException)
        {
            return new VbaRenameFileEvidence(
                fullPath,
                Exists: false,
                Metadata: null,
                ContentDigest: null,
                ReadFailure: null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return new VbaRenameFileEvidence(
                fullPath,
                Exists: true,
                Metadata: metadata,
                ContentDigest: null,
                ReadFailure: ex.Message);
        }
    }

    private VbaRenameFilePreflightResult PreflightRenameFileOperations(
        VbaRenamePlan plan,
        IReadOnlyDictionary<string, VbaRenameFileEvidence> requestStartEvidence,
        VbaProjectResolution resolution)
    {
        var completedFileRenames = new List<VbaRenameFileOperation>();
        foreach (var fileRename in plan.FileRenames)
        {
            if (VbaProjectResolver.TryGetLocalPath(fileRename.OldUri) is not { }
                    sourcePath
                || VbaProjectResolver.TryGetLocalPath(fileRename.NewUri) is not { }
                    destinationPath)
            {
                completedFileRenames.Add(fileRename);
                continue;
            }

            if (!requestStartEvidence.TryGetValue(
                    sourcePath,
                    out var sourceEvidence)
                || !sourceEvidence.Exists)
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The source for a file-following module Rename did not exist at request start.",
                        Condition: "sourceMissing",
                        Path: sourcePath,
                        Guidance: "Restore or reload the source file, then retry Rename."));
            }

            var currentSourceEvidence = CaptureRenameFileEvidence(sourcePath);
            if (!currentSourceEvidence.Exists)
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The source for a file-following module Rename no longer exists.",
                        Condition: "sourceMissing",
                        Path: sourcePath,
                        Guidance: "Restore or reload the source file, then retry Rename."));
            }

            if (sourceEvidence.Metadata != currentSourceEvidence.Metadata
                || sourceEvidence.ContentDigest is null
                || !sourceEvidence.ContentDigest.Equals(
                    currentSourceEvidence.ContentDigest,
                    StringComparison.Ordinal))
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The source for a file-following module Rename changed after request start.",
                        Condition: "sourceChanged",
                        Path: sourcePath,
                        Guidance: "Reload the changed source and retry Rename."));
            }

            var directoryPath = Path.GetDirectoryName(destinationPath);
            if (directoryPath is null || !projectFileSystem.DirectoryExists(directoryPath))
            {
                continue;
            }

            var conflictingPath = projectFileSystem
                .EnumerateSourceFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .FirstOrDefault(candidate =>
                    !projectFileSystem.PathsReferToSameEntry(
                        candidate,
                        sourcePath)
                    && candidate.Equals(
                        destinationPath,
                        StringComparison.OrdinalIgnoreCase));
            if (conflictingPath is not null)
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The destination for a file-following module Rename already exists.",
                        Condition: "destinationExists",
                        Path: conflictingPath,
                        Guidance: "Choose another module name or remove the conflicting destination file."));
            }

            completedFileRenames.Add(new VbaRenameFileOperation(
                fileRename.OldUri,
                fileRename.NewUri,
                Overwrite: IsCaseOnlyFileRename(sourcePath, destinationPath)));

            if (!Path.GetExtension(sourcePath).Equals(
                    ".frm",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sidecarSourcePath = Path.ChangeExtension(sourcePath, ".frx");
            var displacedSidecarPath = FindDisplacedFormSidecar(
                sidecarSourcePath,
                resolution,
                requestStartEvidence);
            if (displacedSidecarPath is not null)
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form sidecar is displaced from its matching form source.",
                        Condition: "sidecarConflict",
                        Path: displacedSidecarPath,
                        Guidance: "Move or re-export the sidecar beside the form, then retry Rename."));
            }

            if (!requestStartEvidence.TryGetValue(
                    sidecarSourcePath,
                    out var sidecarEvidence))
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form sidecar could not be verified from request-start evidence.",
                        Condition: "sidecarConflict",
                        Path: sidecarSourcePath,
                        Guidance: "Reload the form source unit and retry Rename."));
            }

            var currentSidecarEvidence = CaptureRenameFileEvidence(
                sidecarSourcePath);
            if (sidecarEvidence.ReadFailure is not null
                || currentSidecarEvidence.ReadFailure is not null
                || sidecarEvidence.Exists != currentSidecarEvidence.Exists
                || sidecarEvidence.Exists
                    && (sidecarEvidence.Metadata
                            != currentSidecarEvidence.Metadata
                || sidecarEvidence.ContentDigest is null
                || !sidecarEvidence.ContentDigest.Equals(
                    currentSidecarEvidence.ContentDigest,
                    StringComparison.Ordinal)))
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form sidecar appeared, changed, or disappeared after request start.",
                        Condition: "sidecarConflict",
                        Path: sidecarSourcePath,
                        Guidance: "Restore or reload the matching form sidecar, then retry Rename."));
            }

            if (!sidecarEvidence.Exists)
            {
                continue;
            }

            var sidecarDestinationPath = Path.ChangeExtension(
                destinationPath,
                ".frx");
            var sidecarConflictPath = projectFileSystem
                .EnumerateSourceFiles(
                    directoryPath,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .FirstOrDefault(candidate =>
                    !projectFileSystem.PathsReferToSameEntry(
                        candidate,
                        sidecarSourcePath)
                    && candidate.Equals(
                        sidecarDestinationPath,
                        StringComparison.OrdinalIgnoreCase));
            if (sidecarConflictPath is not null)
            {
                return new VbaRenameFilePreflightResult(
                    plan,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The destination for the form sidecar already exists.",
                        Condition: "sidecarConflict",
                        Path: sidecarConflictPath,
                        Guidance: "Choose another module name or remove the conflicting sidecar file."));
            }

            completedFileRenames.Add(new VbaRenameFileOperation(
                new Uri(sidecarSourcePath).AbsoluteUri,
                new Uri(sidecarDestinationPath).AbsoluteUri,
                Overwrite: IsCaseOnlyFileRename(
                    sidecarSourcePath,
                    sidecarDestinationPath)));
        }

        return new VbaRenameFilePreflightResult(
            plan with
            {
                FileRenames = completedFileRenames.ToArray()
            },
            Failure: null);
    }

    private bool IsCaseOnlyFileRename(
        string sourcePath,
        string destinationPath)
        => !sourcePath.Equals(destinationPath, StringComparison.Ordinal)
            && projectFileSystem.PathsReferToSameEntry(
                sourcePath,
                destinationPath);

    private string? FindDisplacedFormSidecar(
        string expectedSidecarPath,
        VbaProjectResolution resolution,
        IReadOnlyDictionary<string, VbaRenameFileEvidence> requestStartEvidence)
    {
        var expectedFileName = Path.GetFileName(expectedSidecarPath);
        var requestStartDisplacedPath = requestStartEvidence.Values
            .Where(evidence => evidence.Exists)
            .Select(evidence => evidence.FullPath)
            .Where(path => Path.GetFileName(path).Equals(
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !projectFileSystem.PathsReferToSameEntry(
                path,
                expectedSidecarPath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (requestStartDisplacedPath is not null)
        {
            return requestStartDisplacedPath;
        }

        if (!projectFileSystem.DirectoryExists(resolution.RootPath))
        {
            return null;
        }

        var searchOption = resolution.Kind
            == VbaProjectResolutionKind.ManifestDocument
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
        return projectFileSystem
            .EnumerateSourceFiles(resolution.RootPath, "*", searchOption)
            .Select(Path.GetFullPath)
            .Where(path => Path.GetExtension(path).Equals(
                ".frx",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileName(path).Equals(
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !projectFileSystem.PathsReferToSameEntry(
                path,
                expectedSidecarPath))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private sealed record VbaRenameFileEvidence(
        string FullPath,
        bool Exists,
        VbaProjectSourceFileMetadata? Metadata,
        string? ContentDigest,
        string? ReadFailure);

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

    private VbaRenameFailure? GetRenameSourceChangeFailureSince(
        VbaProjectResolution resolution,
        string activeUri,
        long capturedRenameSourceVersion,
        IReadOnlyList<string> sourceUris,
        IReadOnlyDictionary<string, bool> manifestBarrierOverrides)
    {
        foreach (var (sourceUri, revision) in
            renameSourceRevisionHistory.CaptureEntries())
        {
            if (revision <= capturedRenameSourceVersion)
            {
                continue;
            }

            if (diskInventory.ContainsSource(
                    resolution,
                    sourceUri,
                    manifestBarrierOverrides)
                || VbaProjectIdentityModel.SameDocument(
                    activeUri,
                    sourceUri)
                || sourceUris.Any(candidate =>
                    VbaProjectIdentityModel.SameDocument(
                        candidate,
                        sourceUri)))
            {
                var affectedPath =
                    VbaProjectResolver.TryGetLocalPath(sourceUri)
                    ?? sourceUri;
                return new VbaRenameFailure(
                    "resourceOperationConflict",
                    "A participating source changed while Rename was being prepared.",
                    Condition: "sourceChanged",
                    Path: affectedPath,
                    Guidance: "Reload the changed source and retry Rename against the latest project snapshot.");
            }
        }

        return null;
    }

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
                                        VbaProjectIdentityModel.SameDocument(
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
        MarkRenameSourceChanged(uri);
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

        return documents.Keys.FirstOrDefault(
            candidate => VbaProjectIdentityModel.SameDocument(
                candidate,
                uri));
    }

    private string? FindAcceptedRevisionKey(string uri)
    {
        if (acceptedRevisions.ContainsKey(uri))
        {
            return uri;
        }

        return acceptedRevisions.Keys.FirstOrDefault(
            candidate => VbaProjectIdentityModel.SameDocument(
                candidate,
                uri));
    }

    private string? FindDiskSourceFailureKey(string uri)
    {
        if (diskSourceFailures.ContainsKey(uri))
        {
            return uri;
        }

        return diskSourceFailures.Keys.FirstOrDefault(
            candidate => VbaProjectIdentityModel.SameDocument(
                candidate,
                uri));
    }

    private bool RecordDiskSourceFailure(
        VbaProjectDiskSourceFailure failure,
        bool participatesInRenameFence = true)
    {
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(failure.Uri);
            var existing = GetDocumentState(failure.Uri);
            if (accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            var failureKey = FindDiskSourceFailureKey(failure.Uri);
            if (failureKey is not null
                && diskSourceFailures[failureKey] == failure
                && accepted is null
                && existing is null)
            {
                return false;
            }

            var acceptedKey = FindAcceptedRevisionKey(failure.Uri);
            if (acceptedKey is not null)
            {
                acceptedRevisions.Remove(acceptedKey);
                Monitor.PulseAll(gate);
            }

            var documentKey = FindDocumentKey(failure.Uri);
            if (documentKey is not null)
            {
                documents.Remove(documentKey);
            }

            if (failureKey is not null)
            {
                diskSourceFailures.Remove(failureKey);
            }

            diskSourceFailures[failure.Uri] = failure;
            MarkWorkspaceChanged(
                failure.Uri,
                participatesInRenameFence);
            return true;
        }
    }

    private bool ClearDiskSourceFailure(string uri)
    {
        lock (gate)
        {
            var key = FindDiskSourceFailureKey(uri);
            if (key is null)
            {
                return false;
            }

            return diskSourceFailures.Remove(key);
        }
    }

    private void ApplyColdDiskSourceDiagnostics(
        VbaProjectSnapshot snapshot)
    {
        var changedUris = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in snapshot.DiskSources)
        {
            if (ClearDiskSourceFailure(source.Uri))
            {
                changedUris.Add(source.Uri);
            }
        }

        foreach (var failure in snapshot.DiskSourceFailures)
        {
            if (RecordDiskSourceFailure(
                failure,
                participatesInRenameFence: false))
            {
                changedUris.Add(failure.Uri);
            }
        }

        foreach (var uri in changedUris)
        {
            DiskSourceDiagnosticsChanged?.Invoke(uri);
        }
    }

    private bool AddExcludedSourceIdentity(string uri)
    {
        if (excludedSourceUris.Any(
            candidate => VbaProjectIdentityModel.SameDocument(
                candidate,
                uri)))
        {
            return false;
        }

        return excludedSourceUris.Add(uri);
    }

    private bool RemoveExcludedSourceIdentity(string uri)
        => excludedSourceUris.RemoveWhere(
            candidate => VbaProjectIdentityModel.SameDocument(
                candidate,
                uri)) > 0;

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

    private void MarkWorkspaceChanged(
        string uri,
        bool participatesInRenameFence = true)
    {
        workspaceVersion++;
        workspaceSnapshotState = null;
        sourceRevisionHistory.Record(uri, workspaceVersion);
        if (participatesInRenameFence)
        {
            MarkRenameSourceChanged(uri);
        }
        snapshotProvider.InvalidateSource(uri, workspaceVersion);
    }

    private void MarkRenameSourceChanged(string uri)
    {
        renameSourceVersion++;
        renameSourceRevisionHistory.Record(uri, renameSourceVersion);
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

    private sealed class CombinedRevisionLease(
        IDisposable first,
        IDisposable second) : IDisposable
    {
        private IDisposable? firstLease = first;
        private IDisposable? secondLease = second;

        public void Dispose()
        {
            Interlocked.Exchange(ref firstLease, null)?.Dispose();
            Interlocked.Exchange(ref secondLease, null)?.Dispose();
        }
    }

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
