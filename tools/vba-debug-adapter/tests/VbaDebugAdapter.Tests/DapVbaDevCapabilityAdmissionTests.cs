using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Fact]
    public async Task UniqueReorderedCapabilityOffersKeepOnlyTheSourceWorkbookRequirementsAndPinnedExecutable()
    {
        using var temp = TempDirectory.Create();
        var runner = new RecordingStdioRunner();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(0,
            """
            {"future":[{"value":1},{"value":2}],"featureVersions":{"invocation.stdinWorkbookConfirmation":"1.0","debug.sourceWorkbookPreparation":"1.0","future":"99.0","sourceSnapshot.activeWindowsCodePage":"1.0","build.sourceSnapshot":"2.0","invocation.stdinCancellation":"1.0"},"contractVersion":"99.0","commands":{"future command":{"outputSchemaVersion":"99.0"}}}
            """, ""));
        var commandLine = CreateCommandLine(runner, probe, new VbaDebugSessionWorkspaceManager(temp.Path));
        var executablePath = Path.GetFullPath("vba-dev.exe");
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", executablePath, "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal([executablePath], probe.Invocations);
        Assert.Equal(executablePath, Assert.Single(runner.Invocations).VbaDevPath);
        Assert.Empty(ReadUtf8(output));
        Assert.Empty(ReadUtf8(error));
    }

    [Fact]
    public async Task WholeResponseCapabilityDuplicateRejectionPrecedesWorkspaceAndStdioInitialization()
    {
        var runner = new RecordingStdioRunner();
        var workspaces = new CapabilityRejectedWorkspaceManager();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(0,
            """
            {"featureVersions":{"build.sourceSnapshot":"2.0","debug.sourceWorkbookPreparation":"1.0","invocation.stdinCancellation":"1.0","invocation.stdinWorkbookConfirmation":"1.0","sourceSnapshot.activeWindowsCodePage":"1.0"},"future":{"nested":[{"value":1,"value":1}]}}
            """, ""));
        var commandLine = CreateCommandLine(runner, probe, workspaces);
        var executablePath = Path.GetFullPath("vba-dev.exe");
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", executablePath, "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal([executablePath], probe.Invocations);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspaces.Invocations);
        Assert.Empty(ReadUtf8(output));
        Assert.Contains("incompatible", ReadUtf8(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatedAbnormalCapabilityProbeExitReportsAttemptsWithoutLeakingProcessOutput()
    {
        var runner = new RecordingStdioRunner();
        var workspaces = new CapabilityRejectedWorkspaceManager();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(
            unchecked((int)0x80131506), "private stdout", "private stderr")
        {
            AttemptCount = 2,
            FirstAbnormalExitCode = unchecked((int)0x80131506)
        });
        var commandLine = CreateCommandLine(runner, probe, workspaces);
        var executablePath = Path.GetFullPath("vba-dev.exe");
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", executablePath, "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal([executablePath], probe.Invocations);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspaces.Invocations);
        Assert.Empty(ReadUtf8(output));
        var diagnostic = ReadUtf8(error);
        Assert.Contains("terminated abnormally", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x80131506", diagnostic, StringComparison.Ordinal);
        Assert.Contains(unchecked((int)0x80131506).ToString(), diagnostic, StringComparison.Ordinal);
        Assert.Contains(executablePath, diagnostic, StringComparison.Ordinal);
        Assert.Contains("capabilities", diagnostic, StringComparison.Ordinal);
        Assert.Contains("2 attempts", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private stdout", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private stderr", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("incompatible", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveredAbnormalCapabilityProbeExitReportsTheRetryBeforeStdioStarts()
    {
        using var temp = TempDirectory.Create();
        var runner = new RecordingStdioRunner();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(
            0,
            """
            {"featureVersions":{"build.sourceSnapshot":"2.0","debug.sourceWorkbookPreparation":"1.0","invocation.stdinCancellation":"1.0","invocation.stdinWorkbookConfirmation":"1.0","sourceSnapshot.activeWindowsCodePage":"1.0"}}
            """,
            "private stderr")
        {
            AttemptCount = 2,
            FirstAbnormalExitCode = unchecked((int)0x80131506)
        });
        var commandLine = CreateCommandLine(
            runner, probe, new VbaDebugSessionWorkspaceManager(temp.Path));
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Single(runner.Invocations);
        Assert.Empty(ReadUtf8(output));
        var diagnostic = ReadUtf8(error);
        Assert.Contains("terminated abnormally", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x80131506", diagnostic, StringComparison.Ordinal);
        Assert.Contains(unchecked((int)0x80131506).ToString(), diagnostic, StringComparison.Ordinal);
        Assert.Contains("attempt 1", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private stderr", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("incompatible", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrdinaryCapabilityProbeFailureIsNotReportedAsCrash()
    {
        var runner = new RecordingStdioRunner();
        var workspaces = new CapabilityRejectedWorkspaceManager();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(
            7, "private stdout", "private stderr"));
        var commandLine = CreateCommandLine(runner, probe, workspaces);
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"),
                "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspaces.Invocations);
        var diagnostic = ReadUtf8(error);
        Assert.Contains("failed with exit code", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("terminated abnormally", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private invalid json", "InvalidJson")]
    [InlineData("{\"featureVersions\":{}}", "MissingCapability")]
    public async Task RejectedCapabilityResponseReportsKindWithoutLeakingPayload(
        string response, string rejectionKind)
    {
        var runner = new RecordingStdioRunner();
        var workspaces = new CapabilityRejectedWorkspaceManager();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(0, response, "private stderr"));
        var commandLine = CreateCommandLine(runner, probe, workspaces);
        var executablePath = Path.GetFullPath("vba-dev.exe");
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var result = await commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", executablePath, "--session", "0123456789abcdef0123456789abcdef"],
            Stream.Null, output, error, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal([executablePath], probe.Invocations);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspaces.Invocations);
        Assert.Empty(ReadUtf8(output));
        var diagnostic = ReadUtf8(error);
        Assert.Contains("incompatible", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rejectionKind, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRetryReportsFirstCrashWithoutMaskingOriginalExceptionOrClaimingWorkspace()
    {
        var runner = new RecordingStdioRunner();
        var workspaces = new CapabilityRejectedWorkspaceManager();
        var executablePath = Path.GetFullPath("vba-dev.exe");
        var original = new InvalidOperationException("private second-attempt failure");
        var probe = new ThrowingRetryProbe(new VbaDevCapabilityRetryFailureException(
            executablePath, unchecked((int)0x80131506), original));
        var commandLine = CreateCommandLine(runner, probe, workspaces);
        using var output = new MemoryStream();
        using var error = new MemoryStream();

        var failure = await Assert.ThrowsAsync<VbaDevCapabilityRetryFailureException>(() =>
            commandLine.InvokeAsync(
                ["--stdio", "--vba-dev", executablePath,
                    "--session", "0123456789abcdef0123456789abcdef"],
                Stream.Null, output, error, CancellationToken.None));

        Assert.Same(original, failure.InnerException);
        Assert.Empty(runner.Invocations);
        Assert.Equal(0, workspaces.Invocations);
        Assert.Empty(ReadUtf8(output));
        var diagnostic = ReadUtf8(error);
        Assert.Contains("terminated abnormally", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(executablePath, diagnostic, StringComparison.Ordinal);
        Assert.Contains("capabilities", diagnostic, StringComparison.Ordinal);
        Assert.Contains("0x80131506", diagnostic, StringComparison.Ordinal);
        Assert.Contains("attempt 2", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("incompatible", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingRetryProbe(Exception failure) : IVbaDevCapabilitiesProbe
    {
        public Task<VbaDevCapabilitiesProbeResult> ProbeAsync(
            string vbaDevPath,
            CancellationToken cancellationToken)
            => Task.FromException<VbaDevCapabilitiesProbeResult>(failure);
    }

    private sealed class CapabilityRejectedWorkspaceManager : IVbaDebugSessionWorkspaceManager
    {
        public int Invocations { get; private set; }

        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(DebugSessionId sessionId, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("Rejected capabilities must not claim a workspace.");
        }

        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(DebugSessionId sessionId, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("Rejected capabilities must not clean a workspace.");
        }

        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(DebugSessionId excludedSessionId, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("Rejected capabilities must not reap a workspace.");
        }
    }
}
