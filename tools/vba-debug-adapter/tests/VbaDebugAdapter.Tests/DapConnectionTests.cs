using System.Text;
using System.Text.Json;
using VbaDebugAdapter.Protocol;
using VbaTools.ContentLengthFraming;
using Xunit;

namespace VbaDebugAdapter.Tests;

public sealed class DapConnectionTests
{
    [Fact]
    public async Task Clean_EOF_before_a_frame_returns_null()
    {
        var connection = new DapConnection(Stream.Null, Stream.Null);

        Assert.Null(await connection.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Byte_at_a_time_input_preserves_the_protocol_local_DAP_request()
    {
        var body = CreateRequestBody();
        await using var input = new ByteAtATimeReadStream(CreateFrame(body));
        var connection = new DapConnection(input, Stream.Null);

        var request = Assert.IsType<DapRequest>(
            await connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(1, request.Sequence);
        Assert.Equal("initialize", request.Command);
    }

    [Fact]
    public async Task Clean_EOF_after_two_adjacent_frames_returns_null_for_the_next_frame()
    {
        var firstFrame = CreateFrame(CreateRequestBody(1, "initialize"));
        var secondFrame = CreateFrame(CreateRequestBody(2, "configurationDone"));
        var inputBytes = new byte[firstFrame.Length + secondFrame.Length];
        firstFrame.CopyTo(inputBytes, 0);
        secondFrame.CopyTo(inputBytes, firstFrame.Length);
        await using var input = new MemoryStream(inputBytes);
        var connection = new DapConnection(input, Stream.Null);

        var first = Assert.IsType<DapRequest>(
            await connection.ReadRequestAsync(CancellationToken.None));
        var second = Assert.IsType<DapRequest>(
            await connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Null(await connection.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_header_of_exactly_one_KiB_is_accepted()
    {
        var body = CreateRequestBody();
        await using var input = new MemoryStream(
            CreateFrame(body, ContentLengthFrameTransport.MaximumHeaderSize));
        var connection = new DapConnection(input, Stream.Null);

        Assert.NotNull(await connection.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_header_larger_than_one_KiB_is_a_typed_failure()
    {
        var body = CreateRequestBody();
        await using var input = new MemoryStream(
            CreateFrame(body, ContentLengthFrameTransport.MaximumHeaderSize + 1));
        var connection = new DapConnection(input, Stream.Null);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(ContentLengthFramingFailureKind.HeaderOverflow, failure.Kind);
    }

    [Theory]
    [InlineData(
        "",
        ContentLengthFramingFailureKind.MissingContentLength)]
    [InlineData(
        "Content-Type: application/vscode-jsonrpc; charset=utf-8",
        ContentLengthFramingFailureKind.MissingContentLength)]
    [InlineData(
        "Content-Length: 2\r\nContent-Length: 2",
        ContentLengthFramingFailureKind.DuplicateContentLength)]
    [InlineData(
        "Content-Length: two",
        ContentLengthFramingFailureKind.NonNumericContentLength)]
    [InlineData(
        "Content-Length: -1",
        ContentLengthFramingFailureKind.NegativeContentLength)]
    [InlineData(
        "Content-Length: 268435457",
        ContentLengthFramingFailureKind.OversizedContentLength)]
    [InlineData(
        "not-a-header",
        ContentLengthFramingFailureKind.MalformedHeader)]
    public async Task Malformed_framing_has_a_specific_typed_failure(
        string header,
        ContentLengthFramingFailureKind expectedKind)
    {
        await using var input = new MemoryStream(
            Encoding.ASCII.GetBytes($"{header}\r\n\r\n"));
        var connection = new DapConnection(input, Stream.Null);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(expectedKind, failure.Kind);
    }

    [Fact]
    public async Task Truncated_header_is_a_typed_framing_failure()
    {
        await using var input = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 2\r\n"));
        var connection = new DapConnection(input, Stream.Null);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(
            ContentLengthFramingFailureKind.TruncatedHeader,
            failure.Kind);
    }

    [Fact]
    public async Task Truncated_body_is_a_typed_framing_failure()
    {
        await using var input = new MemoryStream(
            Encoding.ASCII.GetBytes("Content-Length: 2\r\n\r\n{"));
        var connection = new DapConnection(input, Stream.Null);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(ContentLengthFramingFailureKind.TruncatedBody, failure.Kind);
    }

    [Fact]
    public void DAP_body_limit_is_256_MiB()
    {
        Assert.Equal(256 * 1024 * 1024, DapConnection.MaximumContentLength);
    }

    [Fact]
    public async Task A_body_at_the_configured_DAP_limit_is_accepted()
    {
        var body = CreateRequestBody();
        await using var input = new MemoryStream(CreateFrame(body));
        var connection = new DapConnection(
            input,
            Stream.Null,
            body.Length);

        Assert.NotNull(await connection.ReadRequestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_body_one_byte_over_the_configured_DAP_limit_is_rejected()
    {
        var body = CreateRequestBody();
        await using var input = new MemoryStream(CreateFrame(body));
        var connection = new DapConnection(
            input,
            Stream.Null,
            body.Length - 1);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.ReadRequestAsync(CancellationToken.None));

        Assert.Equal(
            ContentLengthFramingFailureKind.OversizedContentLength,
            failure.Kind);
    }

    [Fact]
    public async Task An_outbound_body_over_the_configured_DAP_limit_writes_zero_bytes()
    {
        await using var output = new MemoryStream();
        var connection = new DapConnection(Stream.Null, output, 1);

        var failure = await Assert.ThrowsAsync<ContentLengthFramingException>(
            () => connection.WriteEventAsync(
                "event",
                body: null,
                CancellationToken.None));

        Assert.Equal(
            ContentLengthFramingFailureKind.OversizedContentLength,
            failure.Kind);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task Concurrent_outbound_messages_preserve_complete_frames_and_wire_sequence()
    {
        await using var output = new YieldingCaptureStream();
        var connection = new DapConnection(Stream.Null, output);

        var writes = Enumerable.Range(1, 32)
            .Select(requestSequence => connection.WriteResponseAsync(
                new DapRequest(requestSequence, "test", default),
                success: true,
                body: new { value = requestSequence },
                message: null,
                CancellationToken.None));
        await Task.WhenAll(writes);

        Assert.False(output.ConcurrentOperationObserved);
        var messages = ReadMessages(output.ToArray());
        Assert.Equal(32, messages.Count);
        Assert.Equal(
            Enumerable.Range(1, 32),
            messages.Select(message => message.GetProperty("seq").GetInt32()));
        Assert.Equal(
            Enumerable.Range(1, 32),
            messages
                .Select(message => message.GetProperty("request_seq").GetInt32())
                .Order());
    }

    [Fact]
    public async Task Cancellation_after_an_outbound_frame_starts_completes_that_frame()
    {
        await using var output = new PausingCaptureStream();
        var connection = new DapConnection(Stream.Null, output);
        using var cancellation = new CancellationTokenSource();

        var firstWrite = connection.WriteEventAsync(
            "first",
            new { value = 1 },
            cancellation.Token);
        await output.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        output.ReleaseFirstWrite();
        await firstWrite.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.WriteEventAsync(
            "second",
            new { value = 2 },
            CancellationToken.None);

        var messages = ReadMessages(output.ToArray());
        Assert.Equal(["first", "second"], messages
            .Select(message => message.GetProperty("event").GetString()));
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_output_ownership_starts_no_frame()
    {
        await using var output = new PausingCaptureStream();
        var connection = new DapConnection(Stream.Null, output);
        using var cancellation = new CancellationTokenSource();

        var firstWrite = connection.WriteEventAsync(
            "first",
            body: null,
            CancellationToken.None);
        await output.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelledWrite = connection.WriteEventAsync(
            "cancelled",
            body: null,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledWrite.WaitAsync(TimeSpan.FromSeconds(5)));
        output.ReleaseFirstWrite();
        await firstWrite.WaitAsync(TimeSpan.FromSeconds(5));

        var message = Assert.Single(ReadMessages(output.ToArray()));
        Assert.Equal("first", message.GetProperty("event").GetString());
    }

    [Fact]
    public async Task Cancellation_during_DAP_serialization_starts_no_frame()
    {
        await using var output = new MemoryStream();
        var connection = new DapConnection(Stream.Null, output);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => connection.WriteEventAsync(
                "event",
                new CancellationDuringSerialization(cancellation),
                cancellation.Token));

        Assert.Empty(output.ToArray());
    }

    private static byte[] CreateRequestBody(
        int sequence = 1,
        string command = "initialize")
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            seq = sequence,
            type = "request",
            command,
            arguments = new { }
        });

    private static byte[] CreateFrame(byte[] body, int? totalHeaderSize = null)
    {
        var prefix = $"Content-Length: {body.Length}\r\nX-Fill: ";
        const string suffix = "\r\n\r\n";
        var fillerSize = totalHeaderSize is null
            ? 0
            : totalHeaderSize.Value - Encoding.ASCII.GetByteCount(prefix + suffix);
        Assert.True(fillerSize >= 0);
        var header = Encoding.ASCII.GetBytes(
            prefix + new string('x', fillerSize) + suffix);
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        return frame;
    }

    private static IReadOnlyList<JsonElement> ReadMessages(byte[] framedMessages)
    {
        var messages = new List<JsonElement>();
        var offset = 0;
        while (offset < framedMessages.Length)
        {
            var headerEnd = framedMessages.AsSpan(offset).IndexOf("\r\n\r\n"u8);
            Assert.True(headerEnd >= 0);
            var header = Encoding.ASCII.GetString(
                framedMessages,
                offset,
                headerEnd);
            var contentLengthLine = Assert.Single(
                header.Split("\r\n", StringSplitOptions.None),
                line => line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase));
            var contentLength = int.Parse(
                contentLengthLine[(contentLengthLine.IndexOf(':') + 1)..].Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            offset += headerEnd + 4;
            Assert.True(offset + contentLength <= framedMessages.Length);
            using var document = JsonDocument.Parse(
                framedMessages.AsMemory(offset, contentLength));
            messages.Add(document.RootElement.Clone());
            offset += contentLength;
        }

        return messages;
    }

    private sealed class ByteAtATimeReadStream : Stream
    {
        private readonly MemoryStream content;

        public ByteAtATimeReadStream(byte[] content)
        {
            this.content = new MemoryStream(content);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => content.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => content.Read(buffer, offset, Math.Min(1, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => content.ReadAsync(
                buffer[..Math.Min(1, buffer.Length)],
                cancellationToken);

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
                content.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancellationDuringSerialization
    {
        private readonly CancellationTokenSource cancellation;

        public CancellationDuringSerialization(
            CancellationTokenSource cancellation)
        {
            this.cancellation = cancellation;
        }

        public string Value
        {
            get
            {
                cancellation.Cancel();
                return "cancelled";
            }
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
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                var prefixLength = Math.Max(1, buffer.Length / 2);
                lock (gate)
                {
                    content.Write(buffer.Span[..prefixLength]);
                }

                FirstWriteStarted.TrySetResult();
                await releaseFirstWrite.Task;
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    content.Write(buffer.Span[prefixLength..]);
                }

                return;
            }

            lock (gate)
            {
                content.Write(buffer.Span);
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
