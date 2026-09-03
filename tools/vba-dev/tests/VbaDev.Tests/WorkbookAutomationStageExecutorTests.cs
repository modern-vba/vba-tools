using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
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
    public async Task FailedLaunchSettlementPermanentlyRejectsControllerReuse()
    {
        var priorCleanupFailure = new WorkbookAutomationCleanupException(
            "The first launch could not prove cleanup.");
        using var controller = new OwnedExcelTerminationController();
        using (var launch = controller.BeginLaunch(CancellationToken.None))
        {
            launch.CompleteWithCleanupFailure(priorCleanupFailure);
        }

        OwnedExcelTerminationController.OwnedExcelLaunchLease? unexpectedLaunch = null;
        var reuseError = Record.Exception(() =>
            unexpectedLaunch = controller.BeginLaunch(CancellationToken.None));
        unexpectedLaunch?.Dispose();

        Assert.IsType<InvalidOperationException>(reuseError);
        var cleanupError = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(
            () => controller.RequestCleanupAsync(TimeSpan.Zero));
        Assert.Same(priorCleanupFailure, cleanupError);
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
        var gateProbe = Task.Factory.StartNew(
            () => controller.HasAttachedProcess,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var gateWasAvailable = await Task.WhenAny(
            gateProbe,
            Task.Delay(TimeSpan.FromSeconds(1))) == gateProbe;
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

    [Fact]
    public async Task LiveEvidenceCaptureFailureWaitsForLaunchSettlementAndExactCleanupBeforeReportingReleasedFailure()
    {
        var captureFailure = new InvalidOperationException("live evidence capture failed");
        var owned = new FailingIsolationEvidenceOwnedExcelProcessControl(captureFailure);
        using var controller = new OwnedExcelTerminationController();
        using var launch = controller.BeginLaunch(CancellationToken.None);
        controller.Attach(owned);

        controller.CaptureAutomationStage(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport));
        var diagnostics = controller.DescribeIsolationEvidence();
        var cleanup = controller.RequestCleanupAsync(TimeSpan.Zero);
        var completedBeforeLaunchSettlement = await Task.WhenAny(
            cleanup,
            Task.Delay(TimeSpan.FromMilliseconds(50))) == cleanup;

        Assert.False(completedBeforeLaunchSettlement);
        Assert.Equal(0, owned.TerminationCalls);
        Assert.Equal(0, owned.DisposeCalls);
        Assert.Contains("live evidence capture failed", diagnostics, StringComparison.Ordinal);

        launch.Dispose();
        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => cleanup.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Same(captureFailure, error.InnerException);
        Assert.Same(error, controller.TerminationFailure);
        Assert.Equal(1, owned.TerminationCalls);
        Assert.Equal(1, owned.DisposeCalls);
        Assert.True(owned.HasExited);
    }

    [Fact]
    public async Task EvidenceCaptureAndReleaseFailuresAreAggregatedAsUnverifiedCleanup()
    {
        var captureFailure = new InvalidOperationException("live evidence capture failed");
        var terminationFailure = new InvalidOperationException("termination failed");
        var disposalFailure = new InvalidOperationException("release failed");
        var owned = new FailingIsolationEvidenceOwnedExcelProcessControl(
            captureFailure,
            terminationFailure,
            disposalFailure);
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        controller.CaptureAutomationStage(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport));
        var error = await Assert.ThrowsAsync<WorkbookAutomationCleanupException>(
            () => controller.RequestCleanupAsync(TimeSpan.Zero));

        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(captureFailure, aggregate.InnerExceptions);
        Assert.Contains(terminationFailure, aggregate.InnerExceptions);
        Assert.Contains(disposalFailure, aggregate.InnerExceptions);
        Assert.Same(error, controller.TerminationFailure);
        Assert.Equal(1, owned.TerminationCalls);
        Assert.Equal(1, owned.DisposeCalls);
        Assert.False(owned.HasExited);
    }

    [Fact]
    public async Task TimeoutCarriesExactIsolationEvidenceAndCapturesStageBeforeTermination()
    {
        const string evidence =
            "Automation Excel isolation evidence: PID=4242, HWND=0x1234, " +
            "privateDesktop='WinSta0\\vba-dev-private-4242', class='#32770', " +
            "title='Microsoft Excel', phase=VbeAutomation.";
        var operation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalEvents = new List<string>();
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            _ =>
            {
                terminalEvents.Add("terminate");
                operation.TrySetResult(true);
            },
            forcedTerminationObservationAllowance: TimeSpan.Zero,
            captureAutomationStage: _ => terminalEvents.Add("capture"),
            describeIsolationEvidence: () =>
            {
                terminalEvents.Add("describe");
                return evidence;
            });
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ModuleImport,
            "Feature.bas");

        var error = await Assert.ThrowsAsync<WorkbookAutomationTimeoutException>(() =>
            executor.ExecuteAsync(
                stage,
                TimeSpan.FromMilliseconds(20),
                TimeSpan.Zero,
                CancellationToken.None,
                () => operation.Task));

        Assert.Equal(evidence, error.IsolationDiagnostics);
        AssertIsolationEvidence(error.Message, evidence);
        Assert.Equal(["capture", "describe", "terminate"], terminalEvents.TakeLast(3));
    }

    [Fact]
    public async Task CancellationCarriesExactIsolationEvidence()
    {
        const string evidence =
            "Automation Excel isolation evidence: PID=5252, HWND=0x5678, " +
            "privateDesktop='WinSta0\\vba-dev-private-5252', class='ThunderDFrame', " +
            "title='Blocked UserForm', phase=TestExecution.";
        var operation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            _ => operation.TrySetResult(true),
            forcedTerminationObservationAllowance: TimeSpan.Zero,
            describeIsolationEvidence: () => evidence);
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.TestExecution,
            "BlockedTest");
        var execution = executor.ExecuteAsync(
            stage,
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            cancellation.Token,
            () => operation.Task);

        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<WorkbookAutomationCanceledException>(
            () => execution);

        Assert.Equal(evidence, error.IsolationDiagnostics);
        AssertIsolationEvidence(error.Message, evidence);
    }

    [Fact]
    public async Task ProcessLossCarriesExactIsolationEvidenceWithoutRequestingTermination()
    {
        const string evidence =
            "Automation Excel isolation evidence: PID=6262, HWND=0x9ABC, " +
            "privateDesktop='WinSta0\\vba-dev-private-6262', class='XLMAIN', " +
            "title='Book1', phase=WorkbookAutomation.";
        var operation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var processExit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminationCalls = 0;
        var executor = new WorkbookAutomationStageExecutor(
            () => false,
            _ => terminationCalls++,
            forcedTerminationObservationAllowance: TimeSpan.Zero,
            getOwnedProcessCompletion: () => processExit.Task,
            describeIsolationEvidence: () => evidence);
        var stage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.WorkbookOpen,
            "Book1.xlsm");
        var execution = executor.ExecuteAsync(
            stage,
            TimeSpan.FromSeconds(10),
            TimeSpan.Zero,
            CancellationToken.None,
            () => operation.Task);

        processExit.TrySetResult();
        var error = await Assert.ThrowsAsync<WorkbookAutomationProcessLostException>(
            () => execution);

        Assert.Equal(evidence, error.IsolationDiagnostics);
        AssertIsolationEvidence(error.Message, evidence);
        Assert.Equal(0, terminationCalls);
    }

    [Fact]
    public void TerminationControllerMapsEveryAutomationStageToDesktopLifecyclePhase()
    {
        var cases = new[]
        {
            (WorkbookAutomationStageKind.ExcelStartup, DesktopWindowLifecyclePhase.BootstrapBinding),
            (WorkbookAutomationStageKind.WorkbookOpen, DesktopWindowLifecyclePhase.WorkbookAutomation),
            (WorkbookAutomationStageKind.ReferenceAttempt, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.ReferenceIdentityInspection, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.ModuleRemoval, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.ModuleImport, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.Verification, DesktopWindowLifecyclePhase.WorkbookAutomation),
            (WorkbookAutomationStageKind.WorkbookSave, DesktopWindowLifecyclePhase.WorkbookAutomation),
            (WorkbookAutomationStageKind.ProcessCleanup, DesktopWindowLifecyclePhase.Shutdown),
            (WorkbookAutomationStageKind.OutputCommit, DesktopWindowLifecyclePhase.WorkbookAutomation),
            (WorkbookAutomationStageKind.TestExecution, DesktopWindowLifecyclePhase.TestExecution),
            (WorkbookAutomationStageKind.ModuleExport, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.ModuleInspection, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.WorkbookCreate, DesktopWindowLifecyclePhase.WorkbookAutomation),
            (WorkbookAutomationStageKind.UserFormCreate, DesktopWindowLifecyclePhase.VbeAutomation),
            (WorkbookAutomationStageKind.HostEventInspection, DesktopWindowLifecyclePhase.VbeAutomation)
        };

        foreach (var (stageKind, expectedPhase) in cases)
        {
            var owned = new FakeIsolatedOwnedExcelProcessControl("evidence");
            using var controller = new OwnedExcelTerminationController();
            controller.Attach(owned);

            controller.CaptureAutomationStage(new WorkbookAutomationStage(stageKind));

            Assert.Equal([expectedPhase], owned.CapturedPhases);
            Assert.Equal("evidence", controller.DescribeIsolationEvidence());
        }
    }

    [Fact]
    public void TerminationControllerIgnoresLateIsolationEvidenceAfterVerifiedProcessExit()
    {
        var owned = new ReleasedIsolatedOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        controller.Attach(owned);

        controller.CaptureAutomationStage(
            new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup));
        var evidence = controller.DescribeIsolationEvidence();

        Assert.Equal(1, owned.CaptureCalls);
        Assert.Equal(1, owned.DescribeCalls);
        Assert.Null(evidence);
    }

    private static void AssertIsolationEvidence(string message, string evidence)
    {
        Assert.Contains(evidence, message, StringComparison.Ordinal);
        Assert.Contains("PID=", message, StringComparison.Ordinal);
        Assert.Contains("HWND=", message, StringComparison.Ordinal);
        Assert.Contains("privateDesktop=", message, StringComparison.Ordinal);
        Assert.Contains("class=", message, StringComparison.Ordinal);
        Assert.Contains("title=", message, StringComparison.Ordinal);
        Assert.Contains("phase=", message, StringComparison.Ordinal);
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

    private sealed class FakeIsolatedOwnedExcelProcessControl(string evidence)
        : IOwnedExcelProcessControl,
          IExcelAutomationDesktopProcessControl
    {
        public List<DesktopWindowLifecyclePhase> CapturedPhases { get; } = [];

        public bool HasExited => false;

        public Task Completion { get; } = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public void Capture(DesktopWindowLifecyclePhase phase)
            => CapturedPhases.Add(phase);

        public string DescribeCurrentEvidence() => evidence;

        public Task TerminateAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingIsolationEvidenceOwnedExcelProcessControl(
        Exception captureFailure,
        Exception? terminationFailure = null,
        Exception? disposalFailure = null)
        : IOwnedExcelProcessControl,
          IExcelAutomationDesktopProcessControl
    {
        private readonly TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int TerminationCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool HasExited { get; private set; }

        public Task Completion => completion.Task;

        public void Capture(DesktopWindowLifecyclePhase phase)
            => throw captureFailure;

        public string DescribeCurrentEvidence()
            => throw new InvalidOperationException("live evidence description failed");

        public Task TerminateAsync()
        {
            TerminationCalls++;
            if (terminationFailure is not null)
            {
                throw terminationFailure;
            }

            HasExited = true;
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return disposalFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposalFailure);
        }
    }

    private sealed class ReleasedIsolatedOwnedExcelProcessControl
        : IOwnedExcelProcessControl,
          IExcelAutomationDesktopProcessControl
    {
        public int CaptureCalls { get; private set; }

        public int DescribeCalls { get; private set; }

        public bool HasExited => true;

        public Task Completion => Task.CompletedTask;

        public void Capture(DesktopWindowLifecyclePhase phase)
        {
            CaptureCalls++;
            throw new ObjectDisposedException("private desktop observer");
        }

        public string DescribeCurrentEvidence()
        {
            DescribeCalls++;
            throw new InvalidOperationException(
                "The private desktop observer was already released.");
        }

        public Task TerminateAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
