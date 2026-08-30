using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents one manifest authority candidate captured for reconciliation.
/// </summary>
internal sealed record VbaProjectReconciliationManifestCandidate(
    string Uri,
    long CapturedRevision,
    VbaProjectDiskManifestBaseline Baseline)
{
    public bool HasOpenOverlay { get; init; }

    public string? OpenOverlayText { get; init; }

    public string? EffectiveManifestText { get; init; }
}

/// <summary>
/// Represents an activated project scope captured before background disk work starts.
/// </summary>
internal sealed record VbaProjectReconciliationScope(
    VbaProjectAuthorityIdentity AuthorityKey,
    string ActiveUri,
    VbaProjectResolution Resolution,
    long CapturedWorkspaceRevision,
    IReadOnlyList<VbaProjectReconciliationManifestCandidate> ManifestCandidates,
    IReadOnlyList<VbaProjectDiskKnownSource> KnownSources)
{
    /// <summary>
    /// Gets the manifest-barrier snapshot that owns this scan.
    /// </summary>
    public VbaProjectManifestBarrierSnapshot ManifestBarriers { get; init; } =
        new(
            Revision: 0,
            new Dictionary<string, bool>(
                StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the structural incarnation of the captured reconciliation authority.
    /// </summary>
    public long AuthorityGeneration { get; init; }

    public IReadOnlyList<VbaProjectReconciliationManifestCandidate>
        ObservedManifestBarrierCandidates { get; init; } = [];

    public IReadOnlyList<string> OpenSourceUris { get; init; } = [];

    public IReadOnlyList<string> OpenDocumentUris { get; init; } = [];
}

/// <summary>
/// Owns one reconciliation scope capture and its source-revision watermark.
/// </summary>
internal sealed class VbaProjectReconciliationCapture : IDisposable
{
    private IDisposable? revisionCapture;

    public VbaProjectReconciliationCapture(
        IReadOnlyList<VbaProjectReconciliationScope> scopes,
        IDisposable revisionCapture)
    {
        Scopes = scopes;
        this.revisionCapture = revisionCapture;
    }

    public IReadOnlyList<VbaProjectReconciliationScope> Scopes { get; }

    public void Dispose()
        => Interlocked.Exchange(ref revisionCapture, null)?.Dispose();
}

/// <summary>
/// Holds one captured authority fence and its ordered reconciliation mutations.
/// </summary>
internal sealed record VbaProjectReconciliationScopePlan(
    VbaProjectAuthorityIdentity AuthorityKey,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration,
    IReadOnlyList<ReconciliationChange> OrderedMutations);

/// <summary>
/// Reports the ephemeral outcome of committing one reconciliation scope.
/// </summary>
internal sealed record VbaProjectReconciliationCommitResult(
    VbaProjectReconciliationCommitOutcome Outcome,
    bool RequiresFollowUp,
    IReadOnlyList<VbaProjectReconciliationProgress> Progress,
    IReadOnlyList<VbaProjectReconciliationEffect> Effects);

internal enum VbaProjectReconciliationCommitOutcome
{
    Committed,
    RejectedBeforeWrite,
    CommittedWithRejectedTail
}

internal enum VbaProjectReconciliationProgressKind
{
    ManifestCommitted,
    MutationRejected
}

internal sealed record VbaProjectReconciliationProgress(
    VbaProjectReconciliationProgressKind Kind,
    string Identity);

internal abstract record VbaProjectReconciliationEffect;

internal sealed record ReconciledSourceDiagnosticsEffect(string Uri)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledProjectDiagnosticsEffect(string Uri)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledSourceDiagnosticsClearedEffect(string Uri)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledManifestSelectionChangedEffect(
    string Uri,
    string Text)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledManifestValidationFailedEffect(
    string Uri,
    VbaProjectManifestException Error)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledManifestValidationRecoveredEffect(string Uri)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledManifestDeletedEffect(string Uri)
    : VbaProjectReconciliationEffect;

internal sealed record ReconciledProjectAuthorityTransferredEffect(
    string SourceUri)
    : VbaProjectReconciliationEffect;

internal abstract record ReconciliationChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    string Uri,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
{
    public VbaProjectResolution? PreviousResolution { get; init; }

    public IReadOnlyList<string> CapturedOpenSourceUris { get; init; } = [];

    public IReadOnlyList<string> CapturedProjectSourceUris { get; init; } = [];

    public IReadOnlyList<string> CapturedManifestSourceUris { get; init; } = [];
}

internal sealed record ReloadChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    string Uri,
    string FullPath,
    string Text,
    VbaProjectDiskContentIdentity ContentIdentity,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        Uri,
        CapturedWorkspaceRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record DecodeFailureChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaProjectDiskSourceFailure Failure,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        Failure.Uri,
        CapturedWorkspaceRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record DeleteChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record ReleaseSourceOwnershipChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record DeletedManifestCandidate(
    string Uri,
    long CapturedRevision);

internal sealed record ReplaceDeletedManifestAuthorityChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record ReloadManifestChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record TransferInvalidManifestAuthorityChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record ObserveManifestBarrierChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

internal sealed record DeleteObservedManifestBarrierChange(
    VbaProjectAuthorityIdentity AuthorityKey,
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

public sealed partial class VbaLanguageWorkspace
{
    /// <summary>
    /// Validates and commits one authority plan as one ordered mutation.
    /// </summary>
    internal VbaProjectReconciliationCommitResult
        TryCommitProjectReconciliationScope(
            VbaProjectReconciliationScopePlan plan,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> trackedUris;
        lock (gate)
        {
            trackedUris = CaptureTrackedDocumentUris();
        }
        if (!snapshotProvider.IsReconciliationScopeCurrent(
                plan.AuthorityKey,
                plan.CapturedManifestBarrierRevision,
                plan.CapturedAuthorityGeneration))
        {
            var rejectedMutation = plan.OrderedMutations.FirstOrDefault();
            return new VbaProjectReconciliationCommitResult(
                VbaProjectReconciliationCommitOutcome.RejectedBeforeWrite,
                RequiresFollowUp: true,
                rejectedMutation is null
                    ? []
                    : [CreateRejectedProgress("scope", rejectedMutation)],
                []);
        }

        var progress = new List<VbaProjectReconciliationProgress>();
        var effects = new List<VbaProjectReconciliationEffect>();
        var projectDiagnosticsCandidates =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var initialOpenAuthorities =
            new Dictionary<
                VbaDocumentIdentity,
                CapturedOpenAuthority>();
        var requiresFollowUp = false;
        var committedMutation = false;
        var rejectedMutationTail = false;

        foreach (var change in plan.OrderedMutations)
        {
            var isManifestChange = IsManifestChange(change);
            var manifestVersionBefore =
                isManifestChange ? ManifestWorkspace.Version : 0;
            var manifestAuthorityMutated = false;
            var mutationRejected = false;
            if (isManifestChange)
            {
                CaptureOpenAuthorities(
                    change,
                    initialOpenAuthorities,
                    CancellationToken.None);
            }

            switch (change)
            {
                case ReplaceDeletedManifestAuthorityChange replace:
                {
                    if (!snapshotProvider.TryUseReconciliationAuthority(
                            plan.AuthorityKey,
                            plan.CapturedManifestBarrierRevision,
                            plan.CapturedAuthorityGeneration,
                            lease =>
                            {
                    var replacement = ManifestWorkspace
                        .ReplaceDeletedReconciledManifestAuthority(
                            replace.DeletedManifests
                                .Select(deleted =>
                                    new VbaProjectManifestReconciliationTarget(
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
                        progress.Add(
                            CreateRejectedProgress("replace", replace));
                        mutationRejected = true;
                        return false;
                    }

                    var mutationEffects =
                        new List<VbaProjectReconciliationEffect>();
                    foreach (var deleted in replacement.DeletedManifests)
                    {
                        if (deleted.Update.Status
                            != VbaProjectManifestReconciliationStatus.Applied)
                        {
                            continue;
                        }

                        progress.Add(
                            CreateManifestProgress(deleted.Uri));
                        mutationEffects.Add(
                            new ReconciledManifestDeletedEffect(deleted.Uri));
                    }

                    var reloadUpdate = replacement.ReloadedManifest;
                    if (!replace.FallbackHiddenByOpenOverlay
                        && reloadUpdate?.Update.Status
                            == VbaProjectManifestReconciliationStatus.Applied)
                    {
                        progress.Add(
                            CreateManifestProgress(reloadUpdate.Uri));
                        mutationEffects.Add(
                            new ReconciledManifestSelectionChangedEffect(
                                reloadUpdate.Uri,
                                replace.FallbackText));
                    }
                    else if (!replace.FallbackHiddenByOpenOverlay
                        && reloadUpdate?.Update.Status
                            == VbaProjectManifestReconciliationStatus.Invalid
                        && reloadUpdate.Update.Error is not null)
                    {
                        if (!reloadUpdate.Update.RetainedLastKnownGood)
                        {
                            progress.Add(
                                CreateManifestProgress(reloadUpdate.Uri));
                        }

                        mutationEffects.Add(
                            new ReconciledManifestValidationFailedEffect(
                                reloadUpdate.Uri,
                                reloadUpdate.Update.Error));
                    }

                    lease.CommitManifestScope(
                            replace.ActiveUri,
                            replace.Resolution,
                            retainPreviousAuthority: false,
                            replace.RetainedPreviousSourceUris,
                            trackedUris);

                    effects.AddRange(mutationEffects);
                    committedMutation = true;
                    manifestAuthorityMutated = true;
                    if (replace.AuthorityTransferred)
                    {
                        progress.Add(
                            CreateManifestProgress(replace.Uri));
                    }

                    return true;
                            },
                            out _))
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "authority-lease",
                                replace));
                        mutationRejected = true;
                    }

                    break;
                }
                case ReloadManifestChange reloadManifest:
                {
                    var resolution = reloadManifest.Resolution;
                    if (resolution is null)
                    {
                        try
                        {
                            resolution =
                                VbaProjectManifestWorkspace.ResolveManifestText(
                                    reloadManifest.ActiveUri,
                                    reloadManifest.Uri,
                                    reloadManifest.Text);
                        }
                        catch (VbaProjectManifestException)
                        {
                            // Invalid text is still committed as validation
                            // state and may retain the last-known-good scope.
                        }
                    }

                    if (!snapshotProvider.TryUseReconciliationAuthority(
                            plan.AuthorityKey,
                            plan.CapturedManifestBarrierRevision,
                            plan.CapturedAuthorityGeneration,
                            lease =>
                            {
                    var update = ManifestWorkspace.ReloadReconciledManifest(
                        reloadManifest.Uri,
                        reloadManifest.Text,
                        reloadManifest.CapturedManifestRevision);
                    if (update.Status
                        == VbaProjectManifestReconciliationStatus.Rejected)
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "reload",
                                reloadManifest));
                        mutationRejected = true;
                        return false;
                    }

                    committedMutation = true;
                    if (update.Status
                        == VbaProjectManifestReconciliationStatus.Applied)
                    {
                        lease.CommitManifestScope(
                            reloadManifest.ActiveUri,
                            resolution!,
                            reloadManifest.RetainPreviousAuthority,
                            reloadManifest.RetainedPreviousSourceUris,
                            trackedUris);
                        progress.Add(
                            CreateManifestProgress(reloadManifest.Uri));
                        effects.Add(
                            new ReconciledManifestSelectionChangedEffect(
                                reloadManifest.Uri,
                                reloadManifest.Text));
                        manifestAuthorityMutated = true;
                    }
                    else if (update.Status
                            == VbaProjectManifestReconciliationStatus.Invalid
                        && update.Error is not null)
                    {
                        if (!update.RetainedLastKnownGood)
                        {
                            progress.Add(
                                CreateManifestProgress(
                                    reloadManifest.Uri));
                        }

                        if (!update.RetainedLastKnownGood
                            && reloadManifest.AuthorityTransferred
                            && reloadManifest.InvalidFallbackResolution
                                is not null)
                        {
                            lease.CommitManifestScope(
                                reloadManifest.ActiveUri,
                                reloadManifest.InvalidFallbackResolution,
                                retainPreviousAuthority: false,
                                reloadManifest.RetainedPreviousSourceUris,
                                trackedUris);
                            manifestAuthorityMutated = true;
                        }

                        effects.Add(
                            new ReconciledManifestValidationFailedEffect(
                                reloadManifest.Uri,
                                update.Error));
                    }
                    return true;
                            },
                            out _))
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "authority-lease",
                                reloadManifest));
                        mutationRejected = true;
                    }

                    break;
                }
                case TransferInvalidManifestAuthorityChange
                    transferInvalidManifest:
                {
                    if (!snapshotProvider.TryUseReconciliationAuthority(
                            plan.AuthorityKey,
                            plan.CapturedManifestBarrierRevision,
                            plan.CapturedAuthorityGeneration,
                            lease =>
                            {
                    if (ManifestWorkspace.GetReconciliationRevision(
                                transferInvalidManifest.Uri)
                            != transferInvalidManifest
                                .CapturedManifestRevision)
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "transfer-invalid",
                                transferInvalidManifest));
                        mutationRejected = true;
                        return false;
                    }

                    lease.CommitManifestScope(
                            transferInvalidManifest.ActiveUri,
                            transferInvalidManifest.Resolution,
                            retainPreviousAuthority: false,
                            transferInvalidManifest.RetainedPreviousSourceUris,
                            trackedUris);

                    progress.Add(
                        CreateManifestProgress(
                            transferInvalidManifest.Uri));
                    committedMutation = true;
                    manifestAuthorityMutated = true;
                    return true;
                            },
                            out _))
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "authority-lease",
                                transferInvalidManifest));
                        mutationRejected = true;
                    }

                    break;
                }
                case ObserveManifestBarrierChange observeManifestBarrier:
                {
                    if (!snapshotProvider.TryUseReconciliationAuthority(
                            plan.AuthorityKey,
                            plan.CapturedManifestBarrierRevision,
                            plan.CapturedAuthorityGeneration,
                            lease =>
                            {
                    var update = ManifestWorkspace.ReloadReconciledManifest(
                        observeManifestBarrier.Uri,
                        observeManifestBarrier.Text,
                        observeManifestBarrier.CapturedManifestRevision);
                    if (update.Status
                        == VbaProjectManifestReconciliationStatus.Rejected)
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "observe",
                                observeManifestBarrier));
                        mutationRejected = true;
                        return false;
                    }

                    committedMutation = true;
                    if (update.Status
                            == VbaProjectManifestReconciliationStatus.Invalid
                        && update.Error is not null)
                    {
                        if (!update.RetainedLastKnownGood)
                        {
                            progress.Add(
                                CreateManifestProgress(
                                    observeManifestBarrier.Uri));
                        }

                        effects.Add(
                            new ReconciledManifestValidationFailedEffect(
                                observeManifestBarrier.Uri,
                                update.Error));
                    }
                    else if (update.Status
                        == VbaProjectManifestReconciliationStatus.Applied)
                    {
                        progress.Add(
                            CreateManifestProgress(
                                observeManifestBarrier.Uri));
                        if (observeManifestBarrier.HadValidationFailure)
                        {
                            effects.Add(
                                new ReconciledManifestValidationRecoveredEffect(
                                    observeManifestBarrier.Uri));
                        }
                    }
                    return true;
                            },
                            out _))
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "authority-lease",
                                observeManifestBarrier));
                        mutationRejected = true;
                    }

                    break;
                }
                case DeleteObservedManifestBarrierChange
                    deleteObservedManifestBarrier:
                {
                    if (!snapshotProvider.TryUseReconciliationAuthority(
                            plan.AuthorityKey,
                            plan.CapturedManifestBarrierRevision,
                            plan.CapturedAuthorityGeneration,
                            lease =>
                            {
                    var update = ManifestWorkspace.DeleteReconciledManifest(
                        deleteObservedManifestBarrier.Uri,
                        deleteObservedManifestBarrier
                            .CapturedManifestRevision);
                    if (update.Status
                        == VbaProjectManifestReconciliationStatus.Rejected)
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "delete-observed",
                                deleteObservedManifestBarrier));
                        mutationRejected = true;
                        return false;
                    }

                    committedMutation = true;
                    if (update.Status
                        == VbaProjectManifestReconciliationStatus.Applied)
                    {
                        progress.Add(
                            CreateManifestProgress(
                                deleteObservedManifestBarrier.Uri));
                        if (deleteObservedManifestBarrier
                            .HadValidationFailure)
                        {
                            effects.Add(
                                new ReconciledManifestValidationRecoveredEffect(
                                    deleteObservedManifestBarrier.Uri));
                        }
                    }
                    return true;
                            },
                            out _))
                    {
                        progress.Add(
                            CreateRejectedProgress(
                                "authority-lease",
                                deleteObservedManifestBarrier));
                        mutationRejected = true;
                    }

                    break;
                }
                case ReloadChange reload:
                    if (ReloadReconciledSourceDocument(
                            reload.Uri,
                            reload.Text,
                            reload.CapturedWorkspaceRevision,
                            CancellationToken.None))
                    {
                        snapshotProvider.CommitReconciledSourceBaseline(
                            reload.AuthorityKey,
                            new VbaProjectDiskKnownSource(
                                reload.Uri,
                                Path.GetFullPath(reload.FullPath),
                                reload.Text,
                                reload.ContentIdentity));
                        projectDiagnosticsCandidates.Add(reload.Uri);
                        committedMutation = true;
                    }

                    break;
                case DecodeFailureChange decodeFailure:
                    if (CommitReconciledDiskSourceFailure(
                            decodeFailure.Failure,
                            decodeFailure.CapturedWorkspaceRevision))
                    {
                        snapshotProvider
                            .CommitDeletedReconciledSourceBaseline(
                                decodeFailure.AuthorityKey,
                                decodeFailure.Failure.Uri);
                        effects.Add(
                            new ReconciledSourceDiagnosticsEffect(
                                decodeFailure.Failure.Uri));
                        projectDiagnosticsCandidates.Add(
                            decodeFailure.Failure.Uri);
                        committedMutation = true;
                    }

                    break;
                case DeleteChange delete:
                    if (DeleteReconciledSourceDocument(
                            delete.Uri,
                            delete.CapturedWorkspaceRevision,
                            CancellationToken.None))
                    {
                        snapshotProvider
                            .CommitDeletedReconciledSourceBaseline(
                                delete.AuthorityKey,
                                delete.Uri);
                        effects.Add(
                            new ReconciledSourceDiagnosticsClearedEffect(
                                delete.Uri));
                        projectDiagnosticsCandidates.Add(delete.Uri);

                        committedMutation = true;
                    }

                    break;
                case ReleaseSourceOwnershipChange release:
                    snapshotProvider.ReleaseReconciledSourceOwnership(
                        release.AuthorityKey,
                        release.Uri);
                    projectDiagnosticsCandidates.Add(release.Uri);
                    committedMutation = true;
                    break;
            }

            if (mutationRejected)
            {
                rejectedMutationTail = true;
                requiresFollowUp = true;
                break;
            }

            if (manifestAuthorityMutated
                || isManifestChange
                    && ManifestWorkspace.Version != manifestVersionBefore)
            {
                foreach (var affectedSourceUri in
                    change.CapturedManifestSourceUris
                        .Concat(change.CapturedProjectSourceUris)
                        .Concat(trackedUris.Where(uri =>
                            IsPotentiallyAffectedSource(change, uri))))
                {
                    projectDiagnosticsCandidates.Add(affectedSourceUri);
                }

                requiresFollowUp = true;
                if (!progress.Any(
                        item => item.Kind
                            == VbaProjectReconciliationProgressKind
                                .ManifestCommitted))
                {
                    progress.Add(CreateManifestProgress(change.Uri));
                }

                break;
            }
        }

        AddChangedOpenAuthorityEffects(
            initialOpenAuthorities,
            effects,
            CancellationToken.None);
        AddProjectDiagnosticsEffects(
            projectDiagnosticsCandidates,
            effects);
        var outcome = rejectedMutationTail
            ? committedMutation
                ? VbaProjectReconciliationCommitOutcome
                    .CommittedWithRejectedTail
                : VbaProjectReconciliationCommitOutcome.RejectedBeforeWrite
            : VbaProjectReconciliationCommitOutcome.Committed;
        return new VbaProjectReconciliationCommitResult(
            outcome,
            requiresFollowUp,
            progress,
            effects);
    }

    private void AddProjectDiagnosticsEffects(
        IEnumerable<string> candidates,
        List<VbaProjectReconciliationEffect> effects)
    {
        var anchors = candidates
            .DistinctBy(
                VbaProjectIdentityModel.GetDocumentStableKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(uri =>
            {
                ManifestWorkspace.TryResolveKnownState(
                    uri,
                    out var resolution);
                return new
                {
                    Uri = uri,
                    ProjectKey = VbaProjectSnapshotIdentity
                        .Create(uri, resolution)
                        .Key
                };
            })
            .GroupBy(candidate => candidate.ProjectKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(
                    candidate => candidate.Uri,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Uri, StringComparer.Ordinal)
                .First()
                .Uri)
            .OrderBy(uri => uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(uri => uri, StringComparer.Ordinal);
        foreach (var anchor in anchors)
        {
            effects.Add(new ReconciledProjectDiagnosticsEffect(anchor));
        }
    }

    private void CaptureOpenAuthorities(
        ReconciliationChange change,
        Dictionary<VbaDocumentIdentity, CapturedOpenAuthority>
            initialOpenAuthorities,
        CancellationToken cancellationToken)
    {
        var capturedOpenSources =
            change.CapturedOpenSourceUris
                .Select(uri => VbaProjectIdentityModel.TryIdentifyDocument(
                    uri,
                    out var identity)
                        ? identity
                        : (VbaDocumentIdentity?)null)
                .Where(identity => identity is not null)
                .Select(identity => identity!.Value)
                .ToHashSet();
        foreach (var sourceUri in GetOpenDocumentUris(cancellationToken)
            .OrderBy(uri => uri, StringComparer.OrdinalIgnoreCase))
        {
            if (!VbaProjectIdentityModel.TryIdentifyDocument(
                    sourceUri,
                    out var sourceIdentity)
                || initialOpenAuthorities.ContainsKey(sourceIdentity)
                || !IsPotentiallyAffectedSource(change, sourceUri))
            {
                continue;
            }

            if (change.PreviousResolution is not null
                && capturedOpenSources.Contains(sourceIdentity))
            {
                initialOpenAuthorities[sourceIdentity] =
                    new CapturedOpenAuthority(
                        sourceUri,
                        change.PreviousResolution);
            }
            else if (ManifestWorkspace.TryResolveKnownState(
                    sourceUri,
                    out var knownResolution))
            {
                initialOpenAuthorities[sourceIdentity] =
                    new CapturedOpenAuthority(sourceUri, knownResolution);
            }
            else
            {
                initialOpenAuthorities[sourceIdentity] =
                    new CapturedOpenAuthority(sourceUri, Resolution: null);
            }
        }
    }

    private void AddChangedOpenAuthorityEffects(
        IReadOnlyDictionary<VbaDocumentIdentity, CapturedOpenAuthority>
            initialOpenAuthorities,
        List<VbaProjectReconciliationEffect> effects,
        CancellationToken cancellationToken)
    {
        var currentlyOpen = GetOpenDocumentUris(cancellationToken)
            .Select(uri => VbaProjectIdentityModel.TryIdentifyDocument(
                uri,
                out var identity)
                    ? identity
                    : (VbaDocumentIdentity?)null)
            .Where(identity => identity is not null)
            .Select(identity => identity!.Value)
            .ToHashSet();
        foreach (var (sourceIdentity, capturedAuthority) in
            initialOpenAuthorities.OrderBy(
                item => item.Value.Uri,
                StringComparer.OrdinalIgnoreCase))
        {
            if (!currentlyOpen.Contains(sourceIdentity))
            {
                continue;
            }

            var sourceUri = capturedAuthority.Uri;
            var previousResolution = capturedAuthority.Resolution;
            if (previousResolution is null
                || !ManifestWorkspace.TryResolveKnownState(
                    sourceUri,
                    out var currentResolution)
                || VbaProjectIdentityModel.Relate(
                        sourceIdentity,
                        previousResolution,
                        currentResolution)
                    .Kind != VbaProjectAuthorityRelationKind.Same)
            {
                effects.Add(
                    new ReconciledProjectAuthorityTransferredEffect(
                        sourceUri));
            }
        }
    }

    private sealed record CapturedOpenAuthority(
        string Uri,
        VbaProjectResolution? Resolution);

    private static bool IsPotentiallyAffectedSource(
        ReconciliationChange change,
        string sourceUri)
    {
        var sourcePath = VbaProjectResolver.TryGetLocalPath(sourceUri);
        if (sourcePath is null)
        {
            return change.CapturedOpenSourceUris.Any(
                captured => VbaProjectIdentityModel.SameDocument(
                    captured,
                    sourceUri));
        }

        if (change.CapturedOpenSourceUris.Any(
                captured => VbaProjectIdentityModel.SameDocument(
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

    private static bool IsManifestChange(ReconciliationChange change)
        => change is ReplaceDeletedManifestAuthorityChange
            or ReloadManifestChange
            or TransferInvalidManifestAuthorityChange
            or ObserveManifestBarrierChange
            or DeleteObservedManifestBarrierChange;

    private static VbaProjectReconciliationProgress
        CreateManifestProgress(string uri)
        => new(
            VbaProjectReconciliationProgressKind.ManifestCommitted,
            VbaProjectIdentityModel.GetDocumentStableKey(uri));

    private static VbaProjectReconciliationProgress CreateRejectedProgress(
        string reason,
        ReconciliationChange change)
    {
        var changeFingerprint =
            change is ReplaceDeletedManifestAuthorityChange replace
                ? string.Join(
                    ",",
                    replace.DeletedManifests.Select(
                        deleted =>
                            $"{VbaProjectIdentityModel.GetDocumentStableKey(deleted.Uri)}@{deleted.CapturedRevision}"))
                    + $"|fallback={VbaProjectIdentityModel.GetDocumentStableKey(replace.FallbackUri)}"
                    + $"@{replace.CapturedFallbackRevision}"
                : $"{VbaProjectIdentityModel.GetDocumentStableKey(change.Uri)}"
                    + $"@{change.CapturedWorkspaceRevision}";
        return new(
            VbaProjectReconciliationProgressKind.MutationRejected,
            $"{reason}:{change.GetType().Name}"
                + $":{change.AuthorityKey}"
                + $":barrier={change.CapturedManifestBarrierRevision}"
                + $":generation={change.CapturedAuthorityGeneration}"
                + $":{changeFingerprint}");
    }

}
