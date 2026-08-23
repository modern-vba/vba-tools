using System.Text;
using VbaDebugAdapter.Build;
using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class StandaloneVbaDebugLaunchServiceTests
{
    [Fact]
    public async Task LaunchBuildsThenOpensAndRunsTheTemporarySameNameWorkbook()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var events = new List<string>();
        var builder = new RecordingWorkbookBuilder(
            events,
            new VbaDevSnapshotBuildResult(
                Path.Combine(sessionWorkspace, "source"),
                workbookPath,
                sessionWorkspace)
            {
                Output = ["WARN Protected reference remains."]
            });
        var visibleSession = new RecordingVbeDebugSession(events);
        var lifecycleSink = new RecordingDebugLifecycleSink();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            builder,
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var sourceBytes = Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\nEnd Sub\r\n");

        try
        {
            var runningSession = await service.LaunchAsync(
                Path.GetFullPath("vba-dev.exe"),
                "0123456789abcdef0123456789abcdef",
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    new TransportedDebugSourceSnapshot(
                        1,
                        [
                            new TransportedDebugSource(
                                "Module1.bas",
                                "file:///C:/persistent/Module1.bas",
                                "utf8",
                                Convert.ToBase64String(sourceBytes))
                        ])),
                CancellationToken.None,
                lifecycleSink);

            Assert.Equal(
                ["build:Book1.xlsm", "start-visible", "open:Book1.xlsm", "run:Module1.Run"],
                events);
            Assert.Equal(workbookPath, visibleSession.OpenedWorkbookPath);
            Assert.Same(lifecycleSink, visibleSession.WorkbookOpenInputWaitSink);
            Assert.Equal(
                [new DebugLifecycleMessage("WARN Protected reference remains.")],
                lifecycleSink.Messages);
            Assert.Equal(new DebugTargetProcedure("Module1", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
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
    public async Task LaunchAcceptsTheClientRawUtf16OrdinalPathOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(
                    Path.Combine(sessionWorkspace, "source"),
                    workbookPath,
                    sessionWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        var snapshot = new TransportedDebugSourceSnapshot(
            1,
            [
                Source("B.bas", "ModuleB", "Other"),
                Source("a.bas", "ModuleA", "Run"),
                Source("j.bas", "ModuleJ", "OtherJ"),
                Source("İ.bas", "ModuleDottedI", "OtherDottedI")
            ]);

        try
        {
            var runningSession = await service.LaunchAsync(
                Path.GetFullPath("vba-dev.exe"),
                "0123456789abcdef0123456789abcdef",
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "ModuleA",
                    "Run",
                    snapshot),
                CancellationToken.None);

            Assert.Equal(new DebugTargetProcedure("ModuleA", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        static TransportedDebugSource Source(
            string relativePath,
            string moduleName,
            string procedureName)
            => new(
                relativePath,
                $"file:///C:/persistent/{relativePath}",
                "utf8",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"Attribute VB_Name = \"{moduleName}\"\r\n" +
                    $"Public Sub {procedureName}()\r\nEnd Sub\r\n")));
    }

    [Fact]
    public async Task LaunchRejectsAMismatchedPersistentSourceIdentityBeforeBuild()
    {
        var events = new List<string>();
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(
                    Path.GetFullPath("source"),
                    Path.GetFullPath("Book1.xlsm"),
                    Path.GetFullPath("workspace"))),
            new RecordingVbeDebugSessionFactory(
                events,
                new RecordingVbeDebugSession(events)));
        var snapshot = new TransportedDebugSourceSnapshot(
            1,
            [
                new TransportedDebugSource(
                    "nested/Module1.bas",
                    "file:///C:/persistent/Elsewhere.bas",
                    "utf8",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        "Attribute VB_Name = \"Module1\"\r\n" +
                        "Public Sub Run()\r\nEnd Sub\r\n")))
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LaunchAsync(
                Path.GetFullPath("vba-dev.exe"),
                "0123456789abcdef0123456789abcdef",
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    snapshot),
                CancellationToken.None));

        Assert.Contains("sourceUri", exception.Message, StringComparison.Ordinal);
        Assert.Empty(events);
    }

    [Fact]
    public async Task LaunchResolvesTheTargetFromTheTransportedPersistentSourcePosition()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(
                    Path.Combine(sessionWorkspace, "source"),
                    workbookPath,
                    sessionWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var sourceBytes = Encoding.UTF8.GetBytes(
            "Attribute VB_Name = \"Module1\"\r\nPublic Sub Run()\r\n    Debug.Print \"run\"\r\nEnd Sub\r\n");
        var transportedSnapshot = new TransportedDebugSourceSnapshot(
            1,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    sourceUri,
                    "utf8",
                    Convert.ToBase64String(sourceBytes))
            ])
        {
            ActiveSource = new TransportedDebugSourcePosition(sourceUri, 2, 4)
        };

        try
        {
            var runningSession = await service.LaunchAsync(
                Path.GetFullPath("vba-dev.exe"),
                "0123456789abcdef0123456789abcdef",
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    ModuleName: null,
                    ProcedureName: null,
                    transportedSnapshot),
                CancellationToken.None);

            Assert.Equal(new DebugTargetProcedure("Module1", "Run"), visibleSession.Target);
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
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
    public async Task LaunchTransfersTheExactTransportedBreakpointBeforeRunningTheTarget()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(
                    Path.Combine(sessionWorkspace, "source"),
                    workbookPath,
                    sessionWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession));
        const string sourceUri = "file:///C:/persistent/Module1.bas";
        var transportedSnapshot = new TransportedDebugSourceSnapshot(
            1,
            [
                new TransportedDebugSource(
                    "Module1.bas",
                    sourceUri,
                    "utf8",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        "Attribute VB_Name = \"Module1\"\r\n" +
                        "Public Sub Run()\r\n" +
                        "    Debug.Print \"break\"\r\n" +
                        "End Sub\r\n")))
            ])
        {
            Breakpoints = [new TransportedDebugSourceBreakpoint(sourceUri, 2)]
        };

        try
        {
            var runningSession = await service.LaunchAsync(
                Path.GetFullPath("vba-dev.exe"),
                "0123456789abcdef0123456789abcdef",
                new StandaloneVbaDebugLaunchRequest(
                    Path.GetFullPath("project"),
                    "Book1",
                    "Book1.xlsm",
                    "Module1",
                    "Run",
                    transportedSnapshot),
                CancellationToken.None);

            var breakpoint = Assert.Single(visibleSession.Breakpoints);
            Assert.Equal(sourceUri, breakpoint.Source.SourceUri);
            Assert.Equal(2, breakpoint.Source.EditorLine);
            Assert.Equal("Module1", breakpoint.ModuleName);
            Assert.Equal(2, breakpoint.VbideLine);
            Assert.True(events.IndexOf("set-breakpoints") < events.IndexOf("run:Module1.Run"));
            await runningSession.TerminateAsync();
            await runningSession.DisposeAsync();
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
    public async Task LaunchDeletesTheBuiltSessionWorkspaceWhenCompilationSettingsReadFails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                [],
                new VbaDevSnapshotBuildResult(
                    Path.Combine(sessionWorkspace, "source"),
                    workbookPath,
                    sessionWorkspace)),
            new RecordingVbeDebugSessionFactory([], new RecordingVbeDebugSession([])),
            breakpointSourceMapper: null,
            compilationSettingsReader: new ThrowingCompilationSettingsReader(),
            compilationEnvironmentFactory: new DebugCompilationEnvironmentFactory(),
            conditionalCompilationPreflight: new DebugConditionalCompilationPreflight());
        var sourceBytes = Encoding.UTF8.GetBytes(string.Join('\n',
        [
            "Attribute VB_Name = \"Module1\"",
            "#If VBA7 Then",
            "Public Sub Run()",
            "End Sub",
            "#End If"
        ]));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.LaunchAsync(
                    Path.GetFullPath("vba-dev.exe"),
                    "0123456789abcdef0123456789abcdef",
                    new StandaloneVbaDebugLaunchRequest(
                        Path.GetFullPath("project"),
                        "Book1",
                        "Book1.xlsm",
                        "Module1",
                        "Run",
                        new TransportedDebugSourceSnapshot(
                            1,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8",
                                    Convert.ToBase64String(sourceBytes))
                            ])),
                    CancellationToken.None));

            Assert.Equal("Synthetic compilation-settings read failure.", exception.Message);
            Assert.False(Directory.Exists(sessionWorkspace));
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
    public async Task LaunchRejectsAnInactiveTargetUsingTheBuiltWorkbookAndVisibleHostFacts()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "vba-debug-adapter-launch-tests",
            Guid.NewGuid().ToString("N"));
        var sessionWorkspace = Path.Combine(root, "workspaces", "session");
        var workbookPath = Path.Combine(sessionWorkspace, "output", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        await File.WriteAllBytesAsync(workbookPath, [0x50, 0x4b]);
        var events = new List<string>();
        var visibleSession = new RecordingVbeDebugSession(events);
        var settings = new DebugCompilationSettings(
            VbaProjectSystemKind.Win64,
            1252,
            [],
            new string('A', 64));
        var service = new StandaloneVbaDebugLaunchService(
            new TransportedDebugSourceSnapshotValidator(932),
            new RecordingWorkbookBuilder(
                events,
                new VbaDevSnapshotBuildResult(
                    Path.Combine(sessionWorkspace, "source"),
                    workbookPath,
                    sessionWorkspace)),
            new RecordingVbeDebugSessionFactory(events, visibleSession),
            breakpointSourceMapper: null,
            compilationSettingsReader: new ConstantCompilationSettingsReader(settings),
            compilationEnvironmentFactory: new DebugCompilationEnvironmentFactory(),
            conditionalCompilationPreflight: new DebugConditionalCompilationPreflight());
        var sourceBytes = Encoding.UTF8.GetBytes(string.Join('\n',
        [
            "Attribute VB_Name = \"Module1\"",
            "#If VBA7 Then",
            "Public Sub ModernTarget()",
            "End Sub",
            "#Else",
            "Public Sub LegacyTarget()",
            "End Sub",
            "#End If"
        ]));

        try
        {
            var exception = await Assert.ThrowsAsync<DebugSetupException>(() =>
                service.LaunchAsync(
                    Path.GetFullPath("vba-dev.exe"),
                    "0123456789abcdef0123456789abcdef",
                    new StandaloneVbaDebugLaunchRequest(
                        Path.GetFullPath("project"),
                        "Book1",
                        "Book1.xlsm",
                        "Module1",
                        "LegacyTarget",
                        new TransportedDebugSourceSnapshot(
                            1,
                            [
                                new TransportedDebugSource(
                                    "Module1.bas",
                                    "file:///C:/persistent/Module1.bas",
                                    "utf8",
                                    Convert.ToBase64String(sourceBytes))
                            ])),
                    CancellationToken.None));

            Assert.Contains("inactive", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("open:Book1.xlsm", events);
            Assert.DoesNotContain("run:Module1.LegacyTarget", events);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RecordingWorkbookBuilder(
        List<string> events,
        VbaDevSnapshotBuildResult result) : IVbaDebugWorkbookBuilder
    {
        public Task<VbaDevSnapshotBuildResult> BuildAsync(
            string vbaDevPath,
            string sessionId,
            VbaDevSnapshotBuildRequest request,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{request.WorkbookFileName}");
            return Task.FromResult(result);
        }
    }

    private sealed class ConstantCompilationSettingsReader(
        DebugCompilationSettings settings) : IDebugCompilationSettingsReader
    {
        public DebugCompilationSettings Read(string workbookPath) => settings;
    }

    private sealed class ThrowingCompilationSettingsReader : IDebugCompilationSettingsReader
    {
        public DebugCompilationSettings Read(string workbookPath)
            => throw new InvalidOperationException(
                "Synthetic compilation-settings read failure.");
    }

    private sealed class RecordingVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class RecordingVbeDebugSession(List<string> events) : IVbeDebugSession
    {
        public int ProcessId => 1234;

        public string? OpenedWorkbookPath { get; private set; }

        public IDebugInputWaitSink? WorkbookOpenInputWaitSink { get; private set; }

        public DebugTargetProcedure? Target { get; private set; }

        public IReadOnlyList<VbeBreakpoint> Breakpoints { get; private set; } = [];

        public Task<DebugProcessExit> Completion { get; } =
            Task.FromResult(new DebugProcessExit(0));

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => Task.FromResult(new DebugCompilationHostFacts(
                "16.0",
                "7.1",
                "Windows",
                DebugExcelProcessArchitecture.X64,
                DebugCompilationHostFactsStatus.Verified,
                new DebugCompilerBuiltInConstants(true, true, false, true, true, false),
                null));

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            OpenedWorkbookPath = workbookPath;
            WorkbookOpenInputWaitSink = inputWaitSink;
            events.Add($"open:{Path.GetFileName(workbookPath)}");
            return Task.CompletedTask;
        }

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            Breakpoints = breakpoints;
            events.Add("set-breakpoints");
            return Task.CompletedTask;
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            Target = target;
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync() => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDebugLifecycleSink : IDebugLifecycleSink
    {
        public List<DebugLifecycleMessage> Messages { get; } = [];

        public ValueTask WriteAsync(
            DebugLifecycleMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
