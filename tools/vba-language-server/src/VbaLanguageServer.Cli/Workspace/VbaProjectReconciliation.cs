using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.Workspace;

/// <summary>
/// Represents one manifest authority candidate captured for reconciliation.
/// </summary>
internal sealed record VbaProjectReconciliationManifestCandidate(
    VbaDocumentIdentity DocumentIdentity,
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
    VbaIdentifiedDocument ActiveDocument,
    VbaProjectResolution Resolution,
    long CapturedWorkspaceRevision,
    IReadOnlyList<VbaProjectReconciliationManifestCandidate> ManifestCandidates,
    IReadOnlyList<VbaProjectDiskKnownSource> KnownSources)
{
    public string ActiveUri => ActiveDocument.Uri;

    public VbaDocumentIdentity ActiveDocumentIdentity
        => ActiveDocument.Identity;

    /// <summary>
    /// Gets the manifest-barrier snapshot that owns this scan.
    /// </summary>
    public VbaProjectManifestBarrierSnapshot ManifestBarriers { get; init; } =
        new(
            Revision: 0,
            new Dictionary<VbaDocumentIdentity, bool>());

    /// <summary>
    /// Gets the structural incarnation of the captured reconciliation authority.
    /// </summary>
    public long AuthorityGeneration { get; init; }

    public IReadOnlyList<VbaProjectReconciliationManifestCandidate>
        ObservedManifestBarrierCandidates
    { get; init; } = [];

    public IReadOnlyList<VbaIdentifiedDocument> OpenSources
    { get; init; } = [];
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

internal enum VbaProjectReconciliationRejectionReason
{
    Scope,
    Replace,
    AuthorityLease,
    Reload,
    TransferInvalid,
    Observe,
    DeleteObserved
}

internal enum VbaProjectReconciliationMutationKind
{
    Reload,
    DecodeFailure,
    Delete,
    ReleaseSourceOwnership,
    ReplaceDeletedManifestAuthority,
    ReloadManifest,
    TransferInvalidManifestAuthority,
    ObserveManifestBarrier,
    DeleteObservedManifestBarrier
}

internal abstract class VbaProjectReconciliationProgressIdentity;

internal sealed class VbaProjectReconciliationManifestProgressIdentity
    : VbaProjectReconciliationProgressIdentity
{
    internal static VbaProjectReconciliationManifestProgressIdentity Instance
    { get; } = new();

    private VbaProjectReconciliationManifestProgressIdentity()
    {
    }
}

internal readonly record struct VbaProjectReconciliationDocumentRevisionIdentity(
    VbaDocumentIdentity DocumentIdentity,
    long Revision);

internal sealed class VbaProjectReconciliationRejectedProgressIdentity
    : VbaProjectReconciliationProgressIdentity,
      IEquatable<VbaProjectReconciliationRejectedProgressIdentity>
{
    private readonly VbaProjectReconciliationDocumentRevisionIdentity[]
        documentRevisions;

    internal VbaProjectReconciliationRejectedProgressIdentity(
        VbaProjectReconciliationRejectionReason reason,
        VbaProjectReconciliationMutationKind mutationKind,
        VbaProjectAuthorityIdentity authority,
        long manifestBarrierRevision,
        long authorityGeneration,
        IReadOnlyList<VbaProjectReconciliationDocumentRevisionIdentity>
            documentRevisions,
        VbaDocumentIdentity? fallbackDocumentIdentity,
        long fallbackRevision)
    {
        Reason = reason;
        MutationKind = mutationKind;
        Authority = authority;
        ManifestBarrierRevision = manifestBarrierRevision;
        AuthorityGeneration = authorityGeneration;
        this.documentRevisions = documentRevisions.ToArray();
        FallbackDocumentIdentity = fallbackDocumentIdentity;
        FallbackRevision = fallbackRevision;
    }

    internal VbaProjectReconciliationRejectionReason Reason { get; }

    internal VbaProjectReconciliationMutationKind MutationKind { get; }

    internal VbaProjectAuthorityIdentity Authority { get; }

    internal long ManifestBarrierRevision { get; }

    internal long AuthorityGeneration { get; }

    internal VbaDocumentIdentity? FallbackDocumentIdentity { get; }

    internal long FallbackRevision { get; }

    public bool Equals(
        VbaProjectReconciliationRejectedProgressIdentity? other)
        => other is not null
            && Reason == other.Reason
            && MutationKind == other.MutationKind
            && Authority == other.Authority
            && ManifestBarrierRevision == other.ManifestBarrierRevision
            && AuthorityGeneration == other.AuthorityGeneration
            && documentRevisions.SequenceEqual(other.documentRevisions)
            && Nullable.Equals(
                FallbackDocumentIdentity,
                other.FallbackDocumentIdentity)
            && FallbackRevision == other.FallbackRevision;

    public override bool Equals(object? obj)
        => obj is VbaProjectReconciliationRejectedProgressIdentity other
            && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Reason);
        hash.Add(MutationKind);
        hash.Add(Authority);
        hash.Add(ManifestBarrierRevision);
        hash.Add(AuthorityGeneration);
        foreach (var documentRevision in documentRevisions)
        {
            hash.Add(documentRevision);
        }

        hash.Add(FallbackDocumentIdentity);
        hash.Add(FallbackRevision);
        return hash.ToHashCode();
    }
}

