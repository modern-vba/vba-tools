using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Processes;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class ProcessInvocationAdapterTests
{
    [Fact]
    public async Task LauncherProcessEvidenceCannotReplaceItsMissingHandleReleaseEvidence()
    {
        var events = new List<string>();
        var cause = new IOException("Launcher setup failed.");
        var launchCompletion = new DebugFailureCompletion(cause);
        launchCompletion.AddEvidence(new("launcher-process-cleanup", "launcher child", DebugResourceKind.Process,
            true, "The launcher proved that no process remains."));
        var platform = new WindowsJobProcessPlatform(() => new Job(events),
            (_, _, _) => throw new DebugFailureException(launchCompletion.Complete()));
        var adapter = new ProcessVbaDevBuildProcess(() => platform);

        var failure = await Assert.ThrowsAsync<DebugFailureException>(() =>
            adapter.RunAsync(Path.GetFullPath("tool.exe"), [], CancellationToken.None));

        Assert.Same(cause, failure.FailureOutcome.PrimaryFailure);
        Assert.True(failure.FailureOutcome.HasUnprovedRelease, failure.Message);
        Assert.Contains(failure.FailureOutcome.Evidence, item => item.Stage == "companion-job-release"
            && item.Kind == DebugResourceKind.Handle && item.Released);
        Assert.Contains(failure.FailureOutcome.Evidence, item => item.Stage == "companion-launch-cleanup"
            && item.Kind == DebugResourceKind.Handle && !item.Released);
    }

    [Fact]
    public async Task Completed_native_companion_returns_its_terminal_and_handle_release_evidence()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await new ProcessVbaDevBuildProcess().RunAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            ["/d", "/c", "echo companion-evidence"], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("companion-evidence", result.StandardOutput, StringComparison.Ordinal);
        var outcome = Assert.IsType<DebugFailureOutcome>(result.CleanupOutcome);
        Assert.False(outcome.HasCleanupFailure, outcome.Describe());
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
        foreach (var stage in new[] { "companion-job-release", "companion-thread-release",
            "companion-output-release", "companion-error-release", "companion-process-release" })
        {
            Assert.Contains(outcome.Evidence, item => item.Stage == stage
                && item.Kind == DebugResourceKind.Handle && item.Released);
        }
    }

    [Fact]
    public async Task Native_creation_failure_preserves_its_cause_and_every_partial_handle_release()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var failure = await Assert.ThrowsAsync<DebugFailureException>(() =>
            new ProcessVbaDevBuildProcess().RunAsync(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
                [], CancellationToken.None));

        var outcome = failure.FailureOutcome;
        Assert.IsType<System.ComponentModel.Win32Exception>(outcome.PrimaryFailure);
        Assert.False(outcome.HasCleanupFailure, outcome.Describe());
        Assert.Contains(outcome.Evidence, item => item.Stage == "launcher-process-cleanup"
            && item.Kind == DebugResourceKind.Process && item.Released);
        foreach (var name in new[] { "stdin read", "stdin write", "stdout read", "stdout write", "stderr read", "stderr write" })
        {
            Assert.Contains(outcome.Evidence, item => item.Resource == name
                && item.Kind == DebugResourceKind.Handle && item.Released);
        }
    }

    [Fact]
    public async Task Job_ownership_and_both_drains_precede_the_single_resume()
    {
        var events = new List<string>();
        var job = new Job(events);
        var process = new OwnedProcess(events);
        using var output = new ObservedReader("output", events);
        using var error = new ObservedReader("error", events);
        var thread = new PrimaryThread(events);
        var platform = new WindowsJobProcessPlatform(
            () => { events.Add("create-job"); return job; },
            (_, _, _) =>
            {
                events.Add("launch-in-job");
                return new DebugSuspendedProcessLaunch(process, thread, output, error);
            });

        var result = await new ProcessInvocation(Path.GetFullPath("tool.exe"), platform).RunAsync([]);

        Assert.Equal(["create-job", "launch-in-job", "output", "error", "resume", "exit",
            "release-job", "release-thread", "release-process"], events);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("output", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
        Assert.True(output.Disposed);
        Assert.True(error.Disposed);
    }

    [Fact]
    public async Task Startup_failure_releases_the_partially_acquired_job()
    {
        var events = new List<string>();
        var failure = new IOException("Process creation failed.");
        var platform = new WindowsJobProcessPlatform(
            () => new Job(events), (_, _, _) => throw failure);
        var actual = await Assert.ThrowsAsync<DebugFailureException>(() =>
            new ProcessInvocation(Path.GetFullPath("missing.exe"), platform).RunAsync([]));
        Assert.Same(failure, actual.FailureOutcome.PrimaryFailure);
        Assert.True(actual.FailureOutcome.HasUnprovedRelease);
        Assert.Equal(["release-job"], events);
    }

    [Fact]
    public async Task Resume_failure_terminates_the_job_and_releases_all_launch_resources()
    {
        var events = new List<string>();
        var failure = new IOException("Resume failed.");
        using var output = new ObservedReader("output", events);
        using var error = new ObservedReader("error", events);
        var platform = new WindowsJobProcessPlatform(() => new Job(events), (_, _, _) =>
            new DebugSuspendedProcessLaunch(new OwnedProcess(events),
                new PrimaryThread(events, failure), output, error));

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            new ProcessInvocation(Path.GetFullPath("tool.exe"), platform).RunAsync([]));

        Assert.Same(failure, actual);
        Assert.Equal(["output", "error", "resume", "terminate-job", "exit",
            "release-job", "release-thread", "release-process"], events);
        Assert.True(output.Disposed);
        Assert.True(error.Disposed);
    }

    [Fact]
    public async Task Invocation_failure_retains_every_release_fault_after_the_original_cause()
    {
        var events = new List<string>();
        var cause = new IOException("Resume failed.");
        var jobFailure = new IOException("Job release failed.");
        var threadFailure = new IOException("Thread release failed.");
        var processFailure = new IOException("Process release failed.");
        using var output = new ObservedReader("output", events);
        using var error = new ObservedReader("error", events);
        var platform = new WindowsJobProcessPlatform(() => new Job(events, jobFailure), (_, _, _) =>
            new DebugSuspendedProcessLaunch(new OwnedProcess(events, processFailure),
                new PrimaryThread(events, cause, threadFailure), output, error));

        var failure = await Assert.ThrowsAsync<ProcessLifecycleException>(() =>
            new ProcessInvocation(Path.GetFullPath("tool.exe"), platform).RunAsync([]));

        Assert.Same(cause, failure.PrimaryFailure);
        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure.CleanupFailure).FailureOutcome;
        Assert.Equal([jobFailure, threadFailure, processFailure],
            outcome.CleanupFailures.Select(item => item.Exception));
        Assert.True(outcome.HasUnprovedRelease);
        Assert.False(outcome.OnlyFileDeletionFailed);
        Assert.True(output.Disposed);
        Assert.True(error.Disposed);
        Assert.Equal(["output", "error", "resume", "terminate-job", "exit",
            "release-job", "release-thread", "release-process"], events);
    }

    private sealed class Job(List<string> events, Exception? releaseFailure = null) : IDebugProcessJob
    {
        public bool HandleReleaseVerified { get; private set; }
        public void Assign(IDebugOwnedProcess process) => throw new InvalidOperationException("Ownership must be atomic.");
        public void Terminate() => events.Add("terminate-job");
        public void Dispose()
        {
            events.Add("release-job");
            if (releaseFailure is not null) { throw releaseFailure; }
            HandleReleaseVerified = true;
        }
    }

    private sealed class PrimaryThread(List<string> events, Exception? failure = null,
        Exception? releaseFailure = null) : IDebugSuspendedPrimaryThread
    {
        public bool HandleReleaseVerified { get; private set; }
        public void ResumeExactlyOnce()
        {
            events.Add("resume");
            if (failure is not null) { throw failure; }
        }
        public void Dispose()
        {
            events.Add("release-thread");
            if (releaseFailure is not null) { throw releaseFailure; }
            HandleReleaseVerified = true;
        }
    }

    private sealed class OwnedProcess(List<string> events, Exception? releaseFailure = null) : IDebugOwnedProcess
    {
        public bool HandleReleaseVerified { get; private set; }
        public int Id => 123;
        public DebugExcelProcessArchitecture Architecture => DebugExcelProcessArchitecture.X64;
        public DateTime StartTime => DateTime.UnixEpoch;
        public bool HasExited => true;
        public int ExitCode => 7;
        public Task WaitForExitAsync(CancellationToken token) { events.Add("exit"); return Task.CompletedTask; }
        public void Kill() => throw new InvalidOperationException("Terminate the owned job.");
        public void Dispose()
        {
            events.Add("release-process");
            if (releaseFailure is not null) { throw releaseFailure; }
            HandleReleaseVerified = true;
        }
    }

    private sealed class ObservedReader(string value, List<string> events)
        : StreamReader(new MemoryStream()), IDebugResourceOwnerEvidence
    {
        public bool Disposed { get; private set; }
        public DebugFailureOutcome? CleanupOutcome { get; private set; }
        public override Task<string> ReadToEndAsync() { events.Add(value); return Task.FromResult(value); }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            var completion = new DebugFailureCompletion();
            completion.AddEvidence(new("test-reader-release", value, DebugResourceKind.Handle,
                true, "This deterministic reader owner acquired only an in-memory stream."));
            CleanupOutcome = completion.Complete();
        }
    }
}
