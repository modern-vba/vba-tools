using System.Runtime.InteropServices;
using System.Text;
using VbaDev.App.Debugging;

namespace VbaDev.Infrastructure.Debugging;

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
    DebugModalPromptObservation Capture(DebugInputWait inputWait);

    Task<T> ObserveAsync<T>(
        DebugModalPromptObservation observation,
        Task<T> operation,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink,
        CancellationToken cancellationToken);

    Task ObserveUntilProcessExitAsync(
        DebugModalPromptObservation observation,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink,
        CancellationToken cancellationToken);
}

internal interface IDebugModalWindowApi
{
    IReadOnlySet<nint> CaptureVisibleModalWindows(int processId);

    Task WaitForNextObservationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Observes modal top-level windows belonging to one exact owned Excel process.
/// </summary>
internal sealed class DebugModalPromptMonitor : IDebugModalPromptMonitor
{
    private readonly IDebugModalWindowApi windowApi;

    public DebugModalPromptMonitor()
        : this(new WindowsDebugModalWindowApi())
    {
    }

    internal DebugModalPromptMonitor(IDebugModalWindowApi windowApi)
    {
        this.windowApi = windowApi;
    }

    public DebugModalPromptObservation Capture(DebugInputWait inputWait)
        => new(
            inputWait,
            windowApi.CaptureVisibleModalWindows(inputWait.ProcessId));

    public async Task<T> ObserveAsync<T>(
        DebugModalPromptObservation observation,
        Task<T> operation,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink,
        CancellationToken cancellationToken)
    {
        async ValueTask<bool> ReportInputIfNewModalAsync()
        {
            var currentWindows = windowApi.CaptureVisibleModalWindows(
                observation.InputWait.ProcessId);
            if (!observation.TryMarkNewModalWindows(currentWindows))
            {
                return false;
            }

            await inputWaitSink
                .InputRequiredAsync(observation.InputWait, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        while (!operation.IsCompleted && !processCompletion.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await ReportInputIfNewModalAsync().ConfigureAwait(false);

            var nextObservation = windowApi.WaitForNextObservationAsync(cancellationToken);
            _ = await Task.WhenAny(
                    operation,
                    processCompletion,
                    nextObservation)
                .ConfigureAwait(false);
            if (nextObservation.IsCompleted)
            {
                await nextObservation.ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!operation.IsCompleted && processCompletion.IsCompleted)
        {
            var processExit = await processCompletion.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var phase = observation.InputWait.Phase switch
            {
                DebugInputWaitPhase.WorkbookOpen => "workbook open",
                DebugInputWaitPhase.TargetStart => "target start",
                _ => observation.InputWait.Phase.ToString()
            };
            throw new DebugSetupException(
                $"Owned Excel process {observation.InputWait.ProcessId} exited with code " +
                $"{processExit.ExitCode} before the {phase} operation completed.");
        }

        return await operation.ConfigureAwait(false);
    }

    public async Task ObserveUntilProcessExitAsync(
        DebugModalPromptObservation observation,
        Task<DebugProcessExit> processCompletion,
        IDebugInputWaitSink inputWaitSink,
        CancellationToken cancellationToken)
    {
        while (!processCompletion.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentWindows = windowApi.CaptureVisibleModalWindows(
                observation.InputWait.ProcessId);
            if (observation.TryMarkNewModalWindows(currentWindows))
            {
                await inputWaitSink
                    .InputRequiredAsync(observation.InputWait, cancellationToken)
                    .ConfigureAwait(false);
            }

            var nextObservation = windowApi.WaitForNextObservationAsync(cancellationToken);
            var completed = await Task.WhenAny(processCompletion, nextObservation)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, nextObservation))
            {
                await nextObservation.ConfigureAwait(false);
            }
        }

        _ = await processCompletion.ConfigureAwait(false);
    }
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
