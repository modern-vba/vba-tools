namespace VbaDebugAdapter.Debugging;

public interface IVbeDebugSessionFactory
{
    Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken);
}

public interface IVbeDebugSession : IAsyncDisposable
{
    int ProcessId { get; }

    Task<DebugProcessExit> Completion { get; }

    Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
        CancellationToken cancellationToken);

    Task OpenGeneratedWorkbookAsync(
        string workbookPath,
        IDebugInputWaitSink? inputWaitSink,
        CancellationToken cancellationToken);

    Task SetNativeBreakpointsAsync(
        IReadOnlyList<VbeBreakpoint> breakpoints,
        CancellationToken cancellationToken);

    Task RunTargetAsync(
        DebugTargetProcedure target,
        IDebugInputWaitSink? inputWaitSink,
        CancellationToken cancellationToken);

    ValueTask TerminateAsync();
}

public sealed record DebugProcessExit(int ExitCode);

public enum DebugInputWaitKind
{
    Excel,
    Vbe,
    ExcelOrVbe
}

public enum DebugInputWaitPhase
{
    WorkbookOpen,
    TargetStart
}

public sealed record DebugInputWait(
    DebugInputWaitKind Kind,
    DebugInputWaitPhase Phase,
    int ProcessId)
{
    public DebugLifecycleMessage ToLifecycleMessage()
    {
        var owner = Kind switch
        {
            DebugInputWaitKind.Excel => "Excel",
            DebugInputWaitKind.Vbe => "the VBE",
            _ => "Excel/VBE"
        };
        var operation = Phase == DebugInputWaitPhase.WorkbookOpen
            ? "opening the generated workbook"
            : "starting the debug target";
        return new DebugLifecycleMessage(
            $"Owned Excel process {ProcessId} is waiting for {owner} input while {operation}. " +
            "Respond to the visible prompt or stop debugging.");
    }
}

public sealed record DebugLifecycleMessage(string Output);

public interface IDebugInputWaitSink
{
    ValueTask InputRequiredAsync(
        DebugInputWait inputWait,
        CancellationToken cancellationToken);
}

public interface IDebugLifecycleSink : IDebugInputWaitSink
{
    ValueTask WriteAsync(
        DebugLifecycleMessage message,
        CancellationToken cancellationToken);

    ValueTask IDebugInputWaitSink.InputRequiredAsync(
        DebugInputWait inputWait,
        CancellationToken cancellationToken)
        => WriteAsync(inputWait.ToLifecycleMessage(), cancellationToken);
}
