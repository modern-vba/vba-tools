using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class LanguageServerProcessTests
{
    [Fact]
    public async Task Server_handles_initialize_text_sync_completion_and_shutdown()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        var initialize = await SendRequestAsync(
            stdin,
            stdout,
            1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });

        Assert.Equal(1, initialize.GetProperty("id").GetInt32());
        var capabilities = initialize
            .GetProperty("result")
            .GetProperty("capabilities");
        Assert.Equal(2, capabilities.GetProperty("textDocumentSync").GetInt32());
        Assert.True(capabilities.TryGetProperty("completionProvider", out _));

        await SendNotificationAsync(stdin, "initialized", new { });
        await SendNotificationAsync(
            stdin,
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    languageId = "vba",
                    version = 1,
                    text = "Public Sub Hello()\nEnd Sub\n"
                }
            });
        await SendNotificationAsync(
            stdin,
            "textDocument/didChange",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    version = 2
                },
                contentChanges = new[]
                {
                    new
                    {
                        text = "Public Sub Hello()\nDebug.Print \"hi\"\nEnd Sub\n"
                    }
                }
            });

        var completion = await SendRequestAsync(
            stdin,
            stdout,
            2,
            "textDocument/completion",
            new
            {
                textDocument = new { uri = "file:///C:/work/Module1.bas" },
                position = new { line = 1, character = 5 }
            });

        var completionItems = completion.GetProperty("result").EnumerateArray().ToArray();
        Assert.Single(completionItems);
        Assert.Equal("CSharpLspTracerBullet", completionItems[0].GetProperty("label").GetString());

        var shutdown = await SendRequestAsync(stdin, stdout, 3, "shutdown", null);
        Assert.Equal(JsonValueKind.Null, shutdown.GetProperty("result").ValueKind);
        await SendNotificationAsync(stdin, "exit", null);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Server_publishes_diagnostics_after_open_and_change_notifications()
    {
        var serverProjectPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "VbaLanguageServer.Cli",
                "VbaLanguageServer.Cli.csproj"));

        using var process = StartLanguageServer(serverProjectPath);
        await using var stdin = process.StandardInput.BaseStream;
        using var stdout = process.StandardOutput.BaseStream;

        await SendRequestAsync(
            stdin,
            stdout,
            1,
            "initialize",
            new
            {
                processId = Environment.ProcessId,
                rootUri = (string?)null,
                capabilities = new { }
            });
        await SendNotificationAsync(stdin, "initialized", new { });

        const string invalidLine = "        \"needle\", _ ' comment";
        await SendNotificationAsync(
            stdin,
            "textDocument/didOpen",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    languageId = "vba",
                    version = 1,
                    text = string.Join('\n', [
                        "Attribute VB_Name = \"Module1\"",
                        "Option Explicit",
                        "",
                        "Public Sub Run()",
                        "    ReadValue( _",
                        invalidLine,
                        "End Sub"
                    ])
                }
            });

        var invalidDiagnostics = await ReadNotificationAsync(stdout, "textDocument/publishDiagnostics");
        var firstDiagnostic = invalidDiagnostics
            .GetProperty("params")
            .GetProperty("diagnostics")
            .EnumerateArray()
            .Single();
        Assert.Equal("syntax.invalidTrailingCommentContinuation", firstDiagnostic.GetProperty("code").GetString());
        Assert.Equal("Code line-continuation marker cannot be followed by a comment.", firstDiagnostic.GetProperty("message").GetString());
        Assert.Equal("vba-language-server", firstDiagnostic.GetProperty("source").GetString());
        Assert.Equal(1, firstDiagnostic.GetProperty("severity").GetInt32());
        Assert.Equal(5, firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(invalidLine.IndexOf('_'), firstDiagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(5, firstDiagnostic.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(invalidLine.Length, firstDiagnostic.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());

        await SendNotificationAsync(
            stdin,
            "textDocument/didChange",
            new
            {
                textDocument = new
                {
                    uri = "file:///C:/work/Module1.bas",
                    version = 2
                },
                contentChanges = new[]
                {
                    new
                    {
                        text = string.Join('\n', [
                            "Attribute VB_Name = \"Module1\"",
                            "Option Explicit",
                            "",
                            "Public Sub Run()",
                            "    ReadValue( _",
                            "        \"needle\")",
                            "End Sub"
                        ])
                    }
                }
            });

        var validDiagnostics = await ReadNotificationAsync(stdout, "textDocument/publishDiagnostics");
        Assert.Empty(validDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        await SendRequestAsync(stdin, stdout, 2, "shutdown", null);
        await SendNotificationAsync(stdin, "exit", null);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(cancellation.Token);
        Assert.Equal(0, process.ExitCode);
    }

    private static Process StartLanguageServer(string serverProjectPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(serverProjectPath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the language server process.");

        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                Debug.WriteLine(args.Data);
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<JsonElement> SendRequestAsync(
        Stream stdin,
        Stream stdout,
        int id,
        string method,
        object? parameters)
    {
        await WriteMessageAsync(stdin, new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await ReadMessageAsync(stdout, cancellation.Token);
            if (message.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
            {
                return message;
            }
        }
    }

    private static Task SendNotificationAsync(Stream stdin, string method, object? parameters)
    {
        return WriteMessageAsync(stdin, new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        });
    }

    private static async Task WriteMessageAsync(Stream stream, object message)
    {
        var json = JsonSerializer.Serialize(
            message,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        var content = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {content.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    private static async Task<JsonElement> ReadNotificationAsync(Stream stdout, string method)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var message = await ReadMessageAsync(stdout, cancellation.Token);
            if (message.TryGetProperty("method", out var methodElement)
                && methodElement.GetString() == method)
            {
                return message;
            }
        }
    }

    private static async Task<JsonElement> ReadMessageAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var headerBytes = new List<byte>();
        var singleByte = new byte[1];
        while (!EndsWithHeaderTerminator(headerBytes))
        {
            var read = await stream.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout before sending a response.");
            }

            headerBytes.Add(singleByte[0]);
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var contentLength = headers
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .Where(parts => string.Equals(parts[0], "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(parts => int.Parse(parts[1].Trim()))
            .Single();

        var content = new byte[contentLength];
        var offset = 0;
        while (offset < content.Length)
        {
            var read = await stream.ReadAsync(content.AsMemory(offset, content.Length - offset), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Language server closed stdout mid-message.");
            }

            offset += read;
        }

        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static bool EndsWithHeaderTerminator(List<byte> bytes)
    {
        return bytes.Count >= 4
            && bytes[^4] == '\r'
            && bytes[^3] == '\n'
            && bytes[^2] == '\r'
            && bytes[^1] == '\n';
    }
}
