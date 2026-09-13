using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ClassMetadataFormattingProcessTests
{
    [Theory]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    public async Task Server_preserves_class_metadata_and_formats_body_idempotently(
        string newline,
        bool finalNewline)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.cls";
        var header = string.Join(newline, [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            " \t",
            "  ' end if remains metadata prose",
            "END",
            ""
        ]);
        var source = header + string.Join(newline, [
            "attribute vb_name = \"Worker\"",
            "option explicit",
            "public sub Run()",
            "if true then",
            "end",
            "end if",
            "end sub"
        ]) + (finalNewline ? newline : "");
        var expected = header + string.Join(newline, [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    If True Then",
            "        End",
            "    End If",
            "End Sub"
        ]) + (finalNewline ? newline : "");
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
        Assert.Equal(expected, edit.GetProperty("newText").GetString());
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = expected } }
        });
        var second = await process.SendRequestAsync(3, "textDocument/formatting", parameters);
        Assert.Empty(second.GetProperty("result").EnumerateArray());
        await process.ShutdownAsync(4);
    }
}
