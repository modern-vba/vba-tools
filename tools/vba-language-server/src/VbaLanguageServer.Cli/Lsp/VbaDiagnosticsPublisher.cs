using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

internal interface IVbaDiagnosticsPublicationObserver
{
    void AfterRevisionReserved(string uri, long revision);

    void AfterProjectValidationAuthorityResolved(
        VbaProjectAuthorityIdentity authority)
    {
    }

    void AfterProjectValidationRevisionReserved(
        VbaProjectAuthorityIdentity authority,
        long revision)
    {
    }

    void AfterProjectValidationRoutingAcquired(
        VbaProjectAuthorityIdentity authority)
    {
    }

    void AfterDocumentLocalDiagnosticsSnapshotCaptured(
        string uri,
        int? clientVersion)
    {
    }

    void BeforeProjectDiagnosticsTransportWrite(
        VbaProjectAuthorityIdentity authority,
        string uri,
        long revision)
    {
    }

    void AfterProjectDiagnosticsTransportWrite(
        VbaProjectAuthorityIdentity authority,
        string uri,
        long revision,
        int? clientVersion)
    {
    }
}

internal sealed class NullVbaDiagnosticsPublicationObserver
    : IVbaDiagnosticsPublicationObserver
{
    public static NullVbaDiagnosticsPublicationObserver Instance { get; } = new();

    private NullVbaDiagnosticsPublicationObserver()
    {
    }

    public void AfterRevisionReserved(string uri, long revision)
    {
    }
}

