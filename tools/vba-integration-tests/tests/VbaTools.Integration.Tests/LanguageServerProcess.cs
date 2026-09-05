using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VbaTools.Integration.Tests;

// This owner communicates only through the public LSP stdio contract. It does not
// link, reference, or build the language server's test or application assemblies.
internal sealed class LanguageServerProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly TempDirectory cache = TempDirectory.Create();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<JsonElement> messages = Channel.CreateUnbounded<JsonElement>();
    private readonly Task stdout;
    private readonly Task<string> stderr;

    private LanguageServerProcess(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["VBA_TOOLS_REFERENCE_CATALOG_CACHE_DIR"] = cache.Path;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not launch the already-built language server.");
        }
        catch
        {
            cache.Dispose();
            lifetime.Dispose();
            throw;
        }

        stderr = process.StandardError.ReadToEndAsync();
        stdout = ReadMessagesAsync();
    }

    public static LanguageServerProcess Start(string executablePath) => new(executablePath);

    public async Task InitializeAsync(object capabilities, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            1,
            "initialize",
            new { processId = Environment.ProcessId, rootUri = (string?)null, capabilities },
            cancellationToken: cancellationToken);
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"LSP initialize failed: {error}");
        }

        await SendNotificationAsync("initialized", new { }, cancellationToken);
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
        => WriteMessageAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);

    public async Task<JsonElement> SendRequestAsync(
        int id,
        string method,
        object? parameters,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        await WriteMessageAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, request.Token);
        await foreach (var message in messages.Reader.ReadAllAsync(request.Token))
        {
            if (!message.TryGetProperty("method", out _)
                && message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                return message;
            }
        }

        var errors = await stderr.WaitAsync(request.Token);
        throw new EndOfStreamException($"Language server exited before replying to {method}. {errors}");
    }

    public async Task ShutdownAsync(int id, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(id, "shutdown", null, cancellationToken: cancellationToken);
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"LSP shutdown failed: {error}");
        }

        await SendNotificationAsync("exit", null, cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (process.ExitCode != 0)
        {
            var errors = await stderr.WaitAsync(cancellationToken);
            throw new InvalidOperationException($"Language server exited with code {process.ExitCode}. {errors}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            lifetime.Cancel();
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            process.Dispose();
            lifetime.Dispose();
            cache.Dispose();
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
        await process.StandardInput.BaseStream.WriteAsync(body, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    private async Task ReadMessagesAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var message = await ReadFrameAsync(process.StandardOutput.BaseStream, lifetime.Token);
                if (message is null)
                {
                    break;
                }

                await messages.Writer.WriteAsync(message.Value, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            messages.Writer.TryComplete(failure);
        }
    }

    private static async Task<JsonElement?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var singleByte = new byte[1];
        while (header.Count < 4 || header[^4] != '\r' || header[^3] != '\n' || header[^2] != '\r' || header[^1] != '\n')
        {
            if (await stream.ReadAsync(singleByte, cancellationToken) == 0)
            {
                if (header.Count == 0)
                {
                    return null;
                }

                throw new EndOfStreamException("Language server closed stdout within an LSP header.");
            }

            header.Add(singleByte[0]);
            if (header.Count > 8192)
            {
                throw new InvalidDataException("Language server returned an oversized LSP header.");
            }
        }

        var contentLength = Encoding.ASCII.GetString(header.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .Single();
        var body = new byte[contentLength];
        await stream.ReadExactlyAsync(body, cancellationToken);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
