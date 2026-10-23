using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;

namespace VbaLanguageServer.Rename;

internal sealed record RenameInput(string Uri, int Line, int Character, string NewName);
internal sealed record RenameCapabilities(bool CanConfirm, bool SupportsRenameFile);
internal enum RenameDecision { Continue, Cancel }
internal sealed record RenameConfirmationInput(string Id, RenameDecision Decision);
internal sealed record RenameChallenge(string Id, string OriginalName, string NewName,
    IReadOnlyList<VbaRenameConflict> Conflicts, IReadOnlyList<VbaRenameImpact> Impacts,
    IReadOnlyList<string> RetainedPaths);

internal abstract record RenameResult
{
    internal sealed record Ready(VbaRenamePlan Plan) : RenameResult;
    internal sealed record NoChange : RenameResult;
    internal sealed record Rejected(VbaRenameFailure Failure) : RenameResult;
    internal sealed record Challenge(RenameChallenge Details) : RenameResult;
    internal sealed record InvalidParameters : RenameResult;
}

// Session-owned planning and ownership boundary. Only decoded inputs and closed
// results cross it; JSON-RPC decoding and edit/error projection stay outside.
internal sealed class RenameModule(IVbaInteractiveWorkspaceCapture workspace) : IDisposable
{
    private readonly object gate = new();
    private long generation;
    private bool stopped;
    private Pending? pending;

    internal Operation CaptureRename(RenameInput? input, RenameCapabilities capabilities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Pending? previous;
        long current;
        lock (gate)
        {
            if (stopped) return CapturedOperation.Immediate(this, Expired(), token);
            current = ++generation;
            previous = pending;
            pending = null;
        }
        previous?.Dispose();
        // Even invalid decoded parameters/name reached Rename Capture and have
        // already invalidated unconsumed consent and older-generation offers.
        if (input is null) return CapturedOperation.Immediate(this, new RenameResult.InvalidParameters(), token);
        if (VbaSemanticInventory.ValidateRenameName(input.NewName) is { } failure)
            return CapturedOperation.Immediate(this, new RenameResult.Rejected(failure), token);
        var capture = workspace.CaptureRenameProjectSnapshot(input.Uri, token);
        return new CapturedOperation(this, current, input, capabilities, capture, token);
    }

