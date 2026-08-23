using VbaDebugAdapter.Build;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class VbaDevSnapshotWorkbookBuilderTests
{
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
            StandardOutput = "WARN Protected reference remains.\r\n"
        };
        var builder = new VbaDevSnapshotWorkbookBuilder(workspaceRoot, process);
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"DebugModule\"\r\nPublic Sub RunTarget()\r\nEnd Sub\r\n");

        try
        {
            await using var result = await builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "nested/DebugModule.bas",
                                "file:///C:/work/BookProject/src/Book1/nested/DebugModule.bas",
                                "utf8",
                                Convert.ToBase64String(sourceBytes))
                        ])),
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
            Assert.Equal(["WARN Protected reference remains."], result.Output);
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
            workspaceRoot,
            new RecordingBuildProcess());
        const string sessionId = "0123456789abcdef0123456789abcdef";
        var snapshot = new TransportedDebugSourceSnapshot(
            1,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    "file:///C:/persistent/Module1.bas",
                    "utf8",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                        "Attribute VB_Name = \"Module1\"\r\n")))
            ]);

        try
        {
            await using var initial = await builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    snapshot)
                {
                    Generation = 0
                },
                CancellationToken.None);
            await using var restarted = await builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    snapshot)
                {
                    Generation = 1
                },
                CancellationToken.None);

            Assert.NotEqual(initial.SessionWorkspacePath, restarted.SessionWorkspacePath);
            Assert.Contains(
                Path.Combine("generations", "generation-0000000000"),
                initial.SessionWorkspacePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Path.Combine("generations", "generation-0000000001"),
                restarted.SessionWorkspacePath,
                StringComparison.OrdinalIgnoreCase);
            await initial.DisposeAsync();
            Assert.False(Directory.Exists(initial.SessionWorkspacePath));
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
            workspaceRoot,
            new RecordingBuildProcess());
        var result = await builder.BuildAsync(
            vbaDevPath,
            sessionId,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                new TransportedDebugSourceSnapshot(
                    1,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8",
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ])),
            CancellationToken.None);
        var displacedPath = result.SessionWorkspacePath + "-displaced";

        try
        {
            Assert.Throws<IOException>(() => Directory.Move(
                result.SessionWorkspacePath,
                displacedPath));

            await result.DisposeAsync();

            Assert.False(Directory.Exists(result.SessionWorkspacePath));
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
            rootBinding,
            new RecordingBuildProcess(),
            new TransportedDebugSourceSnapshotValidator(932));

        try
        {
            await using var lease = await manager.ClaimAsync(
                sessionId,
                CancellationToken.None);
            Directory.Delete(aliasedRoot);
            Directory.CreateSymbolicLink(aliasedRoot, physicalRootB);

            await using var result = await builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8",
                                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ])),
                CancellationToken.None);

            Assert.StartsWith(
                Path.GetFullPath(physicalRootA),
                result.SessionWorkspacePath,
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
        Directory.CreateDirectory(sessionPath);
        Directory.CreateDirectory(outsidePath);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllBytesAsync(vbaDevPath, []);
        Directory.CreateSymbolicLink(
            Path.Combine(sessionPath, "generations"),
            outsidePath);
        var builder = new VbaDevSnapshotWorkbookBuilder(
            workspaceRoot,
            new RecordingBuildProcess());

        await Assert.ThrowsAnyAsync<Exception>(() => builder.BuildAsync(
            vbaDevPath,
            sessionId,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                new TransportedDebugSourceSnapshot(
                    1,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8",
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ])),
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
        var builder = new VbaDevSnapshotWorkbookBuilder(
            workspaceRoot,
            process,
            new TransportedDebugSourceSnapshotValidator(932),
            beforeCreateSourceFile: sourcePath =>
                File.CreateSymbolicLink(sourcePath, outsideSourcePath));

        VbaDevSnapshotBuildResult? result = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            result = await builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8",
                                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ])),
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
        var generationPath = Path.Combine(
            workspaceRoot,
            "workspaces",
            sessionId,
            "generations",
            "generation-0000000000");
        var sentinelPath = Path.Combine(generationPath, "retained.tmp");
        var projectRoot = Path.Combine(temp.Path, "project");
        var vbaDevPath = Path.Combine(temp.Path, "tools", "vba-dev.exe");
        Directory.CreateDirectory(generationPath);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(vbaDevPath)!);
        await File.WriteAllTextAsync(sentinelPath, "retained");
        await File.WriteAllBytesAsync(vbaDevPath, []);
        var process = new RecordingBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(workspaceRoot, process);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(
            vbaDevPath,
            sessionId,
            new VbaDevSnapshotBuildRequest(
                projectRoot,
                "Book1",
                "Book1.xlsm",
                new TransportedDebugSourceSnapshot(
                    1,
                    [
                        new TransportedDebugSource(
                            "Module1.bas",
                            "file:///C:/persistent/Module1.bas",
                            "utf8",
                            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                "Attribute VB_Name = \"Module1\"\r\n")))
                    ])),
            CancellationToken.None));

        Assert.True(File.Exists(sentinelPath));
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task BuildRejectsBytesThatDoNotStrictlyMatchTheDeclaredEncoding()
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
        var process = new RecordingBuildProcess();
        var builder = new VbaDevSnapshotWorkbookBuilder(workspaceRoot, process);
        const string sessionId = "0123456789abcdef0123456789abcdef";

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.BuildAsync(
                    vbaDevPath,
                    sessionId,
                    new VbaDevSnapshotBuildRequest(
                        projectRoot,
                        "Book1",
                        "Book1.xlsm",
                        new TransportedDebugSourceSnapshot(
                            1,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8",
                                    Convert.ToBase64String([0xff]))
                            ])),
                    CancellationToken.None));

            Assert.Contains("utf8", exception.Message, StringComparison.Ordinal);
            Assert.Empty(process.Invocations);
            Assert.False(Directory.Exists(Path.Combine(workspaceRoot, "workspaces", sessionId)));
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
        var builder = new VbaDevSnapshotWorkbookBuilder(workspaceRoot, process);
        const string sessionId = "0123456789abcdef0123456789abcdef";

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.BuildAsync(
                    vbaDevPath,
                    sessionId,
                    new VbaDevSnapshotBuildRequest(
                        projectRoot,
                        "Book1",
                        "Book1.xlsm",
                        new TransportedDebugSourceSnapshot(
                            1,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8",
                                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                        "Attribute VB_Name = \"Module1\"\r\n")))
                            ])),
                    CancellationToken.None));

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
            workspaceRoot,
            new RecordingBuildProcess { CreateOutput = false });
        const string sessionId = "0123456789abcdef0123456789abcdef";

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                builder.BuildAsync(
                    vbaDevPath,
                    sessionId,
                    new VbaDevSnapshotBuildRequest(
                        projectRoot,
                        "Book1",
                        "Book1.xlsm",
                        new TransportedDebugSourceSnapshot(
                            1,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8",
                                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                        "Attribute VB_Name = \"Module1\"\r\n")))
                            ])),
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
            workspaceRoot,
            new RecordingBuildProcess());
        var formBytes = System.Text.Encoding.UTF8.GetBytes(
            "VERSION 5.00\r\nBegin VB.UserForm Dialog\r\nEnd\r\n");
        byte[] sidecarBytes = [0x00, 0xff, 0x10, 0x80, 0x0d, 0x0a, 0x7f];

        try
        {
            await using var result = await builder.BuildAsync(
                vbaDevPath,
                "0123456789abcdef0123456789abcdef",
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "forms/Dialog.frm",
                                "file:///C:/persistent/forms/Dialog.frm",
                                "utf8",
                                Convert.ToBase64String(formBytes)),
                            new TransportedDebugSource(
                                "forms/Dialog.frx",
                                null,
                                null,
                                Convert.ToBase64String(sidecarBytes))
                        ])),
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
        var builder = new VbaDevSnapshotWorkbookBuilder(workspaceRoot, process);
        const string sessionId = "0123456789abcdef0123456789abcdef";
        using var cancellation = new CancellationTokenSource();

        try
        {
            var buildTask = builder.BuildAsync(
                vbaDevPath,
                sessionId,
                new VbaDevSnapshotBuildRequest(
                    projectRoot,
                    "Book1",
                    "Book1.xlsm",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8",
                                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                                    "Attribute VB_Name = \"Module1\"\r\n")))
                        ])),
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
            return new VbaDevBuildProcessResult(ExitCode, StandardOutput, StandardError);
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
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await AllowExit.Task;
                throw;
            }

            throw new InvalidOperationException("The controlled child unexpectedly completed.");
        }
    }
}