internal sealed record VbaProjectReconciliationProgress(
    VbaProjectReconciliationProgressKind Kind,
    VbaProjectReconciliationProgressIdentity Identity);

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
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
{
    public VbaProjectResolution? PreviousResolution { get; init; }

    public IReadOnlyList<VbaDocumentIdentity> CapturedOpenSourceIdentities
    { get; init; } = [];

    public IReadOnlyList<string> CapturedProjectSourceUris { get; init; } = [];

    public IReadOnlyList<string> CapturedManifestSourceUris { get; init; } = [];
}

internal sealed record ReloadChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string FullPath,
    string Text,
    VbaProjectDiskContentIdentity ContentIdentity,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
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
        Failure.DocumentIdentity,
        Failure.Uri,
        CapturedWorkspaceRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record DeleteChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
        Uri,
        CapturedWorkspaceRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record ReleaseSourceOwnershipChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedWorkspaceRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
        Uri,
        CapturedWorkspaceRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record DeletedManifestCandidate(
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedRevision);

internal sealed record ReplaceDeletedManifestAuthorityChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    string Uri,
    IReadOnlyList<DeletedManifestCandidate> DeletedManifests,
    VbaIdentifiedDocument ActiveDocument,
    VbaProjectResolution Resolution,
    VbaDocumentIdentity? FallbackDocumentIdentity,
    string FallbackUri,
    string FallbackText,
    long CapturedFallbackRevision,
    bool ReloadFallbackManifest,
    bool FallbackHiddenByOpenOverlay,
    bool AuthorityTransferred,
    IReadOnlyList<VbaDocumentIdentity> RetainedPreviousSourceIdentities,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration)
    : ReconciliationChange(
        AuthorityKey,
        DeletedManifests[0].DocumentIdentity,
        Uri,
        DeletedManifests[0].CapturedRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration)
{
    public string ActiveUri => ActiveDocument.Uri;
}

internal sealed record ReloadManifestChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string Text,
    long CapturedManifestRevision,
    VbaIdentifiedDocument ActiveDocument,
    VbaProjectResolution? Resolution,
    VbaProjectResolution? InvalidFallbackResolution,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration,
    bool RetainPreviousAuthority,
    bool AuthorityTransferred,
    IReadOnlyList<VbaDocumentIdentity> RetainedPreviousSourceIdentities)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
        Uri,
        CapturedManifestRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration)
{
    public string ActiveUri => ActiveDocument.Uri;
}

internal sealed record TransferInvalidManifestAuthorityChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedManifestRevision,
    VbaIdentifiedDocument ActiveDocument,
    VbaProjectResolution Resolution,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration,
    IReadOnlyList<VbaDocumentIdentity> RetainedPreviousSourceIdentities)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
        Uri,
        CapturedManifestRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration)
{
    public string ActiveUri => ActiveDocument.Uri;
}

internal sealed record ObserveManifestBarrierChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    string Text,
    long CapturedManifestRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration,
    bool HadValidationFailure)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
        Uri,
        CapturedManifestRevision,
        CapturedManifestBarrierRevision,
        CapturedAuthorityGeneration);

