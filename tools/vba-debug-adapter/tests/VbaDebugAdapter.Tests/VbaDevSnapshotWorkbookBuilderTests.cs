using VbaDebugAdapter.Build;
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
    public async Task FailedBuildRetainsChildOutputAndDeletesTheSessionWorkspace()
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
    public async Task SuccessfulExitWithoutAWorkbookDeletesTheSessionWorkspace()
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
            var result = await builder.BuildAsync(
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
    public async Task CancellationWaitsForTheChildBoundaryBeforeDeletingTheWorkspace()
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
            Assert.False(Directory.Exists(Path.Combine(workspaceRoot, "workspaces", sessionId)));
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
