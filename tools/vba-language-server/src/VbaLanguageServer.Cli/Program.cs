using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.SourceModel;

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
                Environment.ExitCode = shutdownRequested ? 0 : 1;
                return;
            }

            await HandleNotificationAsync(method, message["params"], cancellationToken);
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
            case "textDocument/documentSymbol":
                await WriteResponseAsync(idNode, CreateDocumentSymbols(parameters), cancellationToken);
                return;
            case "textDocument/definition":
                await WriteResponseAsync(idNode, CreateDefinitionLocation(parameters), cancellationToken);
                return;
            default:
                await WriteErrorResponseAsync(idNode, -32601, $"Method not found: {method}", cancellationToken);
                return;
        }
    }

    private async Task HandleNotificationAsync(string? method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "textDocument/didOpen":
                await RecordOpenedDocumentAsync(parameters, cancellationToken);
                return;
            case "textDocument/didChange":
                await RecordChangedDocumentAsync(parameters, cancellationToken);
                return;
            default:
                return;
        }
    }

    private async Task RecordOpenedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var text = textDocument?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
        }
    }

    private async Task RecordChangedDocumentAsync(JsonNode? parameters, CancellationToken cancellationToken)
    {
        var textDocument = parameters?["textDocument"];
        var uri = textDocument?["uri"]?.GetValue<string>();
        var changes = parameters?["contentChanges"]?.AsArray();
        var text = changes?.LastOrDefault()?["text"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(uri) && text is not null)
        {
            documents[uri] = text;
            await PublishDiagnosticsAsync(uri, text, cancellationToken);
        }
    }

    private Task PublishDiagnosticsAsync(string uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = VbaSyntaxDiagnostics.Collect(text, uri)
            .Select(diagnostic => new
            {
                code = diagnostic.Code,
                message = diagnostic.Message,
                range = diagnostic.Range,
                severity = 1,
                source = diagnostic.Source
            })
            .ToArray();

        return WriteNotificationAsync(
            "textDocument/publishDiagnostics",
            new
            {
                uri,
                diagnostics
            },
            cancellationToken);
    }

    private static object CreateInitializeResult()
    {
        return new
        {
            capabilities = new
            {
                textDocumentSync = 2,
                definitionProvider = true,
                documentSymbolProvider = true,
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

    private object[] CreateDocumentSymbols(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
        {
            return Array.Empty<object>();
        }

        var sourceIndex = VbaSourceIndex.Build(documents);
        return sourceIndex
            .GetDocumentDefinitions(uri)
            .Select(definition => new
            {
                name = definition.Name,
                kind = GetSymbolKind(definition.Kind),
                range = definition.Range,
                selectionRange = definition.Range
            })
            .ToArray<object>();
    }

    private object? CreateDefinitionLocation(JsonNode? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
        var position = parameters?["position"];
        var line = position?["line"]?.GetValue<int>();
        var character = position?["character"]?.GetValue<int>();
        if (string.IsNullOrEmpty(uri) || line is null || character is null)
        {
            return null;
        }

        var sourceIndex = VbaSourceIndex.Build(documents);
        var definition = sourceIndex.ResolveDefinition(uri, line.Value, character.Value);
        return definition is null
            ? null
            : new
            {
                uri = definition.Uri,
                range = definition.Range
            };
    }

    private static int GetSymbolKind(VbaSourceDefinitionKind kind)
        => kind switch
        {
            VbaSourceDefinitionKind.Module => 2,
            VbaSourceDefinitionKind.Class => 5,
            VbaSourceDefinitionKind.Form => 5,
            VbaSourceDefinitionKind.Procedure => 12,
            VbaSourceDefinitionKind.Property => 7,
            VbaSourceDefinitionKind.Constant => 14,
            VbaSourceDefinitionKind.Variable => 13,
            VbaSourceDefinitionKind.Parameter => 13,
            VbaSourceDefinitionKind.Enum => 10,
            VbaSourceDefinitionKind.EnumMember => 22,
            VbaSourceDefinitionKind.Type => 23,
            VbaSourceDefinitionKind.TypeMember => 8,
            VbaSourceDefinitionKind.Event => 24,
            _ => 13
        };

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

    private Task WriteNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
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
