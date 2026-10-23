using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugSessionCompletionTests
{
    [Theory]
    [InlineData(DebugSessionEndReason.WorkbookClosed, null, false)]
    [InlineData(DebugSessionEndReason.ProcessExited, null, false)]
    [InlineData(DebugSessionEndReason.ProcessExited, 23, false)]
    [InlineData(DebugSessionEndReason.WorkbookClosed, null, true)]
    public async Task RawEndingRevokesEveryAuthorityBeforeFinalCleanup(
        DebugSessionEndReason reason, int? exitCode, bool failRelease)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        DebugSessionTermination? observation = null;
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resumeRelease = new ManualResetEventSlim();
        var releases = 0;
        var cleanupFailure = new IOException("Bound source reference release failed.");
        var binding = new SourceVbeDesktopBinding(model.Excel, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () =>
            {
                Interlocked.Increment(ref releases);
                releaseStarted.TrySetResult();
                Assert.True(resumeRelease.Wait(TimeSpan.FromSeconds(3)));
                if (failRelease) throw cleanupFailure;
            }, completionProbe: () => observation);
        var automation = new SourceVbeDebugAutomation(new Connector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        var running = new Running(session);
        var restart = new DebugRestartLaunchBinding(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), running, temp.Path,
            "Book", "Source.xlsm", "DebugModule", "RunProbe", null, null,
            DebugRestartPreparationId.Parse("abcdef0123456789abcdef0123456789"), DebugRestartGeneration.Initial, 1);
        using var swap = new DebugRestartSwapAuthority(restart);
        observation = new(reason, exitCode);
        try
        {
            await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(session.Liveness.IsLive);
            Assert.False(session.Completion.IsCompleted);
            Assert.False(restart.IsBoundSessionCurrent);
            Assert.True(swap.SessionEnded);
            Assert.True(swap.InvalidationToken.IsCancellationRequested);
            Assert.Throws<DebugSetupException>(() => swap.ClaimForSwap(restart, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.InspectAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ResetExecutionAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunTargetAsync(
                new("DebugModule", "RunProbe"), null, CancellationToken.None));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Completion.WaitAsync(cancelled.Token));
            var dispose = session.DisposeAsync().AsTask();
            var stop = session.TerminateAsync().AsTask();
            Assert.False(dispose.IsCompleted);
            resumeRelease.Set();
            var receipt = await session.Completion.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(reason, receipt.Reason);
            Assert.Equal(reason.ToString(), receipt.InitialTrigger);
            Assert.Equal(exitCode, receipt.ProcessExitCode);
            Assert.Equal(failRelease, receipt.Failed);
            Assert.Same(receipt.Outcome, Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(session).CleanupOutcome);
            if (failRelease)
            {
                Assert.Contains(receipt.Outcome.CleanupFailures, item => ReferenceEquals(cleanupFailure, item.Exception));
                var first = await Assert.ThrowsAnyAsync<Exception>(() => dispose);
                Assert.Same(first, await Assert.ThrowsAnyAsync<Exception>(() => stop));
                Assert.Same(first, await Assert.ThrowsAnyAsync<Exception>(() => session.DisposeAsync().AsTask()));
            }
            else await Task.WhenAll(dispose, stop);
            Assert.Equal(1, releases);
            Assert.DoesNotContain(events, item => item.StartsWith("close:", StringComparison.Ordinal));
        }
        finally { resumeRelease.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStopAndDisposeShareOneNativeResetAndRetainedOutcome(bool resetFails)
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "Source.xlsm");
        File.WriteAllText(path, "source fixture");
        var events = new List<string>();
        var model = FakeVbeModel.Create(path, events);
        model.Project.Mode = 1;
        model.Workbook.Saved = false;
        var primary = new IOException("Native Reset failed.");
        var reset = new FakeCommandBarControl(228, events)
        {
            ExecuteAction = () => model.Project.Mode = 2,
            ExecuteException = resetFails ? primary : null
        };
        var application = new SourceVbeDebugAutomationTests.ResetApplication(model.Excel, reset);
        application.VBE.ActiveVBProject = model.Project;
        var releases = 0;
        var binding = new SourceVbeDesktopBinding(application, model.Workbook, path,
            12345, 638000000000000000, true, () => true, () => releases++);
        var automation = new SourceVbeDebugAutomation(new Connector(binding),
            new FakeStaComDispatcherFactory(new RecordingStaComDispatcher()), new FakeDebugWindowActivator());
        var session = await automation.AttachOrOpenAsync(path, CancellationToken.None);
        var stop = session.TerminateAsync().AsTask();
        Assert.False(session.Liveness.IsLive);
        var dispose = session.DisposeAsync().AsTask();
        var repeated = session.TerminateAsync().AsTask();
        if (resetFails)
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => stop);
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => dispose));
            Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => repeated));
        }
        else await Task.WhenAll(stop, dispose, repeated);
        var receipt = await session.Completion;
        Assert.Equal(DebugSessionEndReason.Detached, receipt.Reason);
        Assert.Equal("Stop", receipt.InitialTrigger);
        Assert.Equal(resetFails, receipt.Failed);
        Assert.Null(receipt.ProcessExitCode);
        Assert.Single(events, item => item == "execute:228");
        Assert.Equal(1, releases);
        Assert.False(model.Workbook.Saved);
        if (resetFails) Assert.Contains("Use Reset", receipt.RecoveryGuidance);
        Assert.DoesNotContain("excel-quit", events);
        Assert.DoesNotContain("close:False", events);
    }

    private sealed class Connector(SourceVbeDesktopBinding binding) : ISourceVbeDesktopConnector
    {
        public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath) => binding;
    }

    private sealed class Running(ISourceVbeDebugSession session) : IStandaloneVbaDebugRunningSession
    {
        public Task<DebugSessionCompletion> Completion => session.Completion;
        public DebugSessionLiveness Liveness => session.Liveness;
        public int ProcessId => session.ProcessId;
        public string TargetModuleName => "DebugModule";
        public string TargetProcedureName => "RunProbe";
        public IReadOnlyList<VbeBreakpoint> VerifiedBreakpoints => [];
        public ValueTask TerminateAsync() => session.TerminateAsync();
        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
