using VbaDebugAdapter.Cli;
using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed partial class VbaDebugAdapterCliSurfaceTests
{
    [Fact]
    public async Task KnownRestartSourceRejectionOutputFailureRetainsCauseAndCleansTheCurrentSessionWithoutRetry()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        await using var lease = new RecordingSourceRejectionLease(
            await manager.ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None));
        var currentSession = new RecordingRunningSession();
        var initialService = new RecordingDebugLaunchService(currentSession);
        var resources = new SourceRejectionResourceSpy();
        var rejectingService = new StandaloneVbaDebugLaunchService(new DebugSourceAdmission(932), resources, resources);
        var runner = new StandaloneVbaDebugAdapterStdioRunner(
            new SourceRejectionPreparationSequence(initialService, rejectingService));
        var initialLaunch = CreateSourceRejectionLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 0 };
        var freshLaunch = CreateSourceRejectionLaunchArguments();
        freshLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 1 };
        SetLaunchContent(freshLaunch, "not-base64");
        using var input = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 5, type = "request", command = "threads", arguments = new { } });
        using var output = new DapResponseFailingStream("restart");

        var failure = await Assert.ThrowsAsync<DebugFailureException>(() => runner.RunAsync(
            Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None));

        var outcome = failure.FailureOutcome;
        Assert.Contains("base64", outcome.PrimaryFailure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.CleanupFailures, item =>
            item.Stage == "DAP output" && item.Exception is IOException &&
            item.Exception.Message.Contains("restart response transport failure", StringComparison.Ordinal));
        var ownerOutcome = Assert.IsType<DebugFailureOutcome>(currentSession.CleanupOutcome);
        Assert.False(ownerOutcome.HasUnprovedRelease, ownerOutcome.Describe());
        Assert.DoesNotContain(outcome.Evidence, item =>
            !item.Released && (item.Kind is DebugResourceKind.Process or DebugResourceKind.Com or DebugResourceKind.Handle));
        foreach (var kind in new[] { DebugResourceKind.Process, DebugResourceKind.Com, DebugResourceKind.Handle })
        {
            Assert.Contains(ownerOutcome.Evidence, item => item.Kind == kind && item.Released);
        }
        Assert.All(ownerOutcome.Evidence, item => Assert.Contains(item, outcome.Evidence));
        Assert.Single(initialService.Invocations);
        Assert.Equal(1, currentSession.TerminateCalls);
        Assert.Equal(1, currentSession.DisposeCalls);
        Assert.Equal(0, output.WritesAfterFailure);
        Assert.DoesNotContain("\"request_seq\":5", ReadUtf8(output), StringComparison.Ordinal);
        Assert.Equal(0, lease.GenerationCalls);
        Assert.Equal(0, resources.BuildCalls);
        Assert.Equal(0, resources.ExcelCalls);
    }

    [Fact]
    public async Task KnownRestartSourceRejectionCannotReviveASessionThatExitsDuringItsResponse()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        await using var lease = new RecordingSourceRejectionLease(
            await manager.ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSession = new RecordingRunningSession(completion: completion.Task);
        var initialService = new RecordingDebugLaunchService(currentSession);
        var resources = new SourceRejectionResourceSpy();
        var rejectingService = new StandaloneVbaDebugLaunchService(new DebugSourceAdmission(932), resources, resources);
        var runner = new StandaloneVbaDebugAdapterStdioRunner(
            new SourceRejectionPreparationSequence(initialService, rejectingService));
        var initialLaunch = CreateSourceRejectionLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 0 };
        var freshLaunch = CreateSourceRejectionLaunchArguments();
        freshLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 1 };
        SetLaunchContent(freshLaunch, "not-base64");
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            });
        using var input = new BlockingTailStream(prefix.ToArray());
        using var output = new GatedDapResponseStream(3);
        var invocation = runner.RunAsync(
            Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None);

        try
        {
            await output.ResponseWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, currentSession.TerminateCalls);
            Assert.Equal(0, currentSession.DisposeCalls);

            completion.SetResult(0);
            output.ReleaseResponseWrite();

            Assert.Equal(0, await invocation.WaitAsync(TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(output);
            Assert.False(AdmissionResponse(messages, 3).GetProperty("success").GetBoolean());
            Assert.Contains("base64", AdmissionResponse(messages, 3).GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            var exited = Assert.Single(messages, message =>
                message.TryGetProperty("event", out var name) && name.GetString() == "exited");
            Assert.Equal(0, exited.GetProperty("body").GetProperty("exitCode").GetInt32());
            Assert.Single(messages, message =>
                message.TryGetProperty("event", out var name) && name.GetString() == "terminated");
            Assert.Single(initialService.Invocations);
            Assert.Equal(0, currentSession.TerminateCalls);
            Assert.Equal(1, currentSession.DisposeCalls);
            Assert.Equal(0, lease.GenerationCalls);
            Assert.Equal(0, resources.BuildCalls);
            Assert.Equal(0, resources.ExcelCalls);
        }
        finally
        {
            output.ReleaseResponseWrite();
            input.Complete();
            _ = await invocation;
        }
    }

    [Fact]
    public async Task KnownRestartSourceRejectionRetainsTheCurrentSessionAndItsCompletionMonitor()
    {
        const string sessionId = "0123456789abcdef0123456789abcdef";
        const string preparationId = "fedcba9876543210fedcba9876543210";
        using var temp = TempDirectory.Create();
        var manager = new VbaDebugSessionWorkspaceManager(Path.Combine(temp.Path, "adapter-root"));
        await using var lease = new RecordingSourceRejectionLease(
            await manager.ClaimAsync(DebugSessionId.Parse(sessionId), CancellationToken.None));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSession = new RecordingRunningSession(completion: completion.Task);
        var initialService = new RecordingDebugLaunchService(currentSession);
        var resources = new SourceRejectionResourceSpy();
        var rejectingService = new StandaloneVbaDebugLaunchService(new DebugSourceAdmission(932), resources, resources);
        var runner = new StandaloneVbaDebugAdapterStdioRunner(
            new SourceRejectionPreparationSequence(initialService, rejectingService));
        var initialLaunch = CreateSourceRejectionLaunchArguments();
        initialLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 0 };
        var freshLaunch = CreateSourceRejectionLaunchArguments();
        freshLaunch["__vbaRestartPreparation"] = new { protocolVersion = 1, id = preparationId, generation = 1 };
        SetLaunchContent(freshLaunch, "not-base64");
        using var prefix = CreateDapInput(
            new { seq = 1, type = "request", command = "launch", arguments = initialLaunch },
            new { seq = 2, type = "request", command = "configurationDone", arguments = new { } },
            new { seq = 3, type = "request", command = "restart", arguments = new { } },
            new
            {
                seq = 4,
                type = "request",
                command = "vba/restartPrepared",
                arguments = new
                {
                    sessionId,
                    restartRequestSequence = 3,
                    preparationId,
                    generation = 1,
                    success = true,
                    launch = freshLaunch
                }
            },
            new { seq = 5, type = "request", command = "threads", arguments = new { } });
        using var input = new BlockingTailStream(prefix.ToArray());
        using var output = new MemoryStream();
        var invocation = runner.RunAsync(
            Path.GetFullPath("vba-dev.exe"), lease, input, output, Stream.Null, CancellationToken.None);

        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => ReadUtf8(output).Contains("\"request_seq\":5", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2)));
            var messages = ReadDapMessages(output);
            var restartResponse = AdmissionResponse(messages, 3);
            Assert.False(restartResponse.GetProperty("success").GetBoolean());
            Assert.Contains("base64", restartResponse.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(AdmissionResponse(messages, 5).GetProperty("success").GetBoolean());
            Assert.DoesNotContain(messages, message =>
                message.TryGetProperty("event", out var name) && name.GetString() == "terminated");
            Assert.False(invocation.IsCompleted);
            Assert.Equal(0, currentSession.TerminateCalls);
            Assert.Equal(0, currentSession.DisposeCalls);
            Assert.Equal(0, lease.GenerationCalls);
            Assert.Equal(0, resources.BuildCalls);
            Assert.Equal(0, resources.ExcelCalls);

            completion.SetException(new IOException("The retained session monitor failed."));

            Assert.Equal(1, await invocation.WaitAsync(TimeSpan.FromSeconds(2)));
            messages = ReadDapMessages(output);
            Assert.Single(messages, message =>
                message.TryGetProperty("event", out var name) && name.GetString() == "terminated");
            Assert.Contains(messages, message =>
                message.TryGetProperty("event", out var name) && name.GetString() == "output" &&
                message.GetProperty("body").GetProperty("output").GetString()!.Contains(
                    "The retained session monitor failed.", StringComparison.Ordinal));
            Assert.Single(initialService.Invocations);
            Assert.Equal(1, currentSession.TerminateCalls);
            Assert.Equal(1, currentSession.DisposeCalls);
            Assert.Equal(0, lease.GenerationCalls);
            Assert.Equal(0, resources.BuildCalls);
            Assert.Equal(0, resources.ExcelCalls);
        }
        finally
        {
            input.Complete();
            _ = await invocation;
        }
    }

    private sealed class RecordingSourceRejectionLease(IVbaDebugSessionWorkspaceLease inner)
        : IVbaDebugSessionWorkspaceLease
    {
        public DebugSessionId SessionId => inner.SessionId;

        public string SessionWorkspacePath => inner.SessionWorkspacePath;

        public int GenerationCalls { get; private set; }

        public IVbaDebugGenerationWorkspace CreateGenerationWorkspace(DebugGenerationId generationId, string workbookFileName)
        {
            GenerationCalls++;
            return inner.CreateGenerationWorkspace(generationId, workbookFileName);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
