using System.Text;

namespace VbaTools.ContentLengthFraming;

public enum ContentLengthFramingFailureKind
{
    MissingContentLength,
    DuplicateContentLength,
    NonNumericContentLength,
    NegativeContentLength,
    OversizedContentLength,
    HeaderOverflow,
    TruncatedHeader,
    TruncatedBody,
    MalformedHeader
}

public sealed class ContentLengthFramingException : IOException
{
    public ContentLengthFramingException(
        ContentLengthFramingFailureKind kind,
        string message)
        : base(message)
    {
        Kind = kind;
    }

    public ContentLengthFramingFailureKind Kind { get; }
}

public sealed class ContentLengthFrameTransport
{
    public const int MaximumHeaderSize = 1024;

    private readonly Stream input;
    private readonly Stream output;
    private readonly int maximumBodySize;
    private readonly SemaphoreSlim writeGate = new(1, 1);

    public ContentLengthFrameTransport(
        Stream input,
        Stream output,
        int maximumBodySize)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        if (maximumBodySize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBodySize));
        }

        this.maximumBodySize = maximumBodySize;
    }

    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[MaximumHeaderSize];
        var headerSize = 0;
        var singleByte = new byte[1];
        while (true)
        {
            var count = await input
                .ReadAsync(singleByte, cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                if (headerSize == 0)
                {
                    return null;
                }

                throw Failure(
                    ContentLengthFramingFailureKind.TruncatedHeader,
                    "Input ended inside a Content-Length header.");
            }

            if (headerSize == header.Length)
            {
                throw Failure(
                    ContentLengthFramingFailureKind.HeaderOverflow,
                    $"Content-Length header exceeded {MaximumHeaderSize} bytes.");
            }

            header[headerSize++] = singleByte[0];
            if (headerSize >= 4 &&
                header[headerSize - 4] == '\r' &&
                header[headerSize - 3] == '\n' &&
                header[headerSize - 2] == '\r' &&
                header[headerSize - 1] == '\n')
            {
                break;
            }
        }

        var contentLength = ParseContentLength(header.AsSpan(0, headerSize - 4));
        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var count = await input
                .ReadAsync(body.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw Failure(
                    ContentLengthFramingFailureKind.TruncatedBody,
                    "Input ended inside a Content-Length body.");
            }

            offset += count;
        }

        return body;
    }

    public async Task WriteFrameAsync(
        Func<byte[]> bodyFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bodyFactory);
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = bodyFactory();
            cancellationToken.ThrowIfCancellationRequested();
            if (body.Length > maximumBodySize)
            {
                throw Failure(
                    ContentLengthFramingFailureKind.OversizedContentLength,
                    $"Content-Length exceeded {maximumBodySize} bytes.");
            }

            var header = Encoding.ASCII.GetBytes(
                $"Content-Length: {body.Length}\r\n\r\n");
            var frame = new byte[header.Length + body.Length];
            header.CopyTo(frame, 0);
            body.CopyTo(frame, header.Length);
            cancellationToken.ThrowIfCancellationRequested();
            await output
                .WriteAsync(frame, CancellationToken.None)
                .ConfigureAwait(false);
            await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private int ParseContentLength(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.IsEmpty)
        {
            throw Failure(
                ContentLengthFramingFailureKind.MissingContentLength,
                "Content-Length framing did not contain a Content-Length header.");
        }

        var values = new List<string>();
        var header = Encoding.ASCII.GetString(headerBytes);
        foreach (var line in header.Split("\r\n", StringSplitOptions.None))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw Failure(
                    ContentLengthFramingFailureKind.MalformedHeader,
                    "Content-Length framing contained a malformed header line.");
            }

            if (line[..separator].Trim().Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase))
            {
                values.Add(line[(separator + 1)..].Trim());
            }
        }

        if (values.Count == 0)
        {
            throw Failure(
                ContentLengthFramingFailureKind.MissingContentLength,
                "Content-Length framing did not contain a Content-Length header.");
        }

        if (values.Count != 1)
        {
            throw Failure(
                ContentLengthFramingFailureKind.DuplicateContentLength,
                "Content-Length framing contained duplicate Content-Length headers.");
        }

        var value = values[0];
        if (value.Length > 1 &&
            value[0] == '-' &&
            value.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            throw Failure(
                ContentLengthFramingFailureKind.NegativeContentLength,
                "Content-Length must not be negative.");
        }

        if (value.Length == 0 || value.AsSpan().IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            throw Failure(
                ContentLengthFramingFailureKind.NonNumericContentLength,
                "Content-Length must contain ASCII decimal digits.");
        }

        var length = 0;
        foreach (var character in value)
        {
            var digit = character - '0';
            if (length > maximumBodySize / 10 ||
                (length == maximumBodySize / 10 &&
                 digit > maximumBodySize % 10))
            {
                throw Failure(
                    ContentLengthFramingFailureKind.OversizedContentLength,
                    $"Content-Length exceeded {maximumBodySize} bytes.");
            }

            length = (length * 10) + digit;
        }

        return length;
    }

    private static ContentLengthFramingException Failure(
        ContentLengthFramingFailureKind kind,
        string message)
        => new(kind, message);
}
