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
}
