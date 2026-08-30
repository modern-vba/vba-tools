using System.Text.Json;
using System.Text.Json.Nodes;
using VbaTools.ContentLengthFraming;

namespace VbaLanguageServer.Lsp;

/// <summary>
/// Reads and writes LSP JSON-RPC messages over byte streams.
/// </summary>
internal sealed class LspMessageTransport
{
    internal const int MaximumContentLength = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ContentLengthFrameTransport framing;

    /// <summary>
    /// Creates a message transport over input and output streams.
    /// </summary>
    /// <param name="input">The stream used to read LSP messages.</param>
    /// <param name="output">The stream used to write LSP messages.</param>
    public LspMessageTransport(Stream input, Stream output)
        : this(input, output, MaximumContentLength)
    {
    }

    internal LspMessageTransport(
        Stream input,
        Stream output,
        int maximumContentLength)
    {
        framing = new ContentLengthFrameTransport(
            input,
            output,
            maximumContentLength);
    }

    /// <summary>
    /// Reads one JSON-RPC message from the input stream.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token for the read.</param>
    /// <returns>The parsed JSON object, or null on clean EOF before a frame.</returns>
    public async Task<JsonObject?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var content = await framing.ReadFrameAsync(cancellationToken);
        return content is null
            ? null
            : JsonNode.Parse(content)?.AsObject();
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
        => WriteErrorResponseAsync(
            idNode,
            code,
            message,
            data: null,
            cancellationToken);

    /// <summary>
    /// Writes a JSON-RPC error response with structured error data.
    /// </summary>
    /// <param name="idNode">The request id node to echo.</param>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">The JSON-RPC error message.</param>
    /// <param name="data">The structured error data.</param>
    /// <param name="cancellationToken">A cancellation token for the write.</param>
    public Task WriteErrorResponseAsync(
        JsonNode? idNode,
        int code,
        string message,
        object? data,
        CancellationToken cancellationToken)
    {
        if (data is null)
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

        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id = idNode,
            error = new
            {
                code,
                message,
                data
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

    private Task WriteMessageAsync(object message, CancellationToken cancellationToken)
        => framing.WriteFrameAsync(
            () => JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions),
            cancellationToken);
}
