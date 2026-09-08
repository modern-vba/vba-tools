using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDebugAdapterCommandLineFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StdioFailureRetainsItsCauseLeaseCleanupAndDiagnosticFailure(bool cancelled, bool failDiagnostic)
    {
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancelled
            ? new OperationCanceledException("Original runner cancellation.", cancellation.Token)
            : new IOException("Original runner failure.");
        var deletion = new IOException("Owned lease deletion failed.");
        var diagnostic = new IOException("Stderr writing failed.");
        var lease = new Lease(deletion);
        var commandLine = VbaDebugAdapterCommandLine.Create(new Runner(primary), new Capabilities(), new Manager(lease));
        using var standardError = failDiagnostic ? new FailedOutput(diagnostic) : new MemoryStream();

        var failure = await Record.ExceptionAsync(() => commandLine.InvokeAsync(
            ["--stdio", "--vba-dev", Path.GetFullPath("vba-dev.exe"), "--session", lease.SessionId.Value],
            Stream.Null, Stream.Null, standardError, CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Same(primary, outcome.PrimaryFailure);
        Assert.Contains(nameof(Runner.RunAsync), outcome.PrimaryFailure!.StackTrace!);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, deletion)
            && item.RetainedPath == lease.SessionWorkspacePath);
        Assert.False(failure is OperationCanceledException);
        Assert.Equal(1, lease.DisposalCalls);
        if (failDiagnostic)
        {
            Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, diagnostic));
            Assert.Equal(1, Assert.IsType<FailedOutput>(standardError).Writes);
        }
    }

    private sealed class Runner(Exception failure) : IVbaDebugAdapterStdioRunner
    {
        public Task<int> RunAsync(string vbaDevPath, IVbaDebugSessionWorkspaceLease workspaceLease,
            Stream standardInput, Stream standardOutput, Stream standardError, CancellationToken cancellationToken)
            => throw failure;
    }

    private sealed class Capabilities : IVbaDevCapabilitiesProbe
    {
        public Task<VbaDevCapabilitiesProbeResult> ProbeAsync(string vbaDevPath, CancellationToken cancellationToken)
            => Task.FromResult(new VbaDevCapabilitiesProbeResult(0,
                "{\"featureVersions\":{\"build.sourceSnapshot\":\"2.0\"}}", string.Empty));
    }

    private sealed class Manager(Lease lease) : IVbaDebugSessionWorkspaceManager
    {
        public ValueTask<IVbaDebugSessionWorkspaceLease> ClaimAsync(DebugSessionId sessionId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IVbaDebugSessionWorkspaceLease>(lease);
        public ValueTask<VbaDebugSessionCleanupResult> CleanupAsync(DebugSessionId sessionId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The CLI owns the claimed lease directly.");
        public ValueTask<IReadOnlyList<VbaDebugSessionCleanupResult>> ReapStaleAsync(
            DebugSessionId excludedSessionId, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<VbaDebugSessionCleanupResult>>([]);
    }

    private sealed class Lease(Exception failure) : IVbaDebugSessionWorkspaceLease, IDebugResourceOwnerEvidence
    {
        public DebugSessionId SessionId { get; } = DebugSessionId.Parse("0123456789abcdef0123456789abcdef");
        public string SessionWorkspacePath { get; } = Path.GetFullPath("retained-session");
        public int DisposalCalls { get; private set; }
        public DebugFailureOutcome? CleanupOutcome { get; private set; }
        public IVbaDebugGenerationWorkspace CreateGenerationWorkspace(DebugGenerationId generationId, string workbookFileName)
            => throw new InvalidOperationException("The failing runner does not create a generation.");
        public ValueTask DisposeAsync()
        {
            DisposalCalls++;
            var completion = new DebugFailureCompletion();
            completion.AddEvidence(new("lease-handle-release", "session lease handle", DebugResourceKind.Handle,
                true, "The fake native owner proved handle release."));
            completion.AddFailure("lease-delete", "session lease directory", DebugResourceKind.FileSystem,
                failure, retainedPath: SessionWorkspacePath);
            completion.AddEvidence(new("lease-delete", "session lease directory", DebugResourceKind.FileSystem,
                false, "The retained lease directory could not be deleted.", RetainedPath: SessionWorkspacePath));
            CleanupOutcome = completion.Complete();
            return ValueTask.FromException(new DebugFailureException(CleanupOutcome));
        }
    }

    private sealed class FailedOutput(Exception failure) : MemoryStream
    {
        public int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.FromException(failure);
        }
    }
}
