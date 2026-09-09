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
        using var cancellationRegistration = cancellation.Token.Register(
            () => controller.RequestForcedTermination(TimeSpan.Zero));
        var excel = new CancelingExcelSetupApplication(() =>
        {
            cancellation.Cancel();
            controller.RequestCleanupAsync(TimeSpan.Zero)
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
        using var cancellationRegistration = cancellation.Token.Register(
            () => controller.RequestForcedTermination(TimeSpan.Zero));
        var excel = new CancelingExcelSetupApplication(() =>
        {
            cancellation.Cancel();
            controller.RequestCleanupAsync(TimeSpan.Zero)
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
        using var cancellationRegistration = cancellation.Token.Register(
            () => controller.RequestForcedTermination(TimeSpan.Zero));
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
        using var cancellationRegistration = cancellation.Token.Register(
            () => controller.RequestForcedTermination(TimeSpan.Zero));
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
        Assert.False(process.HasExited);
        Assert.True(process.Disposed);
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
}
