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
