using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ClassMetadataFormattingProcessTests
{
    [Theory]
    [InlineData("\r\n", "\r\n", "\r\n", true)]
    [InlineData("\r\n", "\r\n", "\r\n", false)]
    [InlineData("\n", "\n", "\n", true)]
    [InlineData("\n", "\n", "\n", false)]
    [InlineData("\r", "\r", "\r", true)]
    [InlineData("\r", "\r", "\r", false)]
    [InlineData("\r\n", "\n", "\n", true)]
    [InlineData("\r\n", "\n", "\r\n", false)]
    public async Task Server_preserves_class_metadata_and_formats_body_idempotently(
        string headerNewline,
        string bodyNewline,
        string expectedNewline,
        bool finalNewline)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.cls";
        string[] headerLines = [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            " \t",
            "  ' end if remains metadata prose",
            "END",
            ""
        ];
        var source = string.Join(headerNewline, headerLines) + string.Join(bodyNewline, [
            "attribute vb_name = \"Worker\"",
            "option explicit",
            "public sub Run()",
            "if true then",
            "end",
            "end if",
            "end sub"
        ]) + (finalNewline ? bodyNewline : "");
        var expected = string.Join(expectedNewline, headerLines) + string.Join(expectedNewline, [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    If True Then",
            "        End",
            "    End If",
            "End Sub"
        ]) + (finalNewline ? expectedNewline : "");
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = source }
        });
        var parameters = new
        {
            textDocument = new { uri },
            options = new { tabSize = 4, insertSpaces = true }
        };

        var response = await process.SendRequestAsync(2, "textDocument/formatting", parameters);

        var edit = Assert.Single(response.GetProperty("result").EnumerateArray());
        var range = edit.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(finalNewline ? 13 : 12, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(finalNewline ? 0 : 7, range.GetProperty("end").GetProperty("character").GetInt32());
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
