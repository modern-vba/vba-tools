namespace VbaDev.Infrastructure.Debugging;

internal sealed class OwnedDesktopWindowExposureObserver : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IDesktopWindowObservationNativeApi nativeApi;
    private readonly int exactProcessId;
    private readonly DesktopWindowObservationScope callerDesktop;
    private readonly DesktopWindowObservationScope privateDesktop;
    private readonly List<DesktopWindowObservation> observations = [];
    private readonly SemaphoreSlim disposalGate = new(1, 1);
    private IDesktopWindowEventSubscription? subscription;
    private DesktopWindowLifecyclePhase currentPhase;
    private long nextSequence;
    private int disposed;

    private OwnedDesktopWindowExposureObserver(
        IDesktopWindowObservationNativeApi nativeApi,
        int exactProcessId,
        DesktopWindowObservationScope callerDesktop,
        DesktopWindowObservationScope privateDesktop,
        DesktopWindowLifecyclePhase initialPhase)
    {
        this.nativeApi = nativeApi;
        this.exactProcessId = exactProcessId;
        this.callerDesktop = callerDesktop;
        this.privateDesktop = privateDesktop;
        currentPhase = initialPhase;
    }

    public DesktopWindowExposureEvidence Evidence
    {
        get
        {
            lock (gate)
            {
                return new DesktopWindowExposureEvidence(
                    exactProcessId,
                    observations.ToArray());
            }
        }
    }

    public static async Task<OwnedDesktopWindowExposureObserver> StartAsync(
        IDesktopWindowObservationNativeApi nativeApi,
        int exactProcessId,
        DesktopWindowObservationScope callerDesktop,
        DesktopWindowObservationScope privateDesktop,
        DesktopWindowLifecyclePhase initialPhase,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nativeApi);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exactProcessId);
        ValidateScope(callerDesktop, DesktopWindowLocation.CallerInteractive);
        ValidateScope(privateDesktop, DesktopWindowLocation.Private);

        var observer = new OwnedDesktopWindowExposureObserver(
            nativeApi,
            exactProcessId,
            callerDesktop,
            privateDesktop,
            initialPhase);
        try
        {
            observer.subscription = nativeApi.StartCallerDesktopEvents(
                exactProcessId,
                callerDesktop,
                observer.ObserveCallerDesktopEvent);
            await observer.subscription.Ready.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            observer.CaptureCore(DesktopWindowObservationCause.InitialSnapshot);
            return observer;
        }
        catch
        {
            await observer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Capture(DesktopWindowLifecyclePhase phase)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        lock (gate)
        {
            currentPhase = phase;
        }

        CaptureCore(DesktopWindowObservationCause.LifecycleSnapshot);
    }

    public async Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
        Task exactProcessExit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exactProcessExit);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        try
        {
            await exactProcessExit.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                currentPhase = DesktopWindowLifecyclePhase.ProcessExited;
            }

            CaptureCore(DesktopWindowObservationCause.ProcessExitSnapshot);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }

        return Evidence;
    }

    public async ValueTask DisposeAsync()
    {
        await disposalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
                subscription = null;
            }

            Volatile.Write(ref disposed, 1);
        }
        finally
        {
            disposalGate.Release();
        }
    }

    private static void ValidateScope(
        DesktopWindowObservationScope scope,
        DesktopWindowLocation expectedLocation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Handle == nint.Zero)
        {
            throw new ArgumentException("The desktop handle must be nonzero.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(scope.QualifiedName))
        {
            throw new ArgumentException("The desktop name must be nonempty.", nameof(scope));
        }

        if (scope.Location != expectedLocation)
        {
            throw new ArgumentException(
                $"Expected a {expectedLocation} desktop scope.",
                nameof(scope));
        }
    }

    private void CaptureCore(DesktopWindowObservationCause cause)
    {
        CaptureDesktop(callerDesktop, cause);
        CaptureDesktop(privateDesktop, cause);
    }

    private void CaptureDesktop(
        DesktopWindowObservationScope desktop,
        DesktopWindowObservationCause cause)
    {
        foreach (var window in nativeApi.EnumerateTopLevelWindows(desktop))
        {
            Record(desktop, window, cause);
        }
    }

    private void ObserveCallerDesktopEvent(DesktopWindowEvent windowEvent)
        => Record(callerDesktop, windowEvent.Window, windowEvent.Cause);

    private void Record(
        DesktopWindowObservationScope desktop,
        DesktopWindowSnapshot window,
        DesktopWindowObservationCause cause)
    {
        if (window.ProcessId != exactProcessId || !window.IsTopLevel)
        {
            return;
        }

        lock (gate)
        {
            observations.Add(new DesktopWindowObservation(
                ++nextSequence,
                window.ProcessId,
                window.WindowHandle,
                desktop.QualifiedName,
                desktop.Location,
                window.WindowClass,
                window.Title,
                window.IsVisible,
                currentPhase,
                cause));
        }
    }
}
