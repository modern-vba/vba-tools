using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

internal interface IVbeDebugProbeControl
{
    bool StrongProcessOwnershipEstablished { get; }

    Task WaitForBreakModeAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task ContinueTargetAsync(
        DebugTargetProcedure target,
        CancellationToken cancellationToken);

    Task WaitForCompletionAsync(
        string expectedMarker,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IVbeDebugDoctorControl : IVbeDebugProbeControl
{
    Task CreateFixtureWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken);

    Task OpenFixtureWorkbookAsync(
        string workbookPath,
        CancellationToken cancellationToken);

    Task ImportFixtureModuleAsync(
        string sourcePath,
        VbeCodeModuleSourceMap sourceMap,
        CancellationToken cancellationToken);

    Task VerifyCommandContextAsync(
        VbeBreakpoint breakpoint,
        DebugTargetProcedure target,
        CancellationToken cancellationToken);

    Task WaitForBreakModeAsync(CancellationToken cancellationToken);

    Task WaitForCompletionAsync(
        string expectedMarker,
        CancellationToken cancellationToken);

    Task ClearNativeBreakpointAsync(
        VbeBreakpoint breakpoint,
        CancellationToken cancellationToken);

    Task CloseOwnedProcessCooperativelyAsync(
        CancellationToken cancellationToken);
}
