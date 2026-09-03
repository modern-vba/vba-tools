using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelAutomationCleanupTimingTests
{
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
        await process.TerminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Assert.False(observation.IsCompleted);
        }
        finally
        {
            process.CompleteTermination();
        }

        await observation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, process.DisposeCalls);
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

        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
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
