using System.Text.Json;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaDebugAdapter.Protocol;

namespace VbaDebugAdapter.Cli;

internal sealed class DebugRestartPreparation(DebugSessionId sessionId) : IDisposable
{
    private DebugRestartGeneration generation = DebugRestartGeneration.Initial;
    private int lastRequestSequence = -1;

    private PendingDebugRestartRequest? Pending { get; set; }

    private DebugRestartSwapAuthority? ActiveSwap { get; set; }

    public string? Begin(
        DapRequest request,
        StandaloneVbaDebugLaunchRequest? activeLaunch,
        IStandaloneVbaDebugRunningSession? session,
        bool launchPending)
    {
        if (launchPending || Pending is not null || ActiveSwap is not null)
        {
            return "DebugLaunchBusy: A VBA debug restart preparation is already pending.";
        }
        if (session is null || activeLaunch?.RestartPreparation is not { } descriptor)
        {
            return "DebugSetupError: The active VBA debug session is not bound for restart.";
        }
        if (request.Sequence <= lastRequestSequence)
        {
            return "DebugSetupError: VBA restart request sequences must increase monotonically.";
        }
        if (generation.Value == int.MaxValue)
        {
            return "DebugSetupError: The VBA restart generation is exhausted.";
        }

        generation = generation.Next();
        lastRequestSequence = request.Sequence;
        Pending = new PendingDebugRestartRequest(
            request,
            new DebugRestartLaunchBinding(
                sessionId,
                session,
                activeLaunch.ProjectRoot,
                activeLaunch.DocumentName,
                activeLaunch.WorkbookFileName,
                session.TargetModuleName,
                session.TargetProcedureName,
                null,
                null,
                descriptor.Id,
                generation,
                request.Sequence));
        return null;
    }

    public PendingDebugRestartRequest? ConsumeNotification(JsonElement arguments)
    {
        var pending = Pending;
        return pending is not null && MatchesCorrelation(arguments, pending.Binding)
            ? TakePending()
            : null;
    }

    public PendingDebugRestartRequest? TakePending()
    {
        var pending = Pending;
        Pending = null;
        return pending;
    }

    public DebugRestartSwapAuthority StartBuild(DebugRestartLaunchBinding binding)
    {
        if (Pending is not null || ActiveSwap is not null)
        {
            throw new InvalidOperationException("A restart preparation is already active.");
        }
        return ActiveSwap = new DebugRestartSwapAuthority(binding);
    }

    public void CompleteLaunch(StandaloneVbaDebugLaunchRequest? activeLaunch = null)
    {
        generation = DebugRestartGeneration.Max(
            generation,
            activeLaunch?.RestartPreparation?.Generation ?? DebugRestartGeneration.Initial);
        ActiveSwap?.Dispose();
        ActiveSwap = null;
    }

    public void Cancel()
    {
        ActiveSwap?.InvalidateForCancellation();
    }

    public void Dispose()
    {
        Pending = null;
        Cancel();
        CompleteLaunch();
    }

    private static bool MatchesCorrelation(
        JsonElement arguments,
        DebugRestartLaunchBinding binding)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (property.Name is "sessionId" or "preparationId" or
                "restartRequestSequence" or "generation")
            {
                if (!seen.Add(property.Name))
                {
                    return false;
                }
            }
        }
        if (seen.Count != 4)
        {
            return false;
        }
        var session = arguments.GetProperty("sessionId");
        var preparation = arguments.GetProperty("preparationId");
        var sequence = arguments.GetProperty("restartRequestSequence");
        var notificationGeneration = arguments.GetProperty("generation");
        return session.ValueKind == JsonValueKind.String &&
            session.GetString() == binding.SessionId.Value &&
            preparation.ValueKind == JsonValueKind.String &&
            preparation.GetString() == binding.PreparationId.Value &&
            sequence.ValueKind == JsonValueKind.Number &&
            sequence.TryGetInt32(out var requestSequence) &&
            requestSequence == binding.DapRequestSequence &&
            notificationGeneration.ValueKind == JsonValueKind.Number &&
            notificationGeneration.TryGetInt32(out var generationValue) &&
            generationValue == binding.Generation.Value;
    }
}

internal sealed record PendingDebugRestartRequest(
    DapRequest Request,
    DebugRestartLaunchBinding Binding)
{
    public DebugRestartLaunchBinding BindRequestedTarget(string? module, string? procedure)
        => Binding with { RequestedModuleName = module, RequestedProcedureName = procedure };
}

