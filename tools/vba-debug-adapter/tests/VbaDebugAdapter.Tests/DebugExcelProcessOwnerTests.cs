using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DebugExcelProcessOwnerTests
{
    [Fact]
    public async Task FailedTerminationCarriesSeparateCleanupStagesAndRetainedEvidence()
    {
        var jobFailure = new IOException("Job termination failed.");
        var killFailure = new IOException("Process fallback kill failed.");
        var process = new OwnedProcess { KillFailure = killFailure };
        var job = new OwnedJob(process) { TerminateFailure = jobFailure };
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        try
        {
            var failure = await Record.ExceptionAsync(() => owner.TerminateAsync().AsTask());

            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, jobFailure)
                && item.Stage == "process-job-termination");
            Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, killFailure)
                && item.Stage == "process-fallback-kill");
            Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process
                && !item.Released && item.ProcessId == process.Id);
            var repeated = await Record.ExceptionAsync(() => owner.TerminateAsync().AsTask());
            Assert.Same(outcome, Assert.IsAssignableFrom<IDebugFailureEvidence>(repeated).FailureOutcome);
        }
        finally
        {
            process.Exit();
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public void FailedAdoptionRetainsItsCauseAndPositivePartialCleanupEvidence()
    {
        var events = new List<string>();
        var process = new FakeDebugOwnedProcess(130, DateTime.UnixEpoch, events: events);
        process.Exit(0);
        var job = new FakeDebugProcessJob(process, events);

        var failure = Record.Exception(() => DebugExcelProcessOwner.AdoptPreassignedProcess(process, job));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.IsType<DebugSetupException>(outcome.PrimaryFailure);
        Assert.Contains(nameof(DebugExcelProcessOwner.AdoptPreassignedProcess), outcome.PrimaryFailure!.StackTrace!);
        Assert.False(outcome.HasCleanupFailure);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
        Assert.Equal(2, outcome.Evidence.Count(item => item.Kind == DebugResourceKind.Handle && item.Released));
        Assert.Equal(1, events.Count(item => item == "job-dispose"));
        Assert.Equal(1, events.Count(item => item == "process-dispose"));
    }

    [Fact]
    public async Task SuccessfulDisposalRetainsSeparateProcessAndHandleEvidence()
    {
        var process = new OwnedProcess();
        var job = new OwnedJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);

        await owner.DisposeAsync();

        var evidenceOwner = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(owner);
        var outcome = Assert.IsType<DebugFailureOutcome>(evidenceOwner.CleanupOutcome);
        Assert.False(outcome.HasUnprovedRelease);
        Assert.False(outcome.HasCleanupFailure);
        Assert.Equal(1, outcome.Evidence.Count(item => item.Kind == DebugResourceKind.Process && item.Released));
        Assert.Equal(2, outcome.Evidence.Count(item => item.Kind == DebugResourceKind.Handle && item.Released));
        Assert.All(outcome.Evidence, item => Assert.Equal(process.Id, item.ProcessId));
        Assert.True(owner.HasExited);
        await owner.DisposeAsync();
        Assert.Same(outcome, evidenceOwner.CleanupOutcome);
    }

    [Fact]
    public async Task FailedDisposalDoesNotClaimThatTheProcessExited()
    {
        var process = new OwnedProcess { KillFailure = new IOException("Kill failed.") };
        var job = new OwnedJob(process) { TerminateFailure = new IOException("Job termination failed.") };
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        try
        {
            Assert.NotNull(await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask()));
            Assert.False(owner.HasExited);
            Assert.False(owner.Completion.IsCompleted);
        }
        finally { process.Exit(); }
    }

    [Fact]
    public async Task FailedTerminationRetainsBothJobAndFallbackFailures()
    {
        var jobFailure = new IOException("Job termination failed.");
        var killFailure = new IOException("Process fallback kill failed.");
        var process = new OwnedProcess { KillFailure = killFailure };
        var job = new OwnedJob(process) { TerminateFailure = jobFailure };
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);
        try
        {
            var failure = await Record.ExceptionAsync(() => owner.TerminateAsync().AsTask());
            Assert.NotNull(failure);
            Assert.Contains(jobFailure, Causes(failure));
            Assert.Contains(killFailure, Causes(failure));
            Assert.Same(failure, await Record.ExceptionAsync(() => owner.TerminateAsync().AsTask()));
            Assert.Equal(1, job.TerminateCalls);
        }
        finally
        {
            process.Exit();
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task RepeatedDisposalRetainsTheFirstHandleReleaseFailure()
    {
        var releaseFailure = new IOException("Process handle release failed.");
        var process = new OwnedProcess { DisposeFailure = releaseFailure };
        var job = new OwnedJob(process);
        var owner = DebugExcelProcessOwner.AdoptPreassignedProcess(process, job);

        var first = Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        var concurrent = Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
        var outcomes = await Task.WhenAll(first, concurrent);
        var repeated = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());

        Assert.NotNull(outcomes[0]);
        Assert.Same(outcomes[0], outcomes[1]);
        Assert.Same(outcomes[0], repeated);
        Assert.Contains(releaseFailure, Causes(outcomes[0]!));
        Assert.Equal(1, process.DisposeCalls);
        Assert.Equal(1, job.DisposeCalls);
        Assert.Equal(1, job.TerminateCalls);
    }

    private static IEnumerable<Exception> Causes(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> children = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } inner ? [inner] : Enumerable.Empty<Exception>();
        foreach (var child in children)
        {
            foreach (var cause in Causes(child))
            {
                yield return cause;
            }
        }
    }

    private sealed class OwnedProcess : IDebugOwnedProcess
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Id => 123;
        public DebugExcelProcessArchitecture Architecture => DebugExcelProcessArchitecture.X64;
        public DateTime StartTime => DateTime.UnixEpoch;
        public bool HasExited => completion.Task.IsCompletedSuccessfully;
        public int ExitCode => 0;
        public Exception? DisposeFailure { get; init; }
        public Exception? KillFailure { get; init; }
        public int DisposeCalls { get; private set; }
        public bool HandleReleaseVerified { get; private set; }
        public Task WaitForExitAsync(CancellationToken cancellationToken) => completion.Task.WaitAsync(cancellationToken);
        public void Kill()
        {
            if (KillFailure is { } failure) { throw failure; }
            Exit();
        }
        public void Exit() => completion.TrySetResult();
        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeFailure is { } failure) { throw failure; }
            HandleReleaseVerified = true;
        }
    }

    private sealed class OwnedJob(OwnedProcess process) : IDebugProcessJob
    {
        public int DisposeCalls { get; private set; }
        public int TerminateCalls { get; private set; }
        public Exception? TerminateFailure { get; init; }
        public bool HandleReleaseVerified { get; private set; }
        public void Assign(IDebugOwnedProcess ownedProcess) => throw new InvalidOperationException();
        public void Terminate()
        {
            TerminateCalls++;
            if (TerminateFailure is { } failure) { throw failure; }
            process.Exit();
        }
        public void Dispose() { DisposeCalls++; HandleReleaseVerified = true; }
    }
}
