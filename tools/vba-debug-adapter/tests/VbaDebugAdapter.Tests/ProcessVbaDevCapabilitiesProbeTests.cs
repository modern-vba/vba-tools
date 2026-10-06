using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class ProcessVbaDevCapabilitiesProbeTests
{
    private const int FatalClrExitCode = unchecked((int)0x80131506);

    [Fact]
    public async Task AbnormalExitWithProvedCleanupRetriesTheSameExecutableOnce()
    {
        var process = new RecordingProcess(
            ProvedResult(FatalClrExitCode, "private first stdout", "private first stderr"),
            ProvedResult(0, "valid second response", ""));
        var probe = new ProcessVbaDevCapabilitiesProbe(
            process, TimeSpan.Zero);
        var executable = Path.GetFullPath("vba-dev.exe");

        var result = await probe.ProbeAsync(executable, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("valid second response", result.StandardOutput);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(FatalClrExitCode, result.FirstAbnormalExitCode);
        Assert.Equal(2, process.Invocations.Count);
        Assert.All(process.Invocations, invocation =>
        {
            Assert.Equal(executable, invocation.FileName);
            Assert.Equal(["capabilities", "--format", "json"], invocation.Arguments);
        });
    }

    [Fact]
    public async Task RepeatedAbnormalExitStopsAfterTwoAttempts()
    {
        var process = new RecordingProcess(
            ProvedResult(FatalClrExitCode),
            ProvedResult(unchecked((int)0xC0000005)));
        var probe = new ProcessVbaDevCapabilitiesProbe(
            process, TimeSpan.Zero);

        var result = await probe.ProbeAsync(
            Path.GetFullPath("vba-dev.exe"), CancellationToken.None);

        Assert.Equal(unchecked((int)0xC0000005), result.ExitCode);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(FatalClrExitCode, result.FirstAbnormalExitCode);
        Assert.Equal(2, process.Invocations.Count);
    }

    [Theory]
    [InlineData(7, "ordinary failure")]
    [InlineData(0, "invalid JSON is an admission failure, not a crash")]
    public async Task OrdinaryExitDoesNotRetry(int exitCode, string response)
    {
        var process = new RecordingProcess(ProvedResult(exitCode, response, ""));
        var probe = new ProcessVbaDevCapabilitiesProbe(
            process, TimeSpan.Zero);

        var result = await probe.ProbeAsync(
            Path.GetFullPath("vba-dev.exe"), CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(1, result.AttemptCount);
        Assert.Null(result.FirstAbnormalExitCode);
        Assert.Single(process.Invocations);
    }

    [Fact]
    public async Task AbnormalExitWithoutProcessReleaseEvidenceDoesNotRetry()
    {
        var process = new RecordingProcess(new VbaDevBuildProcessResult(
            FatalClrExitCode, "", ""));
        var probe = new ProcessVbaDevCapabilitiesProbe(
            process, TimeSpan.Zero);

        var result = await probe.ProbeAsync(
            Path.GetFullPath("vba-dev.exe"), CancellationToken.None);

        Assert.Equal(FatalClrExitCode, result.ExitCode);
        Assert.Equal(1, result.AttemptCount);
        Assert.Single(process.Invocations);
    }

    [Fact]
    public async Task CancellationAfterAbnormalExitPreventsRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var process = new RecordingProcess(ProvedResult(FatalClrExitCode))
        {
            OnRun = cancellation.Cancel
        };
        var probe = new ProcessVbaDevCapabilitiesProbe(
            process, TimeSpan.Zero);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(Path.GetFullPath("vba-dev.exe"), cancellation.Token));

        Assert.Single(process.Invocations);
    }

    [Fact]
    public async Task FailedSecondAttemptRetainsTheFirstCrashAndOriginalFailure()
    {
        var original = new InvalidOperationException("private second-attempt failure");
        var process = new FailingSecondProcess(
            ProvedResult(FatalClrExitCode), original);
        var probe = new ProcessVbaDevCapabilitiesProbe(process, TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<VbaDevCapabilityRetryFailureException>(() =>
            probe.ProbeAsync(Path.GetFullPath("vba-dev.exe"), CancellationToken.None));

        Assert.Same(original, failure.InnerException);
        Assert.Equal(FatalClrExitCode, failure.FirstAbnormalExitCode);
        Assert.Equal(2, process.Invocations.Count);
        Assert.DoesNotContain("private", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationFromSecondAttemptIsNotWrapped()
    {
        var cancellation = new OperationCanceledException("second attempt cancelled");
        var process = new FailingSecondProcess(
            ProvedResult(FatalClrExitCode), cancellation);
        var probe = new ProcessVbaDevCapabilitiesProbe(process, TimeSpan.Zero);

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.ProbeAsync(Path.GetFullPath("vba-dev.exe"), CancellationToken.None));

        Assert.Same(cancellation, failure);
        Assert.Equal(2, process.Invocations.Count);
    }

    private static VbaDevBuildProcessResult ProvedResult(
        int exitCode, string output = "", string error = "")
    {
        var completion = new DebugFailureCompletion();
        completion.AddEvidence(new("companion-exit", "companion process",
            DebugResourceKind.Process, true, "Exit observed.", 123));
        completion.AddEvidence(new("companion-job-release", "companion Job Object",
            DebugResourceKind.Handle, true, "Handle released.", 123));
        return new VbaDevBuildProcessResult(exitCode, output, error)
        {
            CleanupOutcome = completion.Complete()
        };
    }

    private sealed class RecordingProcess(params VbaDevBuildProcessResult[] results)
        : IVbaDevBuildProcess
    {
        private readonly Queue<VbaDevBuildProcessResult> results = new(results);

        public Action? OnRun { get; init; }

        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add((fileName, arguments));
            OnRun?.Invoke();
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class FailingSecondProcess(
        VbaDevBuildProcessResult first,
        Exception failure) : IVbaDevBuildProcess
    {
        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments));
            return Invocations.Count == 1
                ? Task.FromResult(first)
                : Task.FromException<VbaDevBuildProcessResult>(failure);
        }
    }
}
