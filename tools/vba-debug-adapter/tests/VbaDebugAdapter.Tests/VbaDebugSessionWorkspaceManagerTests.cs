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
                manager.ClaimAsync(
                    DebugSessionId.Parse(sessionId),
                    CancellationToken.None).AsTask());

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
                DebugSessionId.Parse(sessionId),
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
                DebugSessionId.Parse(sessionId),
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
                         DebugSessionId.Parse(sessionId),
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
    public async Task DisposedLeaseCannotRecreateAGenerationWorkspace()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        var sessionWorkspacePath = lease.SessionWorkspacePath;
        var generationWorkspacePath = Path.Combine(
            sessionWorkspacePath,
            "generations",
            "generation-0000000000");

        await using (var generationWorkspace = lease.CreateGenerationWorkspace(
                         DebugGenerationId.Initial,
                         "Book1.xlsm"))
        {
            Assert.Equal(
                Path.GetFullPath(generationWorkspacePath),
                generationWorkspace.GenerationWorkspacePath,
                ignoreCase: true);
        }
        await lease.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() =>
            lease.CreateGenerationWorkspace(
                DebugGenerationId.Initial,
                "Book1.xlsm"));
        Assert.False(Directory.Exists(sessionWorkspacePath));
        Assert.False(Directory.Exists(generationWorkspacePath));
    }

    [Fact]
    public async Task LiveLeaseGenerationClaimsAreCreateNewAndPreserveTheExistingOwner()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        await using var generationWorkspace = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        var sentinelPath = Path.Combine(
            generationWorkspace.GenerationWorkspacePath,
            "owned-by-first-claim.tmp");
        await File.WriteAllTextAsync(sentinelPath, "first-owner");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            lease.CreateGenerationWorkspace(
                DebugGenerationId.Initial,
                "Book1.xlsm"));

        Assert.Contains(
            "already exists",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first-owner", await File.ReadAllTextAsync(sentinelPath));

        await generationWorkspace.DisposeAsync();
        Assert.False(File.Exists(sentinelPath));
        var reuseException = Assert.Throws<InvalidOperationException>(() =>
            lease.CreateGenerationWorkspace(
                DebugGenerationId.Initial,
                "Book1.xlsm"));
        Assert.Contains(
            "already exists",
            reuseException.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SealedGenerationCapabilityRejectsSourceContentMutation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        await using var generationWorkspace = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        var sourcePath = Path.Combine(
            generationWorkspace.SourceSnapshotPath,
            "Module1.bas");
        await using (var sourceStream =
                     generationWorkspace.CreateSourceFile("Module1.bas"))
        {
            await sourceStream.WriteAsync("source"u8.ToArray());
        }

        generationWorkspace.SealSourceSnapshot();
        generationWorkspace.VerifySourceSnapshot();
        var exception = Record.Exception(() =>
        {
            using var mutationStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            mutationStream.Write("mutated"u8);
        });

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected sealed source mutation to fail, but received: {exception}");
        generationWorkspace.VerifySourceSnapshot();
        Assert.Equal("source", await File.ReadAllTextAsync(sourcePath));

        await generationWorkspace.DisposeAsync();

        Assert.False(File.Exists(sourcePath));
        Assert.False(Directory.Exists(generationWorkspace.GenerationWorkspacePath));
    }

    [Fact]
    public async Task SourceCreationFailureBeforeOwnershipTransferReleasesCreatedHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"),
            cleanupOperations: null,
            afterCreateSourceFileBeforeOwnershipTransfer: _ =>
                throw new IOException("Synthetic identity-read failure."));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        var generationWorkspace = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        var generationPath = generationWorkspace.GenerationWorkspacePath;

        var exception = Assert.Throws<IOException>(() =>
        {
            using var source = generationWorkspace.CreateSourceFile(
                "Module1.bas");
        });
        Assert.Equal("Synthetic identity-read failure.", exception.Message);

        await generationWorkspace.DisposeAsync();

        Assert.False(Directory.Exists(generationPath));
    }

    [Fact]
    public async Task SealedGenerationCapabilityRejectsPersistentSourceInventoryInjection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        await using var generationWorkspace = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        await using (var sourceStream =
                     generationWorkspace.CreateSourceFile("nested/Module1.bas"))
        {
            await sourceStream.WriteAsync("source"u8.ToArray());
        }
        generationWorkspace.SealSourceSnapshot();
        string[] injectedPaths =
        [
            Path.Combine(
                generationWorkspace.SourceSnapshotPath,
                "Injected.bas"),
            Path.Combine(
                generationWorkspace.SourceSnapshotPath,
                "nested",
                "Injected.cls")
        ];

        foreach (var injectedPath in injectedPaths)
        {
            File.WriteAllText(injectedPath, "injected");
        }

        var exception = Assert.Throws<IOException>(
            generationWorkspace.VerifySourceSnapshot);
        Assert.Contains("inventory changed", exception.Message);
    }

    [Fact]
    public async Task PinnedGenerationWorkbookRejectsConcurrentWriteAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        await using var generationWorkspace = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        await File.WriteAllTextAsync(
            generationWorkspace.WorkbookPath,
            "workbook");

        generationWorkspace.PinGeneratedWorkbook();
        generationWorkspace.VerifyGeneratedWorkbook();

        Assert.Throws<InvalidOperationException>(() =>
            generationWorkspace.PinGeneratedWorkbook());
        var exception = Record.Exception(() =>
        {
            using var mutationStream = new FileStream(
                generationWorkspace.WorkbookPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            mutationStream.Write("mutated"u8);
        });

        Assert.True(
            exception is IOException or UnauthorizedAccessException,
            $"Expected pinned workbook mutation to fail, but received: {exception}");
        generationWorkspace.VerifyGeneratedWorkbook();
        Assert.Equal(
            "workbook",
            await File.ReadAllTextAsync(generationWorkspace.WorkbookPath));

        await generationWorkspace.DisposeAsync();

        Assert.False(File.Exists(generationWorkspace.WorkbookPath));
        Assert.False(Directory.Exists(generationWorkspace.GenerationWorkspacePath));
    }

    [Fact]
    public async Task PinGeneratedWorkbookRejectsAReparsePointWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "adapter-root"));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        await using (var generationWorkspace = lease.CreateGenerationWorkspace(
                         DebugGenerationId.Initial,
                         "Book1.xlsm"))
        {
            var outsidePath = Path.Combine(temp.Path, "outside-Book1.xlsm");
            await File.WriteAllTextAsync(outsidePath, "outside");
            File.CreateSymbolicLink(
                generationWorkspace.WorkbookPath,
                outsidePath);

            var exception = Assert.Throws<IOException>(() =>
                generationWorkspace.PinGeneratedWorkbook());

            Assert.Contains(
                "physical file",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                (File.GetAttributes(generationWorkspace.WorkbookPath) &
                 FileAttributes.ReparsePoint) != 0);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath));
        }

        Assert.Equal(
            "outside",
            await File.ReadAllTextAsync(Path.Combine(
                temp.Path,
                "outside-Book1.xlsm")));
    }

    [Fact]
    public async Task LiveLeaseRejectsAPreexistingGenerationLinkWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var root = Path.Combine(temp.Path, "adapter-root");
        var outsideGenerationPath = Path.Combine(temp.Path, "outside-generation");
        var outsideSentinelPath = Path.Combine(
            outsideGenerationPath,
            "outside.tmp");
        Directory.CreateDirectory(outsideGenerationPath);
        await File.WriteAllTextAsync(outsideSentinelPath, "outside");
        var manager = new VbaDebugSessionWorkspaceManager(root);

        await using (var lease = await manager.ClaimAsync(
                         DebugSessionId.Parse(sessionId),
                         CancellationToken.None))
        {
            var generationsPath = Path.Combine(
                lease.SessionWorkspacePath,
                "generations");
            var generationWorkspacePath = Path.Combine(
                generationsPath,
                "generation-0000000000");
            Directory.CreateDirectory(generationsPath);
            Directory.CreateSymbolicLink(
                generationWorkspacePath,
                outsideGenerationPath);

            Assert.Throws<InvalidOperationException>(() =>
                lease.CreateGenerationWorkspace(
                    DebugGenerationId.Initial,
                    "Book1.xlsm"));
            Assert.True(
                (File.GetAttributes(generationWorkspacePath) &
                 FileAttributes.ReparsePoint) != 0);
            Assert.Equal(
                "outside",
                await File.ReadAllTextAsync(outsideSentinelPath));
        }

        Assert.Equal(
            "outside",
            await File.ReadAllTextAsync(outsideSentinelPath));
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
                DebugSessionId.Parse(sessionId),
                CancellationToken.None))
            {
                var result = await manager.CleanupAsync(
                    DebugSessionId.Parse(sessionId),
                    CancellationToken.None);

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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            root,
            transientFailures: 0,
            reparsePointPath: sessionWorkspacePath);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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

        var cleanup = await manager.CleanupAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
        var retained = await manager.ReapStaleAsync(
            DebugSessionId.Parse(excludedSessionId),
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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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

        var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);
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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            root,
            transientFailures: 2);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            root,
            transientFailures: int.MaxValue);
        var manager = new VbaDebugSessionWorkspaceManager(root, cleanupOperations);

        try
        {
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
            var result = await manager.CleanupAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);

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
        string workspaceRoot,
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

        public Stream OpenSessionLeaseStream(DebugSessionId sessionId)
            => new FileStream(
                Path.Combine(
                    workspaceRoot,
                    "workspaces",
                    sessionId.Value,
                    "lease.json"),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

        public IVbaDebugWorkspaceCleanupScope OpenSessionCleanupScope(
            DebugSessionId sessionId)
            => new ControlledWorkspaceCleanupScope(
                this,
                Path.Combine(workspaceRoot, "workspaces", sessionId.Value));

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            elapsedTicks += delay.Ticks;
            return ValueTask.CompletedTask;
        }

        private void DeleteSessionDirectory(string directoryPath)
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
                => owner.DeleteSessionDirectory(sessionWorkspacePath);

            public void Dispose()
            {
            }
        }
    }
}
