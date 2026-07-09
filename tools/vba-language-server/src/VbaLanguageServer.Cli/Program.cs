using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var server = new MinimalLanguageServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync();

internal sealed class MinimalLanguageServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Stream input;
    private readonly Stream output;
    private readonly Dictionary<string, string> documents = new(StringComparer.OrdinalIgnoreCase);
    private bool shutdownRequested;

    public MinimalLanguageServer(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken);
            if (message is null)
            {
                return;
            }

            if (!message.TryGetPropertyValue("method", out var methodNode))
            {
                continue;
            }

            var method = methodNode?.GetValue<string>();
            var hasId = message.TryGetPropertyValue("id", out var idNode);

            if (hasId)
            {
                await HandleRequestAsync(idNode, method, message["params"], cancellationToken);
                continue;
            }

            if (method == "exit")
            {
                return;
            }

            HandleNotification(method, message["params"]);
        }
    }

    private async Task HandleRequestAsync(
        JsonNode? idNode,
        string? method,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
                await WriteResponseAsync(idNode, CreateInitializeResult(), cancellationToken);
                return;
            case "shutdown":
                shutdownRequested = true;
                await WriteResponseAsync(idNode, null, cancellationToken);
                return;
            case "textDocument/completion":
                await WriteResponseAsync(idNode, CreateCompletionItems(), cancellationToken);
                return;
            default:
                await WriteErrorResponseAsync(idNode, -32601, $"Method not found: {method}", cancellationToken);
                return;
        }
    }

    private void HandleNotification(string? method, JsonNode? parameters)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                RecordOpenedDocument(parameters);
                return;
            case "textDocument/didChange":
                RecordChangedDocument(parameters);
                return;
            default:
                return;
        }
    }

    private void RecordOpenedDocument(JsonNode? parameters)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var text = textDocument?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
        }
    }

    private void RecordChangedDocument(JsonNode? parameters)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var changes = parameters?["contentChanges"]?.AsArray();
        var text = changes?.LastOrDefault()?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
        }
    }

    private static object CreateInitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = 2,
                completionProvider = new
                {
                    triggerCharacters = new[] { ".", " " }
                }
            },
            serverInfo = new
            {
                name = "vba-language-server",
                version = "0.1.0"
            }
        };
    }

    private static object[] CreateCompletionItems()
    {
        return new object[]
        {
            new
            {
                label = "CSharpLspTracerBullet",
                kind = 1,
                detail = "C# language server tracer bullet"
            }
        };
    }

    private async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
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

    private Task WriteResponseAsync(JsonNode? idNode, object? result, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            result
        }, cancellationToken);
    }

    private Task WriteErrorResponseAsync(
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

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(content, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