internal sealed record DeleteObservedManifestBarrierChange(
    VbaProjectAuthorityIdentity AuthorityKey,
    VbaDocumentIdentity DocumentIdentity,
    string Uri,
    long CapturedManifestRevision,
    long CapturedManifestBarrierRevision,
    long CapturedAuthorityGeneration,
    bool HadValidationFailure)
    : ReconciliationChange(
        AuthorityKey,
        DocumentIdentity,
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
        IReadOnlyList<VbaIdentifiedDocument> trackedDocuments;
        lock (gate)
        {
            trackedDocuments = CaptureTrackedDocuments();
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
                    : [CreateRejectedProgress(
                        VbaProjectReconciliationRejectionReason.Scope,
                        rejectedMutation)],
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
                                                    new VbaIdentifiedDocument(
                                                        deleted.DocumentIdentity,
                                                        deleted.Uri),
                                                    deleted.CapturedRevision))
                                            .ToArray(),
                                        replace.ReloadFallbackManifest
                                            ? new VbaProjectManifestReconciliationTarget(
                                                new VbaIdentifiedDocument(
                                                    replace.FallbackDocumentIdentity
                                                        ?? throw new InvalidOperationException(
                                                            "A reconciliation fallback manifest has no document identity."),
                                                    replace.FallbackUri),
                                                replace.CapturedFallbackRevision)
                                            : null,
                                        replace.ReloadFallbackManifest
                                            ? replace.FallbackText
                                            : null);
                                    if (!replacement.Accepted)
                                    {
                                        progress.Add(
                                        CreateRejectedProgress(
                                            VbaProjectReconciliationRejectionReason.Replace,
                                            replace));
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

                                        progress.Add(CreateManifestProgress());
                                        mutationEffects.Add(
                                        new ReconciledManifestDeletedEffect(deleted.Uri));
                                    }

                                    var reloadUpdate = replacement.ReloadedManifest;
                                    if (!replace.FallbackHiddenByOpenOverlay
                                    && reloadUpdate?.Update.Status
                                        == VbaProjectManifestReconciliationStatus.Applied)
                                    {
                                        progress.Add(CreateManifestProgress());
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
                                            progress.Add(CreateManifestProgress());
                                        }

                                        mutationEffects.Add(
                                        new ReconciledManifestValidationFailedEffect(
                                            reloadUpdate.Uri,
                                            reloadUpdate.Update.Error));
                                    }

                                    lease.CommitManifestScope(
                                        replace.ActiveDocument,
                                        replace.Resolution,
                                        retainPreviousAuthority: false,
                                        replace.RetainedPreviousSourceIdentities,
                                        trackedDocuments);

                                    effects.AddRange(mutationEffects);
                                    committedMutation = true;
                                    manifestAuthorityMutated = true;
                                    if (replace.AuthorityTransferred)
                                    {
                                        progress.Add(CreateManifestProgress());
                                    }

                                    return true;
                                },
                                out _))
                        {
                            progress.Add(
                                CreateRejectedProgress(
                                    VbaProjectReconciliationRejectionReason
                                        .AuthorityLease,
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
                                    new VbaIdentifiedDocument(
                                        reloadManifest.DocumentIdentity,
                                        reloadManifest.Uri),
                                    reloadManifest.Text,
                                    reloadManifest.CapturedManifestRevision);
                                    if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Rejected)
                                    {
                                        progress.Add(
                                        CreateRejectedProgress(
                                            VbaProjectReconciliationRejectionReason.Reload,
                                            reloadManifest));
                                        mutationRejected = true;
                                        return false;
                                    }

                                    committedMutation = true;
                                    if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Applied)
                                    {
                                        lease.CommitManifestScope(
                                        reloadManifest.ActiveDocument,
                                        resolution!,
                                        reloadManifest.RetainPreviousAuthority,
                                        reloadManifest.RetainedPreviousSourceIdentities,
                                        trackedDocuments);
                                        progress.Add(CreateManifestProgress());
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
                                            progress.Add(CreateManifestProgress());
                                        }

                                        if (!update.RetainedLastKnownGood
                                        && reloadManifest.AuthorityTransferred
                                        && reloadManifest.InvalidFallbackResolution
                                            is not null)
                                        {
                                            lease.CommitManifestScope(
                                            reloadManifest.ActiveDocument,
                                            reloadManifest.InvalidFallbackResolution,
                                            retainPreviousAuthority: false,
                                            reloadManifest.RetainedPreviousSourceIdentities,
                                            trackedDocuments);
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
                                    VbaProjectReconciliationRejectionReason
                                        .AuthorityLease,
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
                                            new VbaIdentifiedDocument(
                                                transferInvalidManifest
                                                    .DocumentIdentity,
                                                transferInvalidManifest.Uri))
                                        != transferInvalidManifest
                                            .CapturedManifestRevision)
                                    {
                                        progress.Add(
                                        CreateRejectedProgress(
                                            VbaProjectReconciliationRejectionReason
                                                .TransferInvalid,
                                            transferInvalidManifest));
                                        mutationRejected = true;
                                        return false;
                                    }

                                    lease.CommitManifestScope(
                                        transferInvalidManifest.ActiveDocument,
                                        transferInvalidManifest.Resolution,
                                        retainPreviousAuthority: false,
                                        transferInvalidManifest.RetainedPreviousSourceIdentities,
                                        trackedDocuments);

                                    progress.Add(CreateManifestProgress());
                                    committedMutation = true;
                                    manifestAuthorityMutated = true;
                                    return true;
                                },
                                out _))
                        {
                            progress.Add(
                                CreateRejectedProgress(
                                    VbaProjectReconciliationRejectionReason
                                        .AuthorityLease,
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
                                    new VbaIdentifiedDocument(
                                        observeManifestBarrier.DocumentIdentity,
                                        observeManifestBarrier.Uri),
                                    observeManifestBarrier.Text,
                                    observeManifestBarrier.CapturedManifestRevision);
                                    if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Rejected)
                                    {
                                        progress.Add(
                                        CreateRejectedProgress(
                                            VbaProjectReconciliationRejectionReason.Observe,
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
                                            progress.Add(CreateManifestProgress());
                                        }

                                        effects.Add(
                                        new ReconciledManifestValidationFailedEffect(
                                            observeManifestBarrier.Uri,
                                            update.Error));
                                    }
                                    else if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Applied)
                                    {
                                        progress.Add(CreateManifestProgress());
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
                                    VbaProjectReconciliationRejectionReason
                                        .AuthorityLease,
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
                                    new VbaIdentifiedDocument(
                                        deleteObservedManifestBarrier
                                            .DocumentIdentity,
                                        deleteObservedManifestBarrier.Uri),
                                    deleteObservedManifestBarrier
                                        .CapturedManifestRevision);
                                    if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Rejected)
                                    {
                                        progress.Add(
                                        CreateRejectedProgress(
                                            VbaProjectReconciliationRejectionReason
                                                .DeleteObserved,
                                            deleteObservedManifestBarrier));
                                        mutationRejected = true;
                                        return false;
                                    }

                                    committedMutation = true;
                                    if (update.Status
                                    == VbaProjectManifestReconciliationStatus.Applied)
                                    {
                                        progress.Add(CreateManifestProgress());
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
                                    VbaProjectReconciliationRejectionReason
                                        .AuthorityLease,
                                    deleteObservedManifestBarrier));
                            mutationRejected = true;
                        }

                        break;
                    }
                case ReloadChange reload:
                    if (ReloadReconciledSourceDocument(
                            new VbaIdentifiedDocument(
                                reload.DocumentIdentity,
                                reload.Uri),
                            reload.Text,
                            reload.CapturedWorkspaceRevision,
                            CancellationToken.None))
                    {
                        snapshotProvider.CommitReconciledSourceBaseline(
                            reload.AuthorityKey,
                            new VbaProjectDiskKnownSource(
                                reload.DocumentIdentity,
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
                                decodeFailure.Failure.DocumentIdentity);
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
                            new VbaIdentifiedDocument(
                                delete.DocumentIdentity,
                                delete.Uri),
                            delete.CapturedWorkspaceRevision,
                            CancellationToken.None))
                    {
                        snapshotProvider
                            .CommitDeletedReconciledSourceBaseline(
                                delete.AuthorityKey,
                                delete.DocumentIdentity);
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
                        release.DocumentIdentity);
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
                        .Concat(trackedDocuments.Where(document =>
                            IsPotentiallyAffectedSource(
                                change,
                                document.Identity,
                                document.Uri))
                            .Select(document => document.Uri)))
                {
                    projectDiagnosticsCandidates.Add(affectedSourceUri);
                }

                requiresFollowUp = true;
                if (!progress.Any(
                        item => item.Kind
                            == VbaProjectReconciliationProgressKind
                                .ManifestCommitted))
                {
                    progress.Add(CreateManifestProgress());
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
        var anchors = VbaProjectIdentityModel
            .DistinctDocumentUris(candidates)
            .Select(uri =>
            {
                ManifestWorkspace.TryResolveKnownState(
                    uri,
                    out var resolution);
                return new
                {
                    Uri = uri,
                    ProjectIdentity = VbaProjectSnapshotIdentity
                        .Create(
                            RequireIdentifiedDocument(uri).Identity,
                            resolution)
                };
            })
            .GroupBy(candidate => candidate.ProjectIdentity)
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

    private static VbaIdentifiedDocument RequireIdentifiedDocument(
        string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? new VbaIdentifiedDocument(identity, uri)
            : throw new InvalidOperationException(
                "A reconciliation scope has no active document identity.");

    private void CaptureOpenAuthorities(
        ReconciliationChange change,
        Dictionary<VbaDocumentIdentity, CapturedOpenAuthority>
            initialOpenAuthorities,
        CancellationToken cancellationToken)
    {
        var capturedOpenSources =
            change.CapturedOpenSourceIdentities.ToHashSet();
        foreach (var source in GetOpenDocuments(cancellationToken)
            .OrderBy(
                document => document.Uri,
                StringComparer.OrdinalIgnoreCase))
        {
            var sourceUri = source.Uri;
            var sourceIdentity = source.Identity;
            if (initialOpenAuthorities.ContainsKey(sourceIdentity)
                || !IsPotentiallyAffectedSource(
                    change,
                    sourceIdentity,
                    sourceUri))
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
        var currentlyOpen = GetOpenDocuments(cancellationToken)
            .Select(document => document.Identity)
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
        VbaDocumentIdentity sourceIdentity,
        string sourceUri)
    {
        if (change.CapturedOpenSourceIdentities.Contains(sourceIdentity))
        {
            return true;
        }

        var sourcePath = VbaProjectResolver.TryGetLocalPath(sourceUri);
        if (sourcePath is null)
        {
            return false;
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
        CreateManifestProgress()
        => new(
            VbaProjectReconciliationProgressKind.ManifestCommitted,
            VbaProjectReconciliationManifestProgressIdentity.Instance);

    private static VbaProjectReconciliationProgress CreateRejectedProgress(
        VbaProjectReconciliationRejectionReason reason,
        ReconciliationChange change)
    {
        var replace = change as ReplaceDeletedManifestAuthorityChange;
        var documentRevisions = replace is null
            ?
            [
                new VbaProjectReconciliationDocumentRevisionIdentity(
                    change.DocumentIdentity,
                    change.CapturedWorkspaceRevision)
            ]
            : replace.DeletedManifests
                .Select(deleted =>
                    new VbaProjectReconciliationDocumentRevisionIdentity(
                        deleted.DocumentIdentity,
                        deleted.CapturedRevision))
                .ToArray();
        return new(
            VbaProjectReconciliationProgressKind.MutationRejected,
            new VbaProjectReconciliationRejectedProgressIdentity(
                reason,
                GetMutationKind(change),
                change.AuthorityKey,
                change.CapturedManifestBarrierRevision,
                change.CapturedAuthorityGeneration,
                documentRevisions,
                replace?.FallbackDocumentIdentity,
                replace?.CapturedFallbackRevision ?? 0));
    }

    private static VbaProjectReconciliationMutationKind GetMutationKind(
        ReconciliationChange change)
        => change switch
        {
            ReloadChange => VbaProjectReconciliationMutationKind.Reload,
            DecodeFailureChange =>
                VbaProjectReconciliationMutationKind.DecodeFailure,
            DeleteChange => VbaProjectReconciliationMutationKind.Delete,
            ReleaseSourceOwnershipChange =>
                VbaProjectReconciliationMutationKind.ReleaseSourceOwnership,
            ReplaceDeletedManifestAuthorityChange =>
                VbaProjectReconciliationMutationKind
                    .ReplaceDeletedManifestAuthority,
            ReloadManifestChange =>
                VbaProjectReconciliationMutationKind.ReloadManifest,
            TransferInvalidManifestAuthorityChange =>
                VbaProjectReconciliationMutationKind
                    .TransferInvalidManifestAuthority,
            ObserveManifestBarrierChange =>
                VbaProjectReconciliationMutationKind.ObserveManifestBarrier,
            DeleteObservedManifestBarrierChange =>
                VbaProjectReconciliationMutationKind
                    .DeleteObservedManifestBarrier,
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

}
