using System.Diagnostics;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDebugSessionWorkspaceManagerTests
{
    [Fact]
    public async Task ClaimRefusesAnExistingUnverifiedWorkspaceWithoutDeletingIt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var retainedPath = Path.Combine(sessionWorkspacePath, "retained.tmp");
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(retainedPath, "unverified");
        var manager = new VbaDebugSessionWorkspaceManager(root);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ClaimAsync(sessionId, CancellationToken.None).AsTask());

            Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(retainedPath));
            Assert.False(File.Exists(Path.Combine(sessionWorkspacePath, "lease.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClaimCanonicalizesAnAliasedWorkspaceRootBeforeCreatingTheLease()
    {
        var physicalRoot = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        var aliasParent = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-alias-tests",
            Guid.NewGuid().ToString("N"));
        var aliasedRoot = Path.Combine(aliasParent, "workspace-link");
        const string sessionId = "0123456789abcdef0123456789abcdef";
        Directory.CreateDirectory(physicalRoot);
        Directory.CreateDirectory(aliasParent);
        Directory.CreateSymbolicLink(aliasedRoot, physicalRoot);
        var manager = new VbaDebugSessionWorkspaceManager(aliasedRoot);

        try
        {
            await using var lease = await manager.ClaimAsync(
                sessionId,
                CancellationToken.None);

            Assert.Equal(
                Path.Combine(physicalRoot, "workspaces", sessionId),
                lease.SessionWorkspacePath,
                ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(
                physicalRoot,
                "workspaces",
                sessionId,
                "lease.json")));
        }
        finally
        {
            DeleteDirectoryLinkIfPresent(aliasedRoot);
            if (Directory.Exists(aliasParent))
            {
                Directory.Delete(aliasParent, recursive: true);
            }
            if (Directory.Exists(physicalRoot))
            {
                Directory.Delete(physicalRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ClaimDoesNotFollowADanglingLeaseLinkOutsideTheSessionTree()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var root = Path.Combine(temp.Path, "adapter-root");
        var outsideLeasePath = Path.Combine(temp.Path, "outside-lease.json");
        var manager = new VbaDebugSessionWorkspaceManager(
            root,
            cleanupOperations: null,
            beforeCreateLeaseFile: leasePath =>
                File.CreateSymbolicLink(leasePath, outsideLeasePath));

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var lease = await manager.ClaimAsync(
                sessionId,
                CancellationToken.None);
        });

        Assert.NotNull(exception);
        Assert.False(File.Exists(outsideLeasePath));
    }

    [Fact]
    public async Task ClaimPinsTheCreateNewSessionIdentityBeforeItCanBeReplaced()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var root = Path.Combine(temp.Path, "adapter-root");
        var displacedPath = Path.Combine(temp.Path, "displaced-session");
        var replacementBlocked = false;
        var manager = new VbaDebugSessionWorkspaceManager(
            root,
            cleanupOperations: null,
            afterCreateDirectoryBeforeOpen: sessionPath =>
            {
                if (!sessionPath.EndsWith(sessionId, StringComparison.Ordinal))
                {
                    return;
                }
                try
                {
                    Directory.Move(sessionPath, displacedPath);
                    Directory.CreateDirectory(sessionPath);
                }
                catch (IOException)
                {
                    replacementBlocked = true;
                }
            });

        await using (var lease = await manager.ClaimAsync(
                         sessionId,
                         CancellationToken.None))
        {
            Assert.True(replacementBlocked);
            Assert.False(Directory.Exists(displacedPath));
            Assert.True(File.Exists(Path.Combine(
                lease.SessionWorkspacePath,
                "lease.json")));
        }
    }

    [Fact]
    public async Task CleanupRetainsAWorkspaceOwnedByTheExactLiveProcessIdentity()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var manager = new VbaDebugSessionWorkspaceManager(root);

        try
        {
            await using (var lease = await manager.ClaimAsync(
                sessionId,
                CancellationToken.None))
            {
                var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

                Assert.False(result.Succeeded);
                Assert.Equal(Path.GetFullPath(sessionWorkspacePath), result.RetainedPath);
                Assert.Contains("active", result.Message, StringComparison.OrdinalIgnoreCase);
                Assert.True(Directory.Exists(sessionWorkspacePath));
            }

            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupDeletesAWorkspaceWhoseLeasedProcessNoLongerExists()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"0123456789abcdef0123456789abcdef","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "retained.tmp"),
            "stale");
        var manager = new VbaDebugSessionWorkspaceManager(root);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.RetainedPath);
            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupTreatsAMismatchedStartTimeAsAReusedProcessId()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        using var process = Process.GetCurrentProcess();
        var mismatchedStartTime = process.StartTime
            .ToUniversalTime()
            .AddSeconds(1)
            .ToString("O");
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            $$"""
            {"schemaVersion":1,"sessionId":"{{sessionId}}","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":{{process.Id}},"processStartTimeUtc":"{{mismatchedStartTime}}"}
            """);
        var manager = new VbaDebugSessionWorkspaceManager(root);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupRetainsAStaleLeaseAcrossAnUnprovedReparseBoundary()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"0123456789abcdef0123456789abcdef","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        var cleanupOperations = new ControlledWorkspaceCleanupOperations(
            transientFailures: 0,
            reparsePointPath: sessionWorkspacePath);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(Path.GetFullPath(sessionWorkspacePath), result.RetainedPath);
            Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, cleanupOperations.DeleteCalls);
            Assert.True(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupAndReapingRetainADanglingCanonicalSessionLink()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string excludedSessionId = "fedcba9876543210fedcba9876543210";
        var root = Path.Combine(temp.Path, "adapter-root");
        var workspacesPath = Path.Combine(root, "workspaces");
        var sessionWorkspacePath = Path.Combine(workspacesPath, sessionId);
        var missingTargetPath = Path.Combine(temp.Path, "missing-target");
        Directory.CreateDirectory(workspacesPath);
        File.CreateSymbolicLink(sessionWorkspacePath, missingTargetPath);
        var manager = new VbaDebugSessionWorkspaceManager(root);

        var cleanup = await manager.CleanupAsync(sessionId, CancellationToken.None);
        var retained = await manager.ReapStaleAsync(
            excludedSessionId,
            CancellationToken.None);

        Assert.False(cleanup.Succeeded);
        Assert.Equal(Path.GetFullPath(sessionWorkspacePath), cleanup.RetainedPath);
        Assert.Contains("verified", cleanup.Message, StringComparison.OrdinalIgnoreCase);
        var reaped = Assert.Single(retained);
        Assert.False(reaped.Succeeded);
        Assert.Equal(Path.GetFullPath(sessionWorkspacePath), reaped.RetainedPath);
        Assert.True((File.GetAttributes(sessionWorkspacePath) &
                     FileAttributes.ReparsePoint) != 0);
    }

    [Fact]
    public async Task CleanupCannotFollowWorkspacesReplacedAfterValidation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-outside-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspacesPath = Path.Combine(root, "workspaces");
        var retainedWorkspacesPath = Path.Combine(root, "workspaces-retained");
        var sessionWorkspacePath = Path.Combine(workspacesPath, sessionId);
        var outsideSessionPath = Path.Combine(outsideRoot, sessionId);
        var outsideSentinelPath = Path.Combine(outsideSessionPath, "outside.tmp");
        Directory.CreateDirectory(sessionWorkspacePath);
        Directory.CreateDirectory(outsideSessionPath);
        await WriteStaleLeaseAsync(sessionWorkspacePath, sessionId);
        await File.WriteAllTextAsync(outsideSentinelPath, "outside");
        var swapped = false;
        var cleanupOperations = new SystemVbaDebugWorkspaceCleanupOperations(
            root,
            beforeOpenScope: () =>
            {
                if (swapped)
                {
                    return;
                }
                Directory.Move(workspacesPath, retainedWorkspacesPath);
                Directory.CreateSymbolicLink(workspacesPath, outsideRoot);
                swapped = true;
            });
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(swapped);
            Assert.False(result.Succeeded);
            Assert.Equal(Path.GetFullPath(sessionWorkspacePath), result.RetainedPath);
            Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(outsideSentinelPath));
            Assert.True(Directory.Exists(Path.Combine(retainedWorkspacesPath, sessionId)));
        }
        finally
        {
            DeleteDirectoryLinkIfPresent(workspacesPath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupPinsTheStaleSessionIdentityThroughDeletion()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var displacedPath = sessionWorkspacePath + "-displaced";
        Directory.CreateDirectory(sessionWorkspacePath);
        await WriteStaleLeaseAsync(sessionWorkspacePath, sessionId);
        var replacementBlocked = false;
        var cleanupOperations = new SystemVbaDebugWorkspaceCleanupOperations(
            root,
            beforeDelete: path =>
            {
                Assert.Equal(Path.GetFullPath(sessionWorkspacePath), Path.GetFullPath(path));
                try
                {
                    Directory.Move(sessionWorkspacePath, displacedPath);
                }
                catch (IOException)
                {
                    replacementBlocked = true;
                }
            });
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.True(replacementBlocked);
            Assert.False(Directory.Exists(sessionWorkspacePath));
            Assert.False(Directory.Exists(displacedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OwnedLeaseCleanupKeepsItsSessionIdentityPinnedThroughDeletion()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var root = Path.Combine(temp.Path, "adapter-root");
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var displacedPath = Path.Combine(temp.Path, "displaced-session");
        var replacementBlocked = false;
        void AttemptReplacement()
        {
            try
            {
                Directory.Move(sessionWorkspacePath, displacedPath);
                Directory.CreateDirectory(sessionWorkspacePath);
                File.WriteAllText(
                    Path.Combine(sessionWorkspacePath, "replacement.tmp"),
                    "replacement");
            }
            catch (IOException)
            {
                replacementBlocked = true;
            }
        }
        var cleanupOperations = new SystemVbaDebugWorkspaceCleanupOperations(
            root,
            beforeOpenScope: AttemptReplacement);
        var manager = new VbaDebugSessionWorkspaceManager(
            root,
            cleanupOperations,
            beforeDeleteOwnedTree: _ => AttemptReplacement());

        var lease = await manager.ClaimAsync(sessionId, CancellationToken.None);
        await lease.DisposeAsync();

        Assert.True(replacementBlocked);
        Assert.False(Directory.Exists(displacedPath));
        Assert.False(Directory.Exists(sessionWorkspacePath));
    }

    [Fact]
    public async Task CleanupDeletesAReplacedChildLinkWithoutFollowingItsTarget()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-outside-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        var childPath = Path.Combine(sessionWorkspacePath, "generation");
        var outsideSentinelPath = Path.Combine(outsideRoot, "outside.tmp");
        Directory.CreateDirectory(childPath);
        Directory.CreateDirectory(outsideRoot);
        await WriteStaleLeaseAsync(sessionWorkspacePath, sessionId);
        await File.WriteAllTextAsync(Path.Combine(childPath, "inside.tmp"), "inside");
        await File.WriteAllTextAsync(outsideSentinelPath, "outside");
        var childSwapped = false;
        var cleanupOperations = new SystemVbaDebugWorkspaceCleanupOperations(
            root,
            beforeOpenEntry: path =>
            {
                if (childSwapped ||
                    !Path.GetFullPath(path).Equals(
                        Path.GetFullPath(childPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                Directory.Delete(childPath, recursive: true);
                Directory.CreateSymbolicLink(childPath, outsideRoot);
                childSwapped = true;
            });
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.True(childSwapped);
            Assert.True(File.Exists(outsideSentinelPath));
            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupRetriesTransientDeletionFailuresWithinFiveSeconds()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"0123456789abcdef0123456789abcdef","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        var cleanupOperations = new ControlledWorkspaceCleanupOperations(
            transientFailures: 2);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(3, cleanupOperations.DeleteCalls);
            Assert.Equal(TimeSpan.FromMilliseconds(200), cleanupOperations.Elapsed);
            Assert.False(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CleanupReportsOnlyTheScopedRetainedPathAfterFiveSeconds()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            """
            {"schemaVersion":1,"sessionId":"0123456789abcdef0123456789abcdef","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);
        var cleanupOperations = new ControlledWorkspaceCleanupOperations(
            transientFailures: int.MaxValue);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(TimeSpan.FromSeconds(5), cleanupOperations.Elapsed);
            Assert.Equal(Path.GetFullPath(sessionWorkspacePath), result.RetainedPath);
            Assert.Contains("five seconds", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-json")]
    [InlineData("{\"schemaVersion\":2,\"sessionId\":\"0123456789abcdef0123456789abcdef\",\"leaseId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"processId\":1,\"processStartTimeUtc\":\"2020-01-02T03:04:05.0000000Z\"}")]
    [InlineData("{\"schemaVersion\":1,\"sessionId\":\"fedcba9876543210fedcba9876543210\",\"leaseId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"processId\":1,\"processStartTimeUtc\":\"2020-01-02T03:04:05.0000000Z\"}")]
    public async Task CleanupRetainsAWorkspaceWithoutProvableLeaseIdentity(string? leaseJson)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-workspace-tests",
            Guid.NewGuid().ToString("N"));
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sessionWorkspacePath = Path.Combine(root, "workspaces", sessionId);
        Directory.CreateDirectory(sessionWorkspacePath);
        if (leaseJson is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(sessionWorkspacePath, "lease.json"),
                leaseJson);
        }
        var manager = new VbaDebugSessionWorkspaceManager(root);

        try
        {
            var result = await manager.CleanupAsync(sessionId, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(Path.GetFullPath(sessionWorkspacePath), result.RetainedPath);
            Assert.Contains("verified", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(sessionWorkspacePath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task WriteStaleLeaseAsync(string sessionWorkspacePath, string sessionId)
        => File.WriteAllTextAsync(
            Path.Combine(sessionWorkspacePath, "lease.json"),
            $$"""
            {"schemaVersion":1,"sessionId":"{{sessionId}}","leaseId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","processId":2147483647,"processStartTimeUtc":"2020-01-02T03:04:05.0000000Z"}
            """);

    private static void DeleteDirectoryLinkIfPresent(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
        }
    }

    private sealed class ControlledWorkspaceCleanupOperations(
        int transientFailures,
        string? reparsePointPath = null)
        : IVbaDebugWorkspaceCleanupOperations
    {
        private long elapsedTicks;

        public int DeleteCalls { get; private set; }

        public TimeSpan Elapsed => TimeSpan.FromTicks(elapsedTicks);

        public long GetTimestamp() => elapsedTicks;

        public TimeSpan GetElapsedTime(long startingTimestamp)
            => TimeSpan.FromTicks(elapsedTicks - startingTimestamp);

        public bool IsReparsePoint(string directoryPath)
            => reparsePointPath is not null
                && Path.GetFullPath(directoryPath).Equals(
                    Path.GetFullPath(reparsePointPath),
                    StringComparison.OrdinalIgnoreCase);

        public Stream OpenLeaseStream(string sessionWorkspacePath)
            => new FileStream(
                Path.Combine(sessionWorkspacePath, "lease.json"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

        public IVbaDebugWorkspaceCleanupScope OpenCleanupScope(
            string sessionWorkspacePath)
            => new ControlledWorkspaceCleanupScope(this, sessionWorkspacePath);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            elapsedTicks += delay.Ticks;
            return ValueTask.CompletedTask;
        }

        public void DeleteDirectory(string directoryPath)
        {
            DeleteCalls++;
            if (DeleteCalls <= transientFailures)
            {
                throw new IOException("Synthetic transient workspace lock.");
            }
            Directory.Delete(directoryPath, recursive: true);
        }

        private sealed class ControlledWorkspaceCleanupScope(
            ControlledWorkspaceCleanupOperations owner,
            string sessionWorkspacePath)
            : IVbaDebugWorkspaceCleanupScope
        {
            public Stream OpenLeaseStream()
                => new FileStream(
                    Path.Combine(sessionWorkspacePath, "lease.json"),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

            public void DeleteDirectory()
                => owner.DeleteDirectory(sessionWorkspacePath);

            public void Dispose()
            {
            }
        }
    }
}
