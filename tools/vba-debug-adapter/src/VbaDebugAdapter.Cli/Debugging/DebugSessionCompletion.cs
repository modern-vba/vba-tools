using VbaDebugAdapter.Infrastructure;

namespace VbaDebugAdapter.Debugging;

public enum DebugSessionEndReason
{
    WorkbookClosed,
    ProcessExited,
    Detached,
    ObservationFailed,
    Stopped
}

// Raw observations carry facts only. They never imply completed resource release.
internal sealed record DebugSessionTermination(DebugSessionEndReason Reason, int? ProcessExitCode);

public enum DebugSessionOwnership { OwnedExcel, RetainedSource }

/// <summary>Early execution authority; final cleanup completion is independent.</summary>
public sealed class DebugSessionLiveness
{
    private readonly CancellationTokenSource revoked = new();
    public bool IsLive => !revoked.IsCancellationRequested;
    public CancellationToken Revoked => revoked.Token;
    internal void Revoke() => revoked.Cancel();
}

/// <summary>One sealed result containing termination facts and retained failure evidence.</summary>
public sealed record DebugSessionCompletion(DebugSessionEndReason Reason, int? ProcessExitCode)
{
    public string? RecoveryGuidance { get; init; }
    public string InitialTrigger { get; init; } = Reason.ToString();
    internal DebugFailureOutcome Outcome { get; init; } = new DebugFailureCompletion().Complete();
    public bool Failed => Outcome.PrimaryFailure is not null || Outcome.HasCleanupFailure;
    internal void Throw() => Outcome.Throw();
    internal void ThrowWithEvidence() => Outcome.ThrowWithEvidence();
}
