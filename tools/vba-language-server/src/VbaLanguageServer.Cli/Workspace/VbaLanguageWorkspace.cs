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
        DiskSourceFailures
    { get; init; } = [];

    internal IReadOnlyList<VbaProjectDiskSource>
        DiskSources
    { get; init; } = [];

    internal IReadOnlySet<VbaDocumentIdentity> ExistingOpenSourceIdentities
    { get; init; } = new HashSet<VbaDocumentIdentity>();

    internal IReadOnlyDictionary<VbaDocumentIdentity, bool>
        ManifestBarrierOverrides
    { get; init; } = new Dictionary<VbaDocumentIdentity, bool>();

    internal VbaProjectSnapshotProvider.ProjectSnapshotOwnership?
        DiagnosticsOwnership
    { get; init; }
}

/// <summary>
/// Maintains open document text and creates project snapshots for language-server features.
/// </summary>
public sealed partial class VbaLanguageWorkspace : IVbaInteractiveWorkspaceCapture
{
    private const long MaximumSourceTemplateIdentityReadLength =
        512L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<VbaDocumentIdentity, WorkspaceDocumentState>
        documents = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        AcceptedDocumentRevisionState> acceptedRevisions = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        VbaProjectDiskSourceFailure> diskSourceFailures = new();
    private readonly HashSet<VbaDocumentIdentity> excludedSourceIdentities =
        new();
    private readonly VbaSourceRevisionHistory sourceRevisionHistory = new();
    private readonly VbaSourceRevisionHistory renameSourceRevisionHistory =
        new(retainOnlyWhileCapturesActive: true);
    private readonly IVbaProjectDiskInventory diskInventory;
    private readonly IVbaProjectFileSystem projectFileSystem;
    private readonly IVbaProjectIdentityReader projectIdentityReader;
    private readonly VbaProjectSourceDocumentCache diskDocumentCache;
    private readonly VbaProjectSnapshotProvider snapshotProvider;
    private readonly VbaIntrinsicHostEventCatalogStore intrinsicHostEventCatalogStore;
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
        DiskSourceDecoding? sourceDecoding = null,
        IVbaProjectIdentityReader? projectIdentityReader = null)
    {
        this.analysisBuildObserver = analysisBuildObserver;
        this.projectFileSystem = projectFileSystem;
        this.projectIdentityReader = projectIdentityReader
            ?? new OpenXmlVbaProjectIdentityReader();
        diskInventory =
            new VbaFileSystemProjectDiskInventory(
                projectFileSystem,
                sourceDecoding ?? DiskSourceDecoding.ForCurrentProcess);
        diskDocumentCache = new VbaProjectSourceDocumentCache();
        ManifestWorkspace = new VbaProjectManifestWorkspace(projectFileSystem);
        intrinsicHostEventCatalogStore = new VbaIntrinsicHostEventCatalogStore();
        snapshotProvider = new VbaProjectSnapshotProvider(
            referenceCatalogCache,
            diskInventory,
            diskDocumentCache,
            ManifestWorkspace,
            lifecycleObserver,
            snapshotBuildObserver,
            reconciliationAuthorityLeaseObserver,
            intrinsicHostEventCatalogStore);
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
        var document = RequireIdentifiedDocument(uri);
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var existing = GetDocumentState(document.Identity);
            var accepted = GetAcceptedRevisionState(document.Identity);
            var continuesOpenLifecycle =
                accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer;
            var version = continuesOpenLifecycle
                ? (accepted!.Version ?? existing?.Version ?? -1) + 1
                : 0;
            if (RemoveExcludedSourceIdentity(document.Identity))
            {
                MarkWorkspaceChanged(document);
            }
            reservation = ReserveDocumentAnalysis(
                continuesOpenLifecycle
                    ? accepted!.Document
                    : document,
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
        var document = RequireIdentifiedDocument(uri);
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            if (RemoveExcludedSourceIdentity(document.Identity))
            {
                MarkWorkspaceChanged(document);
            }
            var existing = GetDocumentState(document.Identity);
            reservation = ReserveDocumentAnalysis(
                document,
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
        var document = RequireIdentifiedDocument(uri);
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(document.Identity);
            var existing = GetDocumentState(document.Identity);
            if (accepted?.Authority != WorkspaceDocumentAuthority.OpenBuffer
                || version <= accepted.Version)
            {
                return false;
            }

            reservation = ReserveDocumentAnalysis(
                accepted.Document,
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
        var document = RequireIdentifiedDocument(uri);
        InvalidateDiskDocument(document.Identity);
        return ReloadSourceDocumentCore(
            document,
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return false;
        }

        var document = new VbaIdentifiedDocument(documentIdentity, uri);
        lock (gate)
        {
            if (GetAcceptedRevisionState(documentIdentity)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer
                || GetDocumentState(documentIdentity)?.Authority
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
            documentIdentity,
            manifestCapture.Barriers.Overrides,
            out var failure,
            cancellationToken);
        if (source is null)
        {
            return failure is not null
                && RecordDiskSourceFailure(failure);
        }

        ClearDiskSourceFailure(documentIdentity);
        diskDocumentCache.Invalidate(source.DocumentIdentity);
        return ReloadSourceDocumentCore(
            document,
            source.Text,
            cancellationToken);
    }

    private bool ReloadSourceDocumentCore(
        VbaIdentifiedDocument document,
        string text,
        CancellationToken cancellationToken)
    {
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var exclusionRemoved = RemoveExcludedSourceIdentity(
                document.Identity);
            var accepted = GetAcceptedRevisionState(document.Identity);
            var existing = GetDocumentState(document.Identity);
            if (accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                if (exclusionRemoved)
                {
                    MarkWorkspaceChanged(document);
                }

                return false;
            }

            if (exclusionRemoved)
            {
                MarkWorkspaceChanged(document);
            }

            reservation = ReserveDocumentAnalysis(
                document,
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
        VbaIdentifiedDocument source,
        string text,
        long capturedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DocumentAnalysisReservation reservation;
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(source.Identity);
            var existing = GetDocumentState(source.Identity);
            if (GetSourceRevision(source.Identity) > capturedWorkspaceRevision
                || accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            if (RemoveExcludedSourceIdentity(source.Identity))
            {
                MarkWorkspaceChanged(source);
            }

            reservation = ReserveDocumentAnalysis(
                source,
                WorkspaceDocumentAuthority.DiskWatcher,
                version: null,
                accepted?.Authority == WorkspaceDocumentAuthority.DiskWatcher
                    ? accepted.LifecycleEpoch
                    : ++nextDocumentLifecycleEpoch,
                existing?.Analysis);
        }

        ClearDiskSourceFailure(source.Identity);
        InvalidateDiskDocument(source.Identity);
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
            var accepted = GetAcceptedRevisionState(
                failure.DocumentIdentity);
            var existing = GetDocumentState(failure.DocumentIdentity);
            if (GetSourceRevision(failure.DocumentIdentity)
                    > capturedWorkspaceRevision
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

        InvalidateDiskDocument(failure.DocumentIdentity);
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return false;
        }

        var document = new VbaIdentifiedDocument(documentIdentity, uri);
        InvalidateDiskDocument(documentIdentity);
        IReadOnlyList<VbaIdentifiedDocument>? remainingTrackedDocuments = null;
        lock (gate)
        {
            var hasOpenRevision = acceptedRevisions.TryGetValue(
                    documentIdentity,
                    out var accepted)
                && accepted.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documents.TryGetValue(
                    documentIdentity,
                    out var existing)
                && existing.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (!hasOpenRevision && !hasOpenDocument)
            {
                return false;
            }

            if (hasOpenRevision)
            {
                acceptedRevisions.Remove(documentIdentity);
                Monitor.PulseAll(gate);
            }

            if (hasOpenDocument)
            {
                documents.Remove(documentIdentity);
                MarkWorkspaceChanged(document);
                remainingTrackedDocuments = CaptureTrackedDocuments();
            }
        }

        if (remainingTrackedDocuments is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedDocuments);
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return true;
        }

        var document = new VbaIdentifiedDocument(documentIdentity, uri);
        InvalidateDiskDocument(documentIdentity);
        IReadOnlyList<VbaIdentifiedDocument>? remainingTrackedDocuments = null;
        lock (gate)
        {
            var exclusionAdded = AddExcludedSourceIdentity(documentIdentity);
            var failureRemoved = ClearDiskSourceFailure(documentIdentity);
            var hasOpenRevision = acceptedRevisions.TryGetValue(
                    documentIdentity,
                    out var accepted)
                && accepted.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documents.TryGetValue(
                    documentIdentity,
                    out var existing)
                && existing.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (hasOpenRevision || hasOpenDocument)
            {
                if (exclusionAdded || failureRemoved)
                {
                    MarkWorkspaceChanged(document);
                }

                return false;
            }

            if (acceptedRevisions.Remove(documentIdentity))
            {
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documents.Remove(documentIdentity);
            if (exclusionAdded || documentRemoved || failureRemoved)
            {
                MarkWorkspaceChanged(document);
            }

            if (documentRemoved)
            {
                remainingTrackedDocuments = CaptureTrackedDocuments();
            }
        }

        if (remainingTrackedDocuments is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedDocuments);
        }

        return true;
    }

    private bool DeleteReconciledSourceDocument(
        VbaIdentifiedDocument source,
        long capturedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<VbaIdentifiedDocument>? remainingTrackedDocuments = null;
        lock (gate)
        {
            var hasOpenRevision = acceptedRevisions.TryGetValue(
                    source.Identity,
                    out var accepted)
                && accepted.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            var hasOpenDocument = documents.TryGetValue(
                    source.Identity,
                    out var existing)
                && existing.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer;
            if (GetSourceRevision(source.Identity) > capturedWorkspaceRevision
                || hasOpenRevision
                || hasOpenDocument)
            {
                return false;
            }

            var failureRemoved = ClearDiskSourceFailure(source.Identity);
            var exclusionAdded = AddExcludedSourceIdentity(source.Identity);
            if (acceptedRevisions.Remove(source.Identity))
            {
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documents.Remove(source.Identity);
            if (exclusionAdded || documentRemoved || failureRemoved)
            {
                MarkWorkspaceChanged(source);
            }

            if (documentRemoved)
            {
                remainingTrackedDocuments = CaptureTrackedDocuments();
            }
        }

        InvalidateDiskDocument(source.Identity);
        if (remainingTrackedDocuments is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedDocuments);
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return false;
        }

        var document = new VbaIdentifiedDocument(documentIdentity, uri);
        IReadOnlyList<VbaIdentifiedDocument>? remainingTrackedDocuments = null;
        bool removed;
        lock (gate)
        {
            var revisionRemoved = acceptedRevisions.Remove(documentIdentity);
            if (revisionRemoved)
            {
                Monitor.PulseAll(gate);
            }

            var documentRemoved = documents.Remove(documentIdentity);
            if (documentRemoved)
            {
                MarkWorkspaceChanged(document);
                remainingTrackedDocuments = CaptureTrackedDocuments();
            }

            removed = revisionRemoved || documentRemoved;
        }

        if (remainingTrackedDocuments is not null)
        {
            RetireInactiveProjectScopes(remainingTrackedDocuments);
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            var document = GetDocumentState(documentIdentity)?.Document;
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            return GetDocumentState(documentIdentity)?.Document.Text;
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            return GetDocumentState(documentIdentity)?.Analysis;
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            var state = GetDocumentState(documentIdentity);
            var accepted = GetAcceptedRevisionState(documentIdentity);
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
        var projectIdentityCapture = CaptureSourceTemplateProjectIdentity(
            projectSnapshot.Resolution,
            cancellationToken);
        var sourceTemplateEvidence = CreateDiagnosticsEvidence(
            projectIdentityCapture.Evidence);
        lock (gate)
        {
            var captured = new List<(
                WorkspaceDocumentState State,
                AcceptedDocumentRevisionState Accepted)>();
            foreach (var (sourceUri, sourceText) in projectSnapshot.SourceDocuments)
            {
                if (!VbaProjectIdentityModel.TryIdentifyDocument(
                        sourceUri,
                        out var sourceIdentity))
                {
                    continue;
                }

                var state = GetDocumentState(sourceIdentity);
                if (state is null)
                {
                    continue;
                }

                var accepted = GetAcceptedRevisionState(sourceIdentity);
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
                            projectIdentityCapture.ReadResult),
                    ownership,
                    projectSnapshot.DiagnosticsOwnership)
                {
                    SourceTemplateEvidence = sourceTemplateEvidence
                })
                .ToArray();
        }
    }

    internal VbaProjectDiskSourceFailure? GetDiskSourceFailure(
        string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            if (GetAcceptedRevisionState(documentIdentity)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer
                || GetDocumentState(documentIdentity)?.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return null;
            }

            return diskSourceFailures.TryGetValue(
                    documentIdentity,
                    out var failure)
                    ? failure
                    : null;
        }
    }

    internal bool IsCurrentDiskSourceFailure(
        VbaProjectDiskSourceFailure failure)
    {
        lock (gate)
        {
            return diskSourceFailures.TryGetValue(
                    failure.DocumentIdentity,
                    out var current)
                && current == failure
                && GetAcceptedRevisionState(
                    failure.DocumentIdentity)?.Authority
                    != WorkspaceDocumentAuthority.OpenBuffer
                && GetDocumentState(failure.DocumentIdentity)?.Authority
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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return false;
        }

        lock (gate)
        {
            var state = GetDocumentState(documentIdentity);
            var accepted = GetAcceptedRevisionState(documentIdentity);
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
        VbaSourceTemplateDiagnosticsEvidence? sourceTemplateEvidence = null)
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
                    if (!VbaProjectIdentityModel.TryIdentifyDocument(
                            item.Uri,
                            out var documentIdentity))
                    {
                        return false;
                    }

                    var state = GetDocumentState(documentIdentity);
                    var accepted = GetAcceptedRevisionState(documentIdentity);
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
            && IsCurrentDiagnosticsSourceTemplateEvidence(
                projectSnapshotOwnership,
                sourceTemplateEvidence);
    }

    private bool IsCurrentDiagnosticsSourceTemplateEvidence(
        VbaProjectSnapshotProvider.ProjectSnapshotOwnership?
            projectSnapshotOwnership,
        VbaSourceTemplateDiagnosticsEvidence? sourceTemplateEvidence)
    {
        if (sourceTemplateEvidence is null)
        {
            return true;
        }

        if (projectSnapshotOwnership?.Resolution.SourceTemplatePath
                is not { } sourceTemplatePath
            || !projectFileSystem.PathsReferToSameEntry(
                sourceTemplatePath,
                sourceTemplateEvidence.FullPath))
        {
            return false;
        }

        try
        {
            return CreateDiagnosticsEvidence(
                    CaptureRenameFileEvidence(
                        sourceTemplatePath,
                        MaximumSourceTemplateIdentityReadLength))
                == sourceTemplateEvidence;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static VbaSourceTemplateDiagnosticsEvidence?
        CreateDiagnosticsEvidence(VbaRenameFileEvidence? evidence)
        => evidence is null
            ? null
            : new VbaSourceTemplateDiagnosticsEvidence(
                evidence.FullPath,
                evidence.Exists,
                evidence.Metadata,
                evidence.ContentDigest,
                ContentCaptured: evidence.Exists
                    && evidence.ReadFailure is null
                    && evidence.ContentDigest is not null);

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
        if (!VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var documentIdentity))
        {
            return null;
        }

        lock (gate)
        {
            var state = GetDocumentState(documentIdentity);
            var accepted = GetAcceptedRevisionState(documentIdentity);
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
        => GetOpenDocuments(cancellationToken)
            .Select(document => document.Uri)
            .ToArray();

    internal IReadOnlyList<VbaIdentifiedDocument> GetOpenDocuments(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return CaptureOpenDocuments();
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
            RequireIdentifiedDocument(activeUri),
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
            capture.ActiveDocuments,
            capture.WorkspaceState,
            cancellationToken);
        foreach (var snapshot in snapshots)
        {
            ApplyColdDiskSourceDiagnostics(snapshot);
        }

        return snapshots;
    }

    internal bool TryApplyIntrinsicHostEventCatalog(
        VbaIntrinsicHostEventCatalogUpdate update)
        => snapshotProvider.TryApplyIntrinsicHostEventCatalog(update);

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
                    RequireIdentifiedDocument(activeUri),
                    capture.WorkspaceState,
                    cancellationToken);
                ApplyColdDiskSourceDiagnostics(snapshot);
                var sourceUris = snapshot.SourceDocuments.Keys.ToArray();
                var fileEvidence = CaptureRenameFileEvidence(
                    snapshot,
                    sourceUris);
                var projectIdentityCapture =
                    CaptureSourceTemplateProjectIdentity(
                        snapshot.Resolution,
                        cancellationToken);
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
                    projectIdentityCapture.ReadResult,
                    snapshot.Resolution.SourceTemplatePath is { }
                            sourceTemplatePath
                        && projectIdentityCapture.Evidence is { }
                            sourceTemplateEvidence
                        ? () => GetSourceTemplateChangeFailure(
                            sourceTemplatePath,
                            sourceTemplateEvidence)
                        : null);
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

    private VbaSourceTemplateProjectIdentityCapture
        CaptureSourceTemplateProjectIdentity(
            VbaProjectResolution resolution,
            CancellationToken cancellationToken)
    {
        if (resolution.Kind != VbaProjectResolutionKind.ManifestDocument)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                ReadResult: null,
                Evidence: null);
        }

        if (resolution.SourceTemplatePath is not { } sourceTemplatePath)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The manifest document does not identify a source template."),
                Evidence: null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(sourceTemplatePath);
        if (!projectFileSystem.TryGetSourceMetadata(fullPath, out var metadata))
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The containing source-template package is missing."),
                new VbaRenameFileEvidence(
                    fullPath,
                    Exists: false,
                    Metadata: null,
                    ContentDigest: null,
                    ReadFailure: null));
        }

        if (metadata.Length is <= 0
            || metadata.Length > MaximumSourceTemplateIdentityReadLength)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The containing source-template package has an invalid or excessive length."),
                new VbaRenameFileEvidence(
                    fullPath,
                    Exists: true,
                    metadata,
                    ContentDigest: null,
                    ReadFailure: null));
        }

        try
        {
            var bytes = projectFileSystem.ReadSourceBytes(fullPath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!projectFileSystem.TryGetSourceMetadata(
                    fullPath,
                    out var loadedMetadata)
                || loadedMetadata != metadata
                || bytes.LongLength != metadata.Length)
            {
                return new VbaSourceTemplateProjectIdentityCapture(
                    VbaProjectIdentityReadResult.Failed(
                        VbaProjectIdentityReadFailureKind.InvalidPackage,
                        "The containing source-template package changed while it was captured."),
                    new VbaRenameFileEvidence(
                        fullPath,
                        Exists: true,
                        loadedMetadata,
                        ContentDigest: null,
                        ReadFailure: null));
            }

            var evidence = new VbaRenameFileEvidence(
                fullPath,
                Exists: true,
                loadedMetadata,
                Convert.ToHexString(SHA256.HashData(bytes)),
                ReadFailure: null);
            return new VbaSourceTemplateProjectIdentityCapture(
                projectIdentityReader.Read(bytes, cancellationToken),
                evidence);
        }
        catch (FileNotFoundException)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The containing source-template package is missing."),
                new VbaRenameFileEvidence(
                    fullPath,
                    Exists: false,
                    Metadata: null,
                    ContentDigest: null,
                    ReadFailure: null));
        }
        catch (DirectoryNotFoundException)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The containing source-template package is missing."),
                new VbaRenameFileEvidence(
                    fullPath,
                    Exists: false,
                    Metadata: null,
                    ContentDigest: null,
                    ReadFailure: null));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException)
        {
            return new VbaSourceTemplateProjectIdentityCapture(
                VbaProjectIdentityReadResult.Failed(
                    VbaProjectIdentityReadFailureKind.InvalidPackage,
                    "The containing source-template package could not be read."),
                new VbaRenameFileEvidence(
                    fullPath,
                    Exists: true,
                    metadata,
                    ContentDigest: null,
                    ReadFailure: ex.Message));
        }
    }

    private VbaRenameFailure? GetSourceTemplateChangeFailure(
        string sourceTemplatePath,
        VbaRenameFileEvidence requestStartEvidence)
    {
        var currentEvidence = CaptureRenameFileEvidence(
            sourceTemplatePath,
            MaximumSourceTemplateIdentityReadLength);
        return EvidenceMatches(requestStartEvidence, currentEvidence)
            ? null
            : new VbaRenameFailure(
                "analysisIncomplete",
                "The containing source-template package changed after Rename captured its VBA project identity.",
                Condition: "sourceTemplateChanged",
                Path: Path.GetFullPath(sourceTemplatePath),
                Guidance: "Retry Rename against the current source-template package.");
    }

    private IReadOnlyDictionary<string, VbaRenameFileEvidence>
        CaptureRenameFileEvidence(
            VbaProjectSnapshot snapshot,
            IEnumerable<string> sourceUris)
    {
        var paths = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var sourceUri in sourceUris)
        {
            if (VbaProjectResolver.TryGetLocalPath(sourceUri) is not { }
                sourcePath)
            {
                continue;
            }

            var fullSourcePath = Path.GetFullPath(sourcePath);
            paths.TryAdd(fullSourcePath, fullSourcePath);
            if (Path.GetExtension(sourcePath).Equals(
                ".frm",
                StringComparison.OrdinalIgnoreCase))
            {
                var expectedSidecarPath = Path.GetFullPath(
                    Path.ChangeExtension(sourcePath, ".frx"));
                paths.TryAdd(expectedSidecarPath, expectedSidecarPath);
            }
        }

        IReadOnlyList<string> observedSidecarPaths = [];
        if (projectFileSystem.DirectoryExists(snapshot.Resolution.RootPath))
        {
            var searchOption = snapshot.Resolution.Kind
                == VbaProjectResolutionKind.ManifestDocument
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly;
            observedSidecarPaths = projectFileSystem
                .EnumerateSourceFiles(
                    snapshot.Resolution.RootPath,
                    "*",
                    searchOption)
                .Select(Path.GetFullPath)
                .Where(path => Path.GetExtension(path).Equals(
                    ".frx",
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            foreach (var candidate in observedSidecarPaths)
            {
                paths[candidate] = candidate;
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
        return paths.Values.ToDictionary(
            path => path,
            path => (snapshotEvidence.TryGetValue(path, out var evidence)
                    ? evidence
                    : CaptureRenameFileEvidence(path)) with
                {
                    ObservedSidecarPaths = observedSidecarPaths
                        .Where(candidate => candidate.Equals(
                            path,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                },
            StringComparer.OrdinalIgnoreCase);
    }

    private VbaRenameFileEvidence CaptureRenameFileEvidence(
        string path,
        long? maximumLength = null)
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

        if (maximumLength is { } limit
            && (metadata.Length is <= 0 || metadata.Length > limit))
        {
            return new VbaRenameFileEvidence(
                fullPath,
                Exists: true,
                metadata,
                ContentDigest: null,
                ReadFailure: "The file has an invalid or excessive length.");
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
        var formSourceUnitPreflight = PreflightFormSourceUnits(
            plan.FormSourceUnits,
            requestStartEvidence,
            resolution);
        if (formSourceUnitPreflight.Failure is not null)
        {
            return new VbaRenameFilePreflightResult(
                plan,
                formSourceUnitPreflight.Failure);
        }

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
        }

        completedFileRenames.AddRange(formSourceUnitPreflight.FileRenames);

        return new VbaRenameFilePreflightResult(
            plan with
            {
                FileRenames = completedFileRenames.ToArray()
            },
            Failure: null);
    }

    private VbaFormSourceUnitPreflightResult PreflightFormSourceUnits(
        IReadOnlyList<VbaFormSourceUnit> participants,
        IReadOnlyDictionary<string, VbaRenameFileEvidence> requestStartEvidence,
        VbaProjectResolution resolution)
    {
        var completedFileRenames = new List<VbaRenameFileOperation>();
        foreach (var participant in participants)
        {
            if (VbaProjectResolver.TryGetLocalPath(participant.FormUri) is not { }
                    formPath
                || VbaProjectResolver.TryGetLocalPath(participant.SidecarUri) is not { }
                    sidecarPath
                || VbaProjectResolver.TryGetLocalPath(
                    participant.SidecarDestinationUri) is not { }
                    sidecarDestinationPath)
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "analysisIncomplete",
                        "Rename could not identify every local form source-unit participant."));
            }

            if (!requestStartEvidence.TryGetValue(formPath, out var formEvidence)
                || !formEvidence.Exists
                || formEvidence.ReadFailure is not null
                || formEvidence.ContentDigest is null)
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form source was missing or unreadable at request start.",
                        Condition: "sourceMissing",
                        Path: formPath,
                        Guidance: "Restore or reload the complete .frm source, then retry Rename."));
            }

            var currentFormEvidence = CaptureRenameFileEvidence(formPath);
            if (!EvidenceMatches(formEvidence, currentFormEvidence))
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form source changed after Rename captured its source unit.",
                        Condition: "sourceChanged",
                        Path: formPath,
                        Guidance: "Reload the changed .frm source, then retry Rename."));
            }

            if (!requestStartEvidence.TryGetValue(
                    sidecarPath,
                    out var sidecarEvidence))
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The matching form sidecar could not be verified at request start.",
                        Condition: "sidecarConflict",
                        Path: sidecarPath,
                        Guidance: "Reload the complete .frm/.frx source unit, then retry Rename."));
            }

            var actualSidecarPath = sidecarEvidence.Exists
                ? sidecarEvidence.FullPath
                : sidecarPath;
            var effectiveSidecarDestinationPath =
                participant.SidecarPathFollowsIdentity
                    ? sidecarDestinationPath
                    : actualSidecarPath;
            var displacedSidecarPath = FindDisplacedFormSidecar(
                actualSidecarPath,
                resolution,
                requestStartEvidence);
            if (displacedSidecarPath is not null)
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form sidecar is displaced or multiply identified outside its matching form source.",
                        Condition: "sidecarConflict",
                        Path: displacedSidecarPath,
                        Guidance: "Keep exactly one matching sidecar beside the form, then retry Rename."));
            }

            if (!sidecarEvidence.Exists)
            {
                if (participant.SidecarRequired)
                {
                    return new VbaFormSourceUnitPreflightResult(
                        completedFileRenames,
                        new VbaRenameFailure(
                            "resourceOperationConflict",
                            "The form designer references a missing matching sidecar.",
                            Condition: "sidecarMissing",
                            Path: sidecarPath,
                            Guidance: "Restore or re-export the matching .frx beside the form, then retry Rename."));
                }
            }

            var currentSidecarEvidence = CaptureRenameFileEvidence(
                actualSidecarPath);
            if (!EvidenceMatches(sidecarEvidence, currentSidecarEvidence))
            {
                return new VbaFormSourceUnitPreflightResult(
                    completedFileRenames,
                    new VbaRenameFailure(
                        "resourceOperationConflict",
                        "The form sidecar appeared, changed, disappeared, or became unreadable after request start.",
                        Condition: "sidecarConflict",
                        Path: actualSidecarPath,
                        Guidance: "Restore or reload the matching .frx, then retry Rename."));
            }

            var destinationDirectory = Path.GetDirectoryName(
                effectiveSidecarDestinationPath);
            if (destinationDirectory is not null
                && projectFileSystem.DirectoryExists(destinationDirectory))
            {
                var sidecarConflictPath = projectFileSystem
                    .EnumerateSourceFiles(
                        destinationDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .FirstOrDefault(candidate =>
                        !projectFileSystem.PathsReferToSameEntry(
                            candidate,
                            actualSidecarPath)
                        && candidate.Equals(
                            effectiveSidecarDestinationPath,
                            StringComparison.OrdinalIgnoreCase));
                if (sidecarConflictPath is not null)
                {
                    return new VbaFormSourceUnitPreflightResult(
                        completedFileRenames,
                        new VbaRenameFailure(
                            "resourceOperationConflict",
                            "The destination for the form sidecar already exists.",
                            Condition: "sidecarConflict",
                            Path: sidecarConflictPath,
                            Guidance: "Choose another module name or remove the conflicting sidecar file."));
                }
            }

            if (!sidecarEvidence.Exists
                || actualSidecarPath.Equals(
                    effectiveSidecarDestinationPath,
                    StringComparison.Ordinal))
            {
                continue;
            }

            completedFileRenames.Add(new VbaRenameFileOperation(
                new Uri(actualSidecarPath).AbsoluteUri,
                new Uri(effectiveSidecarDestinationPath).AbsoluteUri,
                Overwrite: IsCaseOnlyFileRename(
                    actualSidecarPath,
                    effectiveSidecarDestinationPath)));
        }

        return new VbaFormSourceUnitPreflightResult(
            completedFileRenames,
            Failure: null);
    }

    private static bool EvidenceMatches(
        VbaRenameFileEvidence expected,
        VbaRenameFileEvidence actual)
        => expected.Exists == actual.Exists
            && expected.Metadata == actual.Metadata
            && expected.ReadFailure is null
            && actual.ReadFailure is null
            && (!expected.Exists
                || expected.ContentDigest is not null
                    && expected.ContentDigest.Equals(
                        actual.ContentDigest,
                        StringComparison.Ordinal));

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
            .SelectMany(evidence => evidence.ObservedSidecarPaths.Count > 0
                ? evidence.ObservedSidecarPaths
                : evidence.Exists
                    ? [evidence.FullPath]
                    : [])
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
            .EnumerateSourceFiles(
                resolution.RootPath,
                "*",
                searchOption)
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

    private sealed record VbaFormSourceUnitPreflightResult(
        IReadOnlyList<VbaRenameFileOperation> FileRenames,
        VbaRenameFailure? Failure);

    private sealed record VbaSourceTemplateProjectIdentityCapture(
        VbaProjectIdentityReadResult? ReadResult,
        VbaRenameFileEvidence? Evidence);

    private sealed record VbaRenameFileEvidence(
        string FullPath,
        bool Exists,
        VbaProjectSourceFileMetadata? Metadata,
        string? ContentDigest,
        string? ReadFailure)
    {
        public IReadOnlyList<string> ObservedSidecarPaths { get; init; } = [];
    }

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
        IReadOnlyDictionary<VbaDocumentIdentity, bool>
            manifestBarrierOverrides)
    {
        var activeIdentity = VbaProjectIdentityModel.TryIdentifyDocument(
            activeUri,
            out var identifiedActive)
                ? identifiedActive
                : (VbaDocumentIdentity?)null;
        var sourceIdentities =
            VbaProjectDiskIdentityProjection.CaptureDocuments(sourceUris)
                .ToHashSet();
        foreach (var sourceRevision in
            renameSourceRevisionHistory.CaptureEntries())
        {
            if (sourceRevision.Revision <= capturedRenameSourceVersion)
            {
                continue;
            }

            if (diskInventory.ContainsSource(
                    resolution,
                    sourceRevision.DocumentIdentity,
                    manifestBarrierOverrides)
                || activeIdentity == sourceRevision.DocumentIdentity
                || sourceIdentities.Contains(
                    sourceRevision.DocumentIdentity))
            {
                var affectedPath =
                    VbaProjectResolver.TryGetLocalPath(sourceRevision.Uri)
                    ?? sourceRevision.Uri;
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

            var documentsByIdentity = documents
                .Where(pair => pair.Value.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Document);
            workspaceSnapshotState = new VbaWorkspaceSnapshotState(
                documentsByIdentity,
                new HashSet<VbaDocumentIdentity>(excludedSourceIdentities),
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
                        ? CaptureTrackedDocuments()
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
                var openDocuments = CaptureOpenDocuments();
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
                                    .CaptureReconciliationState(
                                        new VbaIdentifiedDocument(
                                            candidate.DocumentIdentity,
                                            candidate.Uri));
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
                        var activePath =
                            VbaProjectResolver.TryGetLocalPath(
                                scope.ActiveUri);
                        var observedManifestBarrierCandidates =
                            scope.ManifestBarriers.Overrides.Keys
                                .Concat(
                                    scope.ManifestBarriers
                                        .ReconciliationRevisions.Keys)
                                .Distinct()
                                .Select(documentIdentity => new
                                {
                                    Path = documentIdentity.CanonicalValue,
                                    DocumentIdentity = documentIdentity
                                })
                                .Where(candidate =>
                                    IsManifestRelevantToScope(
                                        candidate.Path,
                                        activePath,
                                        scope.Resolution)
                                    && !scope.AuthorityKey.UsesManifest(
                                        candidate.DocumentIdentity))
                                .Select(candidate =>
                                {
                                    var path = candidate.Path;
                                    var uri =
                                        new Uri(Path.GetFullPath(path))
                                            .AbsoluteUri;
                                    var manifestCapture = ManifestWorkspace
                                        .CaptureReconciliationState(
                                            new VbaIdentifiedDocument(
                                                candidate.DocumentIdentity,
                                                uri));
                                    return new
                                        VbaProjectReconciliationManifestCandidate(
                                            candidate.DocumentIdentity,
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
                        var ownedSourceIdentities = scope.KnownSources
                            .Select(source => source.DocumentIdentity)
                            .ToHashSet();
                        ownedSourceIdentities.Add(
                            scope.ActiveDocumentIdentity);

                        var openSources = openDocuments
                            .Where(document =>
                                ownedSourceIdentities.Contains(
                                    document.Identity))
                            .ToArray();
                        return scope with
                        {
                            ManifestCandidates =
                                manifestCandidates,
                            ObservedManifestBarrierCandidates =
                                observedManifestBarrierCandidates,
                            OpenSources = openSources
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
        VbaIdentifiedDocument document,
        WorkspaceDocumentAuthority authority,
        int? version,
        long lifecycleEpoch,
        VbaDocumentAnalysis? previousAnalysis)
    {
        MarkRenameSourceChanged(
            document.Uri,
            document.Identity);
        acceptedRevisions.Remove(document.Identity);

        var reservation = new DocumentAnalysisReservation(
            document,
            authority,
            version,
            lifecycleEpoch,
            ++nextDocumentReservationToken,
            previousAnalysis);
        acceptedRevisions[document.Identity] = new AcceptedDocumentRevisionState(
            document,
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
        if (!acceptedRevisions.TryGetValue(
                reservation.Document.Identity,
                out var accepted))
        {
            return false;
        }

        if (accepted.Authority != reservation.Authority
            || accepted.Version != reservation.Version
            || accepted.LifecycleEpoch != reservation.LifecycleEpoch
            || accepted.ReservationToken != reservation.ReservationToken
            || !accepted.HasPendingBuild)
        {
            return false;
        }

        StoreDocumentAnalysis(reservation, analysis);
        acceptedRevisions[reservation.Document.Identity] = accepted with
        {
            Document = new VbaIdentifiedDocument(
                reservation.Document.Identity,
                analysis.Uri),
            HasPendingBuild = false
        };
        Monitor.PulseAll(gate);
        return true;
    }

    private void AbandonDocumentAnalysis(DocumentAnalysisReservation reservation)
    {
        if (!acceptedRevisions.TryGetValue(
                reservation.Document.Identity,
                out var accepted))
        {
            return;
        }

        if (accepted.LifecycleEpoch == reservation.LifecycleEpoch
            && accepted.ReservationToken == reservation.ReservationToken)
        {
            acceptedRevisions[reservation.Document.Identity] = accepted with
            {
                HasPendingBuild = false
            };
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
                var committed = GetDocumentState(
                    reservation.Document.Identity);
                if (committed?.LifecycleEpoch == reservation.LifecycleEpoch
                    && committed.ReservationToken >= reservation.ReservationToken)
                {
                    return;
                }

                var accepted = GetAcceptedRevisionState(
                    reservation.Document.Identity);
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
        var document = new VbaTrackedDocument(
            analysis.Uri,
            analysis.Text,
            analysis.SyntaxTree,
            analysis.SourceDocument);
        documents[reservation.Document.Identity] = new WorkspaceDocumentState(
            document,
            analysis,
            reservation.Authority,
            reservation.Version,
            reservation.LifecycleEpoch,
            reservation.ReservationToken,
            reservation.Version is null
                ? null
                : VbaVersionedDocumentSnapshot.Create(analysis));
        MarkWorkspaceChanged(
            new VbaIdentifiedDocument(
                reservation.Document.Identity,
                analysis.Uri));
    }

    private WorkspaceDocumentState? GetDocumentState(
        VbaDocumentIdentity documentIdentity)
        => documents.TryGetValue(documentIdentity, out var state)
            ? state
            : null;

    private AcceptedDocumentRevisionState? GetAcceptedRevisionState(
        VbaDocumentIdentity documentIdentity)
        => acceptedRevisions.TryGetValue(documentIdentity, out var state)
            ? state
            : null;

    private bool RecordDiskSourceFailure(
        VbaProjectDiskSourceFailure failure,
        bool participatesInRenameFence = true)
    {
        lock (gate)
        {
            var accepted = GetAcceptedRevisionState(
                failure.DocumentIdentity);
            var existing = GetDocumentState(failure.DocumentIdentity);
            if (accepted?.Authority == WorkspaceDocumentAuthority.OpenBuffer
                || existing?.Authority == WorkspaceDocumentAuthority.OpenBuffer)
            {
                return false;
            }

            if (diskSourceFailures.TryGetValue(
                    failure.DocumentIdentity,
                    out var currentFailure)
                && currentFailure == failure
                && accepted is null
                && existing is null)
            {
                return false;
            }

            if (acceptedRevisions.Remove(failure.DocumentIdentity))
            {
                Monitor.PulseAll(gate);
            }

            documents.Remove(failure.DocumentIdentity);

            diskSourceFailures[failure.DocumentIdentity] = failure;
            MarkWorkspaceChanged(
                new VbaIdentifiedDocument(
                    failure.DocumentIdentity,
                    failure.Uri),
                participatesInRenameFence);
            return true;
        }
    }

    private bool ClearDiskSourceFailure(VbaDocumentIdentity identity)
    {
        lock (gate)
        {
            return diskSourceFailures.Remove(identity);
        }
    }

    private void ApplyColdDiskSourceDiagnostics(
        VbaProjectSnapshot snapshot)
    {
        var changedUris = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in snapshot.DiskSources)
        {
            if (ClearDiskSourceFailure(source.DocumentIdentity))
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

    private bool AddExcludedSourceIdentity(VbaDocumentIdentity identity)
        => excludedSourceIdentities.Add(identity);

    private bool RemoveExcludedSourceIdentity(VbaDocumentIdentity identity)
        => excludedSourceIdentities.Remove(identity);

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

    private void InvalidateDiskDocument(
        VbaDocumentIdentity documentIdentity)
    {
        if (!documentIdentity.IsLocalFile)
        {
            return;
        }

        diskInventory.InvalidateSource(documentIdentity);
        diskDocumentCache.Invalidate(documentIdentity);
    }

    private void MarkWorkspaceChanged(
        VbaIdentifiedDocument document,
        bool participatesInRenameFence = true)
    {
        workspaceVersion++;
        workspaceSnapshotState = null;
        sourceRevisionHistory.Record(document, workspaceVersion);

        if (participatesInRenameFence)
        {
            MarkRenameSourceChanged(
                document.Uri,
                document.Identity);
        }

        snapshotProvider.InvalidateSource(document, workspaceVersion);
    }

    private void MarkRenameSourceChanged(
        string uri,
        VbaDocumentIdentity? documentIdentity)
    {
        renameSourceVersion++;
        if (documentIdentity is { } identity)
        {
            renameSourceRevisionHistory.Record(
                new VbaIdentifiedDocument(identity, uri),
                renameSourceVersion);
        }
    }

    private IReadOnlyList<VbaIdentifiedDocument> CaptureTrackedDocuments()
        => documents
            .Select(pair => new VbaIdentifiedDocument(
                pair.Key,
                pair.Value.Document.Uri))
            .ToArray();

    private IReadOnlyList<VbaIdentifiedDocument> CaptureOpenDocuments()
        => documents
            .Where(
                pair => pair.Value.Authority
                    == WorkspaceDocumentAuthority.OpenBuffer)
            .Select(pair => new VbaIdentifiedDocument(
                pair.Key,
                pair.Value.Document.Uri))
            .ToArray();

    private void RetireInactiveProjectScopes(
        IReadOnlyList<VbaIdentifiedDocument> remainingTrackedDocuments)
    {
        snapshotProvider.RetireInactiveScopes(
            remainingTrackedDocuments);
        ManifestWorkspace.RetireInactiveState(
            remainingTrackedDocuments,
            snapshotProvider.CaptureManifestRetentionScopes());
    }

    internal void RetireInactiveManifestState()
    {
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments;
        lock (gate)
        {
            trackedDocuments = CaptureTrackedDocuments();
        }

        ManifestWorkspace.RetireInactiveState(
            trackedDocuments,
            snapshotProvider.CaptureManifestRetentionScopes());
    }

    private long GetSourceRevision(VbaDocumentIdentity documentIdentity)
        => sourceRevisionHistory.GetRevision(documentIdentity);

    private sealed record WorkspaceProjectSnapshotCapture(
        VbaWorkspaceSnapshotState WorkspaceState,
        IReadOnlyList<VbaIdentifiedDocument> ActiveDocuments,
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
        VbaIdentifiedDocument Document,
        WorkspaceDocumentAuthority Authority,
        int? Version,
        long LifecycleEpoch,
        long ReservationToken,
        bool HasPendingBuild)
    {
        public string Uri => Document.Uri;
    }

    private sealed record DocumentAnalysisReservation(
        VbaIdentifiedDocument Document,
        WorkspaceDocumentAuthority Authority,
        int? Version,
        long LifecycleEpoch,
        long ReservationToken,
        VbaDocumentAnalysis? PreviousAnalysis)
    {
        public string Uri => Document.Uri;
    }

}
