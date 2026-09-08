using System.Runtime.InteropServices;
using System.Text;
using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

internal sealed class DebugModalPromptObservation(
    DebugInputWait inputWait,
    IReadOnlySet<nint> existingModalWindows)
{
    private readonly object syncRoot = new();
    private readonly HashSet<nint> reportedModalWindows = [];

    public DebugInputWait InputWait { get; } = inputWait;

    public IReadOnlySet<nint> ExistingModalWindows { get; } = existingModalWindows;

    public bool TryMarkNewModalWindows(IReadOnlySet<nint> currentModalWindows)
    {
        lock (syncRoot)
        {
            reportedModalWindows.IntersectWith(currentModalWindows);
            var foundNewModal = false;
            foreach (var windowHandle in currentModalWindows)
            {
                if (!ExistingModalWindows.Contains(windowHandle) &&
                    reportedModalWindows.Add(windowHandle))
                {
                    foundNewModal = true;
                }
            }

            return foundNewModal;
        }
    }
}

internal interface IDebugModalPromptMonitor
{
    IDebugModalPromptPhase BeginPhase(
        DebugInputWait inputWait,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink);
}

internal interface IDebugModalPromptPhase : IAsyncDisposable
{
    Task Completion { get; }

    Task<T> ObserveOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken);
}

internal interface IDebugModalWindowApi
{
    IReadOnlySet<nint> CaptureVisibleModalWindows(int processId);

    Task WaitForNextObservationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Starts phase-owned modal observation for one exact owned Excel process.
/// </summary>
internal sealed class DebugModalPromptMonitor : IDebugModalPromptMonitor
{
    private readonly IDebugModalWindowApi windowApi;

    public DebugModalPromptMonitor() : this(new WindowsDebugModalWindowApi()) { }

    internal DebugModalPromptMonitor(IDebugModalWindowApi windowApi) => this.windowApi = windowApi;

    public IDebugModalPromptPhase BeginPhase(
        DebugInputWait inputWait,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink)
        => new DebugModalPromptPhase(windowApi, inputWait, processCompletion, inputWaitSink);
}

internal sealed class WindowsDebugModalWindowApi : IDebugModalWindowApi
{
    public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
    {
        var result = new HashSet<nint>();
        if (!OperatingSystem.IsWindows() || processId <= 0)
        {
            return result;
        }

        _ = EnumWindows(
            (windowHandle, parameter) =>
            {
                _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId == (uint)processId &&
                    IsWindowVisible(windowHandle) &&
                    IsWindowEnabled(windowHandle) &&
                    IsModalCandidate(windowHandle))
                {
                    result.Add(windowHandle);
                }

                return true;
            },
            nint.Zero);
        return result;
    }

    public Task WaitForNextObservationAsync(CancellationToken cancellationToken)
        => Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

    private static bool IsModalCandidate(nint windowHandle)
    {
        if (GetWindow(windowHandle, GetWindowOwner) != nint.Zero)
        {
            return true;
        }

        var className = new StringBuilder(64);
        return GetClassName(windowHandle, className, className.Capacity) > 0 &&
            className.ToString().Equals("#32770", StringComparison.Ordinal);
    }

    private const uint GetWindowOwner = 4;

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint windowHandle,
        StringBuilder className,
        int maximumCount);
}
