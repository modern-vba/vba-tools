using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Fact]
    public async Task UniqueReorderedCapabilityOffersKeepOnlyTheSnapshotRequirementsAndPinnedExecutable()
    {
        using var temp = TempDirectory.Create();
        var runner = new RecordingStdioRunner();
        var probe = new RecordingVbaDevCapabilitiesProbe(new(0,
            """
            {"future":[{"value":1},{"value":2}],"featureVersions":{"build.sourceSnapshotAnalysis":"1.0","future":"99.0","build.sourceSnapshot":"2.0"},"contractVersion":"99.0","commands":{"future command":{"outputSchemaVersion":"99.0"}}}
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
            {"featureVersions":{"build.sourceSnapshot":"2.0","build.sourceSnapshotAnalysis":"1.0"},"future":{"nested":[{"value":1,"value":1}]}}
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
