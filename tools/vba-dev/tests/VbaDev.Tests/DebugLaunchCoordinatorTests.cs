using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugLaunchCoordinatorTests
{
    [Fact]
    public async Task OneBreakpointIsSetAndVerifiedBeforeTheTargetRuns()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 4);
        var mapped = new VbeBreakpoint(requested, "DebugModule", VbideLine: 4, "    value = 1");
        var snapshot = CreateSourceSnapshot() with
        {
            Breakpoints = [requested]
        };
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(events, mapped));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                snapshot),
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None);

        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                $"map:{sourcePath}:4",
                "set:DebugModule:4:    value = 1",
                $"verified:{sourcePath}:4",
                "run:DebugModule.RunTarget"
            ],
            events);

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task AFailedNativeBreakpointIsNeitherVerifiedNorFollowedByTargetExecution()
    {
        var context = CreateContext();
        var sourcePath = Path.Combine(context.DocumentSourceSetPath, "DebugModule.bas");
        var requested = new DebugSourceBreakpoint(sourcePath, EditorLine: 4);
        var mapped = new VbeBreakpoint(requested, "DebugModule", VbideLine: 4, "    value = 1");
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events)
        {
            SetError = new DebugSetupException("The native Toggle Breakpoint command is disabled.")
        };
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession),
            new FakeBreakpointSourceMapper(events, mapped));
        var snapshot = CreateSourceSnapshot() with
        {
            Breakpoints = [requested]
        };

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                snapshot),
            new RecordingDebugLaunchEventSink(events),
            CancellationToken.None));

        Assert.Equal("The native Toggle Breakpoint command is disabled.", error.Message);
        Assert.DoesNotContain(events, item => item.StartsWith("verified:", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.StartsWith("run:", StringComparison.Ordinal));
        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task AnExplicitZeroBreakpointLaunchBuildsThenRunsTheManifestBinWorkbook()
    {
        var context = CreateContext();
        var events = new List<string>();
        var builder = new FakeDebugWorkbookBuilder(events);
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            builder,
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                context,
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None);

        Assert.Equal(31415, running.ProcessId);
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open:{context.BinDocumentPath}",
                "run:DebugModule.RunTarget"
            ],
            events);

        vbeSession.Exit(0);
        Assert.Equal(0, (await running.Completion).ExitCode);
    }

    [Fact]
    public async Task BuildWarningsAreReportedWithoutPreventingVisibleExcel()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var lifecycle = new RecordingDebugLifecycleSink();
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events, ["WARN Book1/Protected Library remains."]),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var running = await coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            lifecycle,
            CancellationToken.None);

        Assert.Contains("WARN Book1/Protected Library remains.", lifecycle.Output);
        Assert.Contains("start-visible", events);

        vbeSession.Exit(0);
        await running.Completion;
    }

    [Fact]
    public async Task ABuildErrorPreventsVisibleExcelFromStarting()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FailingDebugWorkbookBuilder(events, new DebugSetupException("Build failed.")),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Equal("Build failed.", error.Message);
        Assert.Equal(["build:Book1"], events);
    }

    [Fact]
    public async Task ASetupErrorAfterExcelStartsTerminatesTheOwnedProcess()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events)
        {
            OpenError = new DebugSetupException("The native Run command is disabled.")
        };
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession));

        var error = await Assert.ThrowsAsync<DebugSetupException>(() => coordinator.LaunchAsync(
            new DebugLaunchRequest(
                CreateContext(),
                new DebugTargetProcedure("DebugModule", "RunTarget"),
                CreateSourceSnapshot()),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Equal("The native Run command is disabled.", error.Message);
        Assert.True(vbeSession.Terminated);
        Assert.True(vbeSession.Disposed);
    }

    [Fact]
    public async Task ASecondProcessLocalLaunchIsRejectedWhileExcelIsStillRunning()
    {
        var events = new List<string>();
        var vbeSession = new FakeVbeDebugSession(events);
        var coordinator = new DebugLaunchCoordinator(
            new FakeDebugWorkbookBuilder(events),
            new FakeVbeDebugSessionFactory(events, vbeSession));
        var request = new DebugLaunchRequest(
            CreateContext(),
            new DebugTargetProcedure("DebugModule", "RunTarget"),
            CreateSourceSnapshot());

        var running = await coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<DebugLaunchBusyException>(() => coordinator.LaunchAsync(
            request,
            new RecordingDebugLifecycleSink(),
            CancellationToken.None));

        Assert.Contains("already active", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, events.Count(item => item == "start-visible"));

        vbeSession.Exit(0);
        await running.Completion;
    }

    private static ResolvedProjectContext CreateContext()
    {
        var root = Path.GetFullPath(Path.Combine("DebugProject", Guid.NewGuid().ToString("N")));
        var document = ProjectDocument.CreateExcel("Book1") with
        {
            BinPath = "custom-bin/GeneratedBook.xlsm"
        };
        var manifest = new ProjectManifest(
            ProjectManifest.CurrentSchemaVersion,
            "DebugProject",
            "Book1",
            new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = document
            });

        return new ResolvedProjectContext(
            root,
            Path.Combine(root, ProjectManifest.ManifestFileName),
            manifest,
            "Book1",
            document,
            Path.Combine(root, "src", "Book1"),
            Path.Combine(root, "src", "Book1", "Book1.xlsm"),
            Path.Combine(root, "custom-bin", "GeneratedBook.xlsm"),
            Path.Combine(root, "publish", "Book1.xlsm"),
            null);
    }

    private static DebugSourceSnapshot CreateSourceSnapshot()
        => new(DebugSourceSnapshot.CurrentSchemaVersion, [], null);

    private sealed class FakeDebugWorkbookBuilder(
        List<string> events,
        IReadOnlyList<string>? output = null) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromResult(new DebugWorkbookBuildResult(output ?? []));
        }
    }

    private sealed class FailingDebugWorkbookBuilder(
        List<string> events,
        Exception error) : IDebugWorkbookBuilder
    {
        public Task<DebugWorkbookBuildResult> BuildAsync(
            ResolvedProjectContext context,
            CancellationToken cancellationToken)
        {
            events.Add($"build:{context.DocumentName}");
            return Task.FromException<DebugWorkbookBuildResult>(error);
        }
    }

    private sealed class FakeVbeDebugSessionFactory(
        List<string> events,
        IVbeDebugSession session) : IVbeDebugSessionFactory
    {
        public Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
        {
            events.Add("start-visible");
            return Task.FromResult(session);
        }
    }

    private sealed class FakeBreakpointSourceMapper(
        List<string> events,
        VbeBreakpoint mapped) : IBreakpointSourceMapper
    {
        public VbeBreakpoint Map(DebugSourceSnapshot snapshot, DebugSourceBreakpoint breakpoint)
        {
            events.Add($"map:{breakpoint.SourcePath}:{breakpoint.EditorLine}");
            return mapped;
        }
    }

    private sealed class FakeVbeDebugSession(List<string> events) : IVbeDebugSession
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 31415;

        public Task<DebugProcessExit> Completion => completion.Task;

        public Exception? OpenError { get; init; }

        public Exception? SetError { get; init; }

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            events.Add($"open:{workbookPath}");
            return OpenError is null
                ? Task.CompletedTask
                : Task.FromException(OpenError);
        }

        public Task SetNativeBreakpointAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            events.Add(
                $"set:{breakpoint.ModuleName}:{breakpoint.VbideLine}:{breakpoint.ExpectedCodeLine}");
            return SetError is null
                ? Task.CompletedTask
                : Task.FromException(SetError);
        }

        public Task RunTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            events.Add($"run:{target.ModuleName}.{target.ProcedureName}");
            return Task.CompletedTask;
        }

        public ValueTask TerminateAsync()
        {
            Terminated = true;
            completion.TrySetResult(new DebugProcessExit(-1));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public void Exit(int exitCode) => completion.TrySetResult(new DebugProcessExit(exitCode));
    }

    private sealed class RecordingDebugLifecycleSink : IDebugLifecycleSink
    {
        public List<string> Output { get; } = [];

        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken)
        {
            Output.Add(message.Output);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDebugLaunchEventSink(List<string> events) : IDebugLaunchEventSink
    {
        public ValueTask WriteAsync(DebugLifecycleMessage message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask BreakpointVerifiedAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            events.Add($"verified:{breakpoint.Source.SourcePath}:{breakpoint.Source.EditorLine}");
            return ValueTask.CompletedTask;
        }
    }
}
