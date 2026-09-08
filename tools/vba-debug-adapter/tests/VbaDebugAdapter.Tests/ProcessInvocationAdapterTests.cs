using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Processes;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class ProcessInvocationAdapterTests
{
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
        var actual = await Assert.ThrowsAsync<IOException>(() =>
            new ProcessInvocation(Path.GetFullPath("missing.exe"), platform).RunAsync([]));
        Assert.Same(failure, actual);
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

    private sealed class Job(List<string> events) : IDebugProcessJob
    {
        public void Assign(IDebugOwnedProcess process) => throw new InvalidOperationException("Ownership must be atomic.");
        public void Terminate() => events.Add("terminate-job");
        public void Dispose() => events.Add("release-job");
    }

    private sealed class PrimaryThread(List<string> events, Exception? failure = null) : IDebugSuspendedPrimaryThread
    {
        public void ResumeExactlyOnce()
        {
            events.Add("resume");
            if (failure is not null) { throw failure; }
        }
        public void Dispose() => events.Add("release-thread");
    }

    private sealed class OwnedProcess(List<string> events) : IDebugOwnedProcess
    {
        public int Id => 123;
        public DebugExcelProcessArchitecture Architecture => DebugExcelProcessArchitecture.X64;
        public DateTime StartTime => DateTime.UnixEpoch;
        public bool HasExited => true;
        public int ExitCode => 7;
        public Task WaitForExitAsync(CancellationToken token) { events.Add("exit"); return Task.CompletedTask; }
        public void Kill() => throw new InvalidOperationException("Terminate the owned job.");
        public void Dispose() => events.Add("release-process");
    }

    private sealed class ObservedReader(string value, List<string> events)
        : StreamReader(new MemoryStream())
    {
        public bool Disposed { get; private set; }
        public override Task<string> ReadToEndAsync() { events.Add(value); return Task.FromResult(value); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
