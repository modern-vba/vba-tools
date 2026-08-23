using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

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

internal interface IDebugSuspendedPrimaryThread : IDisposable
{
    void ResumeExactlyOnce();
}

internal sealed record DebugSuspendedProcessLaunch(
    IDebugOwnedProcess Process,
    IDebugSuspendedPrimaryThread PrimaryThread,
    StreamReader? StandardOutput = null,
    StreamReader? StandardError = null);

internal sealed class ExistingExcelProcessOwnershipRejectedException : DebugSetupException
{
    public ExistingExcelProcessOwnershipRejectedException()
        : base(
            "The visible Excel window belongs to an existing Excel process; " +
            "debug ownership was rejected.")
    {
    }
}

internal sealed class DebugProcessOwnershipCleanupException(
    Exception ownershipException,
    Exception cleanupException) : DebugSetupException(
        "Exact Excel process ownership failed, and cleanup of the launched process could not be verified.",
        new AggregateException(ownershipException, cleanupException))
{
    public Exception OwnershipException { get; } = ownershipException;

    public Exception CleanupException { get; } = cleanupException;
}

/// <summary>
/// Owns one exactly identified visible Excel process for a debug session.
/// </summary>
internal sealed class DebugExcelProcessOwner : IAsyncDisposable
{
    private static readonly TimeSpan FailedOwnershipCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IDebugOwnedProcess process;
    private readonly IDebugProcessJob job;
    private readonly SemaphoreSlim terminationLock = new(1, 1);
    private Exception? terminationFailure;
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

    internal bool HasExited =>
        Volatile.Read(ref disposed) != 0 || process.HasExited;

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
        if (process.Id != processId)
        {
            DisposeAfterOwnershipFailure(
                process,
                job: null,
                new DebugSetupException(
                    "The visible Excel process changed identity before debug ownership was established."));
        }

        return OwnStartedProcess(process, processApi);
    }

    /// <summary>
    /// Establishes kill-on-close ownership over an exact process returned by an explicit launch.
    /// </summary>
    internal static DebugExcelProcessOwner OwnStartedProcess(
        IDebugOwnedProcess process,
        IDebugExcelProcessApi processApi)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(processApi);
        IDebugProcessJob? job = null;
        try
        {
            if (process.HasExited)
            {
                throw new DebugSetupException(
                    "The explicitly launched Excel process exited before ownership was established.");
            }

            job = processApi.CreateKillOnCloseJob();
            job.Assign(process);
            return new DebugExcelProcessOwner(process, job);
        }
        catch (Exception ownershipException)
        {
            DisposeAfterOwnershipFailure(process, job, ownershipException);
            throw;
        }
    }

    /// <summary>
    /// Adopts a suspended process that was atomically created inside the supplied kill-on-close job.
    /// </summary>
    internal static DebugExcelProcessOwner AdoptPreassignedProcess(
        IDebugOwnedProcess process,
        IDebugProcessJob job)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(job);
        if (process.HasExited)
        {
            DisposeAfterOwnershipFailure(
                process,
                job,
                new DebugSetupException(
                    "The atomically owned Excel process exited before its primary thread could be resumed."));
        }

        return new DebugExcelProcessOwner(process, job);
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

            if (terminationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(terminationFailure).Throw();
            }

            Exception? cleanupFailure = null;
            Exception? fallbackKillFailure = null;
            if (!process.HasExited)
            {
                try
                {
                    job.Terminate();
                }
                catch (Exception) when (process.HasExited)
                {
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    try
                    {
                        process.Kill();
                    }
                    catch (Exception killException)
                    {
                        fallbackKillFailure = killException;
                        cleanupFailure = killException;
                    }
                }
            }

            if (cleanupFailure is null || process.HasExited)
            {
                try
                {
                    await Completion.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupFailure = Combine(cleanupFailure, ex);
                }
            }

            if (cleanupFailure is not null)
            {
                terminationFailure = fallbackKillFailure
                    ?? new DebugSetupException(
                        "The owned Excel process tree termination could not be verified.",
                        cleanupFailure);
                ExceptionDispatchInfo.Capture(terminationFailure).Throw();
            }

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

        Exception? terminationException = null;
        try
        {
            await TerminateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            terminationException = ex;
        }

        Exception? disposalFailure = null;
        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            disposalFailure = Combine(disposalFailure, ex);
        }

        try
        {
            job.Dispose();
        }
        catch (Exception ex)
        {
            disposalFailure = Combine(disposalFailure, ex);
        }

        if (disposalFailure is not null)
        {
            throw new DebugSetupException(
                "The owned Excel process tree cleanup could not be verified.",
                Combine(terminationException, disposalFailure));
        }

        if (terminationException is DebugSetupException)
        {
            throw new DebugSetupException(
                "The owned Excel process tree cleanup could not be verified.",
                terminationException);
        }

        if (terminationException is not null)
        {
            ExceptionDispatchInfo.Capture(terminationException).Throw();
        }
    }

    [DoesNotReturn]
    private static void DisposeAfterOwnershipFailure(
        IDebugOwnedProcess process,
        IDebugProcessJob? job,
        Exception ownershipException)
    {
        Exception? cleanupException = null;
        try
        {
            job?.Dispose();
        }
        catch (Exception ex)
        {
            cleanupException = ex;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (Exception ex)
        {
            cleanupException = Combine(cleanupException, ex);
        }

        try
        {
            if (!process.HasExited)
            {
                using var cleanupTimeout = new CancellationTokenSource(
                    FailedOwnershipCleanupTimeout);
                process.WaitForExitAsync(cleanupTimeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }

            if (!process.HasExited)
            {
                cleanupException = Combine(
                    cleanupException,
                    new InvalidOperationException(
                        "The exactly launched Excel process remained live after failed ownership cleanup."));
            }
        }
        catch (OperationCanceledException ex)
        {
            cleanupException = Combine(
                cleanupException,
                new TimeoutException(
                    "Timed out while verifying cleanup of the exactly launched Excel process.",
                    ex));
        }
        catch (Exception ex)
        {
            cleanupException = Combine(cleanupException, ex);
        }

        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            cleanupException = Combine(cleanupException, ex);
        }

        if (cleanupException is not null)
        {
            throw new DebugProcessOwnershipCleanupException(
                ownershipException,
                cleanupException);
        }

        ExceptionDispatchInfo.Capture(ownershipException).Throw();
    }

    private static Exception Combine(Exception? current, Exception next)
        => current is null ? next : new AggregateException(current, next);

    private static async Task<DebugProcessExit> MonitorExitAsync(IDebugOwnedProcess process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return new DebugProcessExit(process.ExitCode);
    }
}
