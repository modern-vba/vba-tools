namespace VbaDebugAdapter.Debugging;

/// <summary>
/// Attaches to the selected source workbook or opens it visibly without taking
/// ownership of the Excel process or the workbook lifetime.
/// </summary>
public interface ISourceVbeDebugSessionFactory
{
    Task<ISourceVbeDebugSession> AttachOrOpenAsync(
        string exactSourceWorkbookPath,
        CancellationToken cancellationToken);
}

/// <summary>A source-workbook debug binding that never saves, closes or kills Excel.</summary>
public interface ISourceVbeDebugSession : IAsyncDisposable
{
    int ProcessId { get; }

    long ProcessStartUtcTicks { get; }

    bool WasAlreadyOpen { get; }

    Task<SourceVbeDebugSessionCompletion> Completion { get; }

    Task<SourceVbeDebugSessionInspection> InspectAsync(CancellationToken cancellationToken);

    Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
        CancellationToken cancellationToken);

    Task SetNativeBreakpointsAsync(
        IReadOnlyList<VbeBreakpoint> breakpoints,
        CancellationToken cancellationToken);

    Task RunTargetAsync(
        DebugTargetProcedure target,
        IDebugInputWaitSink? inputWaitSink,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops VBA execution in the exact source project and proves design mode.
    /// An unconfirmed stop throws guidance for manual Reset; it never terminates Excel.
    /// </summary>
    Task ResetExecutionAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record SourceVbeDebugSessionInspection(
    string WorkbookPath,
    int ProcessId,
    long ProcessStartUtcTicks,
    bool IsSaved,
    int ProjectMode);

public enum SourceVbeDebugSessionEndReason
{
    WorkbookClosed,
    ProcessExited,
    Detached
}

public sealed record SourceVbeDebugSessionCompletion(
    SourceVbeDebugSessionEndReason Reason,
    int? ProcessExitCode);