internal sealed class DebugRestartSwapAuthority : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource invalidation = new();
    private RestartSwapState state;

    public DebugRestartSwapAuthority(DebugRestartLaunchBinding binding)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        var weakAuthority = new WeakReference<DebugRestartSwapAuthority>(this);
        _ = binding.BoundSession.Completion.ContinueWith(
            static (_, state) =>
            {
                var weakAuthority =
                    (WeakReference<DebugRestartSwapAuthority>)state!;
                if (weakAuthority.TryGetTarget(out var authority))
                {
                    authority.Invalidate(RestartSwapState.SessionEnded);
                }
            },
            weakAuthority,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public DebugRestartLaunchBinding Binding { get; }

    public CancellationToken InvalidationToken => invalidation.Token;

    public bool SessionEnded
    {
        get
        {
            lock (gate)
            {
                return state == RestartSwapState.SessionEnded;
            }
        }
    }

    public void InvalidateForCancellation() =>
        Invalidate(RestartSwapState.Cancelled);

    public DebugRestartLaunchBinding ClaimForSwap(
        DebugRestartLaunchBinding? preparedBinding,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (state == RestartSwapState.SessionEnded)
            {
                throw new DebugSetupException(
                    "The owned VBA debug session exited during restart build before replacement committed.");
            }
            if (state is RestartSwapState.Cancelled or RestartSwapState.Disposed)
            {
                throw new OperationCanceledException(
                    "The VBA debug restart swap was cancelled.",
                    cancellationToken);
            }
            if (state == RestartSwapState.Claimed)
            {
                throw new DebugSetupException(
                    "The prepared VBA restart launch binding is stale.");
            }
            if (cancellationToken.IsCancellationRequested)
            {
                SetInvalidated(RestartSwapState.Cancelled);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (preparedBinding is null ||
                !Binding.HasSameIdentityAs(preparedBinding))
            {
                throw new DebugSetupException(
                    "The prepared VBA restart launch binding is stale.");
            }
            if (!Binding.IsBoundSessionCurrent)
            {
                SetInvalidated(RestartSwapState.SessionEnded);
                throw new DebugSetupException(
                    "The owned VBA debug session exited during restart build before replacement committed.");
            }

            state = RestartSwapState.Claimed;
            return Binding with { };
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (state == RestartSwapState.Pending)
            {
                state = RestartSwapState.Disposed;
            }
            invalidation.Dispose();
        }
    }

    private void Invalidate(RestartSwapState invalidatedState)
    {
        lock (gate)
        {
            if (state == RestartSwapState.Pending)
            {
                SetInvalidated(invalidatedState);
            }
        }
    }

    private void SetInvalidated(RestartSwapState invalidatedState)
    {
        state = invalidatedState;
        invalidation.Cancel();
    }

    private enum RestartSwapState
    {
        Pending,
        Claimed,
        SessionEnded,
        Cancelled,
        Disposed
    }
}

internal sealed record DebugRestartLaunchBinding(
    DebugSessionId SessionId,
    IStandaloneVbaDebugRunningSession BoundSession,
    string CanonicalProjectRoot,
    string DocumentName,
    string WorkbookFileName,
    string TargetModuleName,
    string TargetProcedureName,
    string? RequestedModuleName,
    string? RequestedProcedureName,
    DebugRestartPreparationId PreparationId,
    DebugRestartGeneration Generation,
    int DapRequestSequence)
{
    public void ValidateLaunch(
        StandaloneVbaDebugLaunchRequest request,
        string canonicalProjectRoot,
        DebugTargetProcedure target,
        DebugSessionId workspaceSessionId)
    {
        var descriptor = request.RestartPreparation;
        if (descriptor is null ||
            SessionId != workspaceSessionId ||
            descriptor.Id != PreparationId ||
            descriptor.Generation != Generation ||
            !canonicalProjectRoot.Equals(
                CanonicalProjectRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !request.DocumentName.Equals(
                DocumentName,
                StringComparison.OrdinalIgnoreCase) ||
            !request.WorkbookFileName.Equals(
                WorkbookFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !target.ModuleName.Equals(
                TargetModuleName,
                StringComparison.OrdinalIgnoreCase) ||
            !target.ProcedureName.Equals(
                TargetProcedureName,
                StringComparison.OrdinalIgnoreCase) ||
            (RequestedModuleName is not null &&
             !RequestedModuleName.Equals(
                 TargetModuleName,
                 StringComparison.OrdinalIgnoreCase)) ||
            (RequestedProcedureName is not null &&
             !RequestedProcedureName.Equals(
                 TargetProcedureName,
                 StringComparison.OrdinalIgnoreCase)) ||
            !BoundSession.TargetModuleName.Equals(
                TargetModuleName,
                StringComparison.OrdinalIgnoreCase) ||
            !BoundSession.TargetProcedureName.Equals(
                TargetProcedureName,
                StringComparison.OrdinalIgnoreCase) ||
            DapRequestSequence < 0 ||
            BoundSession.Completion.IsCompleted)
        {
            throw new DebugSetupException(
                "The fresh VBA restart launch does not match its bound session, target, or request identity.");
        }
    }

    public bool IsBoundSessionCurrent =>
        !BoundSession.Completion.IsCompleted &&
        BoundSession.TargetModuleName.Equals(
            TargetModuleName,
            StringComparison.OrdinalIgnoreCase) &&
        BoundSession.TargetProcedureName.Equals(
            TargetProcedureName,
            StringComparison.OrdinalIgnoreCase);

    public bool HasSameIdentityAs(DebugRestartLaunchBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(BoundSession, other.BoundSession) &&
               SessionId == other.SessionId &&
               CanonicalProjectRoot.Equals(
                   other.CanonicalProjectRoot,
                   StringComparison.OrdinalIgnoreCase) &&
               DocumentName.Equals(
                   other.DocumentName,
                   StringComparison.OrdinalIgnoreCase) &&
               WorkbookFileName.Equals(
                   other.WorkbookFileName,
                   StringComparison.OrdinalIgnoreCase) &&
               TargetModuleName.Equals(
                   other.TargetModuleName,
                   StringComparison.OrdinalIgnoreCase) &&
               TargetProcedureName.Equals(
                   other.TargetProcedureName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   RequestedModuleName,
                   other.RequestedModuleName,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   RequestedProcedureName,
                   other.RequestedProcedureName,
                   StringComparison.OrdinalIgnoreCase) &&
               PreparationId == other.PreparationId &&
               Generation == other.Generation &&
               DapRequestSequence == other.DapRequestSequence;
    }
}
