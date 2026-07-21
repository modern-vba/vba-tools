using VbaDev.App.Debugging;

namespace VbaDev.Infrastructure.Debugging;

internal interface IDebugExcelProcessApi
{
    IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses();

    int GetProcessId(nint windowHandle);

    IDebugOwnedProcess OpenProcess(int processId);
}

internal interface IDebugOwnedProcess : IDisposable
{
    int Id { get; }

    DateTime StartTime { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

/// <summary>
/// Owns one exactly identified visible Excel process for a debug session.
/// </summary>
internal sealed class DebugExcelProcessOwner : IAsyncDisposable
{
    private readonly IDebugOwnedProcess process;
    private readonly SemaphoreSlim terminationLock = new(1, 1);
    private int terminationCompleted;
    private int disposed;

    private DebugExcelProcessOwner(IDebugOwnedProcess process)
    {
        this.process = process;
        ProcessId = process.Id;
        ProcessStartTime = process.StartTime;
        Completion = MonitorExitAsync(process);
    }

    public int ProcessId { get; }

    internal DateTime ProcessStartTime { get; }

    public Task<DebugProcessExit> Completion { get; }

    public static DebugExcelProcessOwner Capture(
        nint windowHandle,
        IReadOnlyDictionary<int, DateTime> existingExcelProcesses,
        IDebugExcelProcessApi processApi)
    {
        var processId = processApi.GetProcessId(windowHandle);
        if (processId <= 0)
        {
            throw new DebugSetupException(
                "The visible Excel window could not be associated with a process.");
        }

        if (existingExcelProcesses.ContainsKey(processId))
        {
            throw new DebugSetupException(
                "The visible Excel window belongs to an existing Excel process; debug ownership was rejected.");
        }

        var process = processApi.OpenProcess(processId);
        if (process.Id != processId || process.HasExited)
        {
            process.Dispose();
            throw new DebugSetupException(
                "The visible Excel process exited or changed identity before debug ownership was established.");
        }

        return new DebugExcelProcessOwner(process);
    }

    public async ValueTask TerminateAsync()
    {
        if (Volatile.Read(ref terminationCompleted) != 0)
        {
            return;
        }

        await terminationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (terminationCompleted != 0)
            {
                return;
            }

            if (!process.HasExited)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                }
            }

            await Completion.ConfigureAwait(false);
            Volatile.Write(ref terminationCompleted, 1);
        }
        finally
        {
            terminationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await TerminateAsync().ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<DebugProcessExit> MonitorExitAsync(IDebugOwnedProcess process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return new DebugProcessExit(process.ExitCode);
    }
}
