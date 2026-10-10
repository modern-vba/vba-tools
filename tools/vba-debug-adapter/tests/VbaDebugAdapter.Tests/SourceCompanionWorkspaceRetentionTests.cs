using System.Text.Json;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class SourceCompanionWorkspaceRetentionTests
{
    [Fact]
    public async Task CleanupAndReaperNeverDeleteASessionWithAnUnreadableRetentionMarker()
    {
        using var temp = TempDirectory.Create();
        const string session = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var sessionPath = Path.Combine(workspaceRoot, "workspaces", session);
        Directory.CreateDirectory(sessionPath);
        await File.WriteAllTextAsync(Path.Combine(sessionPath, "lease.json"),
            $$"""{"schemaVersion":1,"sessionId":"{{session}}","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}""");
        var markerPath = Path.Combine(sessionPath, "source-companion-retention.json");
        await File.WriteAllTextAsync(markerPath, "{invalid-json");
        var sentinelPath = Path.Combine(sessionPath, "retained.txt");
        await File.WriteAllTextAsync(sentinelPath, "retain complete session");
        var manager = new VbaDebugSessionWorkspaceManager(workspaceRoot);

        var cleanup = await manager.CleanupAsync(DebugSessionId.Parse(session), CancellationToken.None);
        var reaped = await manager.ReapStaleAsync(
            DebugSessionId.Parse("fedcba9876543210fedcba9876543210"), CancellationToken.None);

        Assert.False(cleanup.Succeeded);
        Assert.Equal(Path.GetFullPath(sessionPath), cleanup.RetainedPath);
        Assert.Contains("companion", cleanup.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(sessionPath), Assert.Single(reaped).RetainedPath);
        Assert.Equal("retain complete session", await File.ReadAllTextAsync(sentinelPath));
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task ProvedProcessAndHandleReleaseRemovesOnlyPinnedMarkerThenAllowsNormalCleanup()
    {
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var generation = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "Book.xlsm");
        var generationWorkspacePath = generation.GenerationWorkspacePath;
        await generation.DisposeAsync();
        var retention = Assert.IsAssignableFrom<IVbaDebugSessionWorkspaceRetention>(lease);
        var markerPath = Path.Combine(lease.SessionWorkspacePath, "source-companion-retention.json");

        try
        {
            retention.ArmForUnprovedCompanion(DebugGenerationId.Initial,
                generationWorkspacePath, "Companion release is not yet proved.");
            var completion = new DebugFailureCompletion();
            completion.AddEvidence(new("child-exit", "vba-dev", DebugResourceKind.Process,
                true, "The exact child exit was observed."));
            completion.AddEvidence(new("child-handles", "vba-dev", DebugResourceKind.Handle,
                true, "The exact child handles were released."));

            Assert.True(retention.ConfirmCompanionRelease(DebugGenerationId.Initial,
                completion.Complete()));
            Assert.False(File.Exists(markerPath));
            await lease.DisposeAsync();
            Assert.False(Directory.Exists(lease.SessionWorkspacePath));
        }
        finally
        {
            try { await lease.DisposeAsync(); }
            catch { /* Failed proof retains the test-owned workspace for this assertion. */ }
        }
    }

    [Fact]
    public async Task ArmCreatesClosedPhysicalMarkerBeforeCompanionStarts()
    {
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var generation = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "Book.xlsm");
        var generationWorkspacePath = generation.GenerationWorkspacePath;
        await generation.DisposeAsync();
        var retention = Assert.IsAssignableFrom<IVbaDebugSessionWorkspaceRetention>(lease);
        var markerPath = Path.Combine(lease.SessionWorkspacePath, "source-companion-retention.json");

        try
        {
            retention.ArmForUnprovedCompanion(DebugGenerationId.Initial,
                generationWorkspacePath, "Companion release is not yet proved.");

            Assert.True(File.Exists(markerPath));
            await Assert.ThrowsAnyAsync<Exception>(() => lease.DisposeAsync().AsTask());
            Assert.True(File.Exists(markerPath));
            Assert.True(Directory.Exists(lease.SessionWorkspacePath));
            using (var marker = JsonDocument.Parse(File.ReadAllText(markerPath)))
            {
                var root = marker.RootElement;
                Assert.Equal(
                    ["schemaVersion", "sessionId", "generationId", "retainedPath", "reason"],
                    root.EnumerateObject().Select(property => property.Name));
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(0, root.GetProperty("generationId").GetInt32());
                Assert.Equal(generationWorkspacePath,
                    root.GetProperty("retainedPath").GetString());
            }

        }
        finally
        {
            try { await lease.DisposeAsync(); }
            catch { /* The retained workspace is this test's expected failure evidence. */ }
        }
    }
}
