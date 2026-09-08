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
    bool HandleReleaseVerified => false;

    void Assign(IDebugOwnedProcess process);

    void Terminate();
}

internal interface IDebugOwnedProcess : IDisposable
{
    bool HandleReleaseVerified => false;

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
    bool HandleReleaseVerified => false;

    void ResumeExactlyOnce();
}

internal sealed record DebugSuspendedProcessLaunch(
    IDebugOwnedProcess Process,
    IDebugSuspendedPrimaryThread PrimaryThread,
    StreamReader? StandardOutput = null,
    StreamReader? StandardError = null)
{
    internal DebugFailureOutcome? LaunchCleanupOutcome { get; init; }
}

internal sealed class ExistingExcelProcessOwnershipRejectedException : DebugSetupException, IDebugFailureEvidence
{
    private readonly Lazy<DebugFailureOutcome> outcome;

    public ExistingExcelProcessOwnershipRejectedException()
        : base(
            "The visible Excel window belongs to an existing Excel process; " +
            "debug ownership was rejected.")
    {
        outcome = new(() => new DebugFailureOutcome(ExceptionDispatchInfo.Capture(this), [],
            [new("ownership-check", "Excel process", DebugResourceKind.Process, true,
                "The existing-process inventory rejected ownership before any new process handle was acquired.")]));
    }

    public DebugFailureOutcome FailureOutcome => outcome.Value;
}

internal sealed class DebugProcessOwnershipCleanupException : DebugSetupException, IDebugFailureEvidence
{
    public DebugProcessOwnershipCleanupException(Exception ownershipException, Exception cleanupException)
        : this(CreateOutcome(ownershipException, cleanupException)) { }

    internal DebugProcessOwnershipCleanupException(DebugFailureOutcome outcome)
        : base("Exact Excel process ownership failed, and cleanup of the launched process could not be verified.",
            new AggregateException((outcome.PrimaryFailure is null ? [] : new[] { outcome.PrimaryFailure })
                .Concat(outcome.CleanupFailures.Select(item => item.Exception))))
    {
        FailureOutcome = outcome;
        OwnershipException = outcome.PrimaryFailure ?? new InvalidOperationException("Excel ownership failed.");
        CleanupException = outcome.CleanupFailures.Count == 1 ? outcome.CleanupFailures[0].Exception
            : new AggregateException(outcome.CleanupFailures.Select(item => item.Exception));
    }

    public Exception OwnershipException { get; }

    public Exception CleanupException { get; }

    public DebugFailureOutcome FailureOutcome { get; }

    private static DebugFailureOutcome CreateOutcome(Exception primary, Exception cleanup)
    {
        var completion = new DebugFailureCompletion(primary);
        completion.AddFailure("ownership-cleanup", "Excel process resources", DebugResourceKind.Process, cleanup);
        completion.AddEvidence(new("ownership-cleanup", "Excel process resources", DebugResourceKind.Process,
            false, "The lower process adapter did not report positive resource-release evidence."));
        return completion.Complete();
    }
}

/// <summary>
/// Owns one exactly identified visible Excel process for a debug session.
/// </summary>
internal sealed class DebugExcelProcessOwner : IAsyncDisposable, IDebugResourceOwnerEvidence
{
    private static readonly TimeSpan FailedOwnershipCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IDebugOwnedProcess process;
    private readonly IDebugProcessJob job;
    private readonly object terminationLock = new();
    private Task? terminationTask;
    private readonly object disposalLock = new();
    private Task? disposalTask;
    private DebugFailureOutcome? cleanupOutcome;
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

    public DebugFailureOutcome? CleanupOutcome => Volatile.Read(ref cleanupOutcome);

    internal bool HasExited =>
        Completion.IsCompletedSuccessfully
        || Volatile.Read(ref disposed) == 0 && process.HasExited;

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
        var jobCreationAttempted = false;
        try
        {
            if (process.HasExited)
            {
                throw new DebugSetupException(
                    "The explicitly launched Excel process exited before ownership was established.");
            }

            jobCreationAttempted = true;
            job = processApi.CreateKillOnCloseJob();
            job.Assign(process);
            return new DebugExcelProcessOwner(process, job);
        }
        catch (Exception ownershipException)
        {
            DisposeAfterOwnershipFailure(process, job, ownershipException, jobCreationAttempted);
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
        try
        {
            if (process.HasExited)
            {
                throw new DebugSetupException(
                    "The atomically owned Excel process exited before its primary thread could be resumed.");
            }

            return new DebugExcelProcessOwner(process, job);
        }
        catch (Exception ownershipException)
        {
            DisposeAfterOwnershipFailure(process, job, ownershipException);
            throw;
        }
    }

    public ValueTask TerminateAsync()
    {
        lock (terminationLock)
        {
            return new ValueTask(terminationTask ??= TerminateOnceAsync());
        }
    }

