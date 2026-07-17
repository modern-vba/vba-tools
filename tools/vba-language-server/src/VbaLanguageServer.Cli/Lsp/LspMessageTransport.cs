using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Reads and writes LSP JSON-RPC messages over byte streams.
/// </summary>
internal sealed class LspMessageTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim outputLock = new(1, 1);

    /// <summary>
    /// Creates a message transport over input and output streams.
    /// </summary>
    /// <param name="input">The stream used to read LSP messages.</param>
    /// <param name="output">The stream used to write LSP messages.</param>
    public LspMessageTransport(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    /// <summary>
    /// Reads one JSON-RPC message from the input stream.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the read.</param>
    /// <returns>The parsed JSON object, or null on EOF or invalid framing.</returns>
    public async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var headers = await ReadHeaderAsync(cancellationToken);
        if (headers is null)
        {
            return null;
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText)
            || !int.TryParse(lengthText, out var contentLength))
        {
            return null;
        }

        var content = new byte[contentLength];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await input.ReadAsync(content.AsMemory(offset, content.Length - offset), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return JsonNode.Parse(content)?.AsObject();
    }

    /// <summary>
    /// Writes a successful JSON-RPC response.
    /// </summary>
    /// <param name="idNode">The request id node to echo.</param>
    /// <param name="result">The response result payload.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    public Task WriteResponseAsync(JsonNode? idNode, object? result, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            result
        }, cancellationToken);
    }

    /// <summary>
    /// Writes an error JSON-RPC response.
    /// </summary>
    /// <param name="idNode">The request id node to echo.</param>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">The JSON-RPC error message.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    public Task WriteErrorResponseAsync(
        JsonNode? idNode,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            error = new
            {
                code,
                message
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Writes a JSON-RPC notification.
    /// </summary>
    /// <param name="method">The notification method name.</param>
    /// <param name="parameters">The notification parameters payload.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    public Task WriteNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, cancellationToken);
    }

    /// <summary>
    /// Writes a window/logMessage notification.
    /// </summary>
    /// <param name="type">The LSP message type.</param>
    /// <param name="message">The message text.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    public Task WriteLogMessageAsync(int type, string message, CancellationToken cancellationToken)
    {
        return WriteNotificationAsync(
            "window/logMessage",
            new
            {
                type,
                message
            },
            cancellationToken);
    }

    private async Task<Dictionary<string, string>?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var singleByte = new byte[1];
        while (true)
        {
            var read = await input.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            buffer.GetSpan(1)[0] = singleByte[0];
            buffer.Advance(1);

            var written = buffer.WrittenSpan;
            if (written.Length >= 4
                && written[^4] == '\r'
                && written[^3] == '\n'
                && written[^2] == '\r'
                && written[^1] == '\n')
            {
                break;
            }

        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerText = Encoding.ASCII.GetString(buffer.WrittenSpan);
        foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var delimiter = line.IndexOf(':');
            if (delimiter <= 0)
            {
                continue;
            }

            headers[line[..delimiter]] = line[(delimiter + 1)..].Trim();
        }

        return headers;
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await outputLock.WaitAsync(cancellationToken);
        try
        {
            await output.WriteAsync(header, cancellationToken);
            await output.WriteAsync(content, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            outputLock.Release();
        }
    }
}
