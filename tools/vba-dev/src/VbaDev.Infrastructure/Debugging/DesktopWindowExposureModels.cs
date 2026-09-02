namespace VbaDev.Infrastructure.Debugging;

internal enum DesktopWindowLocation
{
    CallerInteractive,
    Private
}

internal enum DesktopWindowLifecyclePhase
{
    BeforePrimaryThreadResume,
    BootstrapBinding,
    WorkbookAutomation,
    TestExecution,
    VbeAutomation,
    Shutdown,
    ProcessExited
}

internal enum DesktopWindowObservationCause
{
    InitialSnapshot,
    LifecycleSnapshot,
    WinEventCreate,
    WinEventShow,
    WinEventForeground,
    WinEventHide,
    WinEventDestroy,
    ProcessExitSnapshot
}

internal sealed record DesktopWindowObservationScope(
    nint Handle,
    string QualifiedName,
    DesktopWindowLocation Location);

internal sealed record DesktopWindowSnapshot(
    int ProcessId,
    nint WindowHandle,
    bool IsTopLevel,
    bool IsVisible,
    string WindowClass,
    string Title);

internal sealed record DesktopWindowEvent(
    DesktopWindowObservationCause Cause,
    DesktopWindowSnapshot Window);

internal sealed record DesktopWindowObservation(
    long Sequence,
    int ProcessId,
    nint WindowHandle,
    string Desktop,
    DesktopWindowLocation Location,
    string WindowClass,
    string Title,
    bool IsVisible,
    DesktopWindowLifecyclePhase LifecyclePhase,
    DesktopWindowObservationCause Cause);

internal sealed record DesktopWindowExposureEvidence(
    int ExactProcessId,
    IReadOnlyList<DesktopWindowObservation> Observations)
{
    public bool HasCallerDesktopExposure => Observations.Any(static observation =>
        observation.Location == DesktopWindowLocation.CallerInteractive &&
        (observation.Cause is DesktopWindowObservationCause.WinEventShow or
                DesktopWindowObservationCause.WinEventForeground ||
            (observation.IsVisible &&
                observation.Cause is not DesktopWindowObservationCause.WinEventHide and
                    not DesktopWindowObservationCause.WinEventDestroy)));
}

internal interface IDesktopWindowObservationNativeApi
{
    IDesktopWindowEventSubscription StartCallerDesktopEvents(
        int exactProcessId,
        DesktopWindowObservationScope callerDesktop,
        Action<DesktopWindowEvent> observe);

    IReadOnlyList<DesktopWindowSnapshot> EnumerateTopLevelWindows(
        DesktopWindowObservationScope desktop);
}

internal interface IDesktopWindowEventSubscription : IAsyncDisposable
{
    Task Ready { get; }
}
