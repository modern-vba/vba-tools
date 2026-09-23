using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelAutomationCleanupTimingTests
{
    private static readonly TimeSpan FixtureWatchdog = TimeSpan.FromSeconds(5);

    [Fact]
    public void ForcedCleanupObservationAllowanceCoversBothBoundedInternalPhases()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(11),
            PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance);
    }

    [Fact]
    public async Task ControllerObservationWaitsBeyondTheLegacyOneSecondCutoff()
    {
        var process = new DelayedOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        Assert.True(controller.Attach(process));

        controller.RequestForcedTermination(TimeSpan.Zero);
        var observation = controller.ObserveCleanupWithinAsync(
            PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance);
        var cleanup = controller.RequestCleanupAsync(TimeSpan.Zero);
        await AssertObservationRemainsPendingAsync(process, observation, cleanup);

        Assert.True(process.HasExited);
        Assert.Equal(1, process.DisposeCalls);
    }

    [Fact]
    public async Task ObservationFixtureDrainsActualCleanupWhenReadinessAndObservationFail()
    {
        var process = new DelayedOwnedExcelProcessControl();
        var readinessError = new TimeoutException("The fixture did not become ready.");
        var observationError = new TimeoutException("Cleanup observation expired.");
        process.TerminationStarted.SetException(readinessError);
        using var controller = new OwnedExcelTerminationController();
        Assert.True(controller.Attach(process));
        var cleanup = controller.RequestCleanupAsync(TimeSpan.Zero);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCleanup = Task.WhenAll(cleanup, releaseCleanup.Task);
        var verification = AssertObservationRemainsPendingAsync(
            process, Task.FromException(observationError), heldCleanup);
        Exception? pendingFailure;
        try
        {
            // Readiness and observation are already faulted; only the cleanup barrier can keep this pending.
            pendingFailure = Record.Exception(() => Assert.False(verification.IsCompleted));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            process.CompleteTermination();
        }

        var verificationFailure = await Record.ExceptionAsync(
            () => verification.WaitAsync(FixtureWatchdog));
        var cleanupFailure = await Record.ExceptionAsync(
            () => heldCleanup.WaitAsync(FixtureWatchdog));
        if (pendingFailure is not null && cleanupFailure is not null)
            throw new AggregateException("The cleanup drain regression and its teardown failed.",
                pendingFailure, cleanupFailure);
        if (pendingFailure is not null) ExceptionDispatchInfo.Capture(pendingFailure).Throw();
        Assert.Null(cleanupFailure);
        var error = Assert.IsType<AggregateException>(verificationFailure);
        Assert.Collection(error.InnerExceptions,
            failure => Assert.Same(readinessError, failure),
            failure => Assert.Same(observationError, failure));
        Assert.True(cleanup.IsCompletedSuccessfully);
        Assert.True(process.HasExited);
        Assert.Equal(1, process.DisposeCalls);
    }

    private static async Task AssertObservationRemainsPendingAsync(
        DelayedOwnedExcelProcessControl process,
        Task observation,
        Task cleanup)
    {
        Exception? failure = null;
        try
        {
            // One second is the obsolete observation cutoff, not a fixture-startup SLA.
            await process.TerminationStarted.Task.WaitAsync(FixtureWatchdog);
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.False(observation.IsCompleted);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            process.CompleteTermination();
            try
            {
                await Task.WhenAll(observation, cleanup).WaitAsync(FixtureWatchdog);
            }
            catch (Exception drainError)
            {
                failure = failure is null
                    ? drainError
                    : new AggregateException("Cleanup observation and fixture drain failed.", failure, drainError);
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task ObservationCompletionFailureAfterExactReleaseIsReleasedCleanup()
    {
        var process = new FakeDebugOwnedProcess(
            330,
            new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        var observationError = new InvalidOperationException(
            "Desktop observation completion failed.");
        var isolation = new CompletionFailingDesktopIsolation(observationError);
        var control = new PrivateDesktopOwnedExcelProcessControl(owner, isolation);

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => control.DisposeAsync().AsTask());

        Assert.False(WorkbookAutomationTerminalFacts.Analyze(error).HasUnprovedLifecycle);
        Assert.Same(observationError, error.InnerException);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.True(job.Disposed);
        Assert.True(isolation.Disposed);
    }

    private sealed class DelayedOwnedExcelProcessControl : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource terminationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCalls { get; private set; }

        public bool HasExited { get; private set; }

        public Task Completion => completion.Task;

        public async Task TerminateAsync()
        {
            TerminationStarted.TrySetResult();
            await terminationRelease.Task.ConfigureAwait(false);
            HasExited = true;
            completion.TrySetResult();
        }

        public void CompleteTermination() => terminationRelease.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompletionFailingDesktopIsolation(Exception completionError)
        : IExcelAutomationDesktopIsolation
    {
        public string QualifiedDesktopName => "WinSta0\\vba-dev-test";

        public nint DesktopHandle => (nint)1;

        public bool Disposed { get; private set; }

        public Task StartObservingBeforeResumeAsync(
            int exactProcessId,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
            Task exactProcessExit,
            CancellationToken cancellationToken)
            => Task.FromException<DesktopWindowExposureEvidence>(completionError);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
