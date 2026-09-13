using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceTextFormattingProcessTests
{
    [Theory]
    [InlineData("\r\n", "\r\n", "\r\n", "\r\n", "\r\n")]
    [InlineData("\n", "\n", "\n", "\n", "\n")]
    [InlineData("\r", "\r", "\r", "\r", "\r")]
    [InlineData("\r", "\r", "\r\n", "\n", "\r")]
    public async Task Server_formats_exact_source_text_with_resolved_indentation_idempotently(
        string firstNewline,
        string secondNewline,
        string thirdNewline,
        string fourthNewline,
        string expectedNewline)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.bas";
        const string lastLine = "end sub ' 日本😀";
        var source = "option explicit" + firstNewline
            + "public sub Run(ByVal InputValue as string)" + secondNewline
            + "dim LocalValue as string" + thirdNewline
            + "localvalue = \"日本😀 if true\" & inputvalue ' localvalue inputvalue" + fourthNewline
            + lastLine;
        var expected = string.Join(expectedNewline, [
            "Option Explicit",
            "Public Sub Run(ByVal InputValue As String)",
            "  Dim LocalValue As String",
            "  LocalValue = \"日本😀 if true\" & InputValue ' localvalue inputvalue",
            "End Sub ' 日本😀"
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = source }
        });
        var parameters = new
        {
            textDocument = new { uri },
            options = new { tabSize = 8, indentSize = 2, insertSpaces = true }
        };

        var response = await process.SendRequestAsync(2, "textDocument/formatting", parameters);

        var edit = Assert.Single(response.GetProperty("result").EnumerateArray());
        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(4, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(lastLine.Length, range.GetProperty("end").GetProperty("character").GetInt32());
        var applied = edit.GetProperty("newText").GetString();
        Assert.Equal(expected, applied);
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = applied } }
        });

        var second = await process.SendRequestAsync(3, "textDocument/formatting", parameters);

        Assert.Empty(second.GetProperty("result").EnumerateArray());
        await process.ShutdownAsync(4);
    }
}
