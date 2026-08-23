using VbaDebugAdapter.Diagnostics;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugEnvironmentDoctorStageRunnerTests
{
    [Fact]
    public async Task DeadlineClassifiesTheStageAsUnverifiedAndCancelsItsOperation()
    {
        var deadline = new ControlledDebugEnvironmentDoctorDeadline();
        var runner = new DebugEnvironmentDoctorStageRunner(deadline);
        var operationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var running = runner.RunAsync(
            "vbe.breakMode",
            TimeSpan.FromSeconds(60),
            async cancellationToken =>
            {
                using var registration = cancellationToken.Register(
                    () => operationCancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DebugEnvironmentProbeCheckResult.Pass("Unexpected completion.");
            },
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), await deadline.Started);
        deadline.Expire();
        var execution = await running.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            DebugEnvironmentDoctorStageTermination.Timeout,
            execution.Termination);
        Assert.Equal(
            DebugEnvironmentDiagnosticStatus.Unverified,
            execution.Check.Status);
        Assert.Contains("60", execution.Check.Message, StringComparison.Ordinal);
        Assert.True(execution.Check.DurationMilliseconds >= 0);
        await operationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TimeoutReturnsAfterBoundedGraceWhenTheOperationNeverUnwinds()
    {
        var deadlines = new SequencedDebugEnvironmentDoctorDeadlines();
        var runner = new DebugEnvironmentDoctorStageRunner(deadlines);
        var operationCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<DebugEnvironmentProbeCheckResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = runner.RunAsync(
            "excel.startup",
            TimeSpan.FromSeconds(30),
            cancellationToken =>
            {
                _ = cancellationToken.Register(
                    () => operationCancelled.TrySetResult());
                return never.Task;
            },
            CancellationToken.None);

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            await deadlines.Started[0].Task.WaitAsync(TimeSpan.FromSeconds(1)));
        try
        {
            deadlines.Expire(0);
            await operationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var secondStarted = await Task.WhenAny(
                deadlines.Started[1].Task,
                Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(deadlines.Started[1].Task, secondStarted);
            Assert.Equal(
                TimeSpan.FromSeconds(5),
                await deadlines.Started[1].Task);
            Assert.False(running.IsCompleted);

            deadlines.Expire(1);
            var execution = await running.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(
                DebugEnvironmentDoctorStageTermination.Timeout,
                execution.Termination);
            Assert.Equal(
                DebugEnvironmentDiagnosticStatus.Unverified,
                execution.Check.Status);
        }
        finally
        {
            never.TrySetCanceled();
        }
    }

    private sealed class ControlledDebugEnvironmentDoctorDeadline
        : IDebugEnvironmentDoctorDeadline
    {
        private readonly TaskCompletionSource<TimeSpan> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource expired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> Started => started.Task;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            started.TrySetResult(delay);
            return expired.Task.WaitAsync(cancellationToken);
        }

        public void Expire() => expired.TrySetResult();
    }

    private sealed class SequencedDebugEnvironmentDoctorDeadlines
        : IDebugEnvironmentDoctorDeadline
    {
        private readonly TaskCompletionSource[] expired =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];
        private int next;

        public TaskCompletionSource<TimeSpan>[] Started { get; } =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously)
        ];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref next) - 1;
            Started[index].TrySetResult(delay);
            return expired[index].Task.WaitAsync(cancellationToken);
        }

        public void Expire(int index) => expired[index].TrySetResult();
    }
}
