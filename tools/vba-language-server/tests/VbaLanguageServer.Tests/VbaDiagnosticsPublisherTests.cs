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
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var pipeline = new VbaDocumentChangePipeline(
            workspace,
            new RecordingReferenceCatalogLifecycle(),
            new VbaDiagnosticsPublisher(new LspMessageTransport(Stream.Null, output), workspace));

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
    public async Task TrackedDiagnosticsCarryClientDocumentVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
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
    public async Task SupersededQueuedDiagnosticsDoNotPublishOlderClientVersion()
    {
        const string uri = "file:///C:/work/Worker.bas";
        await using var output = new CapturingWriteStream();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.NotNull(workspace.ChangeDocument(
            uri,
            2,
            "Public Sub Run()\nEnd Sub\n"));
        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await DrainQueuedPublicationsAsync();

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
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
        workspace.OpenDocument(
            uri,
            1,
            "Public Sub Run()\n    ");

        await publisher.PublishTrackedDiagnosticsAsync(uri, CancellationToken.None);
        Assert.True(workspace.CloseDocument(uri));
        await publisher.PublishEmptyDiagnosticsAsync(uri, CancellationToken.None);

        var messages = ReadJsonMessages(
            await output.WaitForMessageCountAsync(1));
        await DrainQueuedPublicationsAsync();

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
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        var publisher = new VbaDiagnosticsPublisher(
            new LspMessageTransport(Stream.Null, output),
            workspace);
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
        await DrainQueuedPublicationsAsync();

        messages = ReadJsonMessages(output.ReadText());
        var parameters = Assert.IsType<JsonObject>(
            messages.Last()["params"]);
        Assert.Equal(2, parameters["version"]?.GetValue<int>());
        Assert.DoesNotContain(
            messages.SkipWhile(message =>
                Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() != 2),
            message => Assert.IsType<JsonObject>(message["params"])["version"]?.GetValue<int>() == 1);
    }

    private static async Task DrainQueuedPublicationsAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            await Task.Yield();
        }
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
