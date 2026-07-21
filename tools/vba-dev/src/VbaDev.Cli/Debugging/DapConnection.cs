using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaDev.Cli.Debugging;

internal sealed class DapConnection
{
    private const int MaximumContentLength = 4 * 1024 * 1024;

    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int outgoingSequence;

    public DapConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public async Task<DapRequest?> ReadRequestAsync(CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        if (!header.StartsWith("Content-Length: ", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(header["Content-Length: ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var contentLength) ||
            contentLength < 0 ||
            contentLength > MaximumContentLength)
        {
            throw new InvalidDataException("DAP input must contain one valid Content-Length header.");
        }

        var content = new byte[contentLength];
        await input.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("seq", out var seq) ||
            !seq.TryGetInt32(out var requestSequence) ||
            !root.TryGetProperty("type", out var type) ||
            !string.Equals(type.GetString(), "request", StringComparison.Ordinal) ||
            !root.TryGetProperty("command", out var command) ||
            string.IsNullOrWhiteSpace(command.GetString()))
        {
            throw new InvalidDataException("DAP input was not a valid request message.");
        }

        var arguments = root.TryGetProperty("arguments", out var value)
            ? value.Clone()
            : default;
        return new DapRequest(requestSequence, command.GetString()!, arguments);
    }

    public Task WriteResponseAsync(
        DapRequest request,
        bool success,
        object? body,
        string? message,
        CancellationToken cancellationToken)
        => WriteMessageAsync(
            sequence => new
            {
                seq = sequence,
                type = "response",
                request_seq = request.Sequence,
                success,
                command = request.Command,
                message,
                body
            },
            cancellationToken);

    public Task WriteEventAsync(
        string eventName,
        object? body,
        CancellationToken cancellationToken)
        => WriteMessageAsync(
            sequence => new
            {
                seq = sequence,
                type = "event",
                @event = eventName,
                body
            },
            cancellationToken);

    private async Task<string?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var singleByte = new byte[1];
        while (true)
        {
            var count = await input.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("DAP input ended inside a message header.");
            }

            bytes.Add(singleByte[0]);
            if (bytes.Count >= 4 &&
                bytes[^4] == '\r' &&
                bytes[^3] == '\n' &&
                bytes[^2] == '\r' &&
                bytes[^1] == '\n')
            {
                return Encoding.ASCII.GetString(bytes[..^4].ToArray());
            }

            if (bytes.Count > 1024)
            {
                throw new InvalidDataException("DAP message header exceeded the supported length.");
            }
        }
    }

    private async Task WriteMessageAsync(
        Func<int, object> createMessage,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var message = createMessage(++outgoingSequence);
            var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed record DapRequest(
    int Sequence,
    string Command,
    JsonElement Arguments);
