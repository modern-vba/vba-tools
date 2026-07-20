using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Enqueues latest-only diagnostics after accepted reconciliation mutations.
/// </summary>
internal interface IVbaProjectDiskReconciliationDiagnostics
{
    void EnqueueTrackedDiagnostics(string uri, CancellationToken cancellationToken);

    void EnqueueEmptyDiagnostics(string uri, CancellationToken cancellationToken);
}

internal interface IVbaProjectDiskReconciliationManifestEvents
{
    void ManifestSelectionChanged(
        string uri,
        string text,
        CancellationToken cancellationToken);

    void ManifestValidationFailed(
        string uri,
        VbaProjectManifestException error,
        CancellationToken cancellationToken);

    void ManifestValidationRecovered(
        string uri,
        CancellationToken cancellationToken);

    void ProjectAuthorityTransferred(
        string sourceUri,
        CancellationToken cancellationToken);

    void ManifestDeleted(string uri, CancellationToken cancellationToken);
}

internal sealed class NullVbaProjectDiskReconciliationManifestEvents
    : IVbaProjectDiskReconciliationManifestEvents
{
    public static NullVbaProjectDiskReconciliationManifestEvents Instance { get; } =
        new();

    private NullVbaProjectDiskReconciliationManifestEvents()
    {
    }

    public void ManifestSelectionChanged(
        string uri,
        string text,
        CancellationToken cancellationToken)
    {
    }

    public void ManifestDeleted(
        string uri,
        CancellationToken cancellationToken)
    {
    }

    public void ManifestValidationFailed(
        string uri,
        VbaProjectManifestException error,
        CancellationToken cancellationToken)
    {
    }

    public void ManifestValidationRecovered(
        string uri,
        CancellationToken cancellationToken)
    {
    }

    public void ProjectAuthorityTransferred(
        string sourceUri,
        CancellationToken cancellationToken)
    {
    }

}

internal sealed class NullVbaProjectDiskReconciliationDiagnostics
    : IVbaProjectDiskReconciliationDiagnostics
{
    public static NullVbaProjectDiskReconciliationDiagnostics Instance { get; } = new();

    private NullVbaProjectDiskReconciliationDiagnostics()
    {
    }

    public void EnqueueTrackedDiagnostics(
        string uri,
        CancellationToken cancellationToken)
    {
    }

    public void EnqueueEmptyDiagnostics(
        string uri,
        CancellationToken cancellationToken)
    {
    }
}

internal interface IVbaProjectDiskReconciliationFailureObserver
{
    void ReconciliationFailed(Exception error);
}

internal interface IVbaProjectDiskReconciliationCommitObserver
{
    void ScopeFenceValidated(
        string authorityKey,
        long manifestBarrierRevision,
        long authorityGeneration);
}

internal sealed class NullVbaProjectDiskReconciliationCommitObserver
    : IVbaProjectDiskReconciliationCommitObserver
{
    public static NullVbaProjectDiskReconciliationCommitObserver Instance
        { get; } = new();

    private NullVbaProjectDiskReconciliationCommitObserver()
    {
    }

    public void ScopeFenceValidated(
        string authorityKey,
        long manifestBarrierRevision,
        long authorityGeneration)
    {
    }
}

internal sealed class NullVbaProjectDiskReconciliationFailureObserver
    : IVbaProjectDiskReconciliationFailureObserver
{
    public static NullVbaProjectDiskReconciliationFailureObserver Instance { get; } =
        new();

    private NullVbaProjectDiskReconciliationFailureObserver()
    {
    }

    public void ReconciliationFailed(Exception error)
    {
    }
}

internal interface IVbaProjectDiskReconciliationRuntimeLifecycle
{
    void AttachScheduler(VbaInteractiveWorkScheduler scheduler);

    Task StopAsync();
}

