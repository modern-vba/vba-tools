using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class OwnedExcelApplicationBootstrapperTests
{
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
            binder);

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
                static (_, _) => throw new InvalidOperationException("binding failed")));

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
                }));
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
                }));
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
                }));
        var startup = Task.Run(() => bootstrapper.Start(
            terminationController,
            CancellationToken.None));

        Assert.True(processOwned.Wait(TimeSpan.FromSeconds(1)));
        var cleanup = terminationController.RequestCleanupAsync(TimeSpan.Zero);
        releaseLaunch.Set();
        var error = await Assert.ThrowsAsync<OwnedExcelSessionStartException>(() => startup);
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
            new CallbackNativeObjectModelBinder(static (_, _) => new object()));

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
            (_, _, _) => throw new DebugProcessOwnershipCleanupException(
                launchError,
                cleanupError));

        var error = Assert.Throws<OwnedExcelSessionStartException>(() =>
            launcher.Start(processApi, CancellationToken.None));

        Assert.False(error.CleanupVerified);
        Assert.Same(launchError, error.StartException);
        Assert.Same(cleanupError, error.CleanupException);
        Assert.True(job.Disposed);
        Assert.True(bootstrapDeleted);
    }

    private sealed class FakeOwnedProcessLauncher(
        IDebugOwnedProcess process,
        Action? beforeReturn = null)
        : IExcelOwnedProcessLauncher
    {
        public FakeSuspendedPrimaryThread PrimaryThread { get; } = new();

        public ExcelOwnedProcessLaunch Start(
            IDebugExcelProcessApi processApi,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = DebugExcelProcessOwner.OwnStartedProcess(process, processApi);
            beforeReturn?.Invoke();
            return new(
                owner,
                PrimaryThread,
                "bootstrap.xlsx");
        }
    }

    private sealed class FakeSuspendedPrimaryThread : IDebugSuspendedPrimaryThread
    {
        public int ResumeCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Action? BeforeResume { get; set; }

        public void ResumeExactlyOnce()
        {
            BeforeResume?.Invoke();
            ResumeCalls++;
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }

    private sealed class CallbackNativeObjectModelBinder(
        Func<int, Func<bool>, object> bind) : IExcelNativeObjectModelBinder
    {
        public object BindApplication(int processId, Func<bool> hasProcessExited)
            => bind(processId, hasProcessExited);
    }
}