/// <summary>
/// Publishes document diagnostics to the LSP transport.
/// </summary>
internal sealed class VbaDiagnosticsPublisher
    : IVbaProjectDiskReconciliationDiagnostics,
      IVbaProjectValidationLifecycleSink
{
    private readonly object gate = new();
    private readonly object enqueueGate = new();
    private readonly Dictionary<VbaDocumentIdentity, long>
        latestPublishRevisions = new();
    private readonly Dictionary<VbaDocumentIdentity, long>
        terminalPublishRevisions = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        DocumentLocalDiagnosticsRevision> documentLocalDiagnosticsRevisions =
            new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        ProjectValidationRevision> projectValidationRevisions = new();
    private readonly Dictionary<VbaProjectAuthorityIdentity, string>
        projectValidationActiveUris = new();
    private readonly Dictionary<
        VbaDocumentIdentity,
        ProjectValidationCaptureActivity> projectValidationCaptures = new();
    private readonly HashSet<ProjectValidationRoutingAttempt>
        projectValidationRoutingAttempts = [];
    private readonly Dictionary<VbaProjectAuthorityIdentity, long>
        projectValidationRetirementFences = new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        IReadOnlySet<VbaDocumentIdentity>> projectValidationDocuments = new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        HashSet<ProjectValidationRevision>> projectValidationInFlight = new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        VbaProjectValidationLifecycleLease>
            projectValidationLifecycleLeases = new();
    private readonly Dictionary<
        VbaProjectAuthorityIdentity,
        ProjectValidationRoutingGeneration>
            projectValidationRoutingGenerations = new();
    private readonly LspMessageTransport transport;
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaDiagnosticsPublicationObserver publicationObserver;
    private readonly VbaLspClientCapabilityState clientCapabilities;
    private readonly CancellationTokenSource publisherLifetimeCancellation =
        new();
    private VbaInteractiveWorkScheduler? scheduler;
    private VbaLatestOnlyBackgroundMailbox? projectValidationMailbox;
    private VbaLatestOnlyBackgroundMailbox? publicationMailbox;
    private bool diskSourceDiagnosticsAttached;
    private bool stopping;
    private long nextProjectValidationRevision;
    private long nextProjectValidationRoutingAttempt;

    /// <summary>
    /// Creates a diagnostics publisher.
    /// </summary>
    /// <param name="transport">The transport used to publish diagnostics.</param>
    /// <param name="workspace">The workspace that owns parsed syntax trees.</param>
    public VbaDiagnosticsPublisher(
        LspMessageTransport transport,
        VbaLanguageWorkspace workspace,
        IVbaDiagnosticsPublicationObserver? publicationObserver = null,
        VbaLspClientCapabilityState? clientCapabilities = null)
    {
        this.transport = transport;
        this.workspace = workspace;
        this.publicationObserver = publicationObserver
            ?? NullVbaDiagnosticsPublicationObserver.Instance;
        this.clientCapabilities = clientCapabilities ?? new VbaLspClientCapabilityState();
    }

    internal int RetainedRevisionStateCount
    {
        get
        {
            lock (gate)
            {
                return latestPublishRevisions.Keys
                    .Concat(terminalPublishRevisions.Keys)
                    .Distinct()
                    .Count();
            }
        }
    }

    internal int RetainedProjectValidationStateCount
    {
        get
        {
            lock (gate)
            {
                return projectValidationRevisions.Count;
            }
        }
    }

    internal int RetainedDocumentLocalDiagnosticsStateCount
    {
        get
        {
            lock (gate)
            {
                return documentLocalDiagnosticsRevisions.Count;
            }
        }
    }

    internal int RetainedProjectValidationActivityCount
    {
        get
        {
            lock (gate)
            {
                return projectValidationCaptures.Count
                    + projectValidationInFlight.Values.Sum(
                        revisions => revisions.Count);
            }
        }
    }

    internal int RetainedProjectValidationLifecycleStateCount
    {
        get
        {
            lock (gate)
            {
                return projectValidationLifecycleLeases.Count;
            }
        }
    }

    internal int RetainedProjectValidationRoutingStateCount
    {
        get
        {
            lock (gate)
            {
                return projectValidationRoutingGenerations.Count
                    + projectValidationRoutingAttempts.Count
                    + projectValidationRetirementFences.Count;
            }
        }
    }

    /// <summary>
    /// Attaches the runtime-owned bounded scheduler before document work is admitted.
    /// </summary>
    public void AttachScheduler(VbaInteractiveWorkScheduler interactiveScheduler)
    {
        ArgumentNullException.ThrowIfNull(interactiveScheduler);
        var attachDiskSourceDiagnostics = false;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (scheduler is not null && !ReferenceEquals(scheduler, interactiveScheduler))
                {
                    throw new InvalidOperationException(
                        "The diagnostics publisher is already attached to another scheduler.");
                }

                if (publicationMailbox is not null)
                {
                    return;
                }

                scheduler = interactiveScheduler;
                projectValidationMailbox = new VbaLatestOnlyBackgroundMailbox(
                    interactiveScheduler,
                    VbaInteractiveBackgroundWorkType.ProjectValidation,
                    StringComparer.OrdinalIgnoreCase,
                    projectAuthorityStateChanged:
                        CompleteProjectValidationAuthorityState);
                publicationMailbox = new VbaLatestOnlyBackgroundMailbox(
                    interactiveScheduler,
                    VbaInteractiveBackgroundWorkType.DiagnosticsPublication,
                    StringComparer.OrdinalIgnoreCase,
                    authorityStateChanged: null,
                    documentAuthorityStateChanged:
                        CompleteTerminalRevisionState);
                diskSourceDiagnosticsAttached = true;
                attachDiskSourceDiagnostics = true;
            }
        }

        if (attachDiskSourceDiagnostics)
        {
            workspace.DiskSourceDiagnosticsChanged +=
                OnDiskSourceDiagnosticsChanged;
        }
    }

    /// <summary>
    /// Publishes diagnostics for a tracked document, or clears diagnostics when it is no longer parsed.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishTrackedDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        var snapshot = workspace.GetDocumentDiagnosticsSnapshot(uri, cancellationToken);
        if (snapshot is null)
        {
            if (EnqueueDiskSourceFailure(uri, cancellationToken))
            {
                return Task.CompletedTask;
            }

            return PublishEmptyDiagnosticsAsync(uri, cancellationToken);
        }

        EnqueuePublication(
            uri,
            () => workspace.IsLatestDiagnosticsSnapshot(
                snapshot.Analysis.Uri,
                snapshot.ClientVersion,
                snapshot.LifecycleEpoch,
                snapshot.ReservationToken),
            cancellationToken => PublishDiagnosticsAsync(snapshot, cancellationToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes the latest document and project diagnostics for every tracked
    /// source in the project containing the active URI.
    /// </summary>
    public Task PublishProjectDiagnosticsAsync(
        string activeUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captureScope = BeginProjectValidationCapture(activeUri);
        if (captureScope is null)
        {
            return Task.CompletedTask;
        }

        ProjectValidationRoutingGeneration? routingGeneration = null;
        using var captureCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                cancellationToken,
                publisherLifetimeCancellation.Token);
        try
        {
            if (!workspace.TryCaptureProjectDiagnosticsAuthority(
                    activeUri,
                    captureCancellation.Token,
                    out var authority))
            {
                return Task.CompletedTask;
            }

            publicationObserver.AfterProjectValidationAuthorityResolved(
                authority);
            if (!BindProjectValidationRoutingAttempt(
                    captureScope.RoutingAttempt,
                    authority))
            {
                return Task.CompletedTask;
            }

            routingGeneration = AcquireProjectValidationRouting(
                authority,
                activeUri,
                captureScope.RoutingAttempt);
            if (routingGeneration is null)
            {
                return Task.CompletedTask;
            }

            publicationObserver.AfterProjectValidationRoutingAcquired(
                authority);
            return PublishProjectDiagnosticsCore(
                activeUri,
                authority,
                routingGeneration,
                expectedLifecycleLease: null,
                cancellationToken: captureCancellation.Token);
        }
        catch (OperationCanceledException)
            when (publisherLifetimeCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }
        finally
        {
            if (routingGeneration is not null)
            {
                ReleaseProjectValidationRoutingCapture(
                    routingGeneration);
            }

            CompleteProjectValidationCapture(
                IdentifyDocument(activeUri),
                captureScope.Activity,
                captureScope.RoutingAttempt);
        }
    }

    private Task PublishProjectDiagnosticsCore(
        string activeUri,
        VbaProjectAuthorityIdentity expectedAuthority,
        ProjectValidationRoutingGeneration routingGeneration,
        VbaProjectValidationLifecycleLease? expectedLifecycleLease,
        CancellationToken cancellationToken)
    {
        if (expectedLifecycleLease is not null
            && !IsCurrentProjectValidationLifecycle(
                expectedLifecycleLease))
        {
            return Task.CompletedTask;
        }

        var diskSourceFailureEnqueued = EnqueueDiskSourceFailure(
            activeUri,
            cancellationToken);
        if (!diskSourceFailureEnqueued)
        {
            PublishDocumentLocalDiagnostics(
                activeUri,
                expectedAuthority,
                routingGeneration,
                expectedLifecycleLease,
                cancellationToken);
        }

        var capture = workspace.CaptureProjectDiagnostics(
            activeUri,
            cancellationToken);
        if (capture is null
            || capture.Authority != expectedAuthority
            || capture.DocumentSnapshots.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!workspace.IsCurrentProjectDiagnosticsCapture(capture))
        {
            return Task.CompletedTask;
        }

        VbaLatestOnlyBackgroundMailbox mailbox;
        ProjectValidationRevision? superseded;
        ProjectValidationRevision revision;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (stopping
                    || !IsCurrentProjectValidationRoutingCore(
                        expectedAuthority,
                        routingGeneration)
                    || (expectedLifecycleLease is not null
                        && !IsCurrentProjectValidationLifecycleCore(
                            expectedLifecycleLease)))
                {
                    return Task.CompletedTask;
                }

                mailbox = projectValidationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before validation starts.");
                projectValidationRevisions.TryGetValue(
                    capture.Authority,
                    out superseded);
                var documents = capture.ProjectSnapshot
                    .SourceDocuments.Keys
                    .Select(IdentifyDocument)
                    .ToHashSet();
                revision = new ProjectValidationRevision(
                    capture.Authority,
                    ++nextProjectValidationRevision,
                    documents,
                    routingGeneration,
                    expectedLifecycleLease);
                projectValidationRevisions[capture.Authority] = revision;
                projectValidationActiveUris[capture.Authority] = activeUri;
                projectValidationDocuments[capture.Authority] = documents;
                routingGeneration.SetRouting(documents);
                if (!projectValidationInFlight.TryGetValue(
                        capture.Authority,
                        out var inFlight))
                {
                    inFlight = [];
                    projectValidationInFlight.Add(
                        capture.Authority,
                        inFlight);
                }

                inFlight.Add(revision);
            }

            mailbox.Post(
                capture.Authority,
                async schedulerCancellationToken =>
                {
                    await revision.ReservationObserved.Task
                        .ConfigureAwait(false);
                    if (!IsCurrentProjectValidationRevision(revision))
                    {
                        return;
                    }

                    await ExecuteProjectValidationAsync(
                            capture,
                            revision,
                            schedulerCancellationToken)
                        .ConfigureAwait(false);
                },
                () => CompleteProjectValidation(revision));
        }

        superseded?.Cancel();
        try
        {
            publicationObserver.AfterProjectValidationRevisionReserved(
                capture.Authority,
                revision.Revision);
        }
        finally
        {
            revision.ReservationObserved.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public void RefreshProjectDiagnostics(
        VbaProjectValidationLifecycleLease lease)
    {
        var authority = lease.Authority;
        string? activeUri;
        ProjectValidationRoutingGeneration? routingGeneration;
        ProjectValidationCaptureActivity? captureActivity;
        VbaDocumentIdentity activeDocument;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (stopping
                    || !IsCurrentProjectValidationLifecycleCore(lease)
                    || !projectValidationActiveUris.TryGetValue(
                        authority,
                        out activeUri)
                    || !projectValidationRoutingGenerations.TryGetValue(
                        authority,
                        out routingGeneration)
                    || !routingGeneration.IsValid)
                {
                    return;
                }

                activeDocument = IdentifyDocument(activeUri);
                routingGeneration.AcquireCapture(
                    activeUri,
                    activeDocument);
                captureActivity = BeginProjectValidationCaptureCore(
                    activeDocument);
            }
        }

        using var captureCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                publisherLifetimeCancellation.Token,
                lease.RevocationToken);
        try
        {
            publicationObserver.AfterProjectValidationRoutingAcquired(
                authority);
            _ = PublishProjectDiagnosticsCore(
                activeUri,
                authority,
                routingGeneration,
                lease,
                captureCancellation.Token);
        }
        catch (OperationCanceledException)
            when (captureCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            ReleaseProjectValidationRoutingCapture(routingGeneration);
            CompleteProjectValidationCapture(
                activeDocument,
                captureActivity);
        }
    }

    public void ActivateProjectDiagnostics(
        VbaProjectValidationLifecycleLease lease)
    {
        if (lease.IsRevoked)
        {
            return;
        }

        lock (enqueueGate)
        {
            lock (gate)
            {
                if (!stopping && !lease.IsRevoked)
                {
                    projectValidationLifecycleLeases[lease.Authority] =
                        lease;
                }
            }
        }
    }

    public void InvalidateProjectDiagnostics(
        VbaProjectValidationLifecycleLease lease)
        => CancelProjectValidation(
            lease.Authority,
            retireRouting: false,
            expectedLifecycleLease: lease,
            retireLifecycleLease: false);

    public void RetireProjectDiagnostics(
        VbaProjectValidationLifecycleLease lease)
        => CancelProjectValidation(
            lease.Authority,
            retireRouting: true,
            expectedLifecycleLease: lease,
            retireLifecycleLease: true);

    internal void CancelProjectValidationsForDocuments(
        IEnumerable<string> uris)
        => CancelProjectValidationsForDocuments(
            uris,
            retireRouting: true);

    internal void InvalidateProjectValidationsForDocuments(
        IEnumerable<string> uris)
        => CancelProjectValidationsForDocuments(
            uris,
            retireRouting: false);

    private void CancelProjectValidationsForDocuments(
        IEnumerable<string> uris,
        bool retireRouting)
    {
        var identifiedUris = uris
            .Select(uri => VbaProjectIdentityModel.TryIdentifyDocument(
                    uri,
                    out var identity)
                ? (Uri: uri, Identity: (VbaDocumentIdentity?)identity)
                : (Uri: uri, Identity: (VbaDocumentIdentity?)null))
            .Where(item => item.Identity is not null)
            .ToArray();
        var documents = identifiedUris
            .Select(item => item.Identity!.Value)
            .ToHashSet();
        if (documents.Count == 0)
        {
            return;
        }

        VbaProjectAuthorityIdentity[] knownAuthorities;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (retireRouting)
                {
                    foreach (var document in documents)
                    {
                        documentLocalDiagnosticsRevisions.Remove(document);
                    }
                }

                foreach (var routingAttempt in
                    projectValidationRoutingAttempts.Where(
                        attempt => documents.Contains(
                            attempt.ActiveDocument)))
                {
                    routingAttempt.Invalidate();
                }

                knownAuthorities = projectValidationDocuments
                    .Where(pair => pair.Value.Overlaps(documents))
                    .Select(pair => pair.Key)
                    .Concat(
                        projectValidationInFlight
                        .Where(pair => pair.Value.Any(
                            revision => revision.Documents.Overlaps(
                                documents)))
                        .Select(pair => pair.Key))
                    .Concat(
                        projectValidationRoutingGenerations
                        .Where(pair => pair.Value.Documents.Overlaps(documents))
                        .Select(pair => pair.Key))
                    .Concat(
                        projectValidationRoutingAttempts
                        .Where(attempt => attempt.Authority is not null
                            && documents.Contains(attempt.ActiveDocument))
                        .Select(attempt => attempt.Authority!.Value))
                    .Concat(
                        projectValidationActiveUris
                        .Where(pair => VbaProjectIdentityModel.TryIdentifyDocument(
                                pair.Value,
                                out var activeDocument)
                            && documents.Contains(activeDocument))
                        .Select(pair => pair.Key))
                    .Distinct()
                    .ToArray();
            }

            foreach (var authority in knownAuthorities)
            {
                CancelProjectValidation(authority, retireRouting);
            }
        }

        var resolvedAuthorities = identifiedUris
            .Select(item => TryCaptureProjectDiagnosticsAuthority(
                    item.Uri,
                    CancellationToken.None,
                    out var authority)
                ? authority
                : (VbaProjectAuthorityIdentity?)null)
            .Where(authority => authority is not null)
            .Select(authority => authority!.Value)
            .Except(knownAuthorities)
            .ToArray();
        foreach (var authority in resolvedAuthorities)
        {
            CancelProjectValidation(authority, retireRouting);
        }
    }

    private async Task ExecuteProjectValidationAsync(
        VbaProjectDiagnosticsCapture capture,
        ProjectValidationRevision revision,
        CancellationToken schedulerCancellationToken)
    {
        using var linkedCancellation = revision.LifecycleLease is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                schedulerCancellationToken,
                revision.CancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                schedulerCancellationToken,
                revision.CancellationToken,
                revision.LifecycleLease.RevocationToken);
        try
        {
            await Task.Run(
                    () => ComputeAndEnqueueProjectDiagnostics(
                        capture,
                        revision,
                        linkedCancellation.Token),
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
        }
    }

    private bool IsCurrentProjectValidationRevision(
        ProjectValidationRevision revision)
    {
        lock (gate)
        {
            return projectValidationRevisions.TryGetValue(
                    revision.Authority,
                    out var current)
                && ReferenceEquals(current, revision)
                && IsCurrentProjectValidationRoutingCore(
                    revision.Authority,
                    revision.RoutingGeneration)
                && (revision.LifecycleLease is null
                    || IsCurrentProjectValidationLifecycleCore(
                        revision.LifecycleLease));
        }
    }

    private bool IsCurrentProjectValidationLifecycleCore(
        VbaProjectValidationLifecycleLease lease)
        => !lease.IsRevoked
            && projectValidationLifecycleLeases.TryGetValue(
                lease.Authority,
                out var current)
            && ReferenceEquals(current, lease);

    private bool IsCurrentProjectValidationLifecycle(
        VbaProjectValidationLifecycleLease lease)
    {
        lock (gate)
        {
            return IsCurrentProjectValidationLifecycleCore(lease);
        }
    }

    private bool IsCurrentProjectValidationRevision(
        ProjectValidationRevision revision,
        CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && !revision.CancellationToken.IsCancellationRequested
            && IsCurrentProjectValidationRevision(revision);
    }

    private void PublishDocumentLocalDiagnostics(
        string uri,
        VbaProjectAuthorityIdentity expectedAuthority,
        ProjectValidationRoutingGeneration routingGeneration,
        VbaProjectValidationLifecycleLease? expectedLifecycleLease,
        CancellationToken cancellationToken)
    {
        var snapshot = workspace.GetDocumentDiagnosticsSnapshot(
            uri,
            cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        publicationObserver.AfterDocumentLocalDiagnosticsSnapshotCaptured(
            snapshot.Analysis.Uri,
            snapshot.ClientVersion);

        var documentIdentity = IdentifyDocument(uri);
        var revision = new DocumentLocalDiagnosticsRevision(
            snapshot.ClientVersion,
            snapshot.LifecycleEpoch,
            snapshot.ReservationToken,
            snapshot.Analysis.Diagnostics.Diagnostics.Count > 0);
        var hadPreviousRevision = false;
        DocumentLocalDiagnosticsRevision? previousRevision = null;
        lock (gate)
        {
            if (stopping
                || !IsCurrentProjectValidationRoutingCore(
                    expectedAuthority,
                    routingGeneration)
                || (expectedLifecycleLease is not null
                    && !IsCurrentProjectValidationLifecycleCore(
                        expectedLifecycleLease)))
            {
                return;
            }

            hadPreviousRevision = documentLocalDiagnosticsRevisions.TryGetValue(
                documentIdentity,
                out var previous);
            previousRevision = hadPreviousRevision ? previous : null;
            documentLocalDiagnosticsRevisions[documentIdentity] = revision;
        }
        if ((!hadPreviousRevision && !revision.HadDiagnostics)
            || revision == previousRevision)
        {
            return;
        }

        EnqueuePublication(
            uri,
            () => workspace.IsLatestDiagnosticsSnapshot(
                snapshot.Analysis.Uri,
                snapshot.ClientVersion,
                snapshot.LifecycleEpoch,
                snapshot.ReservationToken),
            publicationCancellationToken =>
                PublishDiagnosticsAsync(
                    snapshot,
                    publicationCancellationToken));
    }

    private void ComputeAndEnqueueProjectDiagnostics(
        VbaProjectDiagnosticsCapture capture,
        ProjectValidationRevision revision,
        CancellationToken cancellationToken)
    {
        var snapshots = workspace.BuildProjectDiagnosticsSnapshots(
            capture,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var batchOwnership = snapshots[0];
        EnqueuePublications(
            snapshots
                .Select(snapshot => new DiagnosticsPublication(
                    snapshot.Analysis.Uri,
                    publicationCancellationToken =>
                        IsProjectDiagnosticsPublicationStillCurrent(
                            revision,
                            snapshot,
                            publicationCancellationToken),
                    publicationCancellationToken =>
                        PublishProjectDiagnosticsSnapshotAsync(
                            revision,
                            snapshot,
                            publicationCancellationToken),
                    revision.ReservePublication,
                    () => CompleteProjectValidationPublication(revision)))
                .ToArray(),
            batchCancellationToken =>
                IsCurrentProjectValidationRevision(
                    revision,
                    batchCancellationToken)
                && workspace.AreLatestDiagnosticsSnapshots(
                    batchOwnership.ProjectOwnership,
                    batchOwnership.ProjectSnapshotOwnership,
                    sourceTemplateEvidence: null,
                    cancellationToken: batchCancellationToken),
            cancellationToken);
    }

    private bool IsProjectDiagnosticsPublicationStillCurrent(
        ProjectValidationRevision revision,
        VbaDocumentDiagnosticsSnapshot snapshot,
        CancellationToken schedulerCancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                schedulerCancellationToken,
                revision.CancellationToken);
        try
        {
            return IsCurrentProjectValidationRevision(
                    revision,
                    linkedCancellation.Token)
                && workspace.AreLatestDiagnosticsSnapshots(
                    snapshot.ProjectOwnership,
                    snapshot.ProjectSnapshotOwnership,
                    snapshot.SourceTemplateEvidence,
                    linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (linkedCancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PublishProjectDiagnosticsSnapshotAsync(
        ProjectValidationRevision revision,
        VbaDocumentDiagnosticsSnapshot snapshot,
        CancellationToken schedulerCancellationToken)
    {
        publicationObserver.BeforeProjectDiagnosticsTransportWrite(
            revision.Authority,
            snapshot.Analysis.Uri,
            revision.Revision);
        using var linkedCancellation = revision.LifecycleLease is null
            ? CancellationTokenSource.CreateLinkedTokenSource(
                schedulerCancellationToken,
                revision.CancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                schedulerCancellationToken,
                revision.CancellationToken,
                revision.LifecycleLease.RevocationToken);
        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            await PublishDiagnosticsAsync(
                    snapshot,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            publicationObserver.AfterProjectDiagnosticsTransportWrite(
                revision.Authority,
                snapshot.Analysis.Uri,
                revision.Revision,
                snapshot.ClientVersion);
        }
        catch (OperationCanceledException)
            when (linkedCancellation.IsCancellationRequested)
        {
        }
    }

    private bool EnqueueDiskSourceFailure(
        string uri,
        CancellationToken cancellationToken)
    {
        var failure = workspace.GetDiskSourceFailure(uri, cancellationToken);
        if (failure is null)
        {
            return false;
        }

        EnqueuePublication(
            failure.Uri,
            () => workspace.IsCurrentDiskSourceFailure(failure),
            publicationCancellationToken =>
                PublishDiskSourceFailureAsync(
                    failure,
                    publicationCancellationToken));
        return true;
    }

    private Task PublishDiskSourceFailureAsync(
        VbaProjectDiskSourceFailure failure,
        CancellationToken cancellationToken)
        => transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri = failure.Uri,
                diagnostics = new object[]
                {
                    new
                    {
                        range = new
                        {
                            start = new { line = 0, character = 0 },
                            end = new { line = 0, character = 1 }
                        },
                        severity = 1,
                        code = "invalid-disk-source-encoding",
                        source = "vba-language-server",
                        message = failure.DiagnosticMessage
                    }
                }
            },
            cancellationToken);

    /// <summary>
    /// Clears diagnostics for a document.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishEmptyDiagnosticsAsync(string uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnqueuePublication(
            uri,
            () => true,
            publicationCancellationToken => transport.WriteNotificationAsync(
                "textDocument/publishDiagnostics",
                new
                {
                    uri,
                    diagnostics = Array.Empty<object>()
                },
                publicationCancellationToken));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes or clears the validation diagnostic for a project manifest.
    /// </summary>
    /// <param name="uri">The project manifest URI.</param>
    /// <param name="error">The current validation error, or null to clear it.</param>
    /// <param name="cancellationToken">A cancellation token for transport work.</param>
    public Task PublishManifestValidationDiagnosticAsync(
        string uri,
        VbaProjectManifestException? error,
        CancellationToken cancellationToken)
    {
        object[] diagnostics = error is null
            ? []
            :
            [
                new
                {
                    range = new
                    {
                        start = new { line = 0, character = 0 },
                        end = new { line = 0, character = 1 }
                    },
                    severity = 1,
                    code = "invalid-project-manifest",
                    source = "vba-language-server",
                    message = error.Message
                }
            ];
        cancellationToken.ThrowIfCancellationRequested();
        EnqueuePublication(
            uri,
            () => true,
            publicationCancellationToken => transport.WriteNotificationAsync(
                "textDocument/publishDiagnostics",
                new
                {
                    uri,
                    diagnostics
                },
                publicationCancellationToken));
        return Task.CompletedTask;
    }

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueTrackedDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishTrackedDiagnosticsAsync(uri, cancellationToken);

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueProjectDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishProjectDiagnosticsAsync(uri, cancellationToken);

    void IVbaProjectDiskReconciliationDiagnostics.EnqueueEmptyDiagnostics(
        string uri,
        CancellationToken cancellationToken)
        => _ = PublishEmptyDiagnosticsAsync(uri, cancellationToken);

    /// <summary>
    /// Waits until the latest diagnostics revision for one URI is terminal and
    /// no publication for that URI is pending or active.
    /// </summary>
    internal async Task WaitForIdleAsync(string uri)
    {
        VbaLatestOnlyBackgroundMailbox validationMailbox;
        VbaLatestOnlyBackgroundMailbox publication;
        VbaProjectAuthorityIdentity[] validationAuthorities;
        Task captureIdle;
        var documentIdentity = IdentifyDocument(uri);
        var hasCaptureAuthority = TryCaptureProjectDiagnosticsAuthority(
            uri,
            CancellationToken.None,
            out var captureAuthority);
        lock (enqueueGate)
        {
            lock (gate)
            {
                validationMailbox = projectValidationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before validation starts.");
                publication = publicationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before publication starts.");
                captureIdle = projectValidationCaptures.TryGetValue(
                        documentIdentity,
                        out var captureActivity)
                    ? captureActivity.Completion.Task
                    : Task.CompletedTask;
                if (hasCaptureAuthority)
                {
                    var authorityCaptureTasks =
                        projectValidationRoutingAttempts
                            .Where(attempt => !attempt.IsBound
                                || attempt.Authority == captureAuthority)
                            .Select(attempt => attempt.Completion.Task)
                            .ToList();
                    if (projectValidationRoutingGenerations.TryGetValue(
                            captureAuthority,
                            out var routingGeneration))
                    {
                        authorityCaptureTasks.Add(
                            routingGeneration.CaptureCompletion);
                    }

                    authorityCaptureTasks.Add(captureIdle);
                    captureIdle = Task.WhenAll(authorityCaptureTasks);
                }
            }
        }

        await captureIdle.ConfigureAwait(false);

        lock (enqueueGate)
        {
            lock (gate)
            {
                validationAuthorities = projectValidationDocuments
                    .Where(pair => pair.Value.Contains(documentIdentity))
                    .Select(pair => pair.Key)
                    .Concat(projectValidationInFlight
                        .Where(pair => pair.Value.Any(
                            revision => revision.Documents.Contains(
                                documentIdentity)))
                        .Select(pair => pair.Key))
                    .Concat(projectValidationActiveUris
                        .Where(pair => VbaProjectIdentityModel.TryIdentifyDocument(
                                pair.Value,
                                out var activeDocument)
                            && activeDocument == documentIdentity)
                        .Select(pair => pair.Key))
                    .Distinct()
                    .ToArray();
            }
        }

        await Task.WhenAll(validationAuthorities
                .Select(validationMailbox.WaitForIdleAsync))
            .ConfigureAwait(false);
        await publication.WaitForIdleAsync(documentIdentity)
            .ConfigureAwait(false);
    }

    private bool TryCaptureProjectDiagnosticsAuthority(
        string uri,
        CancellationToken cancellationToken,
        out VbaProjectAuthorityIdentity authority)
    {
        using var linkedCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(
                cancellationToken,
                publisherLifetimeCancellation.Token);
        try
        {
            return workspace.TryCaptureProjectDiagnosticsAuthority(
                uri,
                linkedCancellation.Token,
                out authority);
        }
        catch (OperationCanceledException)
            when (publisherLifetimeCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
        {
            authority = default;
            return false;
        }
    }

    private ProjectValidationCaptureScope? BeginProjectValidationCapture(
        string uri)
    {
        var documentIdentity = IdentifyDocument(uri);
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (stopping)
                {
                    return null;
                }

                var activity = BeginProjectValidationCaptureCore(
                    documentIdentity);
                var routingAttempt = new ProjectValidationRoutingAttempt(
                    ++nextProjectValidationRoutingAttempt,
                    documentIdentity);
                projectValidationRoutingAttempts.Add(routingAttempt);
                return new ProjectValidationCaptureScope(
                    activity,
                    routingAttempt);
            }
        }
    }

    private ProjectValidationCaptureActivity BeginProjectValidationCaptureCore(
        VbaDocumentIdentity documentIdentity)
    {
        if (!projectValidationCaptures.TryGetValue(
                documentIdentity,
                out var activity))
        {
            activity = new ProjectValidationCaptureActivity();
            projectValidationCaptures.Add(documentIdentity, activity);
        }

        activity.Count++;
        return activity;
    }

    private ProjectValidationRoutingGeneration?
        AcquireProjectValidationRouting(
            VbaProjectAuthorityIdentity authority,
            string activeUri,
            ProjectValidationRoutingAttempt routingAttempt)
    {
        var activeDocument = IdentifyDocument(activeUri);
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (stopping
                    || !routingAttempt.IsValid
                    || routingAttempt.Authority != authority)
                {
                    return null;
                }

                if (!projectValidationRoutingGenerations.TryGetValue(
                        authority,
                        out var generation)
                    || !generation.IsValid)
                {
                    generation = new ProjectValidationRoutingGeneration(
                        authority);
                    projectValidationRoutingGenerations[authority] =
                        generation;
                }

                generation.AcquireCapture(activeUri, activeDocument);
                return generation;
            }
        }
    }

    private bool BindProjectValidationRoutingAttempt(
        ProjectValidationRoutingAttempt routingAttempt,
        VbaProjectAuthorityIdentity authority)
    {
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (stopping
                    || !routingAttempt.IsValid
                    || !projectValidationRoutingAttempts.Contains(
                        routingAttempt)
                    || projectValidationRetirementFences.TryGetValue(
                        authority,
                        out var retiredThroughAttempt)
                        && routingAttempt.Sequence <= retiredThroughAttempt)
                {
                    routingAttempt.Invalidate();
                    return false;
                }

                routingAttempt.Bind(authority);
                PruneProjectValidationRetirementFencesCore();
                return true;
            }
        }
    }

    private void ReleaseProjectValidationRoutingCapture(
        ProjectValidationRoutingGeneration generation)
    {
        lock (enqueueGate)
        {
            lock (gate)
            {
                generation.ReleaseCapture();
                if (generation.CaptureCount == 0
                    && !generation.HasRouting
                    && projectValidationRoutingGenerations.TryGetValue(
                        generation.Authority,
                        out var current)
                    && ReferenceEquals(current, generation))
                {
                    projectValidationRoutingGenerations.Remove(
                        generation.Authority);
                }
            }
        }
    }

    private bool IsCurrentProjectValidationRoutingCore(
        VbaProjectAuthorityIdentity authority,
        ProjectValidationRoutingGeneration generation)
        => generation.IsValid
            && projectValidationRoutingGenerations.TryGetValue(
                authority,
                out var current)
            && ReferenceEquals(current, generation);

    private void CompleteProjectValidationCapture(
        VbaDocumentIdentity documentIdentity,
        ProjectValidationCaptureActivity activity,
        ProjectValidationRoutingAttempt? routingAttempt = null)
    {
        var complete = false;
        var completeRoutingAttempt = false;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (routingAttempt is not null)
                {
                    completeRoutingAttempt =
                        projectValidationRoutingAttempts.Remove(
                            routingAttempt);
                    PruneProjectValidationRetirementFencesCore();
                }

                activity.Count--;
                if (activity.Count == 0
                    && projectValidationCaptures.TryGetValue(
                        documentIdentity,
                        out var current)
                    && ReferenceEquals(current, activity))
                {
                    projectValidationCaptures.Remove(documentIdentity);
                    complete = true;
                }
            }
        }

        if (complete)
        {
            activity.Completion.TrySetResult();
        }

        if (completeRoutingAttempt)
        {
            routingAttempt!.Completion.TrySetResult();
        }
    }

    /// <summary>
    /// Stops pending diagnostics before the runtime-owned scheduler stops.
    /// </summary>
    internal void Stop()
    {
        var detachDiskSourceDiagnostics = false;
        ProjectValidationRevision[] validationRevisions;
        Task? lifetimeCancellation = null;
        lock (enqueueGate)
        {
            VbaLatestOnlyBackgroundMailbox? validationMailbox;
            VbaLatestOnlyBackgroundMailbox? publication;
            lock (gate)
            {
                if (stopping)
                {
                    return;
                }

                stopping = true;
                validationMailbox = projectValidationMailbox;
                publication = publicationMailbox;
                validationRevisions = projectValidationRevisions
                    .Values
                    .ToArray();
                projectValidationRevisions.Clear();
                projectValidationActiveUris.Clear();
                projectValidationDocuments.Clear();
                projectValidationLifecycleLeases.Clear();
                foreach (var routingGeneration in
                    projectValidationRoutingGenerations.Values)
                {
                    routingGeneration.Invalidate();
                }

                projectValidationRoutingGenerations.Clear();
                foreach (var routingAttempt in
                    projectValidationRoutingAttempts)
                {
                    routingAttempt.Invalidate();
                }

                projectValidationRetirementFences.Clear();
                documentLocalDiagnosticsRevisions.Clear();
                if (diskSourceDiagnosticsAttached)
                {
                    diskSourceDiagnosticsAttached = false;
                    detachDiskSourceDiagnostics = true;
                }
            }

            try
            {
                lifetimeCancellation =
                    publisherLifetimeCancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }

            foreach (var revision in validationRevisions)
            {
                revision.Cancel();
            }

            validationMailbox?.Stop();
            publication?.Stop();
        }

        if (lifetimeCancellation is not null)
        {
            _ = ObservePublisherLifetimeCancellationAsync(
                lifetimeCancellation);
        }

        if (detachDiskSourceDiagnostics)
        {
            workspace.DiskSourceDiagnosticsChanged -=
                OnDiskSourceDiagnosticsChanged;
        }
    }

    private static async Task ObservePublisherLifetimeCancellationAsync(
        Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void OnDiskSourceDiagnosticsChanged(string uri)
    {
        if (workspace.GetDiskSourceFailure(uri, CancellationToken.None) is null)
        {
            _ = PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None);
        }

        _ = PublishProjectDiagnosticsAsync(
            uri,
            CancellationToken.None);
    }

    private Task PublishDiagnosticsAsync(
        VbaDocumentDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var analysis = snapshot.Analysis;
        var diagnostics = analysis.Diagnostics with
        {
            ProjectValidationDiagnostics = snapshot.ProjectValidationDiagnostics
        };
        object parameters = snapshot.ClientVersion is { } version
            ? new
            {
                uri = analysis.Uri,
                version,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    diagnostics.Diagnostics,
                    clientCapabilities.Snapshot.DiagnosticRelatedInformation)
            }
            : new
            {
                uri = analysis.Uri,
                diagnostics = VbaLspFeatureProjection.CreateDiagnostics(
                    diagnostics.Diagnostics,
                    clientCapabilities.Snapshot.DiagnosticRelatedInformation)
            };
        return transport.WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            parameters,
            cancellationToken);
    }

    private bool IsLatestPublishRevision(
        VbaDocumentIdentity documentIdentity,
        long revision)
    {
        lock (gate)
        {
            return latestPublishRevisions.TryGetValue(
                    documentIdentity,
                    out var latest)
                && latest == revision;
        }
    }

    private void EnqueuePublication(
        string uri,
        Func<bool> isStillPublishable,
        Func<CancellationToken, Task> publish)
        => EnqueuePublications(
        [
            new DiagnosticsPublication(
                uri,
                _ => isStillPublishable(),
                publish,
                OnReserved: null,
                OnTerminal: null)
        ]);

    private void EnqueuePublications(
        IReadOnlyList<DiagnosticsPublication> publications,
        Func<CancellationToken, bool>? isBatchStillPublishable = null,
        CancellationToken cancellationToken = default)
    {
        var reservations = new List<DiagnosticsPublicationReservation>(
            publications.Count);
        lock (enqueueGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isBatchStillPublishable is not null
                && !isBatchStillPublishable(cancellationToken))
            {
                return;
            }

            VbaLatestOnlyBackgroundMailbox mailbox;
            lock (gate)
            {
                mailbox = publicationMailbox
                    ?? throw new InvalidOperationException(
                        "The diagnostics scheduler must be attached before publication starts.");
                foreach (var publication in publications)
                {
                    var documentIdentity =
                        IdentifyDocument(publication.Uri);
                    latestPublishRevisions.TryGetValue(
                        documentIdentity,
                        out var previous);
                    var revision = previous + 1;
                    latestPublishRevisions[documentIdentity] = revision;
                    publication.OnReserved?.Invoke();
                    reservations.Add(new DiagnosticsPublicationReservation(
                        publication,
                        documentIdentity,
                        revision,
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously)));
                }
            }

            foreach (var reservation in reservations)
            {
                mailbox.Post(
                    reservation.DocumentIdentity,
                    async cancellationToken =>
                    {
                        await reservation.RevisionObserved.Task.ConfigureAwait(false);
                        if (!IsLatestPublishRevision(
                                reservation.DocumentIdentity,
                                reservation.Revision)
                            || !reservation.Publication.IsStillPublishable(
                                cancellationToken))
                        {
                            return;
                        }

                        try
                        {
                            await reservation.Publication.Publish(cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                    },
                    () =>
                    {
                        MarkPublishRevisionTerminal(
                            reservation.DocumentIdentity,
                            reservation.Revision);
                        reservation.Publication.OnTerminal?.Invoke();
                    });
            }
        }

        try
        {
            foreach (var reservation in reservations)
            {
                publicationObserver.AfterRevisionReserved(
                    reservation.Publication.Uri,
                    reservation.Revision);
            }
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                reservation.RevisionObserved.TrySetResult();
            }
        }
    }

    private sealed record DiagnosticsPublication(
        string Uri,
        Func<CancellationToken, bool> IsStillPublishable,
        Func<CancellationToken, Task> Publish,
        Action? OnReserved,
        Action? OnTerminal);

    private sealed record DiagnosticsPublicationReservation(
        DiagnosticsPublication Publication,
        VbaDocumentIdentity DocumentIdentity,
        long Revision,
        TaskCompletionSource RevisionObserved);

    private sealed record ProjectValidationCaptureScope(
        ProjectValidationCaptureActivity Activity,
        ProjectValidationRoutingAttempt RoutingAttempt);

    private sealed class ProjectValidationCaptureActivity
    {
        public int Count { get; set; }

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProjectValidationRoutingAttempt
    {
        public ProjectValidationRoutingAttempt(
            long sequence,
            VbaDocumentIdentity activeDocument)
        {
            Sequence = sequence;
            ActiveDocument = activeDocument;
        }

        public long Sequence { get; }

        public VbaDocumentIdentity ActiveDocument { get; }

        public VbaProjectAuthorityIdentity? Authority { get; private set; }

        public bool IsBound => Authority is not null;

        public bool IsValid { get; private set; } = true;

        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Bind(VbaProjectAuthorityIdentity authority)
            => Authority = authority;

        public void Invalidate()
            => IsValid = false;
    }

    private sealed class ProjectValidationRoutingGeneration
    {
        private readonly HashSet<VbaDocumentIdentity> documents = [];
        private TaskCompletionSource? captureCompletion;

        public ProjectValidationRoutingGeneration(
            VbaProjectAuthorityIdentity authority)
        {
            Authority = authority;
        }

        public VbaProjectAuthorityIdentity Authority { get; }

        public bool IsValid { get; private set; } = true;

        public bool HasRouting { get; private set; }

        public int CaptureCount { get; private set; }

        public string? ActiveUri { get; private set; }

        public IReadOnlySet<VbaDocumentIdentity> Documents => documents;

        public Task CaptureCompletion => CaptureCount == 0
            ? Task.CompletedTask
            : captureCompletion!.Task;

        public void AcquireCapture(
            string activeUri,
            VbaDocumentIdentity activeDocument)
        {
            if (CaptureCount == 0)
            {
                captureCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            CaptureCount++;
            ActiveUri = activeUri;
            documents.Add(activeDocument);
        }

        public void ReleaseCapture()
        {
            CaptureCount--;
            if (CaptureCount == 0)
            {
                captureCompletion!.TrySetResult();
            }
        }

        public void SetRouting(
            IReadOnlySet<VbaDocumentIdentity> routedDocuments)
        {
            HasRouting = true;
            documents.UnionWith(routedDocuments);
        }

        public void PreserveRouting(
            string activeUri,
            IEnumerable<VbaDocumentIdentity> routedDocuments)
        {
            ActiveUri = activeUri;
            HasRouting = true;
            documents.UnionWith(routedDocuments);
        }

        public void Invalidate()
            => IsValid = false;
    }

    private readonly record struct DocumentLocalDiagnosticsRevision(
        int? ClientVersion,
        long LifecycleEpoch,
        long ReservationToken,
        bool HadDiagnostics);

    private void MarkPublishRevisionTerminal(
        VbaDocumentIdentity documentIdentity,
        long revision)
    {
        lock (gate)
        {
            terminalPublishRevisions.TryGetValue(
                documentIdentity,
                out var previousTerminalRevision);
            if (revision > previousTerminalRevision)
            {
                terminalPublishRevisions[documentIdentity] = revision;
            }
        }
    }

    private void CompleteTerminalRevisionState(
        VbaDocumentIdentity documentIdentity)
    {
        VbaLatestOnlyBackgroundMailbox? mailbox;
        lock (gate)
        {
            mailbox = publicationMailbox;
        }

        if (mailbox is null || !mailbox.IsIdle(documentIdentity))
        {
            return;
        }

        lock (gate)
        {
            if (!mailbox.IsIdle(documentIdentity))
            {
                return;
            }

            latestPublishRevisions.TryGetValue(
                documentIdentity,
                out var latestRevision);
            terminalPublishRevisions.TryGetValue(
                documentIdentity,
                out var terminalRevision);
            if (terminalRevision < latestRevision)
            {
                return;
            }

            latestPublishRevisions.Remove(documentIdentity);
            terminalPublishRevisions.Remove(documentIdentity);
        }
    }

    private static VbaDocumentIdentity IdentifyDocument(string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? identity
            : throw new InvalidOperationException(
                "A diagnostics publication has no document identity.");

    private void CancelProjectValidation(
        VbaProjectAuthorityIdentity authority,
        bool retireRouting,
        VbaProjectValidationLifecycleLease? expectedLifecycleLease = null,
        bool retireLifecycleLease = false)
    {
        ProjectValidationRevision? revision;
        VbaLatestOnlyBackgroundMailbox? mailbox;
        lock (enqueueGate)
        {
            lock (gate)
            {
                if (expectedLifecycleLease is not null)
                {
                    if (!projectValidationLifecycleLeases.TryGetValue(
                            authority,
                            out var currentLease)
                        || !ReferenceEquals(
                            currentLease,
                            expectedLifecycleLease)
                        || (!retireLifecycleLease
                            && expectedLifecycleLease.IsRevoked))
                    {
                        return;
                    }

                    if (retireLifecycleLease)
                    {
                        projectValidationLifecycleLeases.Remove(authority);
                    }
                }

                FenceProjectValidationRoutingAttemptsCore(authority);
                projectValidationRevisions.Remove(authority, out revision);
                projectValidationRoutingGenerations.Remove(
                    authority,
                    out var invalidatedGeneration);
                invalidatedGeneration?.Invalidate();
                if (retireRouting)
                {
                    projectValidationActiveUris.Remove(authority);
                    projectValidationDocuments.Remove(authority);
                }
                else
                {
                    var activeUri = projectValidationActiveUris.TryGetValue(
                            authority,
                            out var routedActiveUri)
                        ? routedActiveUri
                        : invalidatedGeneration?.ActiveUri;
                    if (activeUri is not null)
                    {
                        var replacementGeneration =
                            new ProjectValidationRoutingGeneration(authority);
                        IEnumerable<VbaDocumentIdentity> routedDocuments;
                        if (projectValidationDocuments.TryGetValue(
                                authority,
                                out var preservedDocuments))
                        {
                            routedDocuments = preservedDocuments;
                        }
                        else if (invalidatedGeneration is not null)
                        {
                            routedDocuments = invalidatedGeneration.Documents;
                        }
                        else
                        {
                            routedDocuments = Array.Empty<VbaDocumentIdentity>();
                        }

                        replacementGeneration.PreserveRouting(
                            activeUri,
                            routedDocuments);
                        projectValidationActiveUris[authority] = activeUri;
                        projectValidationDocuments[authority] =
                            routedDocuments.ToHashSet();
                        projectValidationRoutingGenerations[authority] =
                            replacementGeneration;
                    }
                }

                mailbox = projectValidationMailbox;
            }

            revision?.Cancel();
            mailbox?.Discard(authority);
        }
    }

    private void FenceProjectValidationRoutingAttemptsCore(
        VbaProjectAuthorityIdentity authority)
    {
        foreach (var routingAttempt in projectValidationRoutingAttempts
            .Where(attempt => attempt.Authority == authority))
        {
            routingAttempt.Invalidate();
        }

        var retirementCutoff = nextProjectValidationRoutingAttempt;
        if (!projectValidationRoutingAttempts.Any(
                attempt => !attempt.IsBound
                    && attempt.Sequence <= retirementCutoff))
        {
            return;
        }

        if (!projectValidationRetirementFences.TryGetValue(
                authority,
                out var existingCutoff)
            || retirementCutoff > existingCutoff)
        {
            projectValidationRetirementFences[authority] =
                retirementCutoff;
        }
    }

    private void PruneProjectValidationRetirementFencesCore()
    {
        foreach (var authority in projectValidationRetirementFences
            .Where(pair => !projectValidationRoutingAttempts.Any(
                attempt => !attempt.IsBound
                    && attempt.Sequence <= pair.Value))
            .Select(pair => pair.Key)
            .ToArray())
        {
            projectValidationRetirementFences.Remove(authority);
        }
    }

    private void CompleteProjectValidation(
        ProjectValidationRevision revision)
    {
        var releaseRevision = revision.MarkValidationTerminal();
        lock (gate)
        {
            if (projectValidationInFlight.TryGetValue(
                    revision.Authority,
                    out var inFlight))
            {
                var mailboxIsIdle = projectValidationMailbox is null
                    || projectValidationMailbox.IsIdle(revision.Authority);
                var routingRemains = projectValidationDocuments.ContainsKey(
                    revision.Authority);
                if (routingRemains || mailboxIsIdle || inFlight.Count > 1)
                {
                    inFlight.Remove(revision);
                    if (inFlight.Count == 0)
                    {
                        projectValidationInFlight.Remove(revision.Authority);
                    }
                }
            }

            if (releaseRevision
                && projectValidationRevisions.TryGetValue(
                    revision.Authority,
                    out var current)
                && ReferenceEquals(current, revision))
            {
                projectValidationRevisions.Remove(revision.Authority);
            }
        }

        if (releaseRevision)
        {
            revision.Dispose();
        }
    }

    private void CompleteProjectValidationAuthorityState(
        VbaProjectAuthorityIdentity authority)
    {
        VbaLatestOnlyBackgroundMailbox? mailbox;
        lock (gate)
        {
            mailbox = projectValidationMailbox;
        }

        if (mailbox is null || !mailbox.IsIdle(authority))
        {
            return;
        }

        lock (gate)
        {
            if (mailbox.IsIdle(authority))
            {
                if (projectValidationInFlight.TryGetValue(
                        authority,
                        out var inFlight))
                {
                    inFlight.RemoveWhere(
                        revision => revision.IsValidationTerminal);
                    if (inFlight.Count == 0)
                    {
                        projectValidationInFlight.Remove(authority);
                    }
                }
            }
        }
    }

    private void CompleteProjectValidationPublication(
        ProjectValidationRevision revision)
    {
        if (!revision.MarkPublicationTerminal())
        {
            return;
        }

        lock (gate)
        {
            if (projectValidationRevisions.TryGetValue(
                    revision.Authority,
                    out var current)
                && ReferenceEquals(current, revision))
            {
                projectValidationRevisions.Remove(revision.Authority);
            }
        }

        revision.Dispose();
    }

    private sealed class ProjectValidationRevision : IDisposable
    {
        private readonly object cancellationGate = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly CancellationToken cancellationToken;
        private bool cancellationRequested;
        private bool cancellationCompleted;
        private bool disposalRequested;
        private bool cancellationDisposed;
        private bool validationTerminal;
        private bool terminalClaimed;
        private int pendingPublications;

        public ProjectValidationRevision(
            VbaProjectAuthorityIdentity authority,
            long revision,
            IReadOnlySet<VbaDocumentIdentity> documents,
            ProjectValidationRoutingGeneration routingGeneration,
            VbaProjectValidationLifecycleLease? lifecycleLease)
        {
            Authority = authority;
            Revision = revision;
            Documents = documents;
            RoutingGeneration = routingGeneration;
            LifecycleLease = lifecycleLease;
            cancellationToken = cancellation.Token;
        }

        public VbaProjectAuthorityIdentity Authority { get; }

        public long Revision { get; }

        public IReadOnlySet<VbaDocumentIdentity> Documents { get; }

        public ProjectValidationRoutingGeneration RoutingGeneration { get; }

        public VbaProjectValidationLifecycleLease? LifecycleLease { get; }

        public TaskCompletionSource ReservationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken => cancellationToken;

        public bool IsValidationTerminal
        {
            get
            {
                lock (cancellationGate)
                {
                    return validationTerminal;
                }
            }
        }

        public void ReservePublication()
        {
            lock (cancellationGate)
            {
                if (validationTerminal)
                {
                    throw new InvalidOperationException(
                        "A terminal project validation cannot reserve diagnostics publication.");
                }

                pendingPublications++;
            }
        }

        public bool MarkValidationTerminal()
        {
            lock (cancellationGate)
            {
                validationTerminal = true;
                return TryClaimTerminalLocked();
            }
        }

        public bool MarkPublicationTerminal()
        {
            lock (cancellationGate)
            {
                if (pendingPublications <= 0)
                {
                    throw new InvalidOperationException(
                        "A project diagnostics publication has no reservation.");
                }

                pendingPublications--;
                return TryClaimTerminalLocked();
            }
        }

        public void Cancel()
        {
            Task? cancellationTask = null;
            lock (cancellationGate)
            {
                if (cancellationRequested || cancellationDisposed)
                {
                    return;
                }

                cancellationRequested = true;
                try
                {
                    cancellationTask = cancellation.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    cancellationCompleted = true;
                    DisposeCancellationIfReady();
                }
            }

            if (cancellationTask is not null)
            {
                _ = ObserveCancellationAsync(cancellationTask);
            }
        }

        public void Dispose()
        {
            lock (cancellationGate)
            {
                disposalRequested = true;
                DisposeCancellationIfReady();
            }
        }

        private async Task ObserveCancellationAsync(Task cancellationTask)
        {
            try
            {
                await cancellationTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            lock (cancellationGate)
            {
                cancellationCompleted = true;
                DisposeCancellationIfReady();
            }
        }

        private void DisposeCancellationIfReady()
        {
            if (cancellationDisposed
                || !disposalRequested
                || cancellationRequested && !cancellationCompleted)
            {
                return;
            }

            cancellationDisposed = true;
            cancellation.Dispose();
        }

        private bool TryClaimTerminalLocked()
        {
            if (!validationTerminal
                || pendingPublications != 0
                || terminalClaimed)
            {
                return false;
            }

            terminalClaimed = true;
            return true;
        }
    }
}
