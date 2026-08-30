using System.Text.Json;
using System.Text.Json.Serialization;
using VbaTools.ContentLengthFraming;

namespace VbaDebugAdapter.Protocol;

internal sealed class DapConnection
{
    // Launch frames carry a complete base64 source inventory, including image-heavy
    // UserForm sidecars. This bounds allocation while allowing 192 MiB of raw bytes.
    internal const int MaximumContentLength = 256 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ContentLengthFrameTransport framing;
    private int outgoingSequence;

    public DapConnection(Stream input, Stream output)
        : this(input, output, MaximumContentLength)
    {
    }

    internal DapConnection(
        Stream input,
        Stream output,
        int maximumContentLength)
    {
        framing = new ContentLengthFrameTransport(
            input,
            output,
            maximumContentLength);
    }

    public async Task<DapRequest?> ReadRequestAsync(CancellationToken cancellationToken)
    {
        var content = await framing
            .ReadFrameAsync(cancellationToken)
            .ConfigureAwait(false);
        if (content is null)
        {
            return null;
        }
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

    private Task WriteMessageAsync(
        Func<int, object> createMessage,
        CancellationToken cancellationToken)
        => framing.WriteFrameAsync(
            () => JsonSerializer.SerializeToUtf8Bytes(
                createMessage(++outgoingSequence),
                JsonOptions),
            cancellationToken);
}

internal sealed record DapRequest(
    int Sequence,
    string Command,
    JsonElement Arguments);
