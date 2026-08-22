using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class WorkbookAutomationStageExecutorTests
{
    [Fact]
    public async Task TimeoutReturnsAfterGraceEvenWhenTheOperationNeverUnwinds()
    {
        var neverCompletes = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? requestedGrace = null;
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            grace => requestedGrace = grace,
            forcedTerminationObservationAllowance: TimeSpan.Zero);
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.WorkbookOpen,
            "Book1.xlsm");

        var execution = executor.ExecuteAsync(
            stage,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None,
            () => neverCompletes.Task);
        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(TimeSpan.FromMilliseconds(20), requestedGrace);
        Assert.Equal(stage, error.Stage);
        Assert.True(executor.HasAbandonedOperation);
    }

    [Fact]
    public async Task AnyPositiveInt32SecondOverrideCanBeScheduled()
    {
        var executor = new WorkbookAutomationStageExecutor(() => false, _ => { });

        var result = await executor.ExecuteAsync(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen),
            TimeSpan.FromSeconds(int.MaxValue),
            TimeSpan.Zero,
            CancellationToken.None,
            static () => Task.FromResult(42));

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task TimeoutRequestsForcedCleanupAfterTheConfiguredGraceAndIdentifiesTheStage()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? requestedGrace = null;
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            grace =>
            {
                requestedGrace = grace;
                releaseOperation.TrySetResult(true);
            });
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ModuleImport,
            "Feature.bas");

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            executor.ExecuteAsync(
                stage,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(5),
                CancellationToken.None,
                () =>
                {
                    operationStarted.TrySetResult();
                    return releaseOperation.Task;
                }));

        await operationStarted.Task;
        Assert.Equal(TimeSpan.FromSeconds(5), requestedGrace);
        Assert.Equal(stage, error.Stage);
    }

    [Fact]
    public async Task CancellationRequestsTheSameGraceAndReportsTheActiveStage()
    {
        var operationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        TimeSpan? requestedGrace = null;
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            grace =>
            {
                requestedGrace = grace;
                releaseOperation.TrySetResult(true);
            });
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.WorkbookSave,
            "Book1.xlsm");

        var execution = executor.ExecuteAsync(
            stage,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5),
            cancellation.Token,
            () =>
            {
                operationStarted.TrySetResult();
                return releaseOperation.Task;
            });
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(() => execution);

        Assert.Equal(TimeSpan.FromSeconds(5), requestedGrace);
        Assert.Equal(stage, error.Stage);
    }

    [Fact]
    public async Task UnexpectedOwnedProcessExitIsStageSpecific()
    {
        var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen, "Book1.xlsm");
        var executor = new WorkbookAutomationStageExecutor(() => true, _ => { });

        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(() =>
            executor.ExecuteAsync(
                stage,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                static () => Task.FromResult(true)));

        Assert.Equal(stage, error.Stage);
    }

    [Fact]
    public async Task TerminationControllerWaitsForGraceAndTerminatesOnlyItsAttachedOwner()
    {
        var owned = new FakeOwnedExcelProcessControl();
        var unrelated = new FakeOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        controller.RequestForcedTermination(TimeSpan.FromMilliseconds(30));

        Assert.Equal(0, owned.TerminationCalls);
        await owned.Terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, owned.TerminationCalls);
        Assert.Equal(0, unrelated.TerminationCalls);
    }

    [Fact]
    public async Task TerminationRequestedBeforeOwnershipIsAppliedOnlyAfterExactOwnerAttachment()
    {
        var owned = new FakeOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        using var launch = controller.BeginLaunch(CancellationToken.None);

        controller.RequestForcedTermination(TimeSpan.Zero);
        Assert.Equal(0, owned.TerminationCalls);

        controller.Attach(owned);
        launch.Dispose();
        Assert.True(await controller.WaitForExitOrTerminationAttemptAsync());
        Assert.Equal(1, owned.TerminationCalls);
    }

    [Fact]
    public async Task CleanupSealWaitsForAnInFlightLaunchAndDisposesItsLateAttachedOwner()
    {
        var owned = new FakeOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        using var launch = controller.BeginLaunch(CancellationToken.None);

        controller.RequestForcedTermination(TimeSpan.Zero);
        var launchSettlement = controller.WaitForLaunchSettlementAsync();

        Assert.False(launchSettlement.IsCompleted);
        controller.Attach(owned);
        launch.Dispose();
        await launchSettlement.WaitAsync(TimeSpan.FromSeconds(1));
        await controller.ObserveTerminationAsync().WaitAsync(TimeSpan.FromSeconds(1));
        await controller.DisposeAttachedProcessAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, owned.TerminationCalls);
        Assert.Equal(1, owned.DisposeCalls);
        Assert.True(owned.HasExited);
    }

    [Fact]
    public void CleanupSealRejectsAnyLaterLaunchBeforeItCanCreateAProcess()
    {
        using var controller = new OwnedExcelTerminationController();

        controller.RequestForcedTermination(TimeSpan.Zero);

        Assert.Throws<OperationCanceledException>(() =>
            controller.BeginLaunch(CancellationToken.None));
    }

    [Fact]
    public async Task ImmediateTerminationNeverRunsProcessControlWhileHoldingTheControllerGate()
    {
        using var terminationEntered = new ManualResetEventSlim();
        using var releaseTermination = new ManualResetEventSlim();
        var owned = new FakeOwnedExcelProcessControl(
            beforeTerminationReturns: () =>
            {
                terminationEntered.Set();
                releaseTermination.Wait(TimeSpan.FromSeconds(5));
            });
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        var request = Task.Run(() =>
            controller.RequestForcedTermination(TimeSpan.Zero));
        Assert.True(terminationEntered.Wait(TimeSpan.FromSeconds(1)));
        var gateProbe = Task.Run(() => controller.HasAttachedProcess);
        var gateWasAvailable = await Task.WhenAny(
            gateProbe,
            Task.Delay(TimeSpan.FromMilliseconds(200))) == gateProbe;
        releaseTermination.Set();
        await request.WaitAsync(TimeSpan.FromSeconds(1));
        await controller.ObserveTerminationAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(gateWasAvailable);
    }

    [Fact]
    public async Task FailedTerminationAttemptCompletesWithoutWaitingForeverForProcessExit()
    {
        var owned = new FakeOwnedExcelProcessControl(terminationFailure: new InvalidOperationException("denied"));
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        controller.RequestForcedTermination(TimeSpan.Zero);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => controller
            .WaitForExitOrTerminationAttemptAsync()
            .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("denied", error.ToString(), StringComparison.Ordinal);
        Assert.NotNull(controller.TerminationFailure);
        Assert.Equal(1, owned.DisposeCalls);
    }

    [Fact]
    public async Task FaultedExitObservationCanNeverServeAsCleanupProof()
    {
        var owned = new FakeOwnedExcelProcessControl(
            completionFailure: new InvalidOperationException("exit observation failed"));
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            controller.RequestCleanupAsync(TimeSpan.Zero));

        Assert.Contains("exit", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Same(error, controller.TerminationFailure);
        Assert.Equal(1, owned.TerminationCalls);
        Assert.Equal(1, owned.DisposeCalls);
    }

    [Fact]
    public async Task CooperativeExitIsObservedAndDisposedWithoutForcedTermination()
    {
        var owned = new FakeOwnedExcelProcessControl();
        owned.CompleteCooperatively();
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        await controller.RequestCleanupAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, owned.TerminationCalls);
        Assert.Equal(1, owned.DisposeCalls);
    }

    private sealed class FakeOwnedExcelProcessControl(
        Exception? terminationFailure = null,
        Action? beforeTerminationReturns = null,
        Exception? completionFailure = null) : IOwnedExcelProcessControl
    {
        public int TerminationCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool HasExited { get; private set; }

        public TaskCompletionSource Terminated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => Terminated.Task;

        public Task TerminateAsync()
        {
            TerminationCalls++;
            beforeTerminationReturns?.Invoke();
            if (terminationFailure is not null)
            {
                throw terminationFailure;
            }

            HasExited = true;
            CompleteExitObservation();
            return Task.CompletedTask;
        }

        public void CompleteCooperatively()
        {
            HasExited = true;
            CompleteExitObservation();
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        private void CompleteExitObservation()
        {
            if (completionFailure is null)
            {
                Terminated.TrySetResult();
            }
            else
            {
                Terminated.TrySetException(completionFailure);
            }
        }
    }
}
