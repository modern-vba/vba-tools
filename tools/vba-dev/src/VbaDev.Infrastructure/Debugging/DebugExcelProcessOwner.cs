using VbaDev.App.Debugging;

namespace VbaDev.Infrastructure.Debugging;

internal interface IDebugExcelProcessApi
{
    IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses();

    int GetProcessId(nint windowHandle);

    IDebugOwnedProcess OpenProcess(int processId);

    IDebugProcessJob CreateKillOnCloseJob()
        => throw new DebugSetupException(
            "Strong Excel process ownership is not available from this process adapter.");
}

internal interface IDebugProcessJob : IDisposable
{
    void Assign(IDebugOwnedProcess process);

    void Terminate();
}

internal interface IDebugOwnedProcess : IDisposable
{
    int Id { get; }

    DebugExcelProcessArchitecture Architecture { get; }

    DateTime StartTime { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

internal sealed class ExistingExcelProcessOwnershipRejectedException : DebugSetupException
{
    public ExistingExcelProcessOwnershipRejectedException()
        : base(
            "The visible Excel window belongs to an existing Excel process; " +
            "debug ownership was rejected.")
    {
    }
}

/// <summary>
/// Owns one exactly identified visible Excel process for a debug session.
/// </summary>
internal sealed class DebugExcelProcessOwner : IAsyncDisposable
{
    private readonly IDebugOwnedProcess process;
    private readonly IDebugProcessJob job;
    private readonly SemaphoreSlim terminationLock = new(1, 1);
    private int terminationCompleted;
    private int disposed;

    private DebugExcelProcessOwner(IDebugOwnedProcess process, IDebugProcessJob job)
    {
        this.process = process;
        this.job = job;
        ProcessId = process.Id;
        ProcessStartTime = process.StartTime;
        Completion = MonitorExitAsync(process);
    }

    public int ProcessId { get; }

    internal DebugExcelProcessArchitecture ProcessArchitecture => process.Architecture;

    internal DateTime ProcessStartTime { get; }

    internal bool KillOnCloseJobAssigned => true;

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
            throw new ExistingExcelProcessOwnershipRejectedException();
        }

        var process = processApi.OpenProcess(processId);
        IDebugProcessJob? job = null;
        try
        {
            if (process.Id != processId || process.HasExited)
            {
                throw new DebugSetupException(
                    "The visible Excel process exited or changed identity before debug ownership was established.");
            }

            job = processApi.CreateKillOnCloseJob();
            job.Assign(process);
            return new DebugExcelProcessOwner(process, job);
        }
        catch
        {
            job?.Dispose();
            TryTerminateExactProcess(process);
            process.Dispose();
            throw;
        }
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
                    job.Terminate();
                }
                catch (Exception) when (process.HasExited)
                {
                }
                catch
                {
                    process.Kill();
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
            try
            {
                process.Dispose();
            }
            finally
            {
                job.Dispose();
            }
        }
    }

    private static void TryTerminateExactProcess(IDebugOwnedProcess process)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill();
        }
        catch (Exception) when (process.HasExited)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<DebugProcessExit> MonitorExitAsync(IDebugOwnedProcess process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return new DebugProcessExit(process.ExitCode);
    }
}
