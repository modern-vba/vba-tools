using System.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IOwnedExcelProcessControl : IAsyncDisposable
{
    bool HasExited { get; }

    Task Completion { get; }

    Task TerminateAsync();
}

internal sealed class DebugOwnedExcelProcessControl(
    DebugExcelProcessOwner owner) : IOwnedExcelProcessControl
{
    public bool HasExited
    {
        get
        {
            try
            {
                return owner.HasExited;
            }
            catch (Exception ex) when (
                ex is ObjectDisposedException or InvalidOperationException)
            {
                if (owner.Completion.IsCompletedSuccessfully)
                {
                    return true;
                }

                throw;
            }
        }
    }

    public Task Completion => owner.Completion;

    public Task TerminateAsync() => owner.TerminateAsync().AsTask();

    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

/// <summary>
/// Records one forced-cleanup request and applies it only to the exactly attached Excel owner.
/// </summary>
internal sealed class OwnedExcelTerminationController : IDisposable
{
    private readonly object gate = new();
    private IOwnedExcelProcessControl? owner;
    private TaskCompletionSource? activeLaunchSettlement;
    private int activeLaunchId;
    private bool launchSealed;
    private bool disposed;
    private Task? cleanupTask;
    private Exception? cleanupFailure;
    private Stopwatch? cleanupClock;
    private TimeSpan cleanupGrace;

    public Exception? TerminationFailure
    {
        get
        {
            lock (gate)
            {
                return cleanupFailure;
            }
        }
    }

    public bool HasAttachedProcessExited
    {
        get
        {
            IOwnedExcelProcessControl? process;
            lock (gate)
            {
                process = owner;
            }

            return process?.HasExited ?? false;
        }
    }

    public bool HasAttachedProcess
    {
        get
        {
            lock (gate)
            {
                return owner is not null;
            }
        }
    }

    public Task? AttachedProcessCompletion
    {
        get
        {
            lock (gate)
            {
                return owner?.Completion;
            }
        }
    }

    public OwnedExcelLaunchLease BeginLaunch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (launchSealed)
            {
                throw new OperationCanceledException(
                    "Excel process launch was sealed before ownership could begin.",
                    cancellationToken);
            }

            if (activeLaunchSettlement is not null)
            {
                throw new InvalidOperationException(
                    "An owned Excel process launch is already in progress.");
            }

            activeLaunchSettlement = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            activeLaunchId++;
            return new OwnedExcelLaunchLease(this, activeLaunchId);
        }
    }

    public bool Attach(IOwnedExcelProcessControl process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (gate)
        {
            if (disposed && activeLaunchSettlement is null)
            {
                throw new ObjectDisposedException(nameof(OwnedExcelTerminationController));
            }

            if (launchSealed && activeLaunchSettlement is null)
            {
                throw new OperationCanceledException(
                    "Excel process ownership was sealed before attachment.");
            }

            if (owner is not null)
            {
                throw new InvalidOperationException("An owned Excel process is already attached.");
            }

            owner = process;
            return !launchSealed && !disposed;
        }
    }

    public Task WaitForLaunchSettlementAsync()
    {
        lock (gate)
        {
            return activeLaunchSettlement?.Task ?? Task.CompletedTask;
        }
    }

    public Task RequestCleanupAsync(TimeSpan grace)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);
        lock (gate)
        {
            if (cleanupTask is null)
            {
                launchSealed = true;
                cleanupClock = Stopwatch.StartNew();
                cleanupGrace = grace;
                cleanupTask = CleanupCoreAsync(
                    cleanupClock,
                    grace,
                    activeLaunchSettlement?.Task ?? Task.CompletedTask);
                WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
            }

            return cleanupTask;
        }
    }

    public async Task ObserveCleanupWithinAsync(TimeSpan observationAllowance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(observationAllowance, TimeSpan.Zero);
        Task task;
        TimeSpan remaining;
        lock (gate)
        {
            task = cleanupTask
                ?? throw new InvalidOperationException("Owned Excel cleanup has not been requested.");
            remaining = cleanupGrace + observationAllowance - cleanupClock!.Elapsed;
        }

        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException(
                "Timed out while waiting for owned Excel cleanup verification.");
        }

        await task.WaitAsync(remaining).ConfigureAwait(false);
    }

    public Task DisposeAttachedProcessAsync()
        => RequestCleanupAsync(TimeSpan.Zero);

    public void RequestForcedTermination(TimeSpan grace)
        => _ = RequestCleanupAsync(grace);

    public void CancelForcedTermination()
    {
        // Cleanup is irreversible after launch admission is sealed.
    }

    public async Task ObserveTerminationAsync()
    {
        Task? task;
        lock (gate)
        {
            task = cleanupTask;
        }

        if (task is not null)
        {
            await task.ConfigureAwait(false);
        }
    }

    public async Task<bool> WaitForExitOrTerminationAttemptAsync()
    {
        Task cleanup;
        Task? completion;
        lock (gate)
        {
            cleanup = cleanupTask
                ?? throw new InvalidOperationException("Forced termination has not been requested.");
            completion = owner?.Completion;
        }

        await cleanup.ConfigureAwait(false);
        return completion?.IsCompletedSuccessfully ?? true;
    }

    public void Dispose()
    {
        Task? cleanupToObserve = null;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            launchSealed = true;
            if (cleanupTask is null &&
                (owner is not null || activeLaunchSettlement is not null))
            {
                cleanupTask = CleanupCoreAsync(
                    cleanupClock = Stopwatch.StartNew(),
                    TimeSpan.Zero,
                    activeLaunchSettlement?.Task ?? Task.CompletedTask);
                cleanupGrace = TimeSpan.Zero;
                cleanupToObserve = cleanupTask;
            }
        }

        if (cleanupToObserve is not null)
        {
            WorkbookAutomationStageExecutor.ObserveFault(cleanupToObserve);
        }
    }

    private void EndLaunch(int launchId, Exception? cleanupProofFailure)
    {
        TaskCompletionSource settlement;
        lock (gate)
        {
            if (activeLaunchSettlement is null || activeLaunchId != launchId)
            {
                return;
            }

            settlement = activeLaunchSettlement;
            activeLaunchSettlement = null;
        }

        if (cleanupProofFailure is null)
        {
            settlement.TrySetResult();
        }
        else
        {
            settlement.TrySetException(cleanupProofFailure);
        }
    }

    private async Task CleanupCoreAsync(
        Stopwatch requestClock,
        TimeSpan grace,
        Task launchSettlement)
    {
        await Task.Yield();
        var errors = new List<Exception>();
        try
        {
            try
            {
                await launchSettlement.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            IOwnedExcelProcessControl? process;
            lock (gate)
            {
                process = owner;
            }

            if (process is not null)
            {
                var completion = process.Completion;
                var completionVerified = false;
                var remainingGrace = grace - requestClock.Elapsed;
                if (remainingGrace < TimeSpan.Zero)
                {
                    remainingGrace = TimeSpan.Zero;
                }

                if (!completion.IsCompleted && remainingGrace > TimeSpan.Zero)
                {
                    await Task.WhenAny(
                        completion,
                        Task.Delay(remainingGrace)).ConfigureAwait(false);
                }

                if (completion.IsCompleted)
                {
                    try
                    {
                        await completion.ConfigureAwait(false);
                        completionVerified = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }
                }

                if (!completionVerified)
                {
                    try
                    {
                        await process.TerminateAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(ex);
                    }

                    if (completion.IsCompletedSuccessfully)
                    {
                        completionVerified = true;
                    }
                    else if (!completion.IsCompleted)
                    {
                        errors.Add(new InvalidOperationException(
                            "The exactly owned Excel process did not report a completed exit after termination."));
                    }
                }

                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }

                if (!completionVerified)
                {
                    errors.Add(new InvalidOperationException(
                        "The exactly owned Excel process exit could not be verified."));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        if (errors.Count == 0)
        {
            return;
        }

        var failure = errors.Count == 1
            ? errors[0]
            : new AggregateException(errors);
        lock (gate)
        {
            cleanupFailure = failure;
        }

        throw failure;
    }

    internal sealed class OwnedExcelLaunchLease(
        OwnedExcelTerminationController controller,
        int launchId) : IDisposable
    {
        private int completed;

        public void CompleteWithCleanupFailure(Exception cleanupProofFailure)
        {
            ArgumentNullException.ThrowIfNull(cleanupProofFailure);
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                controller.EndLaunch(launchId, cleanupProofFailure);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
            {
                controller.EndLaunch(launchId, cleanupProofFailure: null);
            }
        }
    }
}

/// <summary>
/// Applies one stage deadline while allowing owned-process force cleanup to run off the COM thread.
/// </summary>
internal sealed class WorkbookAutomationStageExecutor
{
    private static readonly Task NeverCompletes = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously).Task;
    private static readonly TimeSpan MaximumTimerChunk = TimeSpan.FromDays(30);
    private readonly Func<bool> hasOwnedProcessExited;
    private readonly Action<TimeSpan> requestForcedTermination;
    private readonly TimeSpan forcedTerminationObservationAllowance;
    private readonly Func<Task?> getOwnedProcessCompletion;
    private int hasAbandonedOperation;

    public WorkbookAutomationStageExecutor(
        Func<bool> hasOwnedProcessExited,
        Action<TimeSpan> requestForcedTermination,
        TimeSpan? forcedTerminationObservationAllowance = null,
        Func<Task?>? getOwnedProcessCompletion = null)
    {
        this.hasOwnedProcessExited = hasOwnedProcessExited;
        this.requestForcedTermination = requestForcedTermination;
        this.forcedTerminationObservationAllowance =
            forcedTerminationObservationAllowance ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            this.forcedTerminationObservationAllowance,
            TimeSpan.Zero);
        this.getOwnedProcessCompletion = getOwnedProcessCompletion ?? (() => null);
    }

    public bool HasAbandonedOperation =>
        Volatile.Read(ref hasAbandonedOperation) != 0;

    public void MarkOperationAbandoned()
        => Interlocked.Exchange(ref hasAbandonedOperation, 1);

    public Task<T> ExecuteAsync<T>(
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        TimeSpan cleanupGrace,
        CancellationToken cancellationToken,
        Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync(
            stage,
            timeout,
            cleanupGrace,
            cancellationToken,
            _ => operation());
    }

    public async Task<T> ExecuteAsync<T>(
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        TimeSpan cleanupGrace,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateStageEntry(stage, timeout, cleanupGrace, cancellationToken);
        var clock = Stopwatch.StartNew();
        using var operationCancellation = new CancellationTokenSource();
        Task<T> operationTask;
        try
        {
            operationTask = operation(operationCancellation.Token);
        }
        catch (Exception ex)
        {
            ThrowCompletedTerminalState(
                stage,
                timeout,
                cleanupGrace,
                clock,
                cancellationToken,
                ex);
            throw;
        }

        using var timeoutCancellation = new CancellationTokenSource();
        try
        {
            var timeoutSignal = DelayInTimerChunksAsync(
                timeout,
                timeoutCancellation.Token);
            var cancellationSignal = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : NeverCompletes;
            var processCompletion = getOwnedProcessCompletion() ?? NeverCompletes;
            var completed = await Task.WhenAny(
                operationTask,
                timeoutSignal,
                cancellationSignal,
                processCompletion).ConfigureAwait(false);
            if (completed == operationTask)
            {
                try
                {
                    var result = await operationTask.ConfigureAwait(false);
                    ThrowCompletedTerminalState(
                        stage,
                        timeout,
                        cleanupGrace,
                        clock,
                        cancellationToken);
                    return result;
                }
                catch (Exception ex) when (ex is not (
                    WorkbookAutomationTimeoutException or
                    WorkbookAutomationCanceledException or
                    WorkbookAutomationProcessLostException))
                {
                    ThrowCompletedTerminalState(
                        stage,
                        timeout,
                        cleanupGrace,
                        clock,
                        cancellationToken,
                        ex);
                    throw;
                }
            }

            var terminal = completed == timeoutSignal
                ? AsyncTerminal.Timeout
                : completed == cancellationSignal
                    ? AsyncTerminal.Cancellation
                    : AsyncTerminal.ProcessLost;
            operationCancellation.Cancel();
            if (terminal is AsyncTerminal.Timeout or AsyncTerminal.Cancellation)
            {
                TryRequestForcedTermination(cleanupGrace);
            }

            var unwindWait = terminal == AsyncTerminal.ProcessLost
                ? forcedTerminationObservationAllowance
                : cleanupGrace + forcedTerminationObservationAllowance;
            if (!operationTask.IsCompleted && unwindWait > TimeSpan.Zero)
            {
                await Task.WhenAny(operationTask, Task.Delay(unwindWait)).ConfigureAwait(false);
            }

            Exception? operationError = null;
            if (operationTask.IsCompleted)
            {
                try
                {
                    _ = await operationTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    operationError = ex;
                }
            }
            else
            {
                MarkOperationAbandoned();
                ObserveFault(operationTask);
            }

            throw terminal switch
            {
                AsyncTerminal.Timeout =>
                    new WorkbookAutomationTimeoutException(stage, timeout, operationError),
                AsyncTerminal.Cancellation =>
                    new WorkbookAutomationCanceledException(
                        stage,
                        cancellationToken,
                        operationError),
                AsyncTerminal.ProcessLost =>
                    new WorkbookAutomationProcessLostException(stage, operationError),
                _ => throw new ArgumentOutOfRangeException(nameof(terminal), terminal, null)
            };
        }
        finally
        {
            operationCancellation.Cancel();
            timeoutCancellation.Cancel();
        }
    }

    private void ValidateStageEntry(
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        TimeSpan cleanupGrace,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(cleanupGrace, TimeSpan.Zero);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new WorkbookAutomationCanceledException(stage, cancellationToken);
        }

        if (hasOwnedProcessExited())
        {
            throw new WorkbookAutomationProcessLostException(stage);
        }
    }

    private void ThrowCompletedTerminalState(
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        TimeSpan cleanupGrace,
        Stopwatch clock,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        if (clock.Elapsed >= timeout)
        {
            TryRequestForcedTermination(cleanupGrace);
            throw new WorkbookAutomationTimeoutException(stage, timeout, innerException);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryRequestForcedTermination(cleanupGrace);
            throw new WorkbookAutomationCanceledException(
                stage,
                cancellationToken,
                innerException);
        }

        if (hasOwnedProcessExited())
        {
            throw new WorkbookAutomationProcessLostException(stage, innerException);
        }
    }

    private void TryRequestForcedTermination(TimeSpan cleanupGrace)
        => new TerminationRequest(requestForcedTermination, cleanupGrace).Invoke();

    internal static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DelayInTimerChunksAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var remaining = delay;
        while (remaining > MaximumTimerChunk)
        {
            await Task.Delay(MaximumTimerChunk, cancellationToken).ConfigureAwait(false);
            remaining -= MaximumTimerChunk;
        }

        await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
    }

    private sealed record TerminationRequest(
        Action<TimeSpan> Request,
        TimeSpan Grace)
    {
        public void Invoke()
        {
            try
            {
                Request(Grace);
            }
            catch
            {
                // Cleanup verification reports termination failures on the command thread.
            }
        }
    }

    private enum AsyncTerminal
    {
        Timeout,
        Cancellation,
        ProcessLost
    }
}
