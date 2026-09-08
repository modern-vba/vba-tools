using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class DocumentedSignatureLanguageServerProcessTests
{
    [Fact]
    public async Task Undocumented_noncallable_hover_has_a_declaration_without_a_separator()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();
        const string uri = "file:///C:/work/UndocumentedDeclaration.bas";
        const string text = "Attribute VB_Name = \"UndocumentedDeclaration\"\nPublic Const Rate As Long = 1\n";
        await server.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });

        var hover = await server.SendRequestAsync(2, "textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line = 1, character = 14 }
        });

        Assert.Equal("```vba\nConst Rate As Long\n```", hover.GetProperty("result")
            .GetProperty("contents").GetProperty("value").GetString());
        await server.ShutdownAsync(3);
    }

    [Fact]
    public async Task Conditional_hover_places_each_document_before_its_separated_signature()
    {
        await using var server = await LanguageServerProcessHarness.StartAsync();
        await server.InitializeAsync();
        const string uri = "file:///C:/work/DocumentedSignatures.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"DocumentedSignatures\"",
            "#If Win64 Then",
            "'* @brief First configuration.",
            "Public Sub Render()",
            "End Sub",
            "#Else",
            "'* @brief Second configuration.",
            "Public Sub render()",
            "End Sub",
            "#End If",
            "Public Sub Example()",
            "    Render",
            "End Sub"
        ]);
        await server.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text }
        });

        var hover = await server.SendRequestAsync(2, "textDocument/hover", new
        {
            textDocument = new { uri },
            position = new { line = 11, character = 6 }
        });
        var markdown = hover.GetProperty("result").GetProperty("contents")
            .GetProperty("value").GetString();

        Assert.Equal(
            "**Render [#If]**\n\n"
            + "First configuration.\n\n---\n\n```vba\nSub Render() [#If]\n```\n\n"
            + "Second configuration.\n\n---\n\n```vba\nSub render() [#If]\n```",
            markdown);
        await server.ShutdownAsync(3);
    }
}
