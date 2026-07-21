using VbaDev.App.Debugging;
using VbaDev.App.Diagnostics;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugEnvironmentDiagnosticTests
{
    [Fact]
    public void ReportsCapabilityProcessOwnershipProbeStagesAndCleanup()
    {
        var session = new FakeDebugEnvironmentProbeSession(
            processId: 4242,
            strongProcessOwnershipEstablished: true,
            [
                DiagnosticResult.Pass("VBIDE project access", "Trusted access is available."),
                DiagnosticResult.Pass(
                    "Native Toggle Breakpoint command (ID 51)",
                    "The command entered break mode."),
                DiagnosticResult.Pass(
                    "Native Run or Continue command (ID 186)",
                    "The command continued to completion.")
            ]);
        var diagnostic = new DebugEnvironmentDiagnostic(
            new FakeDebugEnvironmentProbeFactory(session),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.All(results, result => Assert.Equal(DiagnosticStatus.Pass, result.Status));
        Assert.Contains(results, result =>
            result.Name == "VBA debug capability contract" &&
            result.Message.Contains(VbaDebugCapabilityContract.ProtocolVersion, StringComparison.Ordinal));
        Assert.Contains(results, result =>
            result.Name == "Owned Excel process" &&
            result.Message.Contains("4242", StringComparison.Ordinal));
        Assert.Contains(results, result => result.Name == "Windows Job ownership");
        Assert.Contains(results, result => result.Name == "VBIDE project access");
        Assert.Equal("Temporary debug probe cleanup", results[^1].Name);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void UnsupportedOperatingSystemFailsWithoutStartingExcel()
    {
        var factory = new FakeDebugEnvironmentProbeFactory(
            new FakeDebugEnvironmentProbeSession(1, true, []));
        var diagnostic = new DebugEnvironmentDiagnostic(
            factory,
            () => false,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Windows VBE debugging" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, factory.StartCount);
    }

    [Fact]
    public void MissingExcelIsReportedSeparatelyFromUnsupportedWindows()
    {
        var diagnostic = new DebugEnvironmentDiagnostic(
            new ThrowingDebugEnvironmentProbeFactory(
                new DebugEnvironmentProbeStartException(
                    "Excel COM availability",
                    "Microsoft Excel is not registered for COM automation.",
                    cleanupVerified: true)),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Excel COM availability" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results, result =>
            result.Name == "Temporary debug probe cleanup" &&
            result.Status == DiagnosticStatus.Pass);
    }

    [Fact]
    public void ProbeFailureStillDisposesTheOwnedSession()
    {
        var session = new FakeDebugEnvironmentProbeSession(
            processId: 5252,
            strongProcessOwnershipEstablished: true,
            results: [],
            runError: new DebugSetupException(
                "The native VBE Toggle Breakpoint command (ID 51) is disabled."));
        var diagnostic = new DebugEnvironmentDiagnostic(
            new FakeDebugEnvironmentProbeFactory(session),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Native VBE readiness" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Disposed);
        Assert.Equal(DiagnosticStatus.Pass, results[^1].Status);
    }

    [Fact]
    public void CleanupFailureIsASeparateBlockingDiagnostic()
    {
        var session = new FakeDebugEnvironmentProbeSession(
            processId: 6262,
            strongProcessOwnershipEstablished: true,
            results: [],
            disposeError: new DebugSetupException("Synthetic cleanup failure."));
        var diagnostic = new DebugEnvironmentDiagnostic(
            new FakeDebugEnvironmentProbeFactory(session),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Equal("Temporary debug probe cleanup", results[^1].Name);
        Assert.Equal(DiagnosticStatus.Fail, results[^1].Status);
        Assert.Contains("Synthetic cleanup failure", results[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingStrongOwnershipFailsBeforeRunningNativeProbeStages()
    {
        var session = new FakeDebugEnvironmentProbeSession(
            processId: 7171,
            strongProcessOwnershipEstablished: false,
            results:
            [
                DiagnosticResult.Pass(
                    "Native VBE readiness",
                    "This stage must not run without strong ownership.")
            ]);
        var diagnostic = new DebugEnvironmentDiagnostic(
            new FakeDebugEnvironmentProbeFactory(session),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Windows Job ownership" &&
            result.Status == DiagnosticStatus.Fail);
        Assert.Equal(0, session.RunCount);
        Assert.True(session.Disposed);
        Assert.Equal(DiagnosticStatus.Pass, results[^1].Status);
    }

    [Fact]
    public void CategorizedStartupFailurePreservesStageAndCleanupFailure()
    {
        var cleanupError = new IOException("Synthetic start cleanup failure.");
        var diagnostic = new DebugEnvironmentDiagnostic(
            new ThrowingDebugEnvironmentProbeFactory(
                new DebugEnvironmentProbeStartException(
                    "VBIDE project access",
                    "Trust access is disabled.",
                    cleanupException: cleanupError)),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "VBIDE project access" &&
            result.Status == DiagnosticStatus.Fail);
        Assert.Equal("Temporary debug probe cleanup", results[^1].Name);
        Assert.Equal(DiagnosticStatus.Fail, results[^1].Status);
        Assert.Contains("Synthetic start cleanup failure", results[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiedStartupFailureDoesNotReportCleanupAsPassing()
    {
        var diagnostic = new DebugEnvironmentDiagnostic(
            new ThrowingDebugEnvironmentProbeFactory(
                new DebugSetupException("Synthetic uncategorized startup failure.")),
            () => true,
            TimeSpan.FromSeconds(5));

        var results = diagnostic.RunEnvironmentDiagnostics();

        Assert.Contains(results, result =>
            result.Name == "Temporary debug probe cleanup" &&
            result.Status == DiagnosticStatus.Fail &&
            result.Message.Contains("not verified", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeDebugEnvironmentProbeFactory(
        IDebugEnvironmentProbeSession session) : IDebugEnvironmentProbeFactory
    {
        public int StartCount { get; private set; }

        public Task<IDebugEnvironmentProbeSession> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount += 1;
            return Task.FromResult(session);
        }
    }

    private sealed class ThrowingDebugEnvironmentProbeFactory(Exception error)
        : IDebugEnvironmentProbeFactory
    {
        public Task<IDebugEnvironmentProbeSession> StartAsync(CancellationToken cancellationToken)
            => Task.FromException<IDebugEnvironmentProbeSession>(error);
    }

    private sealed class FakeDebugEnvironmentProbeSession(
        int processId,
        bool strongProcessOwnershipEstablished,
        IReadOnlyList<DiagnosticResult> results,
        Exception? runError = null,
        Exception? disposeError = null) : IDebugEnvironmentProbeSession
    {
        public int ProcessId => processId;

        public bool StrongProcessOwnershipEstablished => strongProcessOwnershipEstablished;

        public bool Disposed { get; private set; }

        public int RunCount { get; private set; }

        public Task<IReadOnlyList<DiagnosticResult>> RunAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunCount += 1;
            return runError is null
                ? Task.FromResult(results)
                : Task.FromException<IReadOnlyList<DiagnosticResult>>(runError);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return disposeError is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(disposeError);
        }
    }
}
