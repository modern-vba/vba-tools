using System.Diagnostics;

namespace VbaDebugAdapter.Diagnostics;

internal interface IDebugEnvironmentDoctorDeadline
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal enum DebugEnvironmentDoctorStageTermination
{
    Completed,
    Timeout,
    CallerCancellation,
    InfrastructureLoss
}

internal sealed record DebugEnvironmentDoctorStageExecution(
    DebugEnvironmentDiagnosticCheck Check,
    DebugEnvironmentDoctorStageTermination Termination);

internal interface IDebugEnvironmentDoctorStageRunner
{
    Task<DebugEnvironmentDoctorStageExecution> RunAsync(
        string checkId,
        TimeSpan timeout,
        Func<CancellationToken, Task<DebugEnvironmentProbeCheckResult>> operation,
        CancellationToken callerCancellationToken);
}

internal sealed class DebugEnvironmentDoctorStageRunner
    : IDebugEnvironmentDoctorStageRunner
{
    private static readonly TimeSpan CancellationUnwindGrace =
        TimeSpan.FromSeconds(5);

    private readonly IDebugEnvironmentDoctorDeadline deadline;

    public DebugEnvironmentDoctorStageRunner()
        : this(SystemDebugEnvironmentDoctorDeadline.Instance)
    {
    }

    internal DebugEnvironmentDoctorStageRunner(
        IDebugEnvironmentDoctorDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        this.deadline = deadline;
    }

    public async Task<DebugEnvironmentDoctorStageExecution> RunAsync(
        string checkId,
        TimeSpan timeout,
        Func<CancellationToken, Task<DebugEnvironmentProbeCheckResult>> operation,
        CancellationToken callerCancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        ArgumentNullException.ThrowIfNull(operation);

        var stopwatch = Stopwatch.StartNew();
        using var stageCancellation = new CancellationTokenSource();
        using var deadlineCancellation = new CancellationTokenSource();
        Task<DebugEnvironmentProbeCheckResult> operationTask;
        try
        {
            operationTask = operation(stageCancellation.Token);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return InfrastructureLoss(checkId, exception, stopwatch.ElapsedMilliseconds);
        }

        var deadlineTask = deadline.DelayAsync(timeout, deadlineCancellation.Token);
        var callerCancellationTask = callerCancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, callerCancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan);
        await Task.WhenAny(
            operationTask,
            deadlineTask,
            callerCancellationTask).ConfigureAwait(false);

        if (callerCancellationToken.IsCancellationRequested)
        {
            deadlineCancellation.Cancel();
            stageCancellation.Cancel();
            await WaitForCancellationUnwindAsync(operationTask).ConfigureAwait(false);
            ObserveAbandoned(operationTask);
            stopwatch.Stop();
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    DebugEnvironmentDiagnosticStatus.Unverified,
                    "The check was canceled before it reached a terminal classification.",
                    stopwatch.ElapsedMilliseconds),
                DebugEnvironmentDoctorStageTermination.CallerCancellation);
        }

        if (deadlineTask.IsCompletedSuccessfully)
        {
            stageCancellation.Cancel();
            await WaitForCancellationUnwindAsync(operationTask).ConfigureAwait(false);
            ObserveAbandoned(operationTask);
            stopwatch.Stop();
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    DebugEnvironmentDiagnosticStatus.Unverified,
                    $"The check did not complete within {timeout.TotalSeconds:0.###} seconds.",
                    stopwatch.ElapsedMilliseconds),
                DebugEnvironmentDoctorStageTermination.Timeout);
        }

        deadlineCancellation.Cancel();
        try
        {
            var result = await operationTask.ConfigureAwait(false);
            stopwatch.Stop();
            return new DebugEnvironmentDoctorStageExecution(
                new DebugEnvironmentDiagnosticCheck(
                    checkId,
                    result.Status,
                    result.Message,
                    stopwatch.ElapsedMilliseconds)
                {
                    Remediation = result.Remediation,
                    Details = result.Details
                },
                DebugEnvironmentDoctorStageTermination.Completed);
        }
        catch (OperationCanceledException exception)
        {
            stopwatch.Stop();
            return InfrastructureLoss(checkId, exception, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return InfrastructureLoss(checkId, exception, stopwatch.ElapsedMilliseconds);
        }
    }

    private static DebugEnvironmentDoctorStageExecution InfrastructureLoss(
        string checkId,
        Exception exception,
        long durationMilliseconds)
        => new(
            new DebugEnvironmentDiagnosticCheck(
                checkId,
                DebugEnvironmentDiagnosticStatus.Unverified,
                $"Doctor infrastructure did not classify the check: {exception.Message}",
                durationMilliseconds),
            DebugEnvironmentDoctorStageTermination.InfrastructureLoss);

    private static void ObserveAbandoned(Task operationTask)
        => _ = operationTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task WaitForCancellationUnwindAsync(Task operationTask)
    {
        if (operationTask.IsCompleted)
        {
            return;
        }

        try
        {
            var grace = deadline.DelayAsync(
                CancellationUnwindGrace,
                CancellationToken.None);
            _ = await Task.WhenAny(operationTask, grace).ConfigureAwait(false);
        }
        catch
        {
            // Timeout/cancellation classification remains authoritative. Any abandoned
            // operation fault is observed separately before the caller continues cleanup.
        }
    }

    private sealed class SystemDebugEnvironmentDoctorDeadline
        : IDebugEnvironmentDoctorDeadline
    {
        public static SystemDebugEnvironmentDoctorDeadline Instance { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
            => Task.Delay(delay, cancellationToken);
    }
}
