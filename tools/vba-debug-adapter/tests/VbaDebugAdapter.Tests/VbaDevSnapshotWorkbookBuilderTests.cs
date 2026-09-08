using VbaDebugAdapter.Build;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDevSnapshotWorkbookBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreInvocationBuildFailureRetainsExplicitNoAcquisitionEvidence(bool generationAlreadyExists)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        await using var lease = await new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "workspace"))
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        if (generationAlreadyExists)
        {
            await File.WriteAllBytesAsync(executable, []);
            await using var existing = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "Book1.xlsm");
            await AssertFailureAsync();
        }
        else { await AssertFailureAsync(); }

        async Task AssertFailureAsync()
        {
            var process = new RecordingBuildProcess();
            var sourceSet = AdmitBuildSources(new TransportedDebugSourceSnapshot(2,
                [new TransportedDebugSource("Module1.bas", "file:///C:/source/Module1.bas", "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes("Attribute VB_Name = \"Module1\"\n")))]));
            var failure = await Record.ExceptionAsync(() => new VbaDevSnapshotWorkbookBuilder(process).BuildAsync(
                executable, lease, new VbaDevSnapshotBuildRequest(projectRoot, "Book1", "Book1.xlsm", sourceSet),
                CancellationToken.None));

            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.NotNull(outcome.PrimaryFailure);
            Assert.False(outcome.HasCleanupFailure);
            Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
            Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Handle && item.Released);
            Assert.Empty(process.Invocations);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulBuildRequiresAndRetainsProcessReleaseEvidence(bool provesProcessCleanup)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        await File.WriteAllBytesAsync(executable, []);
        await using var lease = await new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "workspace"))
            .ClaimAsync(DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var processCompletion = new DebugFailureCompletion();
        processCompletion.AddEvidence(new("build-process-exit", "vba-dev", DebugResourceKind.Process,
            true, "The fake child reached terminal exit.", 42));
        processCompletion.AddEvidence(new("build-process-handles", "vba-dev", DebugResourceKind.Handle,
            true, "The fake owner released its handles.", 42));
        var builder = new VbaDevSnapshotWorkbookBuilder(new RecordingBuildProcess
        {
            CleanupOutcome = provesProcessCleanup ? processCompletion.Complete() : null
        });
        var sourceSet = AdmitBuildSources(new TransportedDebugSourceSnapshot(2,
            [new TransportedDebugSource("Module1.bas", "file:///C:/source/Module1.bas", "utf8bom",
                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                    "Attribute VB_Name = \"Module1\"\nPublic Sub Run()\nEnd Sub\n")))]));
        var build = builder.BuildAsync(executable, lease,
            new VbaDevSnapshotBuildRequest(projectRoot, "Book1", "Book1.xlsm", sourceSet),
            CancellationToken.None);
        if (!provesProcessCleanup)
        {
            var failure = await Record.ExceptionAsync(() => build);
            if (build.IsCompletedSuccessfully)
            {
                await Record.ExceptionAsync(() => build.Result.DisposeAsync().AsTask());
            }
            var failedOutcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
            Assert.True(failedOutcome.HasUnprovedRelease);
            Assert.Contains(failedOutcome.Evidence, item => item.Kind == DebugResourceKind.Process && !item.Released);
            Assert.False(Directory.Exists(Path.Combine(lease.SessionWorkspacePath, "generations", "generation-0000000000")));
            return;
        }
        var result = await build;

        await result.DisposeAsync();

        var outcome = Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(result).CleanupOutcome;
        Assert.NotNull(outcome);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.ProcessId == 42 && item.Released);
        Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.FileSystem && item.Released);
        Assert.False(outcome.HasCleanupFailure);
        await result.DisposeAsync();
        Assert.Same(outcome, Assert.IsAssignableFrom<IDebugResourceOwnerEvidence>(result).CleanupOutcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedBuildClassifiesCleanupFromItsProcessAndWorkspaceOwners(bool provesProcessCleanup)
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectRoot);
        var executable = Path.Combine(temp.Path, "vba-dev.exe");
        await File.WriteAllBytesAsync(executable, []);
        var deletionFailure = new IOException("The failed build generation is retained.");
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            beforeDeleteOwnedTree: path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) == "generations")
                {
                    throw deletionFailure;
                }
            });
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var processCompletion = new DebugFailureCompletion();
        processCompletion.AddEvidence(new("build-process-exit", "vba-dev", DebugResourceKind.Process,
            true, "The fake child reached terminal exit.", 42));
        processCompletion.AddEvidence(new("build-process-handles", "vba-dev", DebugResourceKind.Handle,
            true, "The fake owner released its handles.", 42));
        var builder = new VbaDevSnapshotWorkbookBuilder(new RecordingBuildProcess
        {
            ExitCode = 23,
            CleanupOutcome = provesProcessCleanup ? processCompletion.Complete() : null
        });
        var sourceSet = AdmitBuildSources(new TransportedDebugSourceSnapshot(2,
            [new TransportedDebugSource("Module1.bas", "file:///C:/source/Module1.bas", "utf8bom",
                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                    "Attribute VB_Name = \"Module1\"\nPublic Sub Run()\nEnd Sub\n")))]));

        var failure = await Record.ExceptionAsync(() => builder.BuildAsync(
            executable, lease,
            new VbaDevSnapshotBuildRequest(projectRoot, "Book1", "Book1.xlsm", sourceSet),
            CancellationToken.None));

        var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(failure).FailureOutcome;
        Assert.Contains("snapshot build exited with code 23", outcome.PrimaryFailure!.Message);
        Assert.Contains(outcome.CleanupFailures, item => ReferenceEquals(item.Exception, deletionFailure));
        if (provesProcessCleanup)
        {
            Assert.Contains(outcome.Evidence, item => item.ProcessId == 42 && item.Released);
        }
        Assert.Equal(provesProcessCleanup, outcome.OnlyFileDeletionFailed);
        Assert.Equal(!provesProcessCleanup, outcome.HasUnprovedRelease);
    }

    [Fact]
    public async Task FailedBuildResultDisposalRetainsOneOutcomeForEveryCaller()
    {
        using var temp = TempDirectory.Create();
        var deletionAttempts = 0;
        var manager = new VbaDebugSessionWorkspaceManager(
            Path.Combine(temp.Path, "workspace"), cleanupOperations: null,
            beforeDeleteOwnedTree: path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) == "generations")
                {
                    Interlocked.Increment(ref deletionAttempts);
                    throw new IOException("The completed build generation could not be deleted.");
                }
            });
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        var generation = lease.CreateGenerationWorkspace(DebugGenerationId.Initial, "debug.xlsm");
        var result = new VbaDevSnapshotBuildResult(generation);

        var first = await Record.ExceptionAsync(() => result.DisposeAsync().AsTask());
        var repeated = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(
            () => Record.ExceptionAsync(() => result.DisposeAsync().AsTask()))));

        Assert.NotNull(first);
        Assert.All(repeated, exception => Assert.Same(first, exception));
        Assert.Equal(1, deletionAttempts);
        Assert.True(Directory.Exists(result.GenerationWorkspacePath));
        Assert.Throws<InvalidOperationException>(() => result.TransferGenerationOwnership());
    }

    [Fact]
    public async Task BuildMaterializesTransportedBytesAndInvokesThePinnedCliProcess()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new RecordingBuildProcess
        {
            StandardOutput = "Built Book1.xlsm\r\nWARN Protected reference remains.\r\n",
            StandardError =
                "[WARN] vbeIdentifierRecased: Imported component 'DebugModule' identifier casing (source -> VBE): 'FileName' -> 'Filename'.\r\n"
        };
        var builder = new VbaDevSnapshotWorkbookBuilder(process);
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sourceBytes = DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"DebugModule\"\r\nPublic Sub RunTarget()\r\nEnd Sub\r\n");

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            await using var result = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "nested/DebugModule.bas",
                                "file:///C:/work/BookProject/src/Book1/nested/DebugModule.bas",
                                "utf8bom",
                                Convert.ToBase64String(sourceBytes))
                        ]))),
                CancellationToken.None);

            Assert.Equal("Book1.xlsm", Path.GetFileName(result.WorkbookPath));
            Assert.Equal(
                sourceBytes,
                await File.ReadAllBytesAsync(
                    Path.Combine(result.SourceSnapshotPath, "nested", "DebugModule.bas")));
            var invocation = Assert.Single(process.Invocations);
            Assert.Equal(vbaDevPath, invocation.FileName);
            Assert.Equal(
                [
                    "build",
                    "--project", projectRoot,
                    "--document", "Book1",
                    "--source-snapshot", result.SourceSnapshotPath,
                    "--output", result.WorkbookPath
                ],
                invocation.Arguments);
            Assert.True(File.Exists(result.WorkbookPath));
            Assert.Equal(
                [
                    "Built Book1.xlsm",
                    "WARN Protected reference remains.",
                    "[WARN] vbeIdentifierRecased: Imported component 'DebugModule' identifier casing (source -> VBE): 'FileName' -> 'Filename'."
                ],
                result.Output);
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
    public async Task BuildsUseDistinctGenerationWorkspacesWithinOneSession()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess());
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var snapshot = new TransportedDebugSourceSnapshot(
            2,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "utf8bom",
                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                        "Attribute VB_Name = \"Module1\"\r\n")))
            ]);

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            await using var initial = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(
                        snapshot,
                        DebugGenerationId.Initial)),
                CancellationToken.None);
            await using var restarted = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(
                        snapshot,
                        DebugGenerationId.FromValue(1))),
                CancellationToken.None);

            Assert.NotEqual(
                initial.GenerationWorkspacePath,
                restarted.GenerationWorkspacePath);
            Assert.Contains(
                Path.Combine("generations", "generation-0000000000"),
                initial.GenerationWorkspacePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Path.Combine("generations", "generation-0000000001"),
                restarted.GenerationWorkspacePath,
                StringComparison.OrdinalIgnoreCase);
            await initial.DisposeAsync();
            Assert.False(Directory.Exists(initial.GenerationWorkspacePath));
            Assert.True(File.Exists(restarted.WorkbookPath));
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
    public async Task SuccessfulBuildPinsItsGenerationIdentityUntilTheResultIsDisposed()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess());
        await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
            .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
        var result = await builder.BuildAsync(
            vbaDevPath,
            lease,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                AdmitBuildSources(new TransportedDebugSourceSnapshot(
                    2,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8bom",
                            Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ]))),
            CancellationToken.None);
        var displacedPath = result.GenerationWorkspacePath + "-displaced";

        try
        {
            Assert.Throws<IOException>(() => Directory.Move(
                result.GenerationWorkspacePath,
                displacedPath));

            await result.DisposeAsync();

            Assert.False(Directory.Exists(result.GenerationWorkspacePath));
            Assert.False(Directory.Exists(displacedPath));
        }
        finally
        {
            await result.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionAndBuilderShareOneCanonicalRootAcrossAliasReplacement()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var physicalRootA = Path.Combine(temp.Path, "physical-a");
        var physicalRootB = Path.Combine(temp.Path, "physical-b");
        var aliasedRoot = Path.Combine(temp.Path, "workspace-link");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(physicalRootA);
        Directory.CreateDirectory(physicalRootB);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        Directory.CreateSymbolicLink(aliasedRoot, physicalRootA);
        var rootBinding = new VbaDebugWorkspaceRootBinding(aliasedRoot);
        var manager = new VbaDebugSessionWorkspaceManager(
            rootBinding,
            cleanupOperations: null);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess());

        try
        {
            await using var lease = await manager.ClaimAsync(
                DebugSessionId.Parse(sessionId),
                CancellationToken.None);
            Directory.Delete(aliasedRoot);
            Directory.CreateSymbolicLink(aliasedRoot, physicalRootB);

            await using var result = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ]))),
                CancellationToken.None);

            Assert.StartsWith(
                Path.GetFullPath(physicalRootA),
                result.GenerationWorkspacePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(
                physicalRootB,
                "workspaces",
                sessionId)));
        }
        finally
        {
            if (WindowsVbaDebugWorkspacePath.EntryExistsNoFollow(aliasedRoot))
            {
                Directory.Delete(aliasedRoot);
            }
        }
    }

    [Fact]
    public async Task BuildRejectsAGenerationsLinkWithoutWritingOutsideTheSessionTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var sessionPath = Path.Combine(workspaceRoot, "workspaces", sessionId);
        var outsidePath = Path.Combine(temp.Path, "outside");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(outsidePath);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var manager = new VbaDebugSessionWorkspaceManager(
            workspaceRoot,
            cleanupOperations: null,
            afterCreateDirectoryBeforeOpen: createdPath =>
            {
                if (createdPath.Equals(sessionPath, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateSymbolicLink(
                        Path.Combine(sessionPath, "generations"),
                        outsidePath);
                }
            });
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess());
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => builder.BuildAsync(
            vbaDevPath,
            lease,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                AdmitBuildSources(new TransportedDebugSourceSnapshot(
                    2,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8bom",
                            Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ]))),
            CancellationToken.None));

        Assert.Empty(Directory.EnumerateFileSystemEntries(outsidePath));
    }

    [Fact]
    public async Task BuildDoesNotFollowADanglingSourceLinkOutsideTheGenerationTree()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var outsideSourcePath = Path.Combine(temp.Path, "outside-Module1.bas");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new RecordingBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(process);
        var manager = new VbaDebugSessionWorkspaceManager(
            workspaceRoot,
            cleanupOperations: null,
            beforeCreateSourceFile: sourcePath =>
                File.CreateSymbolicLink(sourcePath, outsideSourcePath));
        await using var lease = await manager.ClaimAsync(
            DebugSessionId.Parse(sessionId),
            CancellationToken.None);

        VbaDevSnapshotBuildResult? result = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ]))),
                CancellationToken.None);
            await result.DisposeAsync();
        });

        if (result is not null)
        {
            await result.DisposeAsync();
        }
        Assert.NotNull(exception);
        Assert.False(File.Exists(outsideSourcePath));
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task BuildRejectsAnExistingGenerationWithoutDeletingItsArtifacts()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new RecordingBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(process);
        await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
            .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
        await using var existingGeneration = lease.CreateGenerationWorkspace(
            DebugGenerationId.Initial,
            "Book1.xlsm");
        var sentinelPath = Path.Combine(
            existingGeneration.GenerationWorkspacePath,
            "retained.tmp");
        await File.WriteAllTextAsync(sentinelPath, "retained");

        await DebugFailureAssertions.ThrowsWithProvedCleanupAsync<InvalidOperationException>(() => builder.BuildAsync(
            vbaDevPath,
            lease,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                AdmitBuildSources(new TransportedDebugSourceSnapshot(
                    2,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8bom",
                            Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ]))),
            CancellationToken.None));

        Assert.True(File.Exists(sentinelPath));
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task FailedBuildRetainsChildOutputAndDeletesOnlyTheGenerationWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new RecordingBuildProcess
        {
            ExitCode = 7,
            StandardOutput = "Preparing snapshot.\r\nImporting sources.\r\n",
            StandardError = "Synthetic build failure.\r\n"
        };
        var builder = new VbaDevSnapshotWorkbookBuilder(process);
        const string sessionId = "0123456789abcdef0123456789abcdef";

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            var exception = await Record.ExceptionAsync(() =>
                builder.BuildAsync(
                    vbaDevPath,
                    lease,
                    new VbaDevSnapshotBuildRequest(
                        projectRoot,
                        "Book1",
                        "Book1.xlsm",
                        AdmitBuildSources(new TransportedDebugSourceSnapshot(
                            2,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8bom",
                                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                        "Attribute VB_Name = \"Module1\"\r\n")))
                            ]))),
                    CancellationToken.None));

            var outcome = Assert.IsAssignableFrom<IDebugFailureEvidence>(exception).FailureOutcome;
            Assert.False(outcome.HasCleanupFailure);
            Assert.Contains(outcome.Evidence, item => item.Kind == DebugResourceKind.Process && item.Released);
            Assert.IsType<InvalidOperationException>(outcome.PrimaryFailure);
            Assert.Contains("code 7", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Preparing snapshot.", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Importing sources.", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Synthetic build failure.", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(
                workspaceRoot,
                "workspaces",
                sessionId,
                "generations",
                "generation-0000000000")));
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
    public async Task SuccessfulExitWithoutAWorkbookDeletesOnlyTheGenerationWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess { CreateOutput = false });
        const string sessionId = "0123456789abcdef0123456789abcdef";

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            var exception = await DebugFailureAssertions.ThrowsWithProvedCleanupAsync<InvalidOperationException>(() =>
                builder.BuildAsync(
                    vbaDevPath,
                    lease,
                    new VbaDevSnapshotBuildRequest(
                        projectRoot,
                        "Book1",
                        "Book1.xlsm",
                        AdmitBuildSources(new TransportedDebugSourceSnapshot(
                            2,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8bom",
                                    Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                        "Attribute VB_Name = \"Module1\"\r\n")))
                            ]))),
                    CancellationToken.None));

            Assert.Contains("without producing", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(
                workspaceRoot,
                "workspaces",
                sessionId,
                "generations",
                "generation-0000000000")));
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
    public async Task BuildMaterializesFormSidecarBytesExactly()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new RecordingBuildProcess());
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var formBytes = DebugSnapshotTestEncoding.Utf8BomBytes(
            "VERSION 5.00\r\nBegin VB.UserForm Dialog\r\nEnd\r\n");
        byte[] sidecarBytes = [0x00, 0xff, 0x10, 0x80, 0x0d, 0x0a, 0x7f];

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            await using var result = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "forms/Dialog.frm",
                                "file:///C:/persistent/forms/Dialog.frm",
                                "utf8bom",
                                Convert.ToBase64String(formBytes)),
                            new TransportedDebugSource(
                                "forms/Dialog.frx",
                                null,
                                null,
                                Convert.ToBase64String(sidecarBytes))
                        ]))),
                CancellationToken.None);

            Assert.Equal(
                sidecarBytes,
                await File.ReadAllBytesAsync(
                    Path.Combine(result.SourceSnapshotPath, "forms", "Dialog.frx")));
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
    public async Task BuildPreventsStagedSourceMutationWhileTheChildRuns()
    {
        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var originalSource = DebugSnapshotTestEncoding.Utf8BomBytes(
            "Attribute VB_Name = \"Module1\"\r\n");
        var process = new SourceMutationBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(process);

        await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
            .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
        var exception = await DebugFailureAssertions.ThrowsWithProvedCleanupAsync<IOException>(() => builder.BuildAsync(
            vbaDevPath,
            lease,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                AdmitBuildSources(new TransportedDebugSourceSnapshot(
                    2,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8bom",
                            Convert.ToBase64String(originalSource))
                    ]))),
            CancellationToken.None));

        Assert.False(process.MutationSucceeded);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.False(Directory.Exists(Path.Combine(
            workspaceRoot,
            "workspaces",
            sessionId,
            "generations",
            "generation-0000000000")));
    }

    [Fact]
    public async Task BuildRejectsAnOutputSymlinkAndRetainsItsOutsideWorkbookTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var workspaceRoot = Path.Combine(temp.Path, "adapter-root");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        var outsideWorkbookPath = Path.Combine(temp.Path, "outside.xlsm");
        byte[] outsideWorkbookBytes = [0x50, 0x4b, 0x03, 0x04, 0x7f];
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        await File.WriteAllBytesAsync(outsideWorkbookPath, outsideWorkbookBytes);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            new SymlinkOutputBuildProcess(outsideWorkbookPath));
        await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
            .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
        VbaDevSnapshotBuildResult? unexpectedResult = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            unexpectedResult = await builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ]))),
                CancellationToken.None);
        });
        if (unexpectedResult is not null)
        {
            await unexpectedResult.DisposeAsync();
        }

        DebugFailureAssertions.HasProvedCleanup<IOException>(exception);
        Assert.True(File.Exists(outsideWorkbookPath));
        Assert.Equal(
            outsideWorkbookBytes,
            await File.ReadAllBytesAsync(outsideWorkbookPath));
    }

    [Fact]
    public async Task CancellationWaitsForTheChildBoundaryBeforeDeletingTheGenerationWorkspace()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-builder-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(root, "project");
        var workspaceRoot = Path.Combine(root, "adapter-root");
        Directory.CreateDirectory(projectRoot);
        var vbaDevPath = Path.Combine(root, "tools", "vba-dev.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new CancellationControlledBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(process);
        const string sessionId = "0123456789abcdef0123456789abcdef";
        using var cancellation = new CancellationTokenSource();

        try
        {
            await using var lease = await new VbaDebugSessionWorkspaceManager(workspaceRoot)
                .ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None);
            var buildTask = builder.BuildAsync(
                vbaDevPath,
                lease,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    AdmitBuildSources(new TransportedDebugSourceSnapshot(
                        2,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8bom",
                                Convert.ToBase64String(DebugSnapshotTestEncoding.Utf8BomBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ]))),
                cancellation.Token);
            await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            cancellation.Cancel();
            await process.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(Directory.Exists(Path.Combine(workspaceRoot, "workspaces", sessionId)));

            process.AllowExit.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask);
            Assert.False(Directory.Exists(Path.Combine(
                workspaceRoot,
                "workspaces",
                sessionId,
                "generations",
                "generation-0000000000")));
        }
        finally
        {
            process.AllowExit.TrySetResult();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessBuildRunnerCapturesChildOutputAndExitCode()
    {
        var runner = new ProcessVbaDevBuildProcess();
        var commandPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = await runner.RunAsync(
            commandPath,
            ["/d", "/c", "echo standard-output & echo standard-error 1>&2 & exit /b 7"],
            CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("standard-output", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("standard-error", result.StandardError, StringComparison.Ordinal);
    }

    private sealed class RecordingBuildProcess : IVbaDevBuildProcess
    {
        public int ExitCode { get; init; }

        public bool CreateOutput { get; init; } = true;

        public string StandardOutput { get; init; } = string.Empty;

        public string StandardError { get; init; } = string.Empty;

        public DebugFailureOutcome? CleanupOutcome { get; init; } = ProvedFakeProcessCompletion();

        public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public async Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add((fileName, arguments));
            if (CreateOutput)
            {
                var outputIndex = arguments.ToList().IndexOf("--output");
                var outputPath = arguments[outputIndex + 1];
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllBytesAsync(outputPath, [0x50, 0x4b], cancellationToken);
            }
            return new VbaDevBuildProcessResult(ExitCode, StandardOutput, StandardError)
            {
                CleanupOutcome = CleanupOutcome
            };
        }
    }

    private sealed class CancellationControlledBuildProcess : IVbaDevBuildProcess
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowExit { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException exception)
            {
                CancellationObserved.TrySetResult();
                await AllowExit.Task;
                throw new DebugFailureException(ProvedFakeProcessCompletion(exception));
            }

            throw new InvalidOperationException("The controlled child unexpectedly completed.");
        }
    }

    private sealed class SourceMutationBuildProcess : IVbaDevBuildProcess
    {
        public bool MutationSucceeded { get; private set; }

        public async Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                return await MutateAsync(arguments, cancellationToken);
            }
            catch (Exception exception)
            {
                throw new DebugFailureException(ProvedFakeProcessCompletion(exception));
            }
        }

        private async Task<VbaDevBuildProcessResult> MutateAsync(
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var sourceSnapshotPath = GetArgumentValue(arguments, "--source-snapshot");
            var sourcePath = Path.Combine(sourceSnapshotPath, "Module1.bas");
            await using (var mutationStream = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Write,
                             FileShare.ReadWrite | FileShare.Delete))
            {
                mutationStream.SetLength(0);
                await mutationStream.WriteAsync(
                    "mutated"u8.ToArray(),
                    cancellationToken);
            }
            MutationSucceeded = true;

            var outputPath = GetArgumentValue(arguments, "--output");
            await File.WriteAllBytesAsync(outputPath, [0x50, 0x4b], cancellationToken);
            return new VbaDevBuildProcessResult(0, string.Empty, string.Empty)
            {
                CleanupOutcome = ProvedFakeProcessCompletion()
            };
        }
    }

    private sealed class SymlinkOutputBuildProcess(string outsideWorkbookPath)
        : IVbaDevBuildProcess
    {
        public Task<VbaDevBuildProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var outputPath = GetArgumentValue(arguments, "--output");
            File.CreateSymbolicLink(outputPath, outsideWorkbookPath);
            return Task.FromResult(new VbaDevBuildProcessResult(
                0,
                string.Empty,
                string.Empty)
            {
                CleanupOutcome = ProvedFakeProcessCompletion()
            });
        }
    }

    private static DebugFailureOutcome ProvedFakeProcessCompletion(Exception? primary = null)
    {
        var completion = new DebugFailureCompletion(primary);
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Handle })
        {
            completion.AddEvidence(new("fake-build-completion", "controlled build", kind, true,
                "The controlled invocation has completed and owns no native process or handles."));
        }
        return completion.Complete();
    }

    private static AdmittedDebugBuildSourceSet AdmitBuildSources(
        TransportedDebugSourceSnapshot snapshot)
        => AdmitBuildSources(snapshot, DebugGenerationId.Initial);

    private static AdmittedDebugBuildSourceSet AdmitBuildSources(
        TransportedDebugSourceSnapshot snapshot,
        DebugGenerationId generationId)
    {
        var validated = new TransportedDebugSourceSnapshotValidator(932)
            .Validate(snapshot);
        return new AdmittedDebugBuildSourceSet(generationId, validated.Sources);
    }

    private static string GetArgumentValue(
        IReadOnlyList<string> arguments,
        string option)
    {
        var optionIndex = arguments.ToList().IndexOf(option);
        Assert.InRange(optionIndex, 0, arguments.Count - 2);
        return arguments[optionIndex + 1];
    }
}
