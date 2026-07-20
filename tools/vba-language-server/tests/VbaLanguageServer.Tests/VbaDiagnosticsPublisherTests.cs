using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaDiagnosticsPublisherTests
{
    [Fact]
    public async Task DocumentChangeCommitsWithoutAwaitingDiagnosticsTransport()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);

        var apply = pipeline.ApplyAsync(
            new VbaTextDocumentOpenedChange(
                uri,
                1,
                "Public Sub Run()\n    "),
            CancellationToken.None);
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Public Sub Run()\n    ", workspace.GetDocumentText(uri));
        Assert.True(apply.IsCompleted);

        output.ReleaseWrites();
        await apply.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Diagnostics_publication_uses_the_bounded_background_scheduler()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(
            timingSink,
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                EnableConcurrentReads: true,
                MaxConcurrentReads: 1,
                MaxConcurrentBulkReads: 1));
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.AdmitRequest(
            requestId: null,
            "textDocument/hover",
            _ => new object(),
            async (_, cancellationToken) =>
            {
                blockerStarted.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            7,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        await timingSink.WaitForAdmissionAsync("textDocument/diagnostic");

        Assert.False(output.WriteStarted.Task.IsCompleted);

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = publisher.WaitForIdleAsync(uri);
        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Complete);

        Assert.False(timingSink.IsCompleted("textDocument/diagnostic"));
        Assert.False(idle.IsCompleted);
        Assert.False(stop.IsCompleted);

        output.ReleaseWrites();
        await timingSink.WaitForCompletionAsync("textDocument/diagnostic");
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Diagnostics_overflow_retries_only_the_latest_revision_after_capacity_returns()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(uri, 1, "Public Sub Run()\n    ");
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            3,
            "Public Sub Latest()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        Assert.Equal(0, output.MessageCount);
        Assert.True(scheduler.IsAccepting);
        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        var parameters = Assert.IsType<JsonObject>(
            Assert.Single(messages)["params"]);
        Assert.Equal(3, parameters["version"]?.GetValue<int>());
    }

    [Fact]
    public async Task Concurrent_enqueue_cannot_replace_a_newer_pending_revision_with_an_older_one()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VbaInteractiveWorkScheduler(
            options: new VbaInteractiveWorkSchedulerOptions(
                CoalesceSupersededMutations: true,
                MaxOwnedWork: 1));
        var blocker = scheduler.AdmitMutation(async cancellationToken =>
        {
            blockerStarted.TrySetResult();
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var observer = new BlockingFirstRevisionObserver();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace,
            observer);
        publisher.AttachScheduler(scheduler);

        var first = Task.Run(
            () => publisher.PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None));
        await observer.FirstRevisionReserved.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        observer.ReleaseFirstRevision();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        releaseBlocker.TrySetResult();
        await blocker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, output.MessageCount);

        await scheduler.StopAsync(VbaInteractiveStopReason.Complete)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Publication_observer_failure_cannot_strand_pending_diagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace,
            new ThrowingPublicationObserver());
        publisher.AttachScheduler(scheduler);

        Assert.Throws<InvalidOperationException>(
            () =>
            {
                _ = publisher.PublishEmptyDiagnosticsAsync(
                    uri,
                    CancellationToken.None);
            });
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Manifest_diagnostics_do_not_block_the_ordered_mutation_lane()
    {
        const string uri = "file:///C:/work/vba-project.json";
        await using var output = new BlockingWriteStream();
        var timingSink = new SignallingTimingSink();
        await using var scheduler = new VbaInteractiveWorkScheduler(timingSink);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            publisher);
        var mutation = scheduler.AdmitMutation(
            "textDocument/didOpen",
            cancellationToken => pipeline.ApplyAsync(
                new VbaTextDocumentOpenedChange(uri, 1, "{"),
                cancellationToken));

        await mutation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await timingSink.WaitForAdmissionAsync("textDocument/diagnostic");
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = scheduler.StopAsync(VbaInteractiveStopReason.Complete);

        Assert.True(mutation.Completion.IsCompletedSuccessfully);
        Assert.False(stop.IsCompleted);

        output.ReleaseWrites();
        await timingSink.WaitForCompletionAsync("textDocument/diagnostic");
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TrackedDiagnosticsCarryClientDocumentVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            7,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        var parameters = Assert.IsType<JsonObject>(
            messages.Single()["params"]);
        Assert.Equal(7, parameters["version"]?.GetValue<int>());
        Assert.NotNull(parameters["diagnostics"]);
    }

    [Fact]
    public async Task Idle_wait_completes_only_after_the_latest_tombstone_is_terminal()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new BlockingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        var publish = publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await output.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var idle = publisher.WaitForIdleAsync(uri);

        Assert.True(publish.IsCompletedSuccessfully);
        Assert.False(idle.IsCompleted);

        output.ReleaseWrites();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Terminal_publications_release_per_uri_revision_state()
    {
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, Stream.Null),
            workspace);
        publisher.AttachScheduler(scheduler);

        for (var index = 0; index < 32; index++)
        {
            var uri = $"file:///C:/work/Retired{index}.bas";
            await publisher.PublishEmptyDiagnosticsAsync(
                uri,
                CancellationToken.None);
            await publisher.WaitForIdleAsync(uri)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(0, publisher.RetainedRevisionStateCount);
    }

    [Fact]
    public async Task Failed_publication_restarts_the_latest_pending_revision_before_becoming_idle()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new FailingThenCapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);

        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        await output.FirstWriteStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        await publisher.PublishEmptyDiagnosticsAsync(
            uri,
            CancellationToken.None);
        var idle = publisher.WaitForIdleAsync(uri);

        Assert.False(idle.IsCompleted);

        output.ReleaseFirstWriteFailure();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, output.SuccessfulMessageCount);
    }

    [Fact]
    public async Task SupersededQueuedDiagnosticsDoNotPublishOlderClientVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Equal(2, parameters["version"]?.GetValue<int>());
        Assert.DoesNotContain(
            messages.SkipWhile(message =>
                Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() != 2),
            message => Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() == 1);
    }

    [Fact]
    public async Task CloseTombstoneSupersedesQueuedDiagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.CloseDocument(uri));
        await publisher.PublishEmptyDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Null(parameters["version"]);
        Assert.Empty(Assert.IsType<JsonArray>(parameters["diagnostics"]));
    }

    [Fact]
    public async Task CloseAndReopenRejectEarlierLifecycleDiagnostics()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        await using var scheduler = new VbaInteractiveWorkScheduler();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        publisher.AttachScheduler(scheduler);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub BeforeClose()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.CloseDocument(uri));
        await publisher.PublishEmptyDiagnosticsAsync(uri, CancellationToken.None);
        workspace.OpenDocument(
            uri,
            2,
            "Public Sub AfterReopen()\nEnd Sub\n");
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await publisher.WaitForIdleAsync(uri)
            .WaitAsync(TimeSpan.FromSeconds(5));

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Equal(2, parameters["version"]?.GetValue<int>());
        Assert.DoesNotContain(
            messages.SkipWhile(message =>
                Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() != 2),
            message => Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() == 1);
    }

    private static IReadOnlyList<JsonObject> ReadJsonMessages(string text)
    {
        var messages = new List<JsonObject>();
        var offset = 0;
        while (offset < text.Length)
        {
            var headerEnd = text.IndexOf("\r\n\r\n", offset, StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                break;
            }

            var header = text[offset..headerEnd];
            var length = header
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0].Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim()))
                .Single();
            var contentStart = headerEnd + 4;
            var json = text.Substring(contentStart, length);
            messages.Add(JsonNode.Parse(json)!.AsObject());
            offset = contentStart + length;
        }

        return messages;
    }

    private sealed class BlockingWriteStream : Stream
    {
        private readonly TaskCompletionSource writeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseWrites =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource WriteStarted => writeStarted;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public void ReleaseWrites()
            => releaseWrites.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            writeStarted.TrySetResult();
            return new ValueTask(releaseWrites.Task.WaitAsync(cancellationToken));
        }
    }

    private sealed class SignallingTimingSink : IVbaInteractiveWorkTimingSink
    {
        private readonly object gate = new();
        private readonly Dictionary<string, WorkSignals> signals =
            new(StringComparer.Ordinal);

        public bool IsCompleted(string method)
        {
            lock (gate)
            {
                return signals.TryGetValue(method, out var workSignals)
                    && workSignals.Completion.Task.IsCompleted;
            }
        }

        public Task WaitForAdmissionAsync(string method)
            => GetSignals(method).Admission.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForCompletionAsync(string method)
            => GetSignals(method).Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void RecordAdmission(VbaInteractiveWorkAdmissionTiming timing)
            => GetSignals(timing.Method).Admission.TrySetResult();

        public void RecordCompletion(VbaInteractiveWorkCompletionTiming timing)
            => GetSignals(timing.Method).Completion.TrySetResult();

        private WorkSignals GetSignals(string method)
        {
            lock (gate)
            {
                if (!signals.TryGetValue(method, out var workSignals))
                {
                    workSignals = new WorkSignals(
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously),
                        new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously));
                    signals[method] = workSignals;
                }

                return workSignals;
            }
        }

        private sealed record WorkSignals(
            TaskCompletionSource Admission,
            TaskCompletionSource Completion);
    }

    private sealed class BlockingFirstRevisionObserver
        : IVbaDiagnosticsPublicationObserver
    {
        private readonly TaskCompletionSource firstRevisionReserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstRevision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstRevisionReserved
            => firstRevisionReserved;

        public void AfterRevisionReserved(string uri, long revision)
        {
            if (revision != 1)
            {
                return;
            }

            firstRevisionReserved.TrySetResult();
            releaseFirstRevision.Task.GetAwaiter().GetResult();
        }

        public void ReleaseFirstRevision()
            => releaseFirstRevision.TrySetResult();
    }

    private sealed class ThrowingPublicationObserver
        : IVbaDiagnosticsPublicationObserver
    {
        public void AfterRevisionReserved(string uri, long revision)
            => throw new InvalidOperationException(
                "Injected diagnostics observer failure.");
    }

    private sealed class CapturingWriteStream : Stream
    {
        private readonly MemoryStream buffer = new();
        private readonly object gate = new();
        private readonly List<TaskCompletionSource> waiters = [];
        private int messageCount;

        public int MessageCount
        {
            get
            {
                lock (gate)
                {
                    return messageCount;
                }
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public async Task<string> WaitForMessageCountAsync(int count)
        {
            Task wait;
            lock (gate)
            {
                if (messageCount >= count)
                {
                    return Encoding.UTF8.GetString(buffer.ToArray());
                }

                var waiter = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add(waiter);
                wait = waiter.Task;
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            lock (gate)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public string ReadText()
        {
            lock (gate)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                messageCount++;
                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult();
                }

                waiters.Clear();
            }

            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                this.buffer.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingThenCapturingWriteStream : Stream
    {
        private readonly MemoryStream buffer = new();
        private readonly TaskCompletionSource firstWriteStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstWriteFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writeAttempts;

        public TaskCompletionSource FirstWriteStarted => firstWriteStarted;

        public int SuccessfulMessageCount { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public void ReleaseFirstWriteFailure()
            => releaseFirstWriteFailure.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            SuccessfulMessageCount++;
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writeAttempts) == 1)
            {
                firstWriteStarted.TrySetResult();
                await releaseFirstWriteFailure.Task
                    .WaitAsync(cancellationToken);
                throw new IOException("Injected diagnostics transport failure.");
            }

            this.buffer.Write(buffer.Span);
        }
    }

    private sealed class RecordingReferenceCatalogLifecycle : IReferenceCatalogLifecycle
    {
        public void ActivateProject(string activeUri)
        {
        }

        public void ApplyManifestSelectionChange(string uri, string text)
        {
        }

        public void DeactivateManifest(string uri)
        {
        }
    }
}
