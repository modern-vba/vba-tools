using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace VbaDev.Infrastructure.Debugging;

internal sealed class WindowsDesktopWindowObservationNativeApi
    : IDesktopWindowObservationNativeApi
{
    private const int UserObjectName = 2;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint WinEventOutOfContext = 0x0000;
    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint GetAncestorRoot = 2;
    private const uint WindowMessageQuit = 0x0012;
    private const uint PeekMessageNoRemove = 0x0000;
    private const int MaximumMetadataCharacters = 512;

    private WindowsDesktopWindowObservationNativeApi()
    {
    }

    public static WindowsDesktopWindowObservationNativeApi Instance { get; } = new();

    public DesktopWindowObservationScope CaptureCurrentThreadDesktop()
    {
        EnsureWindows();
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == nint.Zero)
        {
            throw CreateWin32Exception(
                "The caller thread desktop could not be resolved for window exposure observation.");
        }

        var windowStation = GetProcessWindowStation();
        if (windowStation == nint.Zero)
        {
            throw CreateWin32Exception(
                "The caller window station could not be resolved for window exposure observation.");
        }

        return new DesktopWindowObservationScope(
            desktop,
            $"{ReadUserObjectName(windowStation)}\\{ReadUserObjectName(desktop)}",
            DesktopWindowLocation.CallerInteractive);
    }

    public IDesktopWindowEventSubscription StartCallerDesktopEvents(
        int exactProcessId,
        DesktopWindowObservationScope callerDesktop,
        Action<DesktopWindowEvent> observe)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exactProcessId);
        ArgumentNullException.ThrowIfNull(callerDesktop);
        ArgumentNullException.ThrowIfNull(observe);
        if (callerDesktop.Handle == nint.Zero ||
            callerDesktop.Location != DesktopWindowLocation.CallerInteractive)
        {
            throw new ArgumentException(
                "A nonzero caller interactive desktop is required.",
                nameof(callerDesktop));
        }

        return new WindowsDesktopWindowEventSubscription(
            exactProcessId,
            callerDesktop.Handle,
            observe);
    }

    public IReadOnlyList<DesktopWindowSnapshot> EnumerateTopLevelWindows(
        DesktopWindowObservationScope desktop)
    {
        EnsureWindows();
        ArgumentNullException.ThrowIfNull(desktop);
        if (desktop.Handle == nint.Zero)
        {
            throw new ArgumentException("A nonzero desktop handle is required.", nameof(desktop));
        }

        var windows = new List<DesktopWindowSnapshot>();
        Marshal.SetLastPInvokeError(0);
        if (!EnumDesktopWindows(
            desktop.Handle,
            (windowHandle, _) =>
            {
                var snapshot = TryCaptureWindow(windowHandle);
                if (snapshot is { IsTopLevel: true })
                {
                    windows.Add(snapshot);
                }

                return true;
            },
            nint.Zero))
        {
            var nativeError = Marshal.GetLastPInvokeError();
            if (nativeError == 0)
            {
                // EnumDesktopWindows returns zero for a valid desktop that has no windows
                // on supported Windows versions without setting an error.
                return windows;
            }

            throw new Win32Exception(
                nativeError,
                $"Top-level windows on desktop '{desktop.QualifiedName}' could not be " +
                $"enumerated (native error {nativeError}).");
        }

        return windows;
    }

    private static DesktopWindowSnapshot? TryCaptureWindow(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            return null;
        }

        var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
        if (threadId == 0 || processId == 0)
        {
            return null;
        }

        return new DesktopWindowSnapshot(
            checked((int)processId),
            windowHandle,
            IsTopLevel: GetAncestor(windowHandle, GetAncestorRoot) == windowHandle,
            IsWindowVisible(windowHandle),
            ReadWindowClass(windowHandle),
            ReadWindowTitle(windowHandle));
    }

    internal static DesktopWindowSnapshot? CreateEventWindowSnapshot(
        int exactProcessId,
        DesktopWindowObservationCause cause,
        nint windowHandle,
        DesktopWindowSnapshot? capturedWindow,
        DesktopWindowSnapshot? cachedWindow = null)
    {
        if (windowHandle == nint.Zero)
        {
            return null;
        }

        var exactCapturedSnapshot = IsExactWindow(
            capturedWindow,
            exactProcessId,
            windowHandle)
            ? capturedWindow
            : null;
        var exactCachedSnapshot = IsExactWindow(
            cachedWindow,
            exactProcessId,
            windowHandle)
            ? cachedWindow
            : null;
        if (exactCapturedSnapshot is { IsTopLevel: false } ||
            exactCapturedSnapshot is null && exactCachedSnapshot is { IsTopLevel: false })
        {
            return null;
        }

        var exactCapturedWindow = exactCapturedSnapshot is { IsTopLevel: true }
            ? exactCapturedSnapshot
            : null;
        var exactCachedWindow = exactCachedSnapshot is { IsTopLevel: true }
            ? exactCachedSnapshot
            : null;

        if (cause is DesktopWindowObservationCause.WinEventHide or
                DesktopWindowObservationCause.WinEventDestroy)
        {
            var knownWindow = exactCachedWindow ?? exactCapturedWindow;
            return knownWindow is null
                ? null
                : knownWindow with { IsVisible = false };
        }

        if (exactCapturedWindow is not null)
        {
            return exactCapturedWindow;
        }

        if (cause is not DesktopWindowObservationCause.WinEventShow and
            not DesktopWindowObservationCause.WinEventForeground)
        {
            return null;
        }

        // SetWinEventHook already filtered this callback to the exact process and the
        // observer thread's caller desktop. An out-of-context SHOW or FOREGROUND
        // notification can arrive after the HWND has disappeared, so retain
        // conservative exposure evidence instead of silently losing the event.
        return new DesktopWindowSnapshot(
            exactProcessId,
            windowHandle,
            IsTopLevel: true,
            IsVisible: true,
            WindowClass: "<metadata unavailable>",
            Title: "<metadata unavailable>");
    }

    private static bool IsExactWindow(
        DesktopWindowSnapshot? window,
        int exactProcessId,
        nint exactWindowHandle)
        => window is not null &&
        window.ProcessId == exactProcessId &&
        window.WindowHandle == exactWindowHandle;

    internal sealed class ExactProcessWindowEventTracker(int exactProcessId)
    {
        private readonly Dictionary<nint, DesktopWindowSnapshot> windows = [];

        public DesktopWindowSnapshot? Record(
            DesktopWindowObservationCause cause,
            nint windowHandle,
            DesktopWindowSnapshot? capturedWindow)
        {
            if (cause == DesktopWindowObservationCause.WinEventCreate)
            {
                windows.Remove(windowHandle);
            }

            windows.TryGetValue(windowHandle, out var cachedWindow);
            var window = CreateEventWindowSnapshot(
                exactProcessId,
                cause,
                windowHandle,
                capturedWindow,
                cachedWindow);

            if (cause == DesktopWindowObservationCause.WinEventDestroy)
            {
                windows.Remove(windowHandle);
            }
            else if (IsExactWindow(capturedWindow, exactProcessId, windowHandle))
            {
                windows[windowHandle] = capturedWindow!;
            }
            else if (window is not null)
            {
                windows[windowHandle] = window;
            }

            return window;
        }
    }

    private static string ReadWindowClass(nint windowHandle)
    {
        var buffer = new StringBuilder(MaximumMetadataCharacters);
        var length = GetClassNameW(windowHandle, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString(0, length);
    }

    private static string ReadWindowTitle(nint windowHandle)
    {
        var reportedLength = GetWindowTextLengthW(windowHandle);
        var capacity = Math.Clamp(
            reportedLength + 1,
            1,
            MaximumMetadataCharacters);
        var buffer = new StringBuilder(capacity);
        var length = GetWindowTextW(windowHandle, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString(0, length);
    }

    private static string ReadUserObjectName(nint userObject)
    {
        _ = GetUserObjectInformationW(
            userObject,
            UserObjectName,
            nint.Zero,
            0,
            out var requiredBytes);
        if (requiredBytes == 0)
        {
            throw CreateWin32Exception("A Windows desktop object name size could not be resolved.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetUserObjectInformationW(
                userObject,
                UserObjectName,
                buffer,
                requiredBytes,
                out _))
            {
                throw CreateWin32Exception("A Windows desktop object name could not be read.");
            }

            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DesktopWindowObservationCause MapEventCause(uint eventId)
        => eventId switch
        {
            EventSystemForeground => DesktopWindowObservationCause.WinEventForeground,
            EventObjectCreate => DesktopWindowObservationCause.WinEventCreate,
            EventObjectShow => DesktopWindowObservationCause.WinEventShow,
            EventObjectHide => DesktopWindowObservationCause.WinEventHide,
            EventObjectDestroy => DesktopWindowObservationCause.WinEventDestroy,
            _ => throw new ArgumentOutOfRangeException(nameof(eventId), eventId, null)
        };

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Desktop window exposure observation requires Windows.");
        }
    }

    private static Win32Exception CreateWin32Exception(string message)
        => new(Marshal.GetLastWin32Error(), message);

    private sealed class WindowsDesktopWindowEventSubscription
        : IDesktopWindowEventSubscription
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
        private readonly int exactProcessId;
        private readonly nint callerDesktopHandle;
        private readonly Action<DesktopWindowEvent> observe;
        private readonly ExactProcessWindowEventTracker eventTracker;
        private readonly TaskCompletionSource ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stopped = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;
        private WinEventCallback? callback;
        private GCHandle callbackRoot;
        private nint objectLifecycleHook;
        private nint foregroundHook;
        private uint threadId;
        private int disposed;

        public WindowsDesktopWindowEventSubscription(
            int exactProcessId,
            nint callerDesktopHandle,
            Action<DesktopWindowEvent> observe)
        {
            this.exactProcessId = exactProcessId;
            this.callerDesktopHandle = callerDesktopHandle;
            this.observe = observe;
            eventTracker = new ExactProcessWindowEventTracker(exactProcessId);
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"VbaDev desktop window observer {exactProcessId}"
            };
            thread.Start();
        }

        public Task Ready => ready.Task;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return new ValueTask(stopped.Task.WaitAsync(StopTimeout));
            }

            return new ValueTask(StopAsync());
        }

        private async Task StopAsync()
        {
            try
            {
                try
                {
                    await ready.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
                }
                catch
                {
                    if (stopped.Task.IsCompleted)
                    {
                        await stopped.Task.ConfigureAwait(false);
                        return;
                    }

                    throw;
                }

                if (!stopped.Task.IsCompleted &&
                    !PostThreadMessageW(threadId, WindowMessageQuit, nint.Zero, nint.Zero) &&
                    !stopped.Task.IsCompleted)
                {
                    throw CreateWin32Exception(
                        "The desktop window observer message loop could not be stopped.");
                }

                await stopped.Task.WaitAsync(StopTimeout).ConfigureAwait(false);
            }
            catch
            {
                if (!stopped.Task.IsCompleted)
                {
                    _ = Interlocked.CompareExchange(ref disposed, 0, 1);
                }

                throw;
            }
        }

        private void Run()
        {
            Exception? failure = null;
            try
            {
                if (!SetThreadDesktop(callerDesktopHandle))
                {
                    throw CreateWin32Exception(
                        "The desktop window observer thread could not attach to the caller desktop.");
                }

                threadId = GetCurrentThreadId();
                _ = PeekMessageW(
                    out _,
                    nint.Zero,
                    0,
                    0,
                    PeekMessageNoRemove);
                callback = ObserveWinEvent;
                callbackRoot = GCHandle.Alloc(callback);
                objectLifecycleHook = SetWinEventHook(
                    EventObjectCreate,
                    EventObjectHide,
                    nint.Zero,
                    callback,
                    checked((uint)exactProcessId),
                    0,
                    WinEventOutOfContext);
                if (objectLifecycleHook == nint.Zero)
                {
                    throw CreateWin32Exception(
                        "The exact-process desktop window lifecycle event hook could not be " +
                        "installed.");
                }

                foregroundHook = SetWinEventHook(
                    EventSystemForeground,
                    EventSystemForeground,
                    nint.Zero,
                    callback,
                    checked((uint)exactProcessId),
                    0,
                    WinEventOutOfContext);
                if (foregroundHook == nint.Zero)
                {
                    throw CreateWin32Exception(
                        "The exact-process desktop foreground event hook could not be installed.");
                }

                ready.TrySetResult();
                while (true)
                {
                    var result = GetMessageW(out var message, nint.Zero, 0, 0);
                    if (result == 0)
                    {
                        break;
                    }

                    if (result < 0)
                    {
                        throw CreateWin32Exception(
                            "The desktop window observer message loop failed.");
                    }

                    _ = TranslateMessage(ref message);
                    _ = DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.TrySetException(ex);
            }
            finally
            {
                ReleaseHook(
                    ref foregroundHook,
                    "The exact-process desktop foreground event hook could not be released.",
                    ref failure);
                ReleaseHook(
                    ref objectLifecycleHook,
                    "The exact-process desktop window lifecycle event hook could not be " +
                    "released.",
                    ref failure);

                if (callbackRoot.IsAllocated)
                {
                    callbackRoot.Free();
                }

                callback = null;
                if (failure is null)
                {
                    stopped.TrySetResult();
                }
                else
                {
                    stopped.TrySetException(failure);
                }
            }
        }

        private static void ReleaseHook(
            ref nint hook,
            string failureMessage,
            ref Exception? failure)
        {
            if (hook == nint.Zero)
            {
                return;
            }

            var installedHook = hook;
            hook = nint.Zero;
            if (UnhookWinEvent(installedHook))
            {
                return;
            }

            var releaseFailure = CreateWin32Exception(failureMessage);
            failure = failure is null
                ? releaseFailure
                : new AggregateException(failure, releaseFailure);
        }

        private void ObserveWinEvent(
            nint eventHook,
            uint eventId,
            nint windowHandle,
            int objectId,
            int childId,
            uint eventThreadId,
            uint eventTime)
        {
            if (objectId != ObjectIdWindow || childId != ChildIdSelf)
            {
                return;
            }

            var cause = MapEventCause(eventId);
            var window = eventTracker.Record(
                cause,
                windowHandle,
                TryCaptureWindow(windowHandle));
            if (window is null)
            {
                return;
            }

            try
            {
                observe(new DesktopWindowEvent(cause, window));
            }
            catch
            {
                // Native callbacks cannot propagate managed exceptions across USER32.
            }
        }
    }

    private delegate bool EnumDesktopWindowsCallback(nint windowHandle, nint parameter);

    private delegate void WinEventCallback(
        nint eventHook,
        uint eventId,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThreadId,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public nint WindowHandle;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadDesktop(nint desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(
        nint userObject,
        int index,
        nint information,
        uint informationLength,
        out uint requiredLength);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(
        nint desktop,
        EnumDesktopWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(
        nint windowHandle,
        StringBuilder className,
        int maximumCharacterCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(nint windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(
        nint windowHandle,
        StringBuilder text,
        int maximumCharacterCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint module,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint eventHook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(
        out WindowMessage message,
        nint windowHandle,
        uint filterMinimum,
        uint filterMaximum,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessageW(
        out WindowMessage message,
        nint windowHandle,
        uint filterMinimum,
        uint filterMaximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref WindowMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessageW(ref WindowMessage message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessageW(
        uint threadId,
        uint message,
        nint wParam,
        nint lParam);
}
