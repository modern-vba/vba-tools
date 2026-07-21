using VbaDev.App.Debugging;
using VbaDev.App.Projects;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class DebugLaunchCoordinatorTests
{
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
                new DebugTargetProcedure("DebugModule", "RunTarget")),
            new RecordingDebugLifecycleSink(),
            CancellationToken.None);

        Assert.Equal(31415, running.ProcessId);
        Assert.Equal(
            [
                "build:Book1",
                "start-visible",
                $"open-and-run:{context.BinDocumentPath}:DebugModule.RunTarget"
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
                new DebugTargetProcedure("DebugModule", "RunTarget")),
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
                new DebugTargetProcedure("DebugModule", "RunTarget")),
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
                new DebugTargetProcedure("DebugModule", "RunTarget")),
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
            new DebugTargetProcedure("DebugModule", "RunTarget"));

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

    private sealed class FakeVbeDebugSession(List<string> events) : IVbeDebugSession
    {
        private readonly TaskCompletionSource<DebugProcessExit> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 31415;

        public Task<DebugProcessExit> Completion => completion.Task;

        public Exception? OpenError { get; init; }

        public bool Terminated { get; private set; }

        public bool Disposed { get; private set; }

        public Task OpenGeneratedWorkbookAndRunAsync(
            string workbookPath,
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            events.Add($"open-and-run:{workbookPath}:{target.ModuleName}.{target.ProcedureName}");
            return OpenError is null
                ? Task.CompletedTask
                : Task.FromException(OpenError);
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
}