/// <summary>
/// Reconciles missed project disk changes outside the ordered interactive lane.
/// </summary>
internal sealed class VbaProjectDiskReconciliationCoordinator
    : IVbaProjectDiskReconciliationRuntimeLifecycle,
      IAsyncDisposable
{
    internal static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(30);
    internal const int DefaultMaxConcurrentScans = 2;
    internal const int MaximumImmediateFollowUpPasses = 32;

    private readonly object gate = new();
    private readonly VbaLanguageWorkspace workspace;
    private readonly IVbaProjectDiskReconciliationDiagnostics diagnostics;
    private readonly IVbaProjectDiskReconciliationManifestEvents manifestEvents;
    private readonly IVbaProjectDiskSourceBoundary sourceBoundary;
    private readonly TimeSpan cadence;
    private readonly TimeProvider timeProvider;
    private readonly int maxConcurrentScans;
    private readonly TimeSpan shutdownTimeout;
    private readonly IVbaProjectDiskReconciliationFailureObserver failureObserver;
    private readonly IVbaProjectDiskReconciliationCommitObserver commitObserver;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private VbaInteractiveWorkScheduler? scheduler;
    private Task? activeCycle;
    private Task? activeScan;
    private Task? cadenceLoop;
    private Task? stopTask;
    private bool stopped;

    public VbaProjectDiskReconciliationCoordinator(
        VbaLanguageWorkspace workspace,
        IVbaProjectDiskReconciliationDiagnostics? diagnostics = null,
        IVbaProjectDiskReconciliationManifestEvents? manifestEvents = null,
        IVbaProjectDiskSourceBoundary? sourceBoundary = null,
        TimeSpan? cadence = null,
        TimeProvider? timeProvider = null,
        int maxConcurrentScans = DefaultMaxConcurrentScans,
        TimeSpan? shutdownTimeout = null,
        IVbaProjectDiskReconciliationFailureObserver? failureObserver = null,
        IVbaProjectDiskReconciliationCommitObserver? commitObserver = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maxConcurrentScans,
            1);
        var effectiveShutdownTimeout =
            shutdownTimeout ?? TimeSpan.FromSeconds(2);
        if (effectiveShutdownTimeout != Timeout.InfiniteTimeSpan
            && effectiveShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownTimeout),
                effectiveShutdownTimeout,
                "Shutdown timeout must be positive or infinite.");
        }

        this.workspace = workspace;
        this.diagnostics =
            diagnostics ?? NullVbaProjectDiskReconciliationDiagnostics.Instance;
        this.manifestEvents =
            manifestEvents
            ?? NullVbaProjectDiskReconciliationManifestEvents.Instance;
        this.sourceBoundary =
            sourceBoundary ?? new VbaFileSystemProjectDiskSourceBoundary();
        this.cadence = cadence ?? DefaultCadence;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maxConcurrentScans = maxConcurrentScans;
        this.shutdownTimeout = effectiveShutdownTimeout;
        this.failureObserver =
            failureObserver
            ?? NullVbaProjectDiskReconciliationFailureObserver.Instance;
        this.commitObserver =
            commitObserver
            ?? NullVbaProjectDiskReconciliationCommitObserver.Instance;
    }

    /// <summary>
    /// Attaches the runtime-owned scheduler and starts the internal cadence policy.
    /// </summary>
    public void AttachScheduler(VbaInteractiveWorkScheduler interactiveScheduler)
    {
        ArgumentNullException.ThrowIfNull(interactiveScheduler);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (scheduler is not null
                && !ReferenceEquals(scheduler, interactiveScheduler))
            {
                throw new InvalidOperationException(
                    "Disk reconciliation is already attached to another scheduler.");
            }

            scheduler = interactiveScheduler;
            if (cadence != Timeout.InfiniteTimeSpan && cadenceLoop is null)
            {
                cadenceLoop = RunCadenceAsync(lifetimeCancellation.Token);
            }
        }
    }

    /// <summary>
    /// Runs one deterministic reconciliation cycle.
    /// </summary>
    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        Task cycle;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (scheduler is null)
            {
                throw new InvalidOperationException(
                    "Disk reconciliation must be attached before it is triggered.");
            }

            if (activeCycle is null || activeCycle.IsCompleted)
            {
                var externalCycle = new TaskCompletionSource<Task>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                activeScan = externalCycle.Task.Unwrap();
                activeCycle = TriggerCoreAsync(
                    scheduler,
                    externalCycle,
                    lifetimeCancellation.Token);
            }

            cycle = activeCycle;
        }

        return cancellationToken.CanBeCanceled
            ? cycle.WaitAsync(cancellationToken)
            : cycle;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (gate)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            stopped = true;
            var cancellation = lifetimeCancellation.CancelAsync();
            stopTask = StopCoreAsync(
                cancellation,
                activeCycle,
                activeScan,
                cadenceLoop);
            return stopTask;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
        => new(StopAsync());

    private async Task TriggerCoreAsync(
        VbaInteractiveWorkScheduler interactiveScheduler,
        TaskCompletionSource<Task> externalCycle,
        CancellationToken cancellationToken)
    {
        VbaInteractiveWorkAdmission admission;
        try
        {
            admission = interactiveScheduler.AdmitBackground(
                VbaInteractiveBackgroundWorkType.Reconciliation,
                "workspace",
                schedulerCancellationToken =>
                {
                    if (schedulerCancellationToken.IsCancellationRequested)
                    {
                        externalCycle.TrySetCanceled(
                            schedulerCancellationToken);
                        schedulerCancellationToken.ThrowIfCancellationRequested();
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        externalCycle.TrySetCanceled(cancellationToken);
                        return Task.CompletedTask;
                    }

                    externalCycle.TrySetResult(
                        ScanAndCommitAsync(
                            interactiveScheduler,
                            cancellationToken));
                    return Task.CompletedTask;
                });
        }
        catch (Exception ex)
        {
            externalCycle.TrySetException(ex);
            throw;
        }

        try
        {
            await admission.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            externalCycle.TrySetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            externalCycle.TrySetException(ex);
            throw;
        }

        var scanAndCommit = await externalCycle.Task.ConfigureAwait(false);
        await scanAndCommit.ConfigureAwait(false);
    }

    private async Task StopCoreAsync(
        Task cancellation,
        Task? cycle,
        Task? scan,
        Task? loop)
    {
        var observation = Task.WhenAll(
            IgnoreCancellationAsync(cancellation),
            IgnoreCancellationAsync(cycle),
            IgnoreCancellationAsync(scan, reportFailure: true),
            IgnoreCancellationAsync(loop));
        try
        {
            await observation
                .WaitAsync(shutdownTimeout, timeProvider)
                .ConfigureAwait(false);
            lifetimeCancellation.Dispose();
        }
        catch (TimeoutException)
        {
            _ = DisposeCancellationAfterObservationAsync(observation);
        }
    }

    private async Task ScanAndCommitAsync(
        VbaInteractiveWorkScheduler interactiveScheduler,
        CancellationToken cancellationToken)
    {
        var observedProgressTokens = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        for (var pass = 0;
            pass < MaximumImmediateFollowUpPasses;
            pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progressTokens = await ScanAndCommitPassAsync(
                    interactiveScheduler,
                    cancellationToken)
                .ConfigureAwait(false);
            if (progressTokens.Count == 0)
            {
                return;
            }

            var acceptedManifestProgress = false;
            var madeNewRejectedProgress = false;
            foreach (var progressToken in progressTokens)
            {
                if (progressToken.StartsWith(
                    "manifest:",
                    StringComparison.OrdinalIgnoreCase))
                {
                    acceptedManifestProgress = true;
                    continue;
                }

                madeNewRejectedProgress |=
                    observedProgressTokens.Add(progressToken);
            }

            if (!acceptedManifestProgress
                && !madeNewRejectedProgress)
            {
                return;
            }
        }
    }

    private async Task<IReadOnlySet<string>> ScanAndCommitPassAsync(
        VbaInteractiveWorkScheduler interactiveScheduler,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scopeCapture =
            workspace.CaptureDiskReconciliationScopes();
        using var cancellationRegistration =
            cancellationToken.Register(scopeCapture.Dispose);
        var scopes = scopeCapture.Scopes;
        if (scopes.Count == 0)
        {
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        var scans = await ScanScopesAsync(scopes, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var changes = CreateChanges(scans);
        if (changes.Count == 0)
        {
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        var progressTokens = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var commit = await interactiveScheduler.AdmitRequiredMutationAsync(
                "vba/reconcile/commit",
                commitCancellationToken =>
                {
                    using var linkedCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            commitCancellationToken);
                    var operationCancellationToken =
                        linkedCancellation.Token;
                    var initialOpenAuthorities =
                        new Dictionary<string, VbaProjectResolution?>(
                            StringComparer.OrdinalIgnoreCase);
                    var scopeFenceValidity =
                        new Dictionary<
                            ReconciliationScopeFence,
                            bool>(
                            ReconciliationScopeFenceComparer.Instance);
                    foreach (var change in changes)
                    {
                        operationCancellationToken.ThrowIfCancellationRequested();
                        var scopeFence = new ReconciliationScopeFence(
                            change.AuthorityKey,
                            change.CapturedManifestBarrierRevision,
                            change.CapturedAuthorityGeneration);
                        if (!scopeFenceValidity.TryGetValue(
                                scopeFence,
                                out var scopeIsCurrent))
                        {
                            commitObserver.ScopeFenceValidated(
                                change.AuthorityKey,
                                change.CapturedManifestBarrierRevision,
                                change.CapturedAuthorityGeneration);
                            scopeIsCurrent =
                                workspace.IsReconciliationScopeCurrent(
                                    change.AuthorityKey,
                                    change.CapturedManifestBarrierRevision,
                                    change.CapturedAuthorityGeneration);
                            scopeFenceValidity[scopeFence] = scopeIsCurrent;
                        }

                        if (!scopeIsCurrent)
                        {
                            progressTokens.Add(
                                CreateRejectedChangeProgressToken(
                                    "scope",
                                    change));
                            continue;
                        }

                        var isManifestChange =
                            IsManifestChange(change);
                        var manifestVersionBefore =
                            isManifestChange
                                ? workspace.ManifestWorkspace.Version
                                : 0;
                        var manifestAuthorityMutated = false;
                        switch (change)
                        {
                            case ReplaceDeletedManifestAuthorityChange replace:
                            {
                                CaptureOpenAuthorities(
                                    workspace,
                                    replace,
                                    initialOpenAuthorities,
                                    operationCancellationToken);
                                var replacement = workspace
                                    .ManifestWorkspace
                                    .ReplaceDeletedReconciledManifestAuthority(
                                        replace.DeletedManifests
                                            .Select(deleted => new
                                                VbaProjectManifestReconciliationTarget(
                                                    deleted.Uri,
                                                    deleted.CapturedRevision))
                                            .ToArray(),
                                        replace.ReloadFallbackManifest
                                            ? new VbaProjectManifestReconciliationTarget(
                                                replace.FallbackUri,
                                                replace.CapturedFallbackRevision)
                                            : null,
                                        replace.ReloadFallbackManifest
                                            ? replace.FallbackText
                                            : null);
                                if (!replacement.Accepted)
                                {
                                    progressTokens.Add(
                                        CreateRejectedChangeProgressToken(
                                            "replace",
                                            replace));
                                    break;
                                }

                                foreach (var deleted in
                                    replacement.DeletedManifests)
                                {
                                    if (deleted.Update.Status
                                        == VbaProjectManifestReconciliationStatus
                                            .Applied)
                                    {
                                        progressTokens.Add(
                                            CreateManifestProgressToken(
                                                deleted.Uri));
                                        manifestEvents.ManifestDeleted(
                                            deleted.Uri,
                                            operationCancellationToken);
                                    }
                                }

                                var reloadUpdate =
                                    replacement.ReloadedManifest;
                                if (!replace.FallbackHiddenByOpenOverlay
                                    && reloadUpdate?.Update.Status
                                        == VbaProjectManifestReconciliationStatus
                                            .Applied)
                                {
                                    progressTokens.Add(
                                        CreateManifestProgressToken(
                                            reloadUpdate.Uri));
                                    manifestEvents
                                        .ManifestSelectionChanged(
                                            reloadUpdate.Uri,
                                            replace.FallbackText,
                                            operationCancellationToken);
                                }
                                else if (!replace.FallbackHiddenByOpenOverlay
                                    && reloadUpdate?.Update.Status
                                        == VbaProjectManifestReconciliationStatus
                                            .Invalid
                                    && reloadUpdate.Update.Error is not null)
                                {
                                    if (!reloadUpdate.Update
                                        .RetainedLastKnownGood)
                                    {
                                        progressTokens.Add(
                                            CreateManifestProgressToken(
                                                reloadUpdate.Uri));
                                    }
                                    manifestEvents
                                        .ManifestValidationFailed(
                                            reloadUpdate.Uri,
                                            reloadUpdate.Update.Error,
                                            operationCancellationToken);
                                }

                                workspace.CommitReconciledManifestScope(
                                    replace.AuthorityKey,
                                    replace.ActiveUri,
                                    replace.Resolution,
                                    retainPreviousAuthority: false,
                                    replace.RetainedPreviousSourceUris);
                                manifestAuthorityMutated = true;
                                if (replace.AuthorityTransferred)
                                {
                                    progressTokens.Add(
                                        CreateManifestProgressToken(
                                            replace.Uri));
                                }
                                break;
                            }
                            case ReloadManifestChange reloadManifest:
                            {
                                CaptureOpenAuthorities(
                                    workspace,
                                    reloadManifest,
                                    initialOpenAuthorities,
                                    operationCancellationToken);
                                var update = workspace.ManifestWorkspace
                                    .ReloadReconciledManifest(
                                        reloadManifest.Uri,
                                        reloadManifest.Text,
                                        reloadManifest.CapturedManifestRevision);
                                if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Applied)
                                {
                                    progressTokens.Add(
                                        CreateManifestProgressToken(
                                            reloadManifest.Uri));
                                    var resolution = reloadManifest.Resolution
                                        ?? VbaProjectManifestWorkspace
                                            .ResolveManifestText(
                                                reloadManifest.ActiveUri,
                                                reloadManifest.Uri,
                                                reloadManifest.Text);
                                    workspace.CommitReconciledManifestScope(
                                        reloadManifest.AuthorityKey,
                                        reloadManifest.ActiveUri,
                                        resolution,
                                        reloadManifest.RetainPreviousAuthority,
                                        reloadManifest.RetainedPreviousSourceUris);
                                    manifestAuthorityMutated = true;
                                    manifestEvents.ManifestSelectionChanged(
                                        reloadManifest.Uri,
                                        reloadManifest.Text,
                                        operationCancellationToken);
                                }
                                else if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Invalid
                                    && update.Error is not null)
                                {
                                    if (!update.RetainedLastKnownGood)
                                    {
                                        progressTokens.Add(
                                            CreateManifestProgressToken(
                                                reloadManifest.Uri));
                                    }
                                    if (!update.RetainedLastKnownGood
                                        && reloadManifest.AuthorityTransferred
                                        && reloadManifest
                                            .InvalidFallbackResolution
                                            is not null)
                                    {
                                        workspace
                                            .CommitReconciledManifestScope(
                                                reloadManifest.AuthorityKey,
                                                reloadManifest.ActiveUri,
                                                reloadManifest
                                                    .InvalidFallbackResolution,
                                                retainPreviousAuthority: false,
                                                reloadManifest
                                                    .RetainedPreviousSourceUris);
                                        manifestAuthorityMutated = true;
                                    }
                                    manifestEvents.ManifestValidationFailed(
                                        reloadManifest.Uri,
                                        update.Error,
                                        operationCancellationToken);
                                }
                                else if (update.Status
                                    == VbaProjectManifestReconciliationStatus
                                        .Rejected)
                                {
                                    progressTokens.Add(
                                        CreateRejectedChangeProgressToken(
                                            "reload",
                                            reloadManifest));
                                }
                                break;
                            }
                            case TransferInvalidManifestAuthorityChange
                                transferInvalidManifest:
                            {
                                if (workspace.ManifestWorkspace
                                        .GetReconciliationRevision(
                                            transferInvalidManifest.Uri)
                                    != transferInvalidManifest
                                        .CapturedManifestRevision)
                                {
                                    progressTokens.Add(
                                        CreateRejectedChangeProgressToken(
                                            "transfer-invalid",
                                            transferInvalidManifest));
                                    break;
                                }

                                CaptureOpenAuthorities(
                                    workspace,
                                    transferInvalidManifest,
                                    initialOpenAuthorities,
                                    operationCancellationToken);
                                progressTokens.Add(
                                    CreateManifestProgressToken(
                                        transferInvalidManifest.Uri));
                                workspace.CommitReconciledManifestScope(
                                    transferInvalidManifest.AuthorityKey,
                                    transferInvalidManifest.ActiveUri,
                                    transferInvalidManifest.Resolution,
                                    retainPreviousAuthority: false,
                                    transferInvalidManifest
                                        .RetainedPreviousSourceUris);
                                manifestAuthorityMutated = true;
                                break;
                            }
                            case ObserveManifestBarrierChange
                                observeManifestBarrier:
                            {
                                CaptureOpenAuthorities(
                                    workspace,
                                    observeManifestBarrier,
                                    initialOpenAuthorities,
                                    operationCancellationToken);
                                var update = workspace.ManifestWorkspace
                                    .ReloadReconciledManifest(
                                        observeManifestBarrier.Uri,
                                        observeManifestBarrier.Text,
                                        observeManifestBarrier
                                            .CapturedManifestRevision);
                                if (update.Status
                                        == VbaProjectManifestReconciliationStatus
                                            .Invalid
                                    && update.Error is not null)
                                {
                                    if (!update.RetainedLastKnownGood)
                                    {
                                        progressTokens.Add(
                                            CreateManifestProgressToken(
                                                observeManifestBarrier.Uri));
                                    }
                                    manifestEvents.ManifestValidationFailed(
                                        observeManifestBarrier.Uri,
                                        update.Error,
                                        operationCancellationToken);
                                }
                                else if (update.Status
                                    == VbaProjectManifestReconciliationStatus
                                        .Applied)
                                {
                                    progressTokens.Add(
                                        CreateManifestProgressToken(
                                            observeManifestBarrier.Uri));
                                    if (observeManifestBarrier
                                        .HadValidationFailure)
                                    {
                                        manifestEvents
                                            .ManifestValidationRecovered(
                                                observeManifestBarrier.Uri,
                                                operationCancellationToken);
                                    }
                                }
                                else if (update.Status
                                    == VbaProjectManifestReconciliationStatus
                                        .Rejected)
                                {
                                    progressTokens.Add(
                                        CreateRejectedChangeProgressToken(
                                            "observe",
                                            observeManifestBarrier));
                                }
                                break;
                            }
                            case DeleteObservedManifestBarrierChange
                                deleteObservedManifestBarrier:
                            {
                                CaptureOpenAuthorities(
                                    workspace,
                                    deleteObservedManifestBarrier,
                                    initialOpenAuthorities,
                                    operationCancellationToken);
                                var update = workspace.ManifestWorkspace
                                    .DeleteReconciledManifest(
                                        deleteObservedManifestBarrier.Uri,
                                        deleteObservedManifestBarrier
                                            .CapturedManifestRevision);
                                if (update.Status
                                    == VbaProjectManifestReconciliationStatus
                                        .Applied)
                                {
                                    progressTokens.Add(
                                        CreateManifestProgressToken(
                                            deleteObservedManifestBarrier
                                                .Uri));
                                    if (deleteObservedManifestBarrier
                                        .HadValidationFailure)
                                    {
                                        manifestEvents
                                            .ManifestValidationRecovered(
                                                deleteObservedManifestBarrier
                                                    .Uri,
                                                operationCancellationToken);
                                    }
                                }
                                else if (update.Status
                                    == VbaProjectManifestReconciliationStatus
                                        .Rejected)
                                {
                                    progressTokens.Add(
                                        CreateRejectedChangeProgressToken(
                                            "delete-observed",
                                            deleteObservedManifestBarrier));
                                }
                                break;
                            }
                            case ReloadChange reload:
                                if (workspace.ReloadReconciledSourceDocument(
                                    reload.Uri,
                                    reload.Text,
                                    reload.CapturedWorkspaceRevision,
                                    operationCancellationToken))
                                {
                                    workspace.CommitReconciledSourceBaseline(
                                        reload.AuthorityKey,
                                        reload.Uri,
                                        reload.FullPath,
                                        reload.Text);
                                    diagnostics.EnqueueTrackedDiagnostics(
                                        reload.Uri,
                                        operationCancellationToken);
                                }

                                break;
                            case DeleteChange delete:
                                if (workspace.DeleteReconciledSourceDocument(
                                    delete.Uri,
                                    delete.CapturedWorkspaceRevision,
                                    operationCancellationToken))
                                {
                                    workspace.CommitDeletedReconciledSourceBaseline(
                                        delete.AuthorityKey,
                                        delete.Uri);
                                    diagnostics.EnqueueEmptyDiagnostics(
                                        delete.Uri,
                                        operationCancellationToken);
                                }

                                break;
                            case ReleaseSourceOwnershipChange release:
                                workspace.ReleaseReconciledSourceOwnership(
                                    release.AuthorityKey,
                                    release.Uri);
                                break;
                        }

                        if (manifestAuthorityMutated
                            || isManifestChange
                                && workspace.ManifestWorkspace.Version
                                    != manifestVersionBefore)
                        {
                            scopeFenceValidity.Clear();
                        }
                    }

                    NotifyChangedOpenAuthorities(
                        workspace,
                        manifestEvents,
                        new OpenAuthoritySnapshot(
                            initialOpenAuthorities),
                        operationCancellationToken);
                    return Task.CompletedTask;
                },
                cancellationToken)
            .ConfigureAwait(false);
        await commit.Completion.ConfigureAwait(false);
        return progressTokens;
    }

    private async Task<IReadOnlyList<ScopeScan>> ScanScopesAsync(
        IReadOnlyList<VbaProjectDiskReconciliationScope> scopes,
        CancellationToken cancellationToken)
    {
        var orderedScopes = scopes
            .OrderBy(
                scope => scope.AuthorityKey,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var scans = new ScopeScan[orderedScopes.Length];
        var nextIndex = -1;
        async Task ScanNextAsync()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= orderedScopes.Length)
                {
                    return;
                }

                var scope = orderedScopes[index];
                scans[index] = new ScopeScan(
                    scope,
                    await sourceBoundary.ScanAsync(scope, cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        var workerCount = Math.Min(
            maxConcurrentScans,
            orderedScopes.Length);
        await Task.WhenAll(
                Enumerable.Range(0, workerCount)
                    .Select(_ => ScanNextAsync()))
            .ConfigureAwait(false);
        return scans;
    }

    private static IReadOnlyList<ReconciliationChange> CreateChanges(
        IReadOnlyList<ScopeScan> scans)
    {
        var changes = new List<ReconciliationChange>();
        foreach (var scan in scans)
        {
            var scopeChanges =
                new List<ReconciliationChange>();
            var deferredBarrierChanges =
                new List<ReconciliationChange>();
            var manifestChange = CreateManifestChange(scan);
            if (manifestChange is not null)
            {
                scopeChanges.Add(manifestChange);
            }

            foreach (var observedManifest in
                scan.Disk.ObservedManifestBarriers
                    .OrderByDescending(
                        manifest =>
                            GetManifestPathDepth(
                                manifest.FullPath))
                    .ThenBy(
                        manifest => manifest.FullPath,
                        StringComparer.OrdinalIgnoreCase))
            {
                var capturedCandidate =
                    scan.Scope.ObservedManifestBarrierCandidates
                        .FirstOrDefault(
                            candidate => SameDocumentIdentity(
                                candidate.Uri,
                                observedManifest.Uri));
                if (capturedCandidate?.Baseline.Exists == true
                    && string.Equals(
                        capturedCandidate.Baseline.Text,
                        observedManifest.Text,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var hadValidationFailure = HasValidationFailure(
                    scan.Scope.ActiveUri,
                    observedManifest.Uri,
                    capturedCandidate);
                deferredBarrierChanges.Add(
                    new ObserveManifestBarrierChange(
                    scan.Scope.AuthorityKey,
                    observedManifest.Uri,
                    observedManifest.Text,
                    capturedCandidate?.CapturedRevision
                        ?? GetCapturedManifestRevision(
                            scan.Scope,
                            observedManifest.Uri),
                    scan.Scope.ManifestBarriers.Revision,
                    scan.Scope.AuthorityGeneration,
                    hadValidationFailure));
            }

            foreach (var missingManifestUri in
                scan.Disk.MissingObservedManifestBarrierUris
                    .OrderBy(GetManifestUriDepth)
                    .ThenBy(
                        uri => uri,
                        StringComparer.OrdinalIgnoreCase))
            {
                var capturedCandidate =
                    scan.Scope.ObservedManifestBarrierCandidates
                        .FirstOrDefault(
                            candidate => SameDocumentIdentity(
                                candidate.Uri,
                                missingManifestUri));
                if (capturedCandidate?.Baseline.Exists != true)
                {
                    continue;
                }

                deferredBarrierChanges.Add(
                    new DeleteObservedManifestBarrierChange(
                        scan.Scope.AuthorityKey,
                        missingManifestUri,
                        capturedCandidate.CapturedRevision,
                        scan.Scope.ManifestBarriers.Revision,
                        scan.Scope.AuthorityGeneration,
                        HasValidationFailure(
                            scan.Scope.ActiveUri,
                            missingManifestUri,
                            capturedCandidate)));
            }

            var knownByPath = scan.Scope.KnownSources.ToDictionary(
                source => source.FullPath,
                StringComparer.OrdinalIgnoreCase);
            var currentByPath = scan.Disk.Sources.ToDictionary(
                source => source.FullPath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var known in knownByPath.Values
                .OrderBy(source => source.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                if (!currentByPath.ContainsKey(known.FullPath))
                {
                    scopeChanges.Add(
                        scan.Disk.ExistingNonOwnedSourcePaths.Contains(
                            known.FullPath)
                            ? new ReleaseSourceOwnershipChange(
                                scan.Scope.AuthorityKey,
                                known.Uri,
                                scan.Scope.CapturedWorkspaceRevision,
                                scan.Scope.ManifestBarriers.Revision,
                                scan.Scope.AuthorityGeneration)
                            : new DeleteChange(
                                scan.Scope.AuthorityKey,
                                known.Uri,
                                scan.Scope.CapturedWorkspaceRevision,
                                scan.Scope.ManifestBarriers.Revision,
                                scan.Scope.AuthorityGeneration));
                }
            }

            foreach (var current in currentByPath.Values
                .OrderBy(source => source.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                if (!knownByPath.TryGetValue(current.FullPath, out var known)
                    || !known.Text.Equals(current.Text, StringComparison.Ordinal))
                {
                    scopeChanges.Add(new ReloadChange(
                        scan.Scope.AuthorityKey,
                        current.Uri,
                        current.FullPath,
                        current.Text,
                        scan.Scope.CapturedWorkspaceRevision,
                        scan.Scope.ManifestBarriers.Revision,
                        scan.Scope.AuthorityGeneration));
                }
            }

            scopeChanges.AddRange(deferredBarrierChanges);
            changes.AddRange(
                scopeChanges.Select(
                    change => change with
                    {
                        PreviousResolution =
                            scan.Scope.Resolution,
                        CapturedOpenSourceUris =
                            scan.Scope.OpenSourceUris
                    }));
        }

        return changes;
    }

    private static ReconciliationChange? CreateManifestChange(ScopeScan scan)
    {
        if (scan.Disk.Manifest is null)
        {
            var deletedCandidates = scan.Scope.ManifestCandidates
                .Where(
                    candidate =>
                        !IsKnownInvalidBarrier(
                            scan.Scope,
                            candidate)
                        && (candidate.Baseline.Exists
                            || candidate.HasOpenOverlay))
                .Select(
                    candidate => new DeletedManifestCandidate(
                        candidate.Uri,
                        candidate.CapturedRevision))
                .ToArray();
            if (deletedCandidates.Length == 0
                && (scan.Scope.Resolution.Kind
                    != VbaProjectResolutionKind.ManifestDocument
                    || string.IsNullOrWhiteSpace(
                        scan.Scope.Resolution.ManifestPath)))
            {
                return null;
            }

            if (deletedCandidates.Length == 0)
            {
                var manifestUri = new Uri(
                    Path.GetFullPath(scan.Scope.Resolution.ManifestPath!))
                    .AbsoluteUri;
                deletedCandidates =
                [
                    new DeletedManifestCandidate(
                        manifestUri,
                        GetCapturedManifestRevision(
                            scan.Scope,
                            manifestUri))
                ];
            }

            var effectiveMissingOverlay =
                scan.Scope.ManifestCandidates.FirstOrDefault(
                    candidate => candidate.HasOpenOverlay
                        && candidate.OpenOverlayText is not null);
            var resolution = TryResolveOpenOverlay(
                    scan.Scope.ActiveUri,
                    effectiveMissingOverlay)
                ?? CreateAdHocResolution(scan.Scope.ActiveUri);
            return new ReplaceDeletedManifestAuthorityChange(
                scan.Scope.AuthorityKey,
                deletedCandidates[0].Uri,
                deletedCandidates,
                scan.Scope.ActiveUri,
                resolution,
                FallbackUri: "",
                FallbackText: "",
                CapturedFallbackRevision: 0,
                ReloadFallbackManifest: false,
                FallbackHiddenByOpenOverlay:
                    effectiveMissingOverlay is not null,
                AuthorityTransferred:
                    !HasSameProjectAuthority(
                        scan.Scope.Resolution,
                        resolution),
                scan.Disk.Sources
                    .Select(source => source.Uri)
                    .ToArray(),
                scan.Scope.ManifestBarriers.Revision,
                scan.Scope.AuthorityGeneration);
        }

        var scannedCandidate = scan.Scope.ManifestCandidates
            .FirstOrDefault(
                candidate => SameDocumentIdentity(
                    candidate.Uri,
                    scan.Disk.Manifest.Uri));

        VbaProjectResolution? scannedResolution;
        try
        {
            scannedResolution =
                VbaProjectManifestWorkspace.ResolveManifestText(
                    scan.Scope.ActiveUri,
                    scan.Disk.Manifest.Uri,
                    scan.Disk.Manifest.Text);
        }
        catch (VbaProjectManifestException)
        {
            scannedResolution = null;
        }
        var nearerCandidates = scan.Scope.ManifestCandidates
            .TakeWhile(
                candidate => !SameDocumentIdentity(
                    candidate.Uri,
                    scan.Disk.Manifest.Uri))
            .ToArray();
        var missingNearerCandidates = nearerCandidates
            .Where(
                candidate =>
                    !IsKnownInvalidBarrier(
                        scan.Scope,
                        candidate)
                    && (candidate.Baseline.Exists
                        || candidate.HasOpenOverlay))
            .Select(
                candidate => new DeletedManifestCandidate(
                    candidate.Uri,
                    candidate.CapturedRevision))
            .ToArray();
        var fartherCandidates = scan.Scope.ManifestCandidates
            .SkipWhile(
                candidate => !SameDocumentIdentity(
                    candidate.Uri,
                    scan.Disk.Manifest.Uri))
            .Skip(1)
            .ToArray();
        var invalidFallbackResolution = scannedResolution is null
            ? TryResolveEffectiveManifest(
                    scan.Scope.ActiveUri,
                    scannedCandidate)
                ?? TryResolveBaseline(
                    scan.Scope.ActiveUri,
                    scannedCandidate)
                ?? (!HasManifestAuthority(
                        scan.Scope.Resolution,
                        scan.Disk.Manifest.Uri)
                    ? scan.Scope.Resolution
                    : null)
                ?? TryResolveFirstEffectiveCandidate(
                    scan.Scope.ActiveUri,
                    fartherCandidates)
                ?? CreateAdHocResolution(scan.Scope.ActiveUri)
            : null;
        if (missingNearerCandidates.Length > 0)
        {
            var effectiveMissingOverlay =
                nearerCandidates.FirstOrDefault(
                    candidate => candidate.HasOpenOverlay
                        && candidate.OpenOverlayText is not null);
            var fallbackResolution = TryResolveOpenOverlay(
                    scan.Scope.ActiveUri,
                    effectiveMissingOverlay)
                ?? TryResolveOpenOverlay(
                    scan.Scope.ActiveUri,
                    scannedCandidate)
                ?? scannedResolution
                ?? TryResolveEffectiveManifest(
                    scan.Scope.ActiveUri,
                    scannedCandidate)
                ?? TryResolveBaseline(
                    scan.Scope.ActiveUri,
                    scannedCandidate)
                ?? TryResolveFirstEffectiveCandidate(
                    scan.Scope.ActiveUri,
                    fartherCandidates)
                ?? CreateAdHocResolution(scan.Scope.ActiveUri);
            var reloadFallbackManifest =
                scannedCandidate?.Baseline.Exists != true
                || !string.Equals(
                    scannedCandidate.Baseline.Text,
                    scan.Disk.Manifest.Text,
                    StringComparison.Ordinal);
            return new ReplaceDeletedManifestAuthorityChange(
                scan.Scope.AuthorityKey,
                missingNearerCandidates[0].Uri,
                missingNearerCandidates,
                scan.Scope.ActiveUri,
                fallbackResolution,
                scan.Disk.Manifest.Uri,
                scan.Disk.Manifest.Text,
                GetCapturedManifestRevision(
                    scan.Scope,
                    scan.Disk.Manifest.Uri),
                reloadFallbackManifest,
                effectiveMissingOverlay is not null,
                !HasSameProjectAuthority(
                    scan.Scope.Resolution,
                    fallbackResolution),
                scan.Disk.Sources
                    .Select(source => source.Uri)
                    .ToArray(),
                scan.Scope.ManifestBarriers.Revision,
                scan.Scope.AuthorityGeneration);
        }

        var effectiveOpenAuthorityCandidate =
            GetOpenAuthorityCandidate(scan.Scope);
        if (effectiveOpenAuthorityCandidate is not null
            && !SameDocumentIdentity(
                effectiveOpenAuthorityCandidate.Uri,
                scan.Disk.Manifest.Uri)
            && !effectiveOpenAuthorityCandidate.Baseline.Exists)
        {
            return null;
        }

        var unchangedBaseline =
            scannedCandidate?.Baseline.Exists == true
            && string.Equals(
                scannedCandidate.Baseline.Text,
                scan.Disk.Manifest.Text,
                StringComparison.Ordinal);
        if (unchangedBaseline
            && scannedResolution is null
            && scannedCandidate?.EffectiveManifestText is null
            && HasManifestAuthority(
                scan.Scope.Resolution,
                scan.Disk.Manifest.Uri)
            && invalidFallbackResolution is not null
            && !HasSameProjectAuthority(
                scan.Scope.Resolution,
                invalidFallbackResolution))
        {
            return new TransferInvalidManifestAuthorityChange(
                scan.Scope.AuthorityKey,
                scan.Disk.Manifest.Uri,
                GetCapturedManifestRevision(
                    scan.Scope,
                    scan.Disk.Manifest.Uri),
                scan.Scope.ActiveUri,
                invalidFallbackResolution,
                scan.Scope.ManifestBarriers.Revision,
                scan.Scope.AuthorityGeneration,
                scan.Disk.Sources
                    .Select(source => source.Uri)
                    .ToArray());
        }

        if (unchangedBaseline
            && (scannedCandidate!.HasOpenOverlay
                || scannedResolution is null
                || HasSameResolution(
                    scan.Scope.ActiveUri,
                    scan.Scope.Resolution,
                    scannedResolution)))
        {
            return null;
        }

        return new ReloadManifestChange(
            scan.Scope.AuthorityKey,
            scan.Disk.Manifest.Uri,
            scan.Disk.Manifest.Text,
            GetCapturedManifestRevision(
                scan.Scope,
                scan.Disk.Manifest.Uri),
            scan.Scope.ActiveUri,
            scannedResolution,
            invalidFallbackResolution,
            scan.Scope.ManifestBarriers.Revision,
            scan.Scope.AuthorityGeneration,
            RetainPreviousAuthority:
                scannedResolution is not null
                && ShouldRetainPreviousAuthority(
                    scan.Scope.Resolution,
                    scannedResolution),
            AuthorityTransferred:
                (scannedResolution ?? invalidFallbackResolution)
                    is { } effectiveResolution
                && !HasSameProjectAuthority(
                    scan.Scope.Resolution,
                    effectiveResolution),
            RetainedPreviousSourceUris:
                scan.Disk.Sources
                    .Select(source => source.Uri)
                    .ToArray());
    }

    private static VbaProjectDiskManifestCandidate?
        GetOpenAuthorityCandidate(
            VbaProjectDiskReconciliationScope scope)
    {
        if (string.IsNullOrWhiteSpace(
                scope.Resolution.ManifestPath))
        {
            return null;
        }

        var authorityUri = new Uri(Path.GetFullPath(
            scope.Resolution.ManifestPath)).AbsoluteUri;
        return scope.ManifestCandidates.FirstOrDefault(
            candidate => candidate.HasOpenOverlay
                && SameDocumentIdentity(
                    candidate.Uri,
                    authorityUri));
    }

    private static VbaProjectResolution? TryResolveBaseline(
        string activeUri,
        VbaProjectDiskManifestCandidate? candidate)
    {
        if (candidate?.Baseline.Exists != true
            || candidate.Baseline.Text is null)
        {
            return null;
        }

        try
        {
            return VbaProjectManifestWorkspace.ResolveManifestText(
                activeUri,
                candidate.Uri,
                candidate.Baseline.Text);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static VbaProjectResolution? TryResolveOpenOverlay(
        string activeUri,
        VbaProjectDiskManifestCandidate? candidate)
    {
        if (candidate?.HasOpenOverlay != true
            || candidate.OpenOverlayText is null)
        {
            return null;
        }

        try
        {
            return VbaProjectManifestWorkspace.ResolveManifestText(
                activeUri,
                candidate.Uri,
                candidate.OpenOverlayText);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static VbaProjectResolution? TryResolveEffectiveManifest(
        string activeUri,
        VbaProjectDiskManifestCandidate? candidate)
    {
        if (candidate?.EffectiveManifestText is not { } effectiveText)
        {
            return null;
        }

        try
        {
            return VbaProjectManifestWorkspace.ResolveManifestText(
                activeUri,
                candidate.Uri,
                effectiveText);
        }
        catch (VbaProjectManifestException)
        {
            return null;
        }
    }

    private static VbaProjectResolution?
        TryResolveFirstEffectiveCandidate(
            string activeUri,
            IReadOnlyList<VbaProjectDiskManifestCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolution = TryResolveOpenOverlay(
                    activeUri,
                    candidate)
                ?? TryResolveEffectiveManifest(
                    activeUri,
                    candidate)
                ?? TryResolveBaseline(
                    activeUri,
                    candidate);
            if (resolution is not null)
            {
                return resolution;
            }
        }

        return null;
    }

    private static bool HasManifestAuthority(
        VbaProjectResolution resolution,
        string manifestUri)
    {
        if (string.IsNullOrWhiteSpace(resolution.ManifestPath))
        {
            return false;
        }

        return SameDocumentIdentity(
            new Uri(Path.GetFullPath(
                resolution.ManifestPath)).AbsoluteUri,
            manifestUri);
    }

    private static bool IsKnownInvalidBarrier(
        VbaProjectDiskReconciliationScope scope,
        VbaProjectDiskManifestCandidate candidate)
    {
        var manifestPath =
            VbaProjectResolver.TryGetLocalPath(candidate.Uri);
        return manifestPath is not null
            && scope.ManifestBarriers.Overrides.TryGetValue(
                Path.GetFullPath(manifestPath),
                out var isBarrier)
            && !isBarrier;
    }

    private static bool HasValidationFailure(
        string activeUri,
        string manifestUri,
        VbaProjectDiskManifestCandidate? candidate)
    {
        if (candidate?.Baseline.Exists != true
            || candidate.Baseline.Text is not { } baselineText)
        {
            return false;
        }

        try
        {
            _ = VbaProjectManifestWorkspace.ResolveManifestText(
                activeUri,
                manifestUri,
                baselineText);
            return false;
        }
        catch (VbaProjectManifestException)
        {
            return true;
        }
    }

    private static string CreateManifestProgressToken(string uri)
        => $"manifest:{NormalizeDocumentIdentity(uri)}";

    private static string CreateRejectedChangeProgressToken(
        string reason,
        ReconciliationChange change)
    {
        var changeFingerprint =
            change is ReplaceDeletedManifestAuthorityChange replace
                ? string.Join(
                    ",",
                    replace.DeletedManifests.Select(
                        deleted =>
                            $"{NormalizeDocumentIdentity(deleted.Uri)}@{deleted.CapturedRevision}"))
                    + $"|fallback={NormalizeDocumentIdentity(replace.FallbackUri)}"
                    + $"@{replace.CapturedFallbackRevision}"
                : $"{NormalizeDocumentIdentity(change.Uri)}"
                    + $"@{change.CapturedWorkspaceRevision}";
        return $"rejected:{reason}:{change.GetType().Name}"
            + $":{change.AuthorityKey}"
            + $":barrier={change.CapturedManifestBarrierRevision}"
            + $":generation={change.CapturedAuthorityGeneration}"
            + $":{changeFingerprint}";
    }

    private static string NormalizeDocumentIdentity(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return "";
        }

        var path = VbaProjectResolver.TryGetLocalPath(uri);
        return path is null
            ? uri
            : Path.GetFullPath(path);
    }

    private static int GetManifestPathDepth(string path)
        => Path.GetFullPath(path)
            .Count(
                character =>
                    character == Path.DirectorySeparatorChar
                    || character == Path.AltDirectorySeparatorChar);

    private static int GetManifestUriDepth(string uri)
    {
        var path = VbaProjectResolver.TryGetLocalPath(uri);
        return path is null
            ? 0
            : GetManifestPathDepth(path);
    }

    private static void CaptureOpenAuthorities(
        VbaLanguageWorkspace workspace,
        ReconciliationChange change,
        Dictionary<string, VbaProjectResolution?>
            initialOpenAuthorities,
        CancellationToken cancellationToken)
    {
        var capturedOpenSources =
            change.CapturedOpenSourceUris.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        foreach (var sourceUri in
            workspace.GetOpenDocumentUris(cancellationToken))
        {
            if (initialOpenAuthorities.ContainsKey(sourceUri))
            {
                continue;
            }

            if (!IsPotentiallyAffectedSource(
                    change,
                    sourceUri))
            {
                continue;
            }

            if (change.PreviousResolution is not null
                && capturedOpenSources.Contains(sourceUri))
            {
                initialOpenAuthorities[sourceUri] =
                    change.PreviousResolution;
                continue;
            }

            if (workspace.ManifestWorkspace
                .TryResolveKnownState(
                    sourceUri,
                    out var knownResolution))
            {
                initialOpenAuthorities[sourceUri] =
                    knownResolution;
            }
            else
            {
                initialOpenAuthorities[sourceUri] = null;
            }
        }
    }

    private static bool IsPotentiallyAffectedSource(
        ReconciliationChange change,
        string sourceUri)
    {
        var sourcePath =
            VbaProjectResolver.TryGetLocalPath(sourceUri);
        if (sourcePath is null)
        {
            return change.CapturedOpenSourceUris.Any(
                captured => SameDocumentIdentity(
                    captured,
                    sourceUri));
        }

        if (change.CapturedOpenSourceUris.Any(
                captured => SameDocumentIdentity(
                    captured,
                    sourceUri)))
        {
            return true;
        }

        return GetImpactManifestUris(change)
            .Select(VbaProjectResolver.TryGetLocalPath)
            .Where(path => path is not null)
            .Select(path => Path.GetDirectoryName(path!))
            .Any(
                impactRootPath =>
                    !string.IsNullOrWhiteSpace(impactRootPath)
                    && VbaProjectResolver.IsPathUnder(
                        sourcePath,
                        impactRootPath));
    }

    private static IEnumerable<string> GetImpactManifestUris(
        ReconciliationChange change)
    {
        if (change is not ReplaceDeletedManifestAuthorityChange replace)
        {
            yield return change.Uri;
            yield break;
        }

        foreach (var deletedManifest in replace.DeletedManifests)
        {
            yield return deletedManifest.Uri;
        }

        if (replace.ReloadFallbackManifest
            && !string.IsNullOrWhiteSpace(replace.FallbackUri))
        {
            yield return replace.FallbackUri;
        }
    }

    private static void NotifyChangedOpenAuthorities(
        VbaLanguageWorkspace workspace,
        IVbaProjectDiskReconciliationManifestEvents manifestEvents,
        OpenAuthoritySnapshot previous,
        CancellationToken cancellationToken)
    {
        var currentlyOpen = workspace
            .GetOpenDocumentUris(cancellationToken)
            .ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (sourceUri, previousResolution) in
            previous.Resolutions)
        {
            if (!currentlyOpen.Contains(sourceUri))
            {
                continue;
            }

            if (previousResolution is null
                || !workspace.ManifestWorkspace
                .TryResolveKnownState(
                    sourceUri,
                    out var currentResolution))
            {
                manifestEvents.ProjectAuthorityTransferred(
                    sourceUri,
                    cancellationToken);
                continue;
            }

            if (!HasSameProjectAuthority(
                    previousResolution,
                    currentResolution))
            {
                manifestEvents.ProjectAuthorityTransferred(
                    sourceUri,
                    cancellationToken);
            }
        }
    }

    private static bool HasSameResolution(
        string activeUri,
        VbaProjectResolution left,
        VbaProjectResolution right)
        => VbaProjectSnapshotIdentity
            .Create(activeUri, left)
            .Key
            .Equals(
                VbaProjectSnapshotIdentity.Create(activeUri, right).Key,
                StringComparison.OrdinalIgnoreCase);

    private static bool HasSameProjectAuthority(
        VbaProjectResolution left,
        VbaProjectResolution right)
        => left.Kind == right.Kind
            && string.Equals(
                Path.GetFullPath(left.RootPath),
                Path.GetFullPath(right.RootPath),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                NormalizeOptionalPath(left.ManifestPath),
                NormalizeOptionalPath(right.ManifestPath),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                left.DocumentName,
                right.DocumentName,
                StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptionalPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(path);

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

    private static VbaProjectResolution CreateAdHocResolution(string activeUri)
    {
        var activePath = VbaProjectResolver.TryGetLocalPath(activeUri);
        return new VbaProjectResolution(
            VbaProjectResolutionKind.AdHoc,
            activePath is null
                ? ""
                : Path.GetDirectoryName(activePath)
                    ?? Directory.GetCurrentDirectory());
    }

    private static long GetCapturedManifestRevision(
        VbaProjectDiskReconciliationScope scope,
        string manifestUri)
        => scope.ManifestCandidates
            .FirstOrDefault(
                candidate => SameDocumentIdentity(
                    candidate.Uri,
                    manifestUri))
            ?.CapturedRevision
            ?? 0;

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
            && Path.GetFullPath(leftPath).Equals(
                Path.GetFullPath(rightPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunCadenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(cadence, timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await TriggerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Reconciliation is best effort; the next cadence retries.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task IgnoreCancellationAsync(
        Task? task,
        bool reportFailure = false)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            if (reportFailure)
            {
                try
                {
                    failureObserver.ReconciliationFailed(ex);
                }
                catch (Exception)
                {
                    // Failure observation must not fault shutdown.
                }
            }
        }
    }

    private async Task DisposeCancellationAfterObservationAsync(
        Task observation)
    {
        await observation.ConfigureAwait(false);
        lifetimeCancellation.Dispose();
    }

    private sealed record ScopeScan(
        VbaProjectDiskReconciliationScope Scope,
        VbaProjectDiskScopeScan Disk);

    private sealed record OpenAuthoritySnapshot(
        IReadOnlyDictionary<string, VbaProjectResolution?> Resolutions);

    private static bool IsManifestChange(ReconciliationChange change)
        => change is ReplaceDeletedManifestAuthorityChange
            or ReloadManifestChange
            or TransferInvalidManifestAuthorityChange
            or ObserveManifestBarrierChange
            or DeleteObservedManifestBarrierChange;

    private readonly record struct ReconciliationScopeFence(
        string AuthorityKey,
        long ManifestBarrierRevision,
        long AuthorityGeneration);

    private sealed class ReconciliationScopeFenceComparer
        : IEqualityComparer<ReconciliationScopeFence>
    {
        public static ReconciliationScopeFenceComparer Instance { get; } =
            new();

        private ReconciliationScopeFenceComparer()
        {
        }

        public bool Equals(
            ReconciliationScopeFence x,
            ReconciliationScopeFence y)
            => x.ManifestBarrierRevision == y.ManifestBarrierRevision
                && x.AuthorityGeneration == y.AuthorityGeneration
                && x.AuthorityKey.Equals(
                    y.AuthorityKey,
                    StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReconciliationScopeFence obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(
                    obj.AuthorityKey),
                obj.ManifestBarrierRevision,
                obj.AuthorityGeneration);
    }

    private abstract record ReconciliationChange(
        string AuthorityKey,
        string Uri,
        long CapturedWorkspaceRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration)
    {
        public VbaProjectResolution? PreviousResolution { get; init; }

        public IReadOnlyList<string> CapturedOpenSourceUris { get; init; } =
            [];
    }

    private sealed record ReloadChange(
        string AuthorityKey,
        string Uri,
        string FullPath,
        string Text,
        long CapturedWorkspaceRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedWorkspaceRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record DeleteChange(
        string AuthorityKey,
        string Uri,
        long CapturedWorkspaceRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedWorkspaceRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record ReleaseSourceOwnershipChange(
        string AuthorityKey,
        string Uri,
        long CapturedWorkspaceRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedWorkspaceRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record DeletedManifestCandidate(
        string Uri,
        long CapturedRevision);

    private sealed record ReplaceDeletedManifestAuthorityChange(
        string AuthorityKey,
        string Uri,
        IReadOnlyList<DeletedManifestCandidate> DeletedManifests,
        string ActiveUri,
        VbaProjectResolution Resolution,
        string FallbackUri,
        string FallbackText,
        long CapturedFallbackRevision,
        bool ReloadFallbackManifest,
        bool FallbackHiddenByOpenOverlay,
        bool AuthorityTransferred,
        IReadOnlyList<string> RetainedPreviousSourceUris,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            DeletedManifests[0].CapturedRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record ReloadManifestChange(
        string AuthorityKey,
        string Uri,
        string Text,
        long CapturedManifestRevision,
        string ActiveUri,
        VbaProjectResolution? Resolution,
        VbaProjectResolution? InvalidFallbackResolution,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration,
        bool RetainPreviousAuthority,
        bool AuthorityTransferred,
        IReadOnlyList<string> RetainedPreviousSourceUris)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedManifestRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record TransferInvalidManifestAuthorityChange(
        string AuthorityKey,
        string Uri,
        long CapturedManifestRevision,
        string ActiveUri,
        VbaProjectResolution Resolution,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration,
        IReadOnlyList<string> RetainedPreviousSourceUris)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedManifestRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record ObserveManifestBarrierChange(
        string AuthorityKey,
        string Uri,
        string Text,
        long CapturedManifestRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration,
        bool HadValidationFailure)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedManifestRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

    private sealed record DeleteObservedManifestBarrierChange(
        string AuthorityKey,
        string Uri,
        long CapturedManifestRevision,
        long CapturedManifestBarrierRevision,
        long CapturedAuthorityGeneration,
        bool HadValidationFailure)
        : ReconciliationChange(
            AuthorityKey,
            Uri,
            CapturedManifestRevision,
            CapturedManifestBarrierRevision,
            CapturedAuthorityGeneration);

}
