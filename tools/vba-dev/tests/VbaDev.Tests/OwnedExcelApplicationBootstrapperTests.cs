using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class OwnedExcelApplicationBootstrapperTests
{
    [Fact]
    public async Task PrivateDesktopObservationIsReadyBeforeResumeAndLivesUntilControllerCleanup()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            409,
            new DateTime(2026, 9, 3, 9, 0, 0, DateTimeKind.Local),
            events: events);
        var job = new FakeDebugProcessJob(process, events);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var isolationFactory = new FakeExcelAutomationDesktopIsolationFactory(
            isolation,
            events);
        var launcher = new FakeOwnedProcessLauncher(process, events: events);
        launcher.PrimaryThread.BeforeResume = () =>
        {
            Assert.True(isolation.ObserverReady);
            events.Add("resume");
        };
        var binder = new CallbackNativeObjectModelBinder(
            (processId, desktopHandle, hasExited) =>
            {
                Assert.Equal(process.Id, processId);
                Assert.Equal(isolation.DesktopHandle, desktopHandle);
                Assert.True(isolation.ObserverReady);
                Assert.False(hasExited());
                events.Add("bind");
                return new object();
            });
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            processApi,
            binder,
            isolationFactory);

        _ = bootstrapper.Start(
            terminationController,
            CancellationToken.None);

        Assert.Equal(isolation.QualifiedDesktopName, launcher.RequestedDesktopName);
        Assert.Equal(1, isolationFactory.CreateCalls);
        Assert.Equal(1, isolation.StartObservationCalls);
        Assert.Equal(0, isolation.CompleteObservationCalls);
        Assert.Equal(0, isolation.DisposeCalls);
        AssertOrdered(
            events,
            "desktop-create",
            $"launch:{isolation.QualifiedDesktopName}",
            $"observe:{process.Id}",
            $"capture:{DesktopWindowLifecyclePhase.BootstrapBinding}",
            "resume",
            "bind");

        await terminationController.RequestCleanupAsync(TimeSpan.Zero);

        Assert.Equal(1, isolation.CompleteObservationCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public void BindingFailureCompletesObservationAndDisposesDesktopAfterOwnedProcessExit()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            410,
            new DateTime(2026, 9, 3, 9, 5, 0, DateTimeKind.Local),
            events: events);
        var job = new FakeDebugProcessJob(process, events);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process, events: events),
            processApi,
            new CallbackNativeObjectModelBinder(
                static (_, _, _) => throw new InvalidOperationException("binding failed")),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        var error = Assert.Throws<OwnedExcelSessionStartException>(
            () => bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.True(error.CleanupVerified);
        Assert.True(process.HasExited);
        Assert.Equal(1, isolation.CompleteObservationCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public async Task OwnedProcessLauncherPassesPrivateDesktopToAtomicSuspendedLaunch()
    {
        var process = new FakeDebugOwnedProcess(
            411,
            new DateTime(2026, 9, 3, 9, 10, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        var primaryThread = new FakeSuspendedPrimaryThread();
        const string expectedDesktop = "WinSta0\\vba-dev-automation-unit-test";
        string? requestedDesktop = null;
        var launcher = new WindowsExcelOwnedProcessLauncher(
            static () => "excel.exe",
            static () => "bootstrap.xlsx",
            static _ => { },
            (requestedJob, applicationPath, arguments, desktopName) =>
            {
                Assert.Same(job, requestedJob);
                Assert.Equal("excel.exe", applicationPath);
                Assert.Equal(["/x", "bootstrap.xlsx"], arguments);
                requestedDesktop = desktopName;
                job.Assign(process);
                return new DebugSuspendedProcessLaunch(process, primaryThread);
            });

        var launch = launcher.Start(
            processApi,
            expectedDesktop,
            CancellationToken.None);

        Assert.Equal(expectedDesktop, requestedDesktop);
        Assert.Same(primaryThread, launch.PrimaryThread);
        launch.PrimaryThread.Dispose();
        await launch.ProcessOwner.DisposeAsync();
    }

    [Fact]
    public void AtomicLauncherCancellationAfterAdoptionRemainsCancellation()
    {
        var process = new FakeDebugOwnedProcess(
            416,
            new DateTime(2026, 9, 3, 9, 11, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        var primaryThread = new FakeSuspendedPrimaryThread();
        using var cancellation = new CancellationTokenSource();
        var launcher = new WindowsExcelOwnedProcessLauncher(
            static () => "excel.exe",
            static () => "bootstrap.xlsx",
            static _ => { },
            (requestedJob, _, _, _) =>
            {
                requestedJob.Assign(process);
                cancellation.Cancel();
                return new DebugSuspendedProcessLaunch(process, primaryThread);
            });

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            launcher.Start(
                processApi,
                "WinSta0\\vba-dev-automation-unit-test",
                cancellation.Token));

        Assert.True(error.CleanupVerified);
        Assert.IsType<OperationCanceledException>(error.StartException);
        Assert.True(process.HasExited);
        Assert.Equal(1, primaryThread.DisposeCalls);
    }

    [Fact]
    public void BootstrapperCancellationAfterOwnedLaunchRemainsCancellation()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            417,
            new DateTime(2026, 9, 3, 9, 12, 0, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var launcher = new FakeOwnedProcessLauncher(
            process,
            beforeReturn: cancellation.Cancel,
            events: events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            bootstrapper.Start(
                terminationController,
                cancellation.Token));

        Assert.True(error.CleanupVerified);
        Assert.IsType<OperationCanceledException>(error.StartException);
        Assert.True(process.HasExited);
        Assert.Equal(1, isolation.DisposeCalls);
        Assert.Equal(0, launcher.PrimaryThread.ResumeCalls);
    }

    [Fact]
    public async Task PreCanceledStartupIsClassifiedWithoutLaunchingAndHasSettledCleanup()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            420,
            new DateTime(2026, 9, 3, 9, 12, 30, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var isolationFactory = new FakeExcelAutomationDesktopIsolationFactory(
            isolation,
            events);
        var launcher = new FakeOwnedProcessLauncher(process, events: events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            isolationFactory);

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            bootstrapper.Start(
                terminationController,
                cancellation.Token));

        Assert.True(error.CleanupVerified);
        Assert.IsType<OperationCanceledException>(error.StartException);
        await terminationController.WaitForLaunchSettlementAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        await terminationController.RequestCleanupAsync(TimeSpan.Zero)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, launcher.StartCalls);
        Assert.Equal(0, isolationFactory.CreateCalls);
        Assert.Equal(0, process.KillCalls);
        Assert.False(process.Disposed);
        Assert.Equal(0, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(0, launcher.PrimaryThread.DisposeCalls);
        Assert.Empty(events);
    }

    [Fact]
    public async Task CancellationDuringNativeBindingCleansExactOwnerAndSettlesLaunch()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            418,
            new DateTime(2026, 9, 3, 9, 13, 0, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var launcher = new FakeOwnedProcessLauncher(process, events: events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(
                (_, _) =>
                {
                    cancellation.Cancel();
                    return new object();
                }),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        OwnedExcelSessionStartCanceledException? error = null;
        try
        {
            error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
                bootstrapper.Start(
                    terminationController,
                    cancellation.Token));
        }
        finally
        {
            await terminationController.RequestCleanupAsync(TimeSpan.Zero);
        }

        Assert.NotNull(error);
        Assert.True(error.CleanupVerified);
        Assert.IsType<OperationCanceledException>(error.StartException);
        await terminationController.WaitForLaunchSettlementAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.True(terminationController.HasAttachedProcessExited);
        Assert.Equal(1, isolation.CompleteObservationCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        Assert.Equal(1, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public async Task CancellationDuringPostBindCaptureDoesNotReturnAStartedApplication()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            419,
            new DateTime(2026, 9, 3, 9, 14, 0, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events)
        {
            CaptureCallback = (_, captureCalls) =>
            {
                if (captureCalls == 2)
                {
                    cancellation.Cancel();
                }
            }
        };
        var launcher = new FakeOwnedProcessLauncher(process, events: events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            bootstrapper.Start(
                terminationController,
                cancellation.Token));

        Assert.True(error.CleanupVerified);
        Assert.IsType<OperationCanceledException>(error.StartException);
        await terminationController.WaitForLaunchSettlementAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, isolation.CompleteObservationCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        Assert.Equal(1, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public async Task CancellationThatKillsNativeBindingPreservesItsExitFailureAsTheCanceledCause()
    {
        var nativeBindingExit = new InvalidOperationException(
            "The explicitly launched Excel process exited before COM automation was available.");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            421,
            new DateTime(2026, 9, 3, 9, 14, 30, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        using var terminationController = new OwnedExcelTerminationController();
        using var cancellationRegistration = ExcelComWorkbookSession.RegisterCallerCancellation(
            terminationController,
            cancellation.Token);
        using var bindingStarted = new ManualResetEventSlim();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var launcher = new FakeOwnedProcessLauncher(process, events: events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(
                (_, hasProcessExited) =>
                {
                    bindingStarted.Set();
                    Assert.True(SpinWait.SpinUntil(
                        hasProcessExited,
                        TimeSpan.FromSeconds(1)));
                    throw nativeBindingExit;
                }),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            cancellation.Token));
        Assert.True(bindingStarted.Wait(TimeSpan.FromSeconds(1)));

        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OwnedExcelSessionStartCanceledException>(
            () => startup.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.True(error.CleanupVerified);
        var cancellationCause = Assert.IsType<OperationCanceledException>(error.StartException);
        Assert.Same(nativeBindingExit, cancellationCause.InnerException);
        await terminationController.WaitForLaunchSettlementAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        await terminationController.ObserveTerminationAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, isolation.CompleteObservationCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        Assert.Equal(1, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public void CancellationDoesNotReplaceAnAlreadyClassifiedCleanupFailure()
    {
        var classifiedFailure = new WorkbookAutomationCleanupException(
            "The private desktop observer could not prove exact cleanup.");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            422,
            new DateTime(2026, 9, 3, 9, 14, 45, DateTimeKind.Local),
            events: events);
        using var cancellation = new CancellationTokenSource();
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process, events: events),
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(
                (_, _) =>
                {
                    cancellation.Cancel();
                    throw classifiedFailure;
                }),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        var error = Assert.Throws<WorkbookAutomationCleanupException>(() =>
            bootstrapper.Start(
                terminationController,
                cancellation.Token));

        Assert.Same(classifiedFailure, error);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, isolation.DisposeCalls);
    }

    [Fact]
    public async Task CallerDesktopExposureFailsCleanupWithExactWindowEvidenceThenDisposesDesktop()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            412,
            new DateTime(2026, 9, 3, 9, 15, 0, DateTimeKind.Local),
            events: events);
        var job = new FakeDebugProcessJob(process, events);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events)
        {
            CompletionEvidence = new DesktopWindowExposureEvidence(
                process.Id,
                [
                    new DesktopWindowObservation(
                        Sequence: 1,
                        ProcessId: process.Id,
                        WindowHandle: (nint)0x84,
                        Desktop: "WinSta0\\Default",
                        Location: DesktopWindowLocation.CallerInteractive,
                        WindowClass: "XLMAIN",
                        Title: "Escaped automation workbook",
                        IsVisible: true,
                        LifecyclePhase: DesktopWindowLifecyclePhase.BootstrapBinding,
                        Cause: DesktopWindowObservationCause.WinEventShow)
                ])
        };
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process, events: events),
            processApi,
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));
        _ = bootstrapper.Start(terminationController, CancellationToken.None);

        var error = await Assert.ThrowsAsync<WorkbookAutomationReleasedProcessCleanupException>(
            () => terminationController.RequestCleanupAsync(TimeSpan.Zero));

        var evidence = error.ToString();
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        Assert.Contains(process.Id.ToString(), evidence, StringComparison.Ordinal);
        Assert.Contains("0x84", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinSta0\\Default", evidence, StringComparison.Ordinal);
        Assert.Contains("XLMAIN", evidence, StringComparison.Ordinal);
        Assert.Contains("Escaped automation workbook", evidence, StringComparison.Ordinal);
        Assert.Contains("BootstrapBinding", evidence, StringComparison.Ordinal);
        Assert.Equal(1, isolation.DisposeCalls);
        AssertOrdered(events, "process-exit", "observer-complete", "desktop-dispose");
    }

    [Fact]
    public async Task ExactProcessOwnershipIsAttachedBeforeNativeComBindingStarts()
    {
        var process = new FakeDebugOwnedProcess(
            401,
            new DateTime(2026, 8, 22, 9, 0, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        using var terminationController = new OwnedExcelTerminationController();
        var application = new object();
        var launcher = new FakeOwnedProcessLauncher(process);
        launcher.PrimaryThread.BeforeResume = () =>
            Assert.True(terminationController.HasAttachedProcess);
        var binder = new CallbackNativeObjectModelBinder(
            (processId, hasExited) =>
            {
                Assert.Equal(process.Id, processId);
                Assert.True(terminationController.HasAttachedProcess);
                Assert.Same(process, job.AssignedProcess);
                Assert.False(hasExited());
                return application;
            });
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            processApi,
            binder,
            CreateNoExposureIsolationFactory());

        var started = bootstrapper.Start(
            terminationController,
            CancellationToken.None);

        Assert.Same(application, started.Application);
        Assert.Equal(process.Id, started.ProcessOwner.ProcessId);
        Assert.Equal("bootstrap.xlsx", started.BootstrapWorkbookPath);
        Assert.Equal(1, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
        await terminationController.RequestCleanupAsync(TimeSpan.Zero);
    }

    [Fact]
    public void BindingFailureTerminatesOnlyTheExactlyOwnedLaunchedProcess()
    {
        var process = new FakeDebugOwnedProcess(
            402,
            new DateTime(2026, 8, 22, 9, 5, 0, DateTimeKind.Local));
        var unrelatedProcess = new FakeDebugOwnedProcess(
            403,
            new DateTime(2026, 8, 22, 9, 5, 1, DateTimeKind.Local));
        var processApi = new FakeDebugExcelProcessApi(process.Id, process);
        using var terminationController = new OwnedExcelTerminationController();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process),
            processApi,
            new CallbackNativeObjectModelBinder(
                static (_, _) => throw new InvalidOperationException("binding failed")),
            CreateNoExposureIsolationFactory());

        var error = Assert.Throws<OwnedExcelSessionStartException>(
            () => bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.True(error.CleanupVerified);
        Assert.True(process.HasExited);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(0, unrelatedProcess.KillCalls);
        Assert.True(terminationController.HasAttachedProcessExited);
    }

    [Fact]
    public async Task CleanupCanTerminateAnAttachedProcessWhileNativeComBindingIsStillWaiting()
    {
        var process = new FakeDebugOwnedProcess(
            405,
            new DateTime(2026, 8, 22, 9, 7, 0, DateTimeKind.Local));
        var processApi = new FakeDebugExcelProcessApi(process.Id, process);
        using var terminationController = new OwnedExcelTerminationController();
        using var bindingStarted = new ManualResetEventSlim();
        using var releaseBinding = new ManualResetEventSlim();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process),
            processApi,
            new CallbackNativeObjectModelBinder(
                (_, _) =>
                {
                    bindingStarted.Set();
                    releaseBinding.Wait(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("binding stopped");
                }),
            CreateNoExposureIsolationFactory());
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));

        Assert.True(bindingStarted.Wait(TimeSpan.FromSeconds(1)));
        var cleanup = terminationController.RequestCleanupAsync(TimeSpan.Zero);
        var completed = await Task.WhenAny(
            cleanup,
            Task.Delay(TimeSpan.FromMilliseconds(250)));
        releaseBinding.Set();
        _ = await Assert.ThrowsAsync<OwnedExcelSessionStartException>(() => startup);

        Assert.Same(cleanup, completed);
        await cleanup;
        Assert.Equal(1, process.KillCalls);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task CleanupDuringNativeComBindingMakesExitProbeStableAfterProcessDisposal()
    {
        var disposedProcessError = new InvalidOperationException(
            "No process is associated with this object.");
        var process = new FakeDebugOwnedProcess(
            408,
            new DateTime(2026, 8, 22, 9, 7, 30, DateTimeKind.Local),
            hasExitedAfterDisposeError: disposedProcessError);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process);
        using var terminationController = new OwnedExcelTerminationController();
        using var bindingStarted = new ManualResetEventSlim();
        using var releaseExitProbe = new ManualResetEventSlim();
        var bindingStopped = new InvalidOperationException("binding stopped after cleanup");
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process),
            processApi,
            new CallbackNativeObjectModelBinder(
                (_, hasProcessExited) =>
                {
                    bindingStarted.Set();
                    Assert.True(releaseExitProbe.Wait(TimeSpan.FromSeconds(5)));
                    Assert.True(hasProcessExited());
                    throw bindingStopped;
                }),
            CreateNoExposureIsolationFactory());
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));

        Assert.True(bindingStarted.Wait(TimeSpan.FromSeconds(1)));
        await terminationController.RequestCleanupAsync(TimeSpan.Zero)
            .WaitAsync(TimeSpan.FromSeconds(1));
        releaseExitProbe.Set();
        var error = await Assert.ThrowsAsync<OwnedExcelSessionStartException>(() => startup);

        Assert.Same(bindingStopped, error.StartException);
        Assert.NotSame(disposedProcessError, error.StartException);
        Assert.True(error.CleanupVerified);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CleanupSealBeforeAttachmentPreventsResumeAndNativeComBinding()
    {
        var process = new FakeDebugOwnedProcess(
            407,
            new DateTime(2026, 8, 22, 9, 8, 0, DateTimeKind.Local));
        var processApi = new FakeDebugExcelProcessApi(process.Id, process);
        using var terminationController = new OwnedExcelTerminationController();
        using var processOwned = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        var launcher = new FakeOwnedProcessLauncher(
            process,
            () =>
            {
                processOwned.Set();
                releaseLaunch.Wait(TimeSpan.FromSeconds(5));
            });
        var bindingCalls = 0;
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            processApi,
            new CallbackNativeObjectModelBinder(
                (_, _) =>
                {
                    bindingCalls++;
                    return new object();
                }),
            CreateNoExposureIsolationFactory());
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));

        Assert.True(processOwned.Wait(TimeSpan.FromSeconds(1)));
        var cleanup = terminationController.RequestCleanupAsync(TimeSpan.Zero);
        releaseLaunch.Set();
        var error = await Assert.ThrowsAsync<OwnedExcelSessionStartCanceledException>(
            () => startup);
        await cleanup;

        Assert.True(error.CleanupVerified);
        Assert.Equal(0, launcher.PrimaryThread.ResumeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
        Assert.Equal(0, bindingCalls);
        Assert.Equal(1, process.KillCalls);
    }

    [Fact]
    public void OwnershipFailureWithFailedExactKillReportsCleanupAsUnverified()
    {
        var process = new FakeDebugOwnedProcess(
            404,
            new DateTime(2026, 8, 22, 9, 10, 0, DateTimeKind.Local),
            killAction: static () => throw new InvalidOperationException("kill failed"));
        var assignmentError = new InvalidOperationException("assignment failed");
        var processApi = new FakeDebugExcelProcessApi(
            process.Id,
            process,
            new FakeDebugProcessJob(process, assignmentError: assignmentError));
        using var terminationController = new OwnedExcelTerminationController();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(process),
            processApi,
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            CreateNoExposureIsolationFactory());

        var error = Assert.Throws<OwnedExcelSessionStartException>(
            () => bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.False(error.CleanupVerified);
        Assert.Same(assignmentError, error.StartException);
        Assert.NotNull(error.CleanupException);
        Assert.Contains("kill failed", error.CleanupException.ToString());
    }

    [Fact]
    public void AtomicLaunchCleanupFailureCannotBeReclassifiedAsVerified()
    {
        var process = new FakeDebugOwnedProcess(
            406,
            new DateTime(2026, 8, 22, 9, 15, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        var launchError = new InvalidOperationException("atomic launch failed");
        var cleanupError = new TimeoutException("process exit was not verified");
        var bootstrapDeleted = false;
        var launcher = new WindowsExcelOwnedProcessLauncher(
            static () => "excel.exe",
            static () => "bootstrap.xlsx",
            path =>
            {
                Assert.Equal("bootstrap.xlsx", path);
                bootstrapDeleted = true;
            },
            (_, _, _, _) => throw new DebugProcessOwnershipCleanupException(
                launchError,
                cleanupError));

        var error = Assert.Throws<OwnedExcelSessionStartException>(() =>
            launcher.Start(
                processApi,
                "WinSta0\\vba-dev-automation-unit-test",
                CancellationToken.None));

        Assert.False(error.CleanupVerified);
        Assert.Same(launchError, error.StartException);
        Assert.Same(cleanupError, error.CleanupException);
        Assert.True(job.Disposed);
        Assert.True(bootstrapDeleted);
    }

    [Fact]
    public void AtomicLauncherKeepsProcessReleaseVerifiedWhenOnlyBootstrapDeleteFails()
    {
        var launchError = new InvalidOperationException("atomic launch failed");
        var deleteError = new IOException("bootstrap delete failed");
        var process = new FakeDebugOwnedProcess(
            413,
            new DateTime(2026, 9, 3, 9, 17, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var processApi = new FakeDebugExcelProcessApi(process.Id, process, job);
        var launcher = new WindowsExcelOwnedProcessLauncher(
            static () => "excel.exe",
            static () => "bootstrap.xlsx",
            _ => throw deleteError,
            (_, _, _, _) => throw launchError);

        var error = Assert.Throws<OwnedExcelSessionStartException>(() =>
            launcher.Start(
                processApi,
                "WinSta0\\vba-dev-automation-unit-test",
                CancellationToken.None));

        Assert.True(error.CleanupVerified);
        Assert.Same(launchError, error.StartException);
        Assert.Same(deleteError, error.CleanupException);
        Assert.True(job.Disposed);
    }

    [Fact]
    public async Task BootstrapDeleteFailureAfterVerifiedPreAttachCleanupDoesNotFaultLaunchSettlement()
    {
        var observationError = new InvalidOperationException("observation failed");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            414,
            new DateTime(2026, 9, 3, 9, 18, 0, DateTimeKind.Local),
            events: events);
        var isolation = new FakeExcelAutomationDesktopIsolation(events)
        {
            StartObservationError = observationError
        };
        using var terminationController = new OwnedExcelTerminationController();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new FakeOwnedProcessLauncher(
                process,
                events: events,
                bootstrapWorkbookPath: "bootstrap.xlsx"),
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events),
            _ => throw new IOException("bootstrap delete failed"));

        var error = Assert.Throws<WorkbookAutomationReleasedProcessCleanupException>(() =>
            bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.Contains(observationError.Message, error.ToString(), StringComparison.Ordinal);
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        await terminationController.RequestCleanupAsync(TimeSpan.Zero);
        Assert.True(process.HasExited);
        Assert.Equal(1, isolation.DisposeCalls);
    }

    [Fact]
    public async Task BootstrapArtifactCleanupFailureBeforeProcessCreationRemainsReleasedCleanup()
    {
        var createError = new IOException("bootstrap creation failed");
        var deleteError = new IOException("partial bootstrap delete failed");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            415,
            new DateTime(2026, 9, 3, 9, 19, 0, DateTimeKind.Local));
        var launcher = new WindowsExcelOwnedProcessLauncher(
            static () => "excel.exe",
            () => throw new OwnedExcelSessionStartException(
                createError,
                deleteError,
                cleanupVerified: true),
            static _ => { },
            static (_, _, _, _) => throw new InvalidOperationException(
                "The process must not be started."));
        using var terminationController = new OwnedExcelTerminationController();
        var isolation = new FakeExcelAutomationDesktopIsolation(events);
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));

        var error = Assert.Throws<WorkbookAutomationReleasedProcessCleanupException>(() =>
            bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.Contains(createError.Message, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(deleteError.Message, error.ToString(), StringComparison.Ordinal);
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error));
        await terminationController.RequestCleanupAsync(TimeSpan.Zero);
        Assert.Equal(1, isolation.DisposeCalls);
    }

    [Fact]
    public void BootstrapperCannotReclassifyAtomicLaunchCleanupFailureAsVerified()
    {
        var launchError = new InvalidOperationException("atomic launch failed");
        var cleanupError = new TimeoutException("process exit was not verified");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            407,
            new DateTime(2026, 9, 3, 9, 20, 0, DateTimeKind.Local));
        using var terminationController = new OwnedExcelTerminationController();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new ThrowingOwnedProcessLauncher(
                new OwnedExcelSessionStartException(
                    launchError,
                    cleanupError,
                    cleanupVerified: false)),
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(
                new FakeExcelAutomationDesktopIsolation(events),
                events));

        var error = Assert.Throws<OwnedExcelSessionStartException>(() =>
            bootstrapper.Start(
                terminationController,
                CancellationToken.None));

        Assert.False(error.CleanupVerified);
        Assert.Same(launchError, error.StartException);
        Assert.Same(cleanupError, error.CleanupException);
        Assert.Contains("desktop-dispose", events);
    }

    [Fact]
    public async Task CleanupWaitingOnAtomicLaunchReceivesItsUnverifiedReleaseFailure()
    {
        var launchError = new InvalidOperationException("atomic launch failed");
        var cleanupError = new TimeoutException("process exit was not verified");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            408,
            new DateTime(2026, 9, 3, 9, 25, 0, DateTimeKind.Local));
        using var launchEntered = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        using var terminationController = new OwnedExcelTerminationController();
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            new ThrowingOwnedProcessLauncher(
                new OwnedExcelSessionStartException(
                    launchError,
                    cleanupError,
                    cleanupVerified: false),
                () =>
                {
                    launchEntered.Set();
                    releaseLaunch.Wait(TimeSpan.FromSeconds(5));
                }),
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(
                new FakeExcelAutomationDesktopIsolation(events),
                events));

        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));
        Assert.True(launchEntered.Wait(TimeSpan.FromSeconds(1)));
        var cleanup = terminationController.RequestCleanupAsync(TimeSpan.Zero);
        releaseLaunch.Set();

        var startupFailure = await Assert.ThrowsAsync<OwnedExcelSessionStartException>(
            async () => await startup);
        var cleanupFailure = await Assert.ThrowsAnyAsync<Exception>(() => cleanup);

        Assert.False(startupFailure.CleanupVerified);
        Assert.Contains(cleanupError.Message, cleanupFailure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryThreadDisposeFailureStillSettlesCleanupWaitingOnLaunch()
    {
        var observationError = new InvalidOperationException("observation failed");
        var primaryThreadDisposeError = new InvalidOperationException(
            "primary thread dispose failed");
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(
            423,
            new DateTime(2026, 9, 3, 9, 26, 0, DateTimeKind.Local),
            events: events);
        using var launchEntered = new ManualResetEventSlim();
        using var releaseLaunch = new ManualResetEventSlim();
        using var terminationController = new OwnedExcelTerminationController();
        var launcher = new FakeOwnedProcessLauncher(
            process,
            () =>
            {
                launchEntered.Set();
                releaseLaunch.Wait(TimeSpan.FromSeconds(5));
            },
            events);
        launcher.PrimaryThread.DisposeError = primaryThreadDisposeError;
        var isolation = new FakeExcelAutomationDesktopIsolation(events)
        {
            StartObservationError = observationError
        };
        var bootstrapper = new OwnedExcelApplicationBootstrapper(
            launcher,
            new FakeDebugExcelProcessApi(process.Id, process),
            new CallbackNativeObjectModelBinder(static (_, _) => new object()),
            new FakeExcelAutomationDesktopIsolationFactory(isolation, events));
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));
        Assert.True(launchEntered.Wait(TimeSpan.FromSeconds(1)));
        var cleanup = terminationController.RequestCleanupAsync(TimeSpan.Zero);
        releaseLaunch.Set();

        var startupFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => startup.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(primaryThreadDisposeError, startupFailure);
        await cleanup.WaitAsync(TimeSpan.FromSeconds(1));
        await terminationController.WaitForLaunchSettlementAsync()
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, isolation.DisposeCalls);
        Assert.Equal(1, launcher.PrimaryThread.DisposeCalls);
    }

    private sealed class FakeOwnedProcessLauncher(
        IDebugOwnedProcess process,
        Action? beforeReturn = null,
        List<string>? events = null,
        string bootstrapWorkbookPath = "bootstrap.xlsx")
        : IExcelOwnedProcessLauncher
    {
        public FakeSuspendedPrimaryThread PrimaryThread { get; } = new();

        public int StartCalls { get; private set; }

        public string? RequestedDesktopName { get; private set; }

        public ExcelOwnedProcessLaunch Start(
            IDebugExcelProcessApi processApi,
            string qualifiedDesktopName,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            RequestedDesktopName = qualifiedDesktopName;
            events?.Add($"launch:{qualifiedDesktopName}");
            return StartCore(processApi, cancellationToken);
        }

        private ExcelOwnedProcessLaunch StartCore(
            IDebugExcelProcessApi processApi,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = DebugExcelProcessOwner.OwnStartedProcess(process, processApi);
            beforeReturn?.Invoke();
            return new(
                owner,
                PrimaryThread,
                bootstrapWorkbookPath);
        }
    }

    private sealed class ThrowingOwnedProcessLauncher(
        Exception error,
        Action? beforeThrow = null)
        : IExcelOwnedProcessLauncher
    {
        public ExcelOwnedProcessLaunch Start(
            IDebugExcelProcessApi processApi,
            string qualifiedDesktopName,
            CancellationToken cancellationToken)
        {
            beforeThrow?.Invoke();
            throw error;
        }
    }

    private static IExcelAutomationDesktopIsolationFactory
        CreateNoExposureIsolationFactory()
    {
        var events = new List<string>();
        return new FakeExcelAutomationDesktopIsolationFactory(
            new FakeExcelAutomationDesktopIsolation(events),
            events);
    }

    private sealed class FakeSuspendedPrimaryThread : IDebugSuspendedPrimaryThread
    {
        public int ResumeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Action? BeforeResume { get; set; }

        public Exception? DisposeError { get; set; }

        public void ResumeExactlyOnce()
        {
            BeforeResume?.Invoke();
            ResumeCalls++;
        }

        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }
    }

    private sealed class CallbackNativeObjectModelBinder : IExcelNativeObjectModelBinder
    {
        private readonly Func<int, nint, Func<bool>, object> bind;

        public CallbackNativeObjectModelBinder(Func<int, Func<bool>, object> bind)
            : this((processId, _, hasProcessExited) => bind(processId, hasProcessExited))
        {
        }

        public CallbackNativeObjectModelBinder(
            Func<int, nint, Func<bool>, object> bind)
        {
            this.bind = bind;
        }

        public object BindApplicationOnDesktop(
            int processId,
            nint desktopHandle,
            Func<bool> hasProcessExited)
            => bind(processId, desktopHandle, hasProcessExited);
    }

    private sealed class FakeExcelAutomationDesktopIsolationFactory(
        IExcelAutomationDesktopIsolation isolation,
        List<string> events) : IExcelAutomationDesktopIsolationFactory
    {
        public int CreateCalls { get; private set; }

        public IExcelAutomationDesktopIsolation Create()
        {
            CreateCalls++;
            events.Add("desktop-create");
            return isolation;
        }
    }

    private sealed class FakeExcelAutomationDesktopIsolation(List<string> events)
        : IExcelAutomationDesktopIsolation,
          IExcelAutomationDesktopEvidence
    {
        private int captureCalls;
        private int observedProcessId;

        public string QualifiedDesktopName { get; } =
            "WinSta0\\vba-dev-automation-unit-test";

        public nint DesktopHandle { get; } = (nint)0x42;

        public bool ObserverReady { get; private set; }

        public int StartObservationCalls { get; private set; }

        public int CompleteObservationCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public DesktopWindowExposureEvidence? CompletionEvidence { get; init; }

        public Exception? StartObservationError { get; init; }

        public Action<DesktopWindowLifecyclePhase, int>? CaptureCallback { get; init; }

        public DesktopWindowExposureEvidence Evidence
            => CompletionEvidence ?? new DesktopWindowExposureEvidence(observedProcessId, []);

        public void Capture(DesktopWindowLifecyclePhase phase)
        {
            events.Add($"capture:{phase}");
            CaptureCallback?.Invoke(phase, ++captureCalls);
        }

        public Task StartObservingBeforeResumeAsync(
            int exactProcessId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StartObservationError is not null)
            {
                throw StartObservationError;
            }

            StartObservationCalls++;
            observedProcessId = exactProcessId;
            ObserverReady = true;
            events.Add($"observe:{exactProcessId}");
            return Task.CompletedTask;
        }

        public async Task<DesktopWindowExposureEvidence> CompleteAfterExitAsync(
            Task exactProcessExit,
            CancellationToken cancellationToken)
        {
            CompleteObservationCalls++;
            await exactProcessExit.WaitAsync(cancellationToken);
            events.Add("observer-complete");
            return CompletionEvidence ?? new DesktopWindowExposureEvidence(observedProcessId, []);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            events.Add("desktop-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static void AssertOrdered(
        IReadOnlyList<string> events,
        params string[] expectedEvents)
    {
        var previousIndex = -1;
        foreach (var expectedEvent in expectedEvents)
        {
            var index = -1;
            for (var candidateIndex = 0; candidateIndex < events.Count; candidateIndex++)
            {
                if (events[candidateIndex].Equals(expectedEvent, StringComparison.Ordinal))
                {
                    index = candidateIndex;
                    break;
                }
            }

            Assert.True(
                index > previousIndex,
                $"Expected '{expectedEvent}' after index {previousIndex}. Events: {string.Join(", ", events)}");
            previousIndex = index;
        }
    }
}
