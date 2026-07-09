using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VbaLanguageServer.Lsp;

internal sealed class LspMessageTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim outputLock = new(1, 1);

    public LspMessageTransport(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

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

    public Task WriteResponseAsync(JsonNode? idNode, object? result, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            result
        }, cancellationToken);
    }

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

    public Task WriteNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        }, cancellationToken);
    }

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
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
            {
                return null;
            }

            buffer.GetSpan(1)[0] = (byte)next;
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

            cancellationToken.ThrowIfCancellationRequested();
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