    internal Operation CaptureConfirmation(RenameConfirmationInput? input, RenameCapabilities capabilities, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!capabilities.CanConfirm || input is null)
            return CapturedOperation.Immediate(this, new RenameResult.InvalidParameters(), token);
        lock (gate)
        {
            if (pending is not { } match || match.Id != input.Id)
                return CapturedOperation.Immediate(this, Expired(), token);
            pending = null;
            // Consumption and lease transfer occur together on ordered Capture.
            return new CapturedOperation(this, match, input.Decision, match.TakeCapture(), token);
        }
    }

    private RenameResult ExecuteRename(CapturedOperation operation, CancellationToken token)
    {
        var input = operation.Input!;
        var capture = operation.Capture;
        var capabilities = operation.Capabilities!;
        var requiresTemplate = capture.SemanticInventory.RequiresSourceTemplateIdentityFence(
            input.Uri, input.Line, input.Character, input.NewName, capture.ProjectIdentityRead);
        var evidence = new RenameEvidencePolicy(capture, requiresTemplate, capabilities.SupportsRenameFile);
        if (evidence.ValidateEvidence() is { } initial) return new RenameResult.Rejected(initial);
        var rename = capture.SemanticInventory.CreateRenameResult(input.Uri, input.Line, input.Character,
            input.NewName, token, capture.ProjectIdentityRead, capabilities.CanConfirm
                ? VbaRenameCollisionMode.PrepareConfirmation : VbaRenameCollisionMode.Reject);
        token.ThrowIfCancellationRequested();
        if (evidence.ValidateEvidence() is { } semanticEvidence) return new RenameResult.Rejected(semanticEvidence);
        if (rename.Failure is { } semantic) return new RenameResult.Rejected(semantic);
        if (rename.Plan is null) return new RenameResult.NoChange();
        VbaRenamePathDecision? paths = null;
        if (capabilities.CanConfirm)
        {
            var prepared = evidence.PreparePaths(rename.Plan);
            if (prepared.Failure is { } pathFailure) return new RenameResult.Rejected(pathFailure);
            paths = prepared.Decision;
            if (paths?.RetainOriginalPaths == true)
            {
                rename = capture.SemanticInventory.CreateRenameResult(input.Uri, input.Line, input.Character,
                    input.NewName, token, capture.ProjectIdentityRead,
                    VbaRenameCollisionMode.PrepareConfirmation, retainOriginalPaths: true);
                if (evidence.ValidateEvidence(paths) is { } retainedEvidence) return new RenameResult.Rejected(retainedEvidence);
                if (rename.Failure is { } retainedFailure) return new RenameResult.Rejected(retainedFailure);
                if (rename.Plan is null) return new RenameResult.Rejected(new("analysisIncomplete", "The captured Rename target is unavailable."));
            }
        }
        if (evidence.ValidateEvidence(paths) is { } plannedEvidence) return new RenameResult.Rejected(plannedEvidence);
        if (capture.AnalysisFailureMessage is { } analysis) return new RenameResult.Rejected(new("analysisIncomplete", analysis));
        var final = evidence.ValidatePlan(rename.Plan, paths);
        if (final.Failure is { } preflight) return new RenameResult.Rejected(preflight);
        if (!capabilities.CanConfirm || (rename.CollisionReview is null && paths?.RetainOriginalPaths != true))
            return new RenameResult.Ready(final.Plan);
        var originalName = rename.CollisionReview?.OriginalName
            ?? capture.SemanticInventory.PrepareRename(input.Uri, input.Line, input.Character)?.Placeholder;
        if (originalName is null) return new RenameResult.Rejected(new("analysisIncomplete", "The original Rename identity is unavailable."));
        var id = Guid.NewGuid().ToString("N");
        // Construct every projected fact before transferring the capture. No
        // further semantic work uses a lease that another Capture can consume.
        var challenge = new RenameChallenge(id, originalName, input.NewName,
            (rename.CollisionReview?.Conflicts ?? []).Concat(paths?.Conflicts ?? []).ToArray(),
            rename.CollisionReview?.Impacts ?? [], paths?.RetainedPaths ?? []);
        token.ThrowIfCancellationRequested();
        var offer = new Pending(id, operation.Generation, final.Plan, evidence, paths, operation.TakeCapture());
        var accepted = false;
        lock (gate)
        {
            if (!stopped && operation.Generation == generation)
            {
                pending = offer;
                operation.Offered = new OfferIdentity(id, operation.Generation);
                accepted = true;
            }
        }
        if (!accepted)
        {
            offer.Dispose();
            return Expired();
        }
        return new RenameResult.Challenge(challenge);
    }

    private static RenameResult ExecuteConfirmation(CapturedOperation operation, CancellationToken token)
    {
        if (operation.Decision == RenameDecision.Cancel) return new RenameResult.NoChange();
        var retained = operation.Confirmation!;
        if (retained.Evidence.ValidatePlan(retained.Plan, retained.Paths, retainCompletePlan: true).Failure is { } failure)
            return new RenameResult.Rejected(failure);
        token.ThrowIfCancellationRequested();
        return new RenameResult.Ready(retained.Plan);
    }

    private void Discard(OfferIdentity identity)
    {
        Pending? discarded;
        lock (gate)
        {
            discarded = pending is { } current && current.Id == identity.Id && current.Generation == identity.Generation ? current : null;
            if (discarded is not null) pending = null;
        }
        discarded?.Dispose();
    }

    public void Dispose()
    {
        Pending? discarded;
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            generation++;
            discarded = pending;
            pending = null;
        }
        discarded?.Dispose();
        // Consumed in-flight operations belong to the scheduler's cancel/drain
        // lifetime, never to a later Rename or a response delivery callback.
    }

    private static RenameResult Expired() => new RenameResult.Rejected(new("confirmationExpired",
        "This Rename confirmation is no longer current. Start a fresh Rename operation."));

    internal sealed record OfferIdentity(string Id, long Generation);

    internal abstract class Operation : IDisposable
    {
        internal abstract RenameResult Execute(CancellationToken token);
        internal abstract void ResponseFailed();
        public abstract void Dispose();
    }

    private sealed class CapturedOperation : Operation
    {
        private readonly RenameModule owner;
        private readonly CancellationToken captureToken;
        private readonly RenameResult? immediate;
        private VbaRenameProjectSnapshotCapture? ownedCapture;
        private int executed;
        private int disposed;
        internal long Generation { get; }
        internal RenameInput? Input { get; }
        internal RenameCapabilities? Capabilities { get; }
        internal Pending? Confirmation { get; }
        internal RenameDecision Decision { get; }
        internal OfferIdentity? Offered { get; set; }
        internal VbaRenameProjectSnapshotCapture Capture => ownedCapture ?? throw new InvalidOperationException("Rename capture is no longer owned.");

        internal CapturedOperation(RenameModule owner, long generation, RenameInput input, RenameCapabilities capabilities,
            VbaRenameProjectSnapshotCapture capture, CancellationToken token)
        {
            this.owner = owner; Generation = generation; Input = input; Capabilities = capabilities;
            ownedCapture = capture; captureToken = token;
        }
        internal CapturedOperation(RenameModule owner, Pending pending, RenameDecision decision,
            VbaRenameProjectSnapshotCapture capture, CancellationToken token)
        {
            this.owner = owner; Confirmation = pending; Decision = decision; Generation = pending.Generation;
            ownedCapture = capture; captureToken = token;
        }
        private CapturedOperation(RenameModule owner, RenameResult immediate, CancellationToken token)
        { this.owner = owner; this.immediate = immediate; captureToken = token; }
        internal static CapturedOperation Immediate(RenameModule owner, RenameResult result, CancellationToken token) => new(owner, result, token);

        internal override RenameResult Execute(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.Exchange(ref executed, 1) != 0) throw new InvalidOperationException("Rename operation was already executed.");
            try
            {
                token.ThrowIfCancellationRequested();
                return immediate ?? (Confirmation is null ? owner.ExecuteRename(this, token) : ExecuteConfirmation(this, token));
            }
            finally { ReleaseCapture(); }
        }
        internal VbaRenameProjectSnapshotCapture TakeCapture() => Interlocked.Exchange(ref ownedCapture, null)
            ?? throw new InvalidOperationException("Rename capture was already transferred.");
        private void ReleaseCapture() => Interlocked.Exchange(ref ownedCapture, null)?.Dispose();
        internal override void ResponseFailed() { if (Offered is { } identity) owner.Discard(identity); }
        public override void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            ReleaseCapture();
            if (captureToken.IsCancellationRequested) ResponseFailed();
        }
    }

    internal sealed class Pending(string id, long generation, VbaRenamePlan plan, RenameEvidencePolicy evidence,
        VbaRenamePathDecision? paths, VbaRenameProjectSnapshotCapture capture) : IDisposable
    {
        private VbaRenameProjectSnapshotCapture? ownedCapture = capture;
        internal string Id { get; } = id;
        internal long Generation { get; } = generation;
        internal VbaRenamePlan Plan { get; } = plan;
        internal RenameEvidencePolicy Evidence { get; } = evidence;
        internal VbaRenamePathDecision? Paths { get; } = paths;
        internal VbaRenameProjectSnapshotCapture TakeCapture() => Interlocked.Exchange(ref ownedCapture, null)
            ?? throw new InvalidOperationException("Pending Rename capture was already transferred.");
        public void Dispose() => Interlocked.Exchange(ref ownedCapture, null)?.Dispose();
    }
}
