using VbaDev.Infrastructure.Debugging;
using Xunit;

namespace VbaDev.Tests;

public sealed class OwnedDesktopWindowExposureObserverTests
{
    [Fact]
    public async Task StartWaitsForTheCallerHookAndInitialExactProcessSnapshotsBeforeResume()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.SetWindows(
            DesktopWindowLocation.CallerInteractive,
            Window(processId: 73, windowHandle: 0x100, visible: true, "XLMAIN", "Book1"),
            Window(processId: 999, windowHandle: 0x101, visible: true, "XLMAIN", "Unrelated"));
        nativeApi.SetWindows(
            DesktopWindowLocation.Private,
            Window(processId: 73, windowHandle: 0x200, visible: true, "XLMAIN", "Private Book"));

        var start = OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 73,
            new DesktopWindowObservationScope(
                (nint)11,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)22,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);

        Assert.False(start.IsCompleted);
        Assert.Equal(["subscribe:73:WinSta0\\Default"], nativeApi.Calls);

        nativeApi.Subscription.MarkReady();
        await using var observer = await start;

        Assert.Equal(
            [
                "subscribe:73:WinSta0\\Default",
                "enumerate:WinSta0\\Default",
                "enumerate:WinSta0\\vba-dev-private"
            ],
            nativeApi.Calls);
        Assert.Equal(
            [0x100L, 0x200L],
            observer.Evidence.Observations
                .Select(observation => observation.WindowHandle.ToInt64())
                .ToArray());
        Assert.All(observer.Evidence.Observations, observation =>
        {
            Assert.Equal(73, observation.ProcessId);
            Assert.Equal(
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
                observation.LifecyclePhase);
            Assert.Equal(DesktopWindowObservationCause.InitialSnapshot, observation.Cause);
        });
        Assert.True(observer.Evidence.HasCallerDesktopExposure);
    }

    [Fact]
    public async Task PrivateWindowsStayHiddenWhileExactCallerEventsRecordTheirLifecyclePhase()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.SetWindows(
            DesktopWindowLocation.Private,
            Window(processId: 84, windowHandle: 0x300, visible: true, "XLMAIN", "Private"));
        nativeApi.Subscription.MarkReady();
        await using var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 84,
            new DesktopWindowObservationScope(
                (nint)31,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)32,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);

        Assert.False(observer.Evidence.HasCallerDesktopExposure);

        observer.Capture(DesktopWindowLifecyclePhase.VbeAutomation);
        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventShow,
            Window(999, 0x401, visible: true, "Unrelated", "Other process")));
        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventShow,
            new DesktopWindowSnapshot(
                ProcessId: 84,
                WindowHandle: (nint)0x402,
                IsTopLevel: false,
                IsVisible: true,
                WindowClass: "Child",
                Title: "Owned child")));
        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventShow,
            Window(84, 0x403, visible: true, "bosa_sdm_XL9", "Microsoft Excel")));

        var exposure = Assert.Single(
            observer.Evidence.Observations,
            observation => observation.Location == DesktopWindowLocation.CallerInteractive);
        Assert.Equal(84, exposure.ProcessId);
        Assert.Equal((nint)0x403, exposure.WindowHandle);
        Assert.Equal("WinSta0\\Default", exposure.Desktop);
        Assert.Equal("bosa_sdm_XL9", exposure.WindowClass);
        Assert.Equal("Microsoft Excel", exposure.Title);
        Assert.Equal(DesktopWindowLifecyclePhase.VbeAutomation, exposure.LifecyclePhase);
        Assert.Equal(DesktopWindowObservationCause.WinEventShow, exposure.Cause);
        Assert.True(observer.Evidence.HasCallerDesktopExposure);
    }

    [Fact]
    public async Task CompletionKeepsTheHookActiveThroughExactProcessExitAndFinalSnapshot()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        await using var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 96,
            new DesktopWindowObservationScope(
                (nint)41,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)42,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);
        var processExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var completion = observer.CompleteAfterExitAsync(
            processExit.Task,
            CancellationToken.None);
        Assert.False(completion.IsCompleted);
        Assert.Equal(0, nativeApi.Subscription.DisposeCalls);

        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventCreate,
            Window(96, 0x501, visible: true, "XLMAIN", "Bootstrap")));
        nativeApi.SetWindows(
            DesktopWindowLocation.Private,
            Window(96, 0x502, visible: false, "EXCEL7", ""));
        processExit.TrySetResult();

        var evidence = await completion;

        Assert.Equal(1, nativeApi.Subscription.DisposeCalls);
        Assert.Equal(
            [
                "subscribe:96:WinSta0\\Default",
                "enumerate:WinSta0\\Default",
                "enumerate:WinSta0\\vba-dev-private",
                "enumerate:WinSta0\\Default",
                "enumerate:WinSta0\\vba-dev-private",
                "unsubscribe"
            ],
            nativeApi.Calls);
        Assert.Contains(evidence.Observations, observation =>
            observation.WindowHandle == (nint)0x501 &&
            observation.Cause == DesktopWindowObservationCause.WinEventCreate &&
            observation.LifecyclePhase ==
                DesktopWindowLifecyclePhase.BeforePrimaryThreadResume);
        Assert.Contains(evidence.Observations, observation =>
            observation.WindowHandle == (nint)0x502 &&
            observation.Cause == DesktopWindowObservationCause.ProcessExitSnapshot &&
            observation.LifecyclePhase == DesktopWindowLifecyclePhase.ProcessExited);
        Assert.Equal(
            evidence.Observations.Select(observation => observation.Sequence).Order().ToArray(),
            evidence.Observations.Select(observation => observation.Sequence).ToArray());
    }

    [Fact]
    public async Task CompletionCancellationStillStopsTheCallerDesktopHook()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 97,
            new DesktopWindowObservationScope(
                (nint)43,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)44,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => observer.CompleteAfterExitAsync(
                Task.Delay(Timeout.InfiniteTimeSpan),
                cancellation.Token));

        Assert.Equal(1, nativeApi.Subscription.DisposeCalls);
    }

    [Fact]
    public async Task CompletionIncludesEventsDeliveredWhileTheHookStops()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        nativeApi.Subscription.EventOnDispose = new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventShow,
            Window(97, 0x507, visible: true, "XLMAIN", "Late exposure"));
        var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 97,
            new DesktopWindowObservationScope(
                (nint)43,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)44,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);

        var evidence = await observer.CompleteAfterExitAsync(
            Task.CompletedTask,
            CancellationToken.None);

        Assert.Contains(evidence.Observations, observation =>
            observation.WindowHandle == (nint)0x507 &&
            observation.Cause == DesktopWindowObservationCause.WinEventShow &&
            observation.LifecyclePhase == DesktopWindowLifecyclePhase.ProcessExited);
    }

    [Fact]
    public async Task FailedHookDisposalCanBeRetriedBeforeObserverBecomesDisposed()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        nativeApi.Subscription.DisposeFailuresRemaining = 1;
        var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 98,
            new DesktopWindowObservationScope(
                (nint)45,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)46,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BeforePrimaryThreadResume,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => observer.DisposeAsync().AsTask());
        observer.Capture(DesktopWindowLifecyclePhase.Shutdown);

        await observer.DisposeAsync();

        Assert.Equal(2, nativeApi.Subscription.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(
            () => observer.Capture(DesktopWindowLifecyclePhase.ProcessExited));
    }

    [Fact]
    public async Task ACallerShowEventProvesTransientExposureEvenWhenTheWindowAlreadyHid()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        await using var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 107,
            new DesktopWindowObservationScope(
                (nint)51,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)52,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BootstrapBinding,
            CancellationToken.None);

        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventShow,
            Window(107, 0x601, visible: false, "XLMAIN", "Transient")));

        Assert.True(observer.Evidence.HasCallerDesktopExposure);
    }

    [Fact]
    public async Task ACallerForegroundEventProvesTransientExposureEvenWhenMetadataIsAlreadyHidden()
    {
        var nativeApi = new FakeDesktopWindowObservationNativeApi();
        nativeApi.Subscription.MarkReady();
        await using var observer = await OwnedDesktopWindowExposureObserver.StartAsync(
            nativeApi,
            exactProcessId: 108,
            new DesktopWindowObservationScope(
                (nint)53,
                "WinSta0\\Default",
                DesktopWindowLocation.CallerInteractive),
            new DesktopWindowObservationScope(
                (nint)54,
                "WinSta0\\vba-dev-private",
                DesktopWindowLocation.Private),
            DesktopWindowLifecyclePhase.BootstrapBinding,
            CancellationToken.None);

        nativeApi.Subscription.Emit(new DesktopWindowEvent(
            DesktopWindowObservationCause.WinEventForeground,
            Window(108, 0x602, visible: false, "XLMAIN", "Transient foreground")));

        Assert.True(observer.Evidence.HasCallerDesktopExposure);
    }

    [Fact]
    public void ExactProcessShowEventSurvivesDeferredMetadataLoss()
    {
        var window = WindowsDesktopWindowObservationNativeApi.CreateEventWindowSnapshot(
            exactProcessId: 118,
            DesktopWindowObservationCause.WinEventShow,
            windowHandle: (nint)0x701,
            capturedWindow: null);

        Assert.NotNull(window);
        Assert.Equal(118, window.ProcessId);
        Assert.Equal((nint)0x701, window.WindowHandle);
        Assert.True(window.IsTopLevel);
        Assert.True(window.IsVisible);
        Assert.Equal("<metadata unavailable>", window.WindowClass);
        Assert.Equal("<metadata unavailable>", window.Title);
    }

    [Fact]
    public void ExactProcessShowEventSurvivesDeferredWindowHandleReuse()
    {
        var reusedWindow = Window(
            processId: 999,
            windowHandle: 0x702,
            visible: true,
            "OtherProcessWindow",
            "Reused HWND");

        var window = WindowsDesktopWindowObservationNativeApi.CreateEventWindowSnapshot(
            exactProcessId: 119,
            DesktopWindowObservationCause.WinEventShow,
            windowHandle: (nint)0x702,
            reusedWindow);

        Assert.NotNull(window);
        Assert.Equal(119, window.ProcessId);
        Assert.Equal("<metadata unavailable>", window.Title);
    }

    [Fact]
    public void LiveExactProcessChildShowIsNotInventedAsTopLevelExposure()
    {
        var childWindow = new DesktopWindowSnapshot(
            ProcessId: 119,
            WindowHandle: (nint)0x708,
            IsTopLevel: false,
            IsVisible: true,
            WindowClass: "EXCEL7",
            Title: "Worksheet child");

        var window = WindowsDesktopWindowObservationNativeApi.CreateEventWindowSnapshot(
            exactProcessId: 119,
            DesktopWindowObservationCause.WinEventShow,
            childWindow.WindowHandle,
            childWindow);

        Assert.Null(window);
    }

    [Fact]
    public void DeferredExactProcessChildShowIsNotInventedAsTopLevelExposure()
    {
        var tracker = new WindowsDesktopWindowObservationNativeApi.ExactProcessWindowEventTracker(
            exactProcessId: 119);
        var childWindow = new DesktopWindowSnapshot(
            ProcessId: 119,
            WindowHandle: (nint)0x709,
            IsTopLevel: false,
            IsVisible: false,
            WindowClass: "EXCEL7",
            Title: "Worksheet child");
        _ = tracker.Record(
            DesktopWindowObservationCause.WinEventCreate,
            childWindow.WindowHandle,
            childWindow);

        var window = tracker.Record(
            DesktopWindowObservationCause.WinEventShow,
            childWindow.WindowHandle,
            capturedWindow: null);

        Assert.Null(window);
    }

    [Fact]
    public void ExactProcessForegroundEventSurvivesDeferredMetadataLoss()
    {
        var tracker = new WindowsDesktopWindowObservationNativeApi.ExactProcessWindowEventTracker(
            exactProcessId: 120);

        var window = tracker.Record(
            DesktopWindowObservationCause.WinEventForeground,
            windowHandle: (nint)0x703,
            capturedWindow: null);

        Assert.NotNull(window);
        Assert.Equal(120, window.ProcessId);
        Assert.Equal((nint)0x703, window.WindowHandle);
        Assert.True(window.IsTopLevel);
        Assert.True(window.IsVisible);
        Assert.Equal("<metadata unavailable>", window.WindowClass);
        Assert.Equal("<metadata unavailable>", window.Title);
    }

    [Fact]
    public void DeferredHideUsesCachedExactWindowMetadataInsteadOfReusedHandleMetadata()
    {
        var tracker = new WindowsDesktopWindowObservationNativeApi.ExactProcessWindowEventTracker(
            exactProcessId: 121);
        var original = Window(121, 0x704, visible: true, "XLMAIN", "Original");
        _ = tracker.Record(
            DesktopWindowObservationCause.WinEventShow,
            original.WindowHandle,
            original);

        var hidden = tracker.Record(
            DesktopWindowObservationCause.WinEventHide,
            original.WindowHandle,
            Window(999, 0x704, visible: true, "OtherProcessWindow", "Reused HWND"));

        Assert.NotNull(hidden);
        Assert.Equal(121, hidden.ProcessId);
        Assert.Equal("XLMAIN", hidden.WindowClass);
        Assert.Equal("Original", hidden.Title);
        Assert.False(hidden.IsVisible);
    }

    [Fact]
    public void DeferredDestroyUsesThenEvictsCachedExactWindowMetadata()
    {
        var tracker = new WindowsDesktopWindowObservationNativeApi.ExactProcessWindowEventTracker(
            exactProcessId: 122);
        var original = Window(122, 0x705, visible: true, "XLMAIN", "Original");
        _ = tracker.Record(
            DesktopWindowObservationCause.WinEventCreate,
            original.WindowHandle,
            original);

        var destroyed = tracker.Record(
            DesktopWindowObservationCause.WinEventDestroy,
            original.WindowHandle,
            capturedWindow: null);
        var laterHide = tracker.Record(
            DesktopWindowObservationCause.WinEventHide,
            original.WindowHandle,
            capturedWindow: null);

        Assert.NotNull(destroyed);
        Assert.Equal(122, destroyed.ProcessId);
        Assert.Equal("XLMAIN", destroyed.WindowClass);
        Assert.Equal("Original", destroyed.Title);
        Assert.False(destroyed.IsVisible);
        Assert.Null(laterHide);
    }

    [Fact]
    public void UncachedDeferredHideDoesNotInventATopLevelWindow()
    {
        var tracker = new WindowsDesktopWindowObservationNativeApi.ExactProcessWindowEventTracker(
            exactProcessId: 123);

        var window = tracker.Record(
            DesktopWindowObservationCause.WinEventHide,
            windowHandle: (nint)0x706,
            capturedWindow: null);

        Assert.Null(window);
    }

    private static DesktopWindowSnapshot Window(
        int processId,
        long windowHandle,
        bool visible,
        string windowClass,
        string title)
        => new(
            processId,
            (nint)windowHandle,
            IsTopLevel: true,
            IsVisible: visible,
            windowClass,
            title);

    private sealed class FakeDesktopWindowObservationNativeApi
        : IDesktopWindowObservationNativeApi
    {
        private readonly Dictionary<DesktopWindowLocation, IReadOnlyList<DesktopWindowSnapshot>>
            windows = [];

        public List<string> Calls { get; } = [];

        public FakeDesktopWindowEventSubscription Subscription { get; }

        public FakeDesktopWindowObservationNativeApi()
        {
            Subscription = new FakeDesktopWindowEventSubscription(Calls);
        }

        public void SetWindows(
            DesktopWindowLocation location,
            params DesktopWindowSnapshot[] snapshots)
            => windows[location] = snapshots;

        public IDesktopWindowEventSubscription StartCallerDesktopEvents(
            int exactProcessId,
            DesktopWindowObservationScope callerDesktop,
            Action<DesktopWindowEvent> observe)
        {
            Calls.Add($"subscribe:{exactProcessId}:{callerDesktop.QualifiedName}");
            Subscription.Observe = observe;
            return Subscription;
        }

        public IReadOnlyList<DesktopWindowSnapshot> EnumerateTopLevelWindows(
            DesktopWindowObservationScope desktop)
        {
            Calls.Add($"enumerate:{desktop.QualifiedName}");
            return windows.GetValueOrDefault(desktop.Location, []);
        }
    }

    private sealed class FakeDesktopWindowEventSubscription(List<string> calls)
        : IDesktopWindowEventSubscription
    {
        private readonly TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<DesktopWindowEvent>? Observe { get; set; }

        public Task Ready => ready.Task;

        public int DisposeCalls { get; private set; }

        public int DisposeFailuresRemaining { get; set; }

        public DesktopWindowEvent? EventOnDispose { get; set; }

        public void MarkReady() => ready.TrySetResult();

        public void Emit(DesktopWindowEvent windowEvent)
            => Observe?.Invoke(windowEvent);

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            calls.Add("unsubscribe");
            if (EventOnDispose is not null)
            {
                Emit(EventOnDispose);
            }

            if (DisposeFailuresRemaining > 0)
            {
                DisposeFailuresRemaining--;
                throw new InvalidOperationException("Injected unsubscribe failure.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
