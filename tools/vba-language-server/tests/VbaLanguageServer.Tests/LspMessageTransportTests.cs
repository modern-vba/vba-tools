using VbaLanguageServer.Lsp;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LspMessageTransportTests
{
    [Fact]
    public async Task Header_read_honors_host_cancellation_while_waiting_for_input()
    {
        using var input = new CancellationGateStream();
        var transport = new LspMessageTransport(input, Stream.Null);
        using var cancellation = new CancellationTokenSource();
        var read = Task.Run(
            () => transport.ReadMessageAsync(cancellation.Token));
        await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await read.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            input.ReleaseSynchronousRead();
            try
            {
                await read;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Concurrent_outbound_messages_use_one_writer_and_preserve_complete_frames()
    {
        await using var output = new YieldingCaptureStream();
        var transport = new LspMessageTransport(Stream.Null, output);

        var writes = Enumerable.Range(1, 32)
            .Select(id => transport.WriteResponseAsync(
                System.Text.Json.Nodes.JsonValue.Create(id),
                new
                {
                    value = id
                },
                CancellationToken.None));
        await Task.WhenAll(writes);

        Assert.False(output.ConcurrentOperationObserved);
        await using var input = new MemoryStream(output.ToArray());
        var reader = new LspMessageTransport(input, Stream.Null);
        var receivedIds = new List<int>();
        for (var index = 0; index < 32; index++)
        {
            var message = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
                await reader.ReadMessageAsync(CancellationToken.None));
            receivedIds.Add(message["id"]!.GetValue<int>());
        }

        Assert.Equal(
            Enumerable.Range(1, 32),
            receivedIds.Order());
        Assert.Null(await reader.ReadMessageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_after_an_outbound_frame_starts_preserves_it_and_the_following_frame()
    {
        await using var output = new PausingCaptureStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        using var cancellation = new CancellationTokenSource();

        var firstWrite = transport.WriteResponseAsync(
            System.Text.Json.Nodes.JsonValue.Create(1),
            new { value = "first" },
            cancellation.Token);
        await output.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        output.ReleaseFirstWrite();
        await firstWrite.WaitAsync(TimeSpan.FromSeconds(5));
        await transport.WriteResponseAsync(
            System.Text.Json.Nodes.JsonValue.Create(2),
            new { value = "second" },
            CancellationToken.None);

        await using var input = new MemoryStream(output.ToArray());
        var reader = new LspMessageTransport(input, Stream.Null);
        var first = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
            await reader.ReadMessageAsync(CancellationToken.None));
        var second = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
            await reader.ReadMessageAsync(CancellationToken.None));

        Assert.Equal(1, first["id"]!.GetValue<int>());
        Assert.Equal(2, second["id"]!.GetValue<int>());
        Assert.Null(await reader.ReadMessageAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_output_ownership_starts_no_frame()
    {
        await using var output = new PausingCaptureStream();
        var transport = new LspMessageTransport(Stream.Null, output);
        using var cancellation = new CancellationTokenSource();

        var firstWrite = transport.WriteResponseAsync(
            System.Text.Json.Nodes.JsonValue.Create(1),
            new { value = "first" },
            CancellationToken.None);
        await output.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelledWrite = transport.WriteResponseAsync(
            System.Text.Json.Nodes.JsonValue.Create(2),
            new { value = "cancelled" },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledWrite.WaitAsync(TimeSpan.FromSeconds(5)));
        output.ReleaseFirstWrite();
        await firstWrite.WaitAsync(TimeSpan.FromSeconds(5));

        await using var input = new MemoryStream(output.ToArray());
        var reader = new LspMessageTransport(input, Stream.Null);
        var first = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
            await reader.ReadMessageAsync(CancellationToken.None));

        Assert.Equal(1, first["id"]!.GetValue<int>());
        Assert.Null(await reader.ReadMessageAsync(CancellationToken.None));
    }

    private sealed class CancellationGateStream : Stream
    {
        private readonly ManualResetEventSlim synchronousReadRelease = new();

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void ReleaseSynchronousRead()
            => synchronousReadRelease.Set();

        public override int ReadByte()
        {
            Started.TrySetResult();
            synchronousReadRelease.Wait();
            return -1;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadByte();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                synchronousReadRelease.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class YieldingCaptureStream : Stream
    {
        private readonly object gate = new();
        private readonly MemoryStream content = new();
        private int activeOperations;

        public bool ConcurrentOperationObserved { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray()
        {
            lock (gate)
            {
                return content.ToArray();
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnterOperation();
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    content.Write(buffer.Span);
                }

                await Task.Yield();
            }
            finally
            {
                ExitOperation();
            }
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            EnterOperation();
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                ExitOperation();
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
            }

            base.Dispose(disposing);
        }

        private void EnterOperation()
        {
            if (Interlocked.Increment(ref activeOperations) != 1)
            {
                ConcurrentOperationObserved = true;
            }
        }

        private void ExitOperation()
            => Interlocked.Decrement(ref activeOperations);
    }

    private sealed class PausingCaptureStream : Stream
    {
        private readonly object gate = new();
        private readonly MemoryStream content = new();
        private readonly TaskCompletionSource releaseFirstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writeCount;

        public TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray()
        {
            lock (gate)
            {
                return content.ToArray();
            }
        }

        public void ReleaseFirstWrite()
            => releaseFirstWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                content.Write(buffer.Span);
            }

            if (Interlocked.Increment(ref writeCount) == 1)
            {
                FirstWriteStarted.TrySetResult();
                await releaseFirstWrite.Task;
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                releaseFirstWrite.TrySetResult();
                content.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