    private async Task TerminateOnceAsync()
    {
        var completion = new DebugFailureCompletion();
        var exited = false;
        try { exited = process.HasExited; }
        catch (Exception exception)
        {
            completion.AddFailure("process-exit-observation", "Excel process", DebugResourceKind.Process,
                exception, ProcessId);
        }

        var terminationFailed = false;
        if (!exited)
        {
            try { job.Terminate(); }
            catch (Exception exception)
            {
                terminationFailed = true;
                completion.AddFailure("process-job-termination", "Excel process tree", DebugResourceKind.Process,
                    exception, ProcessId);
                try { exited = process.HasExited; }
                catch (Exception observationFailure)
                {
                    completion.AddFailure("process-exit-observation", "Excel process", DebugResourceKind.Process,
                        observationFailure, ProcessId);
                }
                if (!exited)
                {
                    try { process.Kill(); }
                    catch (Exception killFailure)
                    {
                        completion.AddFailure("process-fallback-kill", "Excel process", DebugResourceKind.Process,
                            killFailure, ProcessId);
                    }
                }
            }
        }

        try { exited = process.HasExited; }
        catch (Exception exception)
        {
            completion.AddFailure("process-exit-observation", "Excel process", DebugResourceKind.Process,
                exception, ProcessId);
        }
        if (!terminationFailed || exited)
        {
            try { await Completion.ConfigureAwait(false); }
            catch (Exception exception)
            {
                completion.AddFailure("process-exit-completion", "Excel process", DebugResourceKind.Process,
                    exception, ProcessId);
            }
        }
        completion.AddEvidence(new("process-termination", "Excel process", DebugResourceKind.Process,
            Completion.IsCompletedSuccessfully, "The retained process exit task supplies terminal completion evidence.", ProcessId));
        completion.Complete().Throw();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalLock)
        {
            return new ValueTask(disposalTask ??= DisposeOnceAsync());
        }
    }

    private async Task DisposeOnceAsync()
    {
        Volatile.Write(ref disposed, 1);
        var completion = new DebugFailureCompletion();
        try
        {
            await TerminateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            completion.AddFailure("process-termination", "Excel process", DebugResourceKind.Process, ex, ProcessId);
        }
        var exited = Completion.IsCompletedSuccessfully;
        completion.AddEvidence(new("process-termination", "Excel process", DebugResourceKind.Process,
            exited, exited ? "The owned process exit task completed successfully."
                : "The owned process exit task did not establish terminal completion.", ProcessId));
        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            completion.AddFailure("process-handle-release", "Excel process handle", DebugResourceKind.Handle, ex, ProcessId);
        }
        completion.AddEvidence(new("process-handle-release", "Excel process handle", DebugResourceKind.Handle,
            process.HandleReleaseVerified, "Native release evidence reported by the exact process owner.", ProcessId));

        try
        {
            job.Dispose();
        }
        catch (Exception ex)
        {
            completion.AddFailure("job-handle-release", "Excel Job handle", DebugResourceKind.Handle, ex, ProcessId);
        }
        completion.AddEvidence(new("job-handle-release", "Excel Job handle", DebugResourceKind.Handle,
            job.HandleReleaseVerified, "Native release evidence reported by the exact Job owner.", ProcessId));
        var outcome = completion.Complete();
        Volatile.Write(ref cleanupOutcome, outcome);
        outcome.Throw();
    }

    [DoesNotReturn]
    private static void DisposeAfterOwnershipFailure(
        IDebugOwnedProcess process,
        IDebugProcessJob? job,
        Exception ownershipException,
        bool jobCreationAttempted = false)
    {
        var completion = new DebugFailureCompletion(ownershipException);
        var processId = process.Id;
        try
        {
            job?.Dispose();
        }
        catch (Exception ex)
        {
            completion.AddFailure("ownership-job-release", "Excel Job handle", DebugResourceKind.Handle, ex, processId);
        }
        if (job is not null)
        {
            completion.AddEvidence(new("ownership-job-release", "Excel Job handle", DebugResourceKind.Handle,
                job.HandleReleaseVerified, "Native release evidence reported by the acquired Job owner.", processId));
        }
        else if (!(ownershipException is IDebugFailureEvidence lower
            && lower.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle)))
        {
            completion.AddEvidence(new("ownership-job-release", "Excel Job handle", DebugResourceKind.Handle,
                !jobCreationAttempted, jobCreationAttempted
                    ? "Job creation did not return native handle release evidence."
                    : "The Job creation operation was never invoked.", processId));
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
            completion.AddFailure("ownership-process-kill", "Excel process", DebugResourceKind.Process, ex, processId);
        }

        var exited = false;
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

            exited = process.HasExited;
            if (!exited)
            {
                throw new InvalidOperationException(
                    "The exactly launched Excel process remained live after failed ownership cleanup.");
            }
        }
        catch (OperationCanceledException ex)
        {
            completion.AddFailure("ownership-process-exit", "Excel process", DebugResourceKind.Process,
                new TimeoutException(
                    "Timed out while verifying cleanup of the exactly launched Excel process.",
                    ex), processId);
        }
        catch (Exception ex)
        {
            completion.AddFailure("ownership-process-exit", "Excel process", DebugResourceKind.Process, ex, processId);
        }
        completion.AddEvidence(new("ownership-process-exit", "Excel process", DebugResourceKind.Process,
            exited, "The acquired process owner reports its terminal exit observation.", processId));

        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            completion.AddFailure("ownership-process-handle-release", "Excel process handle", DebugResourceKind.Handle,
                ex, processId);
        }
        completion.AddEvidence(new("ownership-process-handle-release", "Excel process handle", DebugResourceKind.Handle,
            process.HandleReleaseVerified, "Native release evidence reported by the acquired process owner.", processId));

        var outcome = completion.Complete();
        if (outcome.HasCleanupFailure || outcome.HasUnprovedRelease)
        {
            throw new DebugProcessOwnershipCleanupException(outcome);
        }

        throw new DebugFailureException(outcome);
    }

    private static async Task<DebugProcessExit> MonitorExitAsync(IDebugOwnedProcess process)
    {
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return new DebugProcessExit(process.ExitCode);
    }
}
