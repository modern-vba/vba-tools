using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceTextBlockSkeletonInsertionProcessTests
{
    [Theory]
    [InlineData("crlf", false)]
    [InlineData("lf", false)]
    [InlineData("cr", false)]
    [InlineData("mixed", false)]
    [InlineData("crlf", true)]
    [InlineData("lf", true)]
    [InlineData("cr", true)]
    [InlineData("mixed", true)]
    public async Task Nested_if_uses_native_enter_newline_and_preserves_unicode_source_through_eof(
        string newlineStyle, bool insertionAtEof)
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();

        const string uri = "file:///C:/source-text-skeleton-tests/Nested.bas";
        var newline = newlineStyle switch
        {
            "crlf" => "\r\n",
            "lf" => "\n",
            "cr" or "mixed" => "\r",
            _ => throw new ArgumentException("Unknown fixture newline style.", nameof(newlineStyle))
        };
        var prefix = newlineStyle == "mixed"
            ? "Public Sub Run()\r\n    If True Then\n"
            : "Public Sub Run()" + newline + "    If True Then" + newline;
        const string header = "        If \"日本😀\" <> \"\" Then";
        var nativeEnter = newline + "        ";
        var suffix = insertionAtEof
            ? string.Empty
            : newlineStyle == "mixed"
                ? "\r\n    End If\rEnd Sub"
                : newline + "    End If" + newline + "End Sub";
        var source = prefix + header + nativeEnter + suffix;
        await server.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = source }
        });

        var response = await RequestAsync(server, 2, uri, version: 1);
        Assert.False(response.TryGetProperty("error", out _), response.ToString());
        var plan = response.GetProperty("result");
        Assert.Equal(1, plan.GetProperty("documentVersion").GetInt32());
        Assert.Equal(2, plan.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(28, plan.GetProperty("position").GetProperty("character").GetInt32());
        Assert.Equal(newline + "          ", plan.GetProperty("textBeforeCursor").GetString());
        Assert.Equal(newline + "        End If", plan.GetProperty("textAfterCursor").GetString());

        var replacement = plan.GetProperty("textBeforeCursor").GetString()
            + plan.GetProperty("textAfterCursor").GetString();
        // Use the known native receipt, independently of production source-coordinate helpers.
        var updated = source.Remove(prefix.Length + 28, nativeEnter.Length)
            .Insert(prefix.Length + 28, replacement);
        Assert.Equal(prefix + header + newline + "          " + newline + "        End If" + suffix, updated);

        await server.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = updated } }
        });
        var repeated = await RequestAsync(server, 3, uri, version: 2);
        Assert.False(repeated.TryGetProperty("error", out _), repeated.ToString());
        Assert.Equal(JsonValueKind.Null, repeated.GetProperty("result").ValueKind);
        await server.ShutdownAsync(4);
    }

    private static Task<JsonElement> RequestAsync(
        LanguageServerProcessHarness server, int id, string uri, int version)
        => server.SendRequestAsync(id, "vba/blockSkeletonInsertion", new
        {
            documentUri = uri,
            documentVersion = version,
            position = new { line = 2, character = 28 },
            options = new { insertSpaces = true, indentSize = 2, tabSize = 4 }
        });
}
