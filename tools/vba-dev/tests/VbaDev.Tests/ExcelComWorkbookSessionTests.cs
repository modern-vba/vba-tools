using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExcelComWorkbookSessionTests
{
    [Fact]
    public void ReleasedBootstrapFailureIsAlreadyClassifiedBeforeOwnerTransfer()
    {
        var failure = new WorkbookAutomationReleasedProcessCleanupException(
            "The process was released, but bootstrap cleanup failed.");

        Assert.True(
            ExcelComWorkbookSession.IsPreOwnershipBootstrapFailureAlreadyClassified(
                failure));
    }

    [Fact]
    public void ReleasedOwnershipCleanupKeepsCooperativeFailureReleaseVerified()
    {
        var cooperativeError = new InvalidOperationException(
            "Cooperative workbook cleanup failed.");
        var ownershipError = new WorkbookAutomationReleasedProcessCleanupException(
            "Exact owned-process release was verified despite an isolation failure.");

        var error = ExcelComWorkbookSession.CombineCooperativeAndOwnershipCleanupErrors(
            cooperativeError,
            ownershipError);

        var released = Assert.IsType<WorkbookAutomationReleasedProcessCleanupException>(error);
        Assert.False(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(released));
        var failures = Assert.IsType<AggregateException>(released.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, ownershipError));
    }

    [Fact]
    public void UnprovedOwnershipCleanupKeepsCooperativeFailureAsProofFailure()
    {
        var cooperativeError = new InvalidOperationException(
            "Cooperative workbook cleanup failed.");
        var ownershipError = new WorkbookAutomationCleanupException(
            "Exact owned-process release could not be proved.");

        var error = ExcelComWorkbookSession.CombineCooperativeAndOwnershipCleanupErrors(
            cooperativeError,
            ownershipError);

        var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(error);
        Assert.True(WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(cleanup));
        var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, ownershipError));
    }

    [Fact]
    public void UnknownOwnershipCleanupFailureCannotBeTreatedAsReleased()
    {
        var cooperativeError = new InvalidOperationException(
            "Cooperative workbook cleanup failed.");
        var ownershipError = new TimeoutException(
            "Owned-process exit verification timed out.");

        var error = ExcelComWorkbookSession.CombineCooperativeAndOwnershipCleanupErrors(
            cooperativeError,
            ownershipError);

        var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(error);
        var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cooperativeError));
        Assert.Contains(failures, failure => ReferenceEquals(failure, ownershipError));
    }

    [Fact]
    public void UnknownStandaloneOwnershipCleanupFailureIsClassifiedAsProofFailure()
    {
        var ownershipError = new TimeoutException(
            "Owned-process exit verification timed out.");

        var error = ExcelComWorkbookSession.ClassifyOwnershipCleanupError(
            ownershipError);

        var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(error);
        Assert.Same(ownershipError, cleanup.InnerException);
    }

    [Fact]
    public async Task CallerCancellationAfterOpenRequestsImmediateExactOwnerCleanup()
    {
        var process = new RecordingOwnedExcelProcessControl();
        using var controller = new OwnedExcelTerminationController();
        Assert.True(controller.Attach(process));
        using var cancellation = new CancellationTokenSource();
        using var registration = ExcelComWorkbookSession.RegisterCallerCancellation(
            controller,
            cancellation.Token);

        cancellation.Cancel();
        await controller.ObserveTerminationAsync().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, process.TerminationCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task CallerCancellationDuringStartupBindingTriggersExactCleanupBeforeCanceledReturn()
    {
        var process = new RecordingOwnedExcelProcessControl();
        using var cancellation = new CancellationTokenSource();
        var bindingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OwnedExcelTerminationController? observedController = null;
        var opening = Task.Run(() => Record.Exception(() =>
        {
            _ = ExcelComWorkbookSession.OpenOwnedForBuild(
                "Book1.xlsm",
                cancellation.Token,
                (_, token, controller) =>
                {
                    observedController = controller;
                    Assert.True(controller.Attach(process));
                    bindingStarted.TrySetResult();
                    process.Completion.GetAwaiter().GetResult();
                    controller.ObserveTerminationAsync().GetAwaiter().GetResult();
                    throw new OwnedExcelSessionStartCanceledException(
                        new OperationCanceledException(
                            "Excel startup was canceled during native binding.",
                            token),
                        cleanupException: null,
                        cleanupVerified: true);
                });
        }));
        await bindingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        var cleanupTriggeredByRegistration = await Task.WhenAny(
            process.Completion,
            Task.Delay(TimeSpan.FromMilliseconds(250))) == process.Completion;
        if (!cleanupTriggeredByRegistration)
        {
            Assert.NotNull(observedController);
            await observedController.RequestCleanupAsync(TimeSpan.Zero);
        }

        var error = await opening.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(cleanupTriggeredByRegistration);
        var canceled = Assert.IsType<OwnedExcelSessionStartCanceledException>(error);
        Assert.True(canceled.CleanupVerified);
        Assert.Equal(1, process.TerminationCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.True(process.HasExited);
    }

    [Fact]
    public void CallerCancellationDuringOwnedHostSetupPreservesComFailureAsCanceledCause()
    {
        var setupFailure = new COMException(
            "Excel disconnected while hidden host settings were applied.");
        var process = new FakeDebugOwnedProcess(
            430,
            new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process, job));
        using var cancellation = new CancellationTokenSource();
        using var controller = new OwnedExcelTerminationController();
        using var cancellationRegistration = ExcelComWorkbookSession.RegisterCallerCancellation(
            controller,
            cancellation.Token);
        var excel = new CancelingExcelSetupApplication(() =>
        {
            cancellation.Cancel();
            controller.ObserveTerminationAsync()
                .WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            throw setupFailure;
        });

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            ExcelComWorkbookSession.StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow: false,
                controller,
                cancellation.Token,
                (observedController, _) =>
                {
                    Assert.Same(controller, observedController);
                    Assert.True(observedController.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new OwnedExcelApplication(
                        excel,
                        owner,
                        "bootstrap.xlsx");
                },
                static _ => { }));

        Assert.True(error.CleanupVerified);
        Assert.Null(error.CleanupException);
        var cancellationCause = Assert.IsType<OperationCanceledException>(
            error.StartException);
        Assert.Same(setupFailure, cancellationCause.InnerException);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, job.TerminateCalls);
    }

    [Fact]
    public void CallerCancellationDuringOwnedHostSetupKeepsReleasedCleanupAndCanceledCause()
    {
        var setupFailure = new COMException(
            "Excel disconnected while hidden host settings were applied.");
        var cleanupFailure = new IOException("Bootstrap cleanup failed.");
        var process = new FakeDebugOwnedProcess(
            432,
            new DateTime(2026, 9, 3, 10, 0, 30, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process, job));
        using var cancellation = new CancellationTokenSource();
        using var controller = new OwnedExcelTerminationController();
        using var cancellationRegistration = ExcelComWorkbookSession.RegisterCallerCancellation(
            controller,
            cancellation.Token);
        var excel = new CancelingExcelSetupApplication(() =>
        {
            cancellation.Cancel();
            controller.ObserveTerminationAsync()
                .WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            throw setupFailure;
        });

        var error = Assert.Throws<WorkbookAutomationReleasedProcessCleanupException>(() =>
            ExcelComWorkbookSession.StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow: false,
                controller,
                cancellation.Token,
                (observedController, _) =>
                {
                    Assert.True(observedController.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new OwnedExcelApplication(
                        excel,
                        owner,
                        "bootstrap.xlsx");
                },
                _ => throw cleanupFailure));

        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .InnerExceptions;
        var cancellationCause = Assert.Single(
            failures.OfType<OperationCanceledException>());
        Assert.Same(setupFailure, cancellationCause.InnerException);
        Assert.Contains(failures, failure => ReferenceEquals(failure, cleanupFailure));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void OwnedHostSetupFailureIsNotReclassifiedByCancellationDuringCleanup()
    {
        var setupFailure = new COMException(
            "Excel rejected hidden host settings before cancellation.");
        using var cancellation = new CancellationTokenSource();
        var process = new FakeDebugOwnedProcess(
            436,
            new DateTime(2026, 9, 3, 10, 0, 45, DateTimeKind.Local),
            killAction: cancellation.Cancel);
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process));
        using var controller = new OwnedExcelTerminationController();
        using var cancellationRegistration = ExcelComWorkbookSession.RegisterCallerCancellation(
            controller,
            cancellation.Token);
        var excel = new CancelingExcelSetupApplication(() => throw setupFailure);

        var error = Assert.Throws<OwnedExcelSessionStartException>(() =>
            ExcelComWorkbookSession.StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow: false,
                controller,
                cancellation.Token,
                (observedController, _) =>
                {
                    Assert.True(observedController.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new OwnedExcelApplication(
                        excel,
                        owner,
                        "bootstrap.xlsx");
                },
                static _ => { }));

        Assert.True(error.CleanupVerified);
        Assert.Same(setupFailure, error.StartException);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void OwnedHostSetupCancellationKeepsCanceledCauseWhenCleanupProofFails()
    {
        var setupFailure = new COMException(
            "Excel disconnected while hidden host settings were applied.");
        var cleanupProofFailure = new InvalidOperationException(
            "The exactly owned Excel process could not be terminated.");
        var process = new FakeDebugOwnedProcess(
            437,
            new DateTime(2026, 9, 3, 10, 0, 50, DateTimeKind.Local),
            killAction: () => throw cleanupProofFailure,
            exitOnKill: false);
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process));
        using var cancellation = new CancellationTokenSource();
        using var controller = new OwnedExcelTerminationController();
        using var cancellationRegistration = ExcelComWorkbookSession.RegisterCallerCancellation(
            controller,
            cancellation.Token);
        var excel = new CancelingExcelSetupApplication(() =>
        {
            cancellation.Cancel();
            throw setupFailure;
        });

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            ExcelComWorkbookSession.StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow: false,
                controller,
                cancellation.Token,
                (observedController, _) =>
                {
                    Assert.True(observedController.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new OwnedExcelApplication(
                        excel,
                        owner,
                        "bootstrap.xlsx");
                },
                static _ => { }));

        Assert.False(error.CleanupVerified);
        var cancellationCause = Assert.IsType<OperationCanceledException>(
            error.StartException);
        Assert.Same(setupFailure, cancellationCause.InnerException);
        Assert.NotNull(error.CleanupException);
        var terminal = ExcelComWorkbookSession.CombineCooperativeAndOwnershipCleanupErrors(
            cancellationCause,
            error.CleanupException);
        var cleanup = Assert.IsType<WorkbookAutomationCleanupException>(terminal);
        var failures = Assert.IsType<AggregateException>(cleanup.InnerException)
            .InnerExceptions;
        Assert.Contains(failures, failure => ReferenceEquals(failure, cancellationCause));
        Assert.Contains(
            failures,
            failure => ReferenceEquals(failure, error.CleanupException));
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void CallerCancellationDuringWorkbookOpenPreservesComFailureAsCanceledCause()
    {
        var openFailure = new COMException(
            "Excel disconnected while the requested workbook was opening.");
        var process = new FakeDebugOwnedProcess(
            431,
            new DateTime(2026, 9, 3, 10, 1, 0, DateTimeKind.Local));
        var job = new FakeDebugProcessJob(process);
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process, job));
        using var cancellation = new CancellationTokenSource();
        OwnedExcelTerminationController? observedController = null;
        var excel = new QuitOnlyExcelApplication();
        var workbooks = new CancelingWorkbooks(() =>
        {
            cancellation.Cancel();
            Assert.NotNull(observedController);
            observedController.ObserveTerminationAsync()
                .WaitAsync(TimeSpan.FromSeconds(1))
                .GetAwaiter()
                .GetResult();
            throw openFailure;
        });

        var error = Assert.Throws<OwnedExcelSessionStartCanceledException>(() =>
            ExcelComWorkbookSession.OpenOwnedForBuild(
                "Book1.xlsm",
                cancellation.Token,
                (_, _, controller) =>
                {
                    observedController = controller;
                    Assert.True(controller.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new ExcelComWorkbookSession.ExcelComHostObjects(
                        excel,
                        workbooks,
                        ExcelProcess: null,
                        owner,
                        controller,
                        CancellationRegistration: default);
                }));

        Assert.True(error.CleanupVerified);
        Assert.Null(error.CleanupException);
        var cancellationCause = Assert.IsType<OperationCanceledException>(
            error.StartException);
        Assert.Same(openFailure, cancellationCause.InnerException);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, job.TerminateCalls);
        Assert.Equal(1, excel.QuitCalls);
    }

    [Fact]
    public void WorkbookOpenFailureIsNotReclassifiedByCancellationDuringCleanup()
    {
        var openFailure = new COMException(
            "Excel rejected the requested workbook before cancellation.");
        var process = new FakeDebugOwnedProcess(
            435,
            new DateTime(2026, 9, 3, 10, 4, 0, DateTimeKind.Local));
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process));
        using var cancellation = new CancellationTokenSource();
        var workbooks = new CancelingWorkbooks(() => throw openFailure);

        var error = Assert.Throws<COMException>(() =>
            ExcelComWorkbookSession.OpenOwnedForBuild(
                "Book1.xlsm",
                cancellation.Token,
                (_, _, controller) =>
                {
                    Assert.True(controller.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new ExcelComWorkbookSession.ExcelComHostObjects(
                        new QuitOnlyExcelApplication(),
                        workbooks,
                        ExcelProcess: null,
                        owner,
                        controller,
                        CancellationRegistration: default);
                },
                (_, controller, _) =>
                {
                    cancellation.Cancel();
                    Assert.NotNull(controller);
                    controller.RequestCleanupAsync(TimeSpan.Zero)
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .GetAwaiter()
                        .GetResult();
                    controller.Dispose();
                }));

        Assert.Same(openFailure, error);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void WorkbookOpenCancellationKeepsCleanupProofFailureAboveCanceledCause()
    {
        var openFailure = new COMException(
            "Excel disconnected while the requested workbook was opening.");
        var cleanupFailure = new WorkbookAutomationCleanupException(
            "Exact owned-process cleanup could not be proved.");
        var process = new FakeDebugOwnedProcess(
            433,
            new DateTime(2026, 9, 3, 10, 2, 0, DateTimeKind.Local));
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process));
        using var cancellation = new CancellationTokenSource();
        OwnedExcelTerminationController? observedController = null;
        var workbooks = new CancelingWorkbooks(() =>
        {
            cancellation.Cancel();
            throw openFailure;
        });

        var error = Assert.Throws<WorkbookAutomationCleanupException>(() =>
            ExcelComWorkbookSession.OpenOwnedForBuild(
                "Book1.xlsm",
                cancellation.Token,
                (_, _, controller) =>
                {
                    observedController = controller;
                    Assert.True(controller.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new ExcelComWorkbookSession.ExcelComHostObjects(
                        new QuitOnlyExcelApplication(),
                        workbooks,
                        ExcelProcess: null,
                        owner,
                        controller,
                        CancellationRegistration: default);
                },
                (_, controller, _) =>
                {
                    Assert.NotNull(controller);
                    controller.ObserveTerminationAsync()
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .GetAwaiter()
                        .GetResult();
                    controller.Dispose();
                    throw cleanupFailure;
                }));

        Assert.NotSame(cleanupFailure, error);
        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .InnerExceptions;
        var cancellationCause = Assert.Single(
            failures.OfType<OperationCanceledException>());
        Assert.Same(openFailure, cancellationCause.InnerException);
        Assert.Contains(failures, failure => ReferenceEquals(failure, cleanupFailure));
        Assert.NotNull(observedController);
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void WorkbookOpenCancellationKeepsReleasedCleanupAboveCanceledCause()
    {
        var openFailure = new COMException(
            "Excel disconnected while the requested workbook was opening.");
        var cleanupFailure = new WorkbookAutomationReleasedProcessCleanupException(
            "Exact process release was proved, but isolation cleanup failed.");
        var process = new FakeDebugOwnedProcess(
            434,
            new DateTime(2026, 9, 3, 10, 3, 0, DateTimeKind.Local));
        var owner = DebugExcelProcessOwner.OwnStartedProcess(
            process,
            new FakeDebugExcelProcessApi(process.Id, process));
        using var cancellation = new CancellationTokenSource();
        var workbooks = new CancelingWorkbooks(() =>
        {
            cancellation.Cancel();
            throw openFailure;
        });

        var error = Assert.Throws<WorkbookAutomationReleasedProcessCleanupException>(() =>
            ExcelComWorkbookSession.OpenOwnedForBuild(
                "Book1.xlsm",
                cancellation.Token,
                (_, _, controller) =>
                {
                    Assert.True(controller.Attach(
                        new DebugOwnedExcelProcessControl(owner)));
                    return new ExcelComWorkbookSession.ExcelComHostObjects(
                        new QuitOnlyExcelApplication(),
                        workbooks,
                        ExcelProcess: null,
                        owner,
                        controller,
                        CancellationRegistration: default);
                },
                (_, controller, _) =>
                {
                    Assert.NotNull(controller);
                    controller.ObserveTerminationAsync()
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .GetAwaiter()
                        .GetResult();
                    controller.Dispose();
                    throw cleanupFailure;
                }));

        Assert.NotSame(cleanupFailure, error);
        var failures = Assert.IsType<AggregateException>(error.InnerException)
            .InnerExceptions;
        var cancellationCause = Assert.Single(
            failures.OfType<OperationCanceledException>());
        Assert.Same(openFailure, cancellationCause.InnerException);
        Assert.Contains(failures, failure => ReferenceEquals(failure, cleanupFailure));
        Assert.True(process.HasExited);
        Assert.True(process.Disposed);
    }

    private sealed class RecordingOwnedExcelProcessControl : IOwnedExcelProcessControl
    {
        private readonly TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TerminationCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool HasExited { get; private set; }

        public Task Completion => completion.Task;

        public Task TerminateAsync()
        {
            TerminationCalls++;
            HasExited = true;
            completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    public sealed class CancelingExcelSetupApplication(Action applyDisplayAlerts)
    {
        public bool Visible { private get; set; }

        public bool DisplayAlerts
        {
            private get => false;
            set => applyDisplayAlerts();
        }
    }

    public sealed class CancelingWorkbooks(Action open)
    {
        public object Open(string workbookPath, int updateLinks, bool readOnly)
        {
            open();
            return new object();
        }
    }

    public sealed class QuitOnlyExcelApplication
    {
        public int QuitCalls { get; private set; }

        public void Quit() => QuitCalls++;
    }
}
