using System.Text.Json;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class IndentationDetectionProcessTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("Public Sub Run()\n\tDebug.Print 1\nEnd Sub", false)]
    [InlineData("Public Sub Run()\n  Debug.Print 1\n    Debug.Print 2\nEnd Sub", true)]
    public async Task Detection_preserves_configured_widths_for_tabs_or_no_unambiguous_space_evidence(
        string code, bool expectedInsertSpaces)
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.cls";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri, languageId = "vba", version = 1,
                text = "VERSION 1.0 CLASS\nBEGIN\n  MultiUse = -1\nEND\n"
                    + "Attribute VB_Name = \"Worker\"\n" + code
            }
        });

        var response = await process.SendRequestAsync(2, "vba/detectIndentation", new
        {
            textDocument = new { uri, version = 1 },
            options = new { tabSize = 8, indentSize = 6, insertSpaces = true }
        });

        var result = response.GetProperty("result");
        Assert.Equal(expectedInsertSpaces, result.GetProperty("insertSpaces").GetBoolean());
        Assert.Equal(6, result.GetProperty("indentSize").GetInt32());
        Assert.Equal(8, result.GetProperty("tabSize").GetInt32());
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Detection_is_bound_to_the_exact_open_document_version()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.bas";
        object Parameters(int version) => new
        {
            textDocument = new { uri, version },
            options = new { tabSize = 8, indentSize = 4, insertSpaces = true }
        };
        var unopened = await process.SendRequestAsync(2, "vba/detectIndentation", Parameters(1));
        Assert.Equal(JsonValueKind.Null, unopened.GetProperty("result").ValueKind);
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri, languageId = "vba", version = 1,
                text = "Public Sub Run()\n    Debug.Print 1\nEnd Sub\n"
            }
        });
        await process.SendNotificationAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = "Public Sub Run()\n  Debug.Print 2\nEnd Sub\n" } }
        });

        var stale = await process.SendRequestAsync(3, "vba/detectIndentation", Parameters(1));
        var current = await process.SendRequestAsync(4, "vba/detectIndentation", Parameters(2));

        Assert.Equal(JsonValueKind.Null, stale.GetProperty("result").ValueKind);
        var result = current.GetProperty("result");
        Assert.Equal(2, result.GetProperty("version").GetInt32());
        Assert.Equal(2, result.GetProperty("indentSize").GetInt32());
        Assert.Equal(8, result.GetProperty("tabSize").GetInt32());
        await process.SendNotificationAsync("textDocument/didClose", new { textDocument = new { uri } });
        var closed = await process.SendRequestAsync(5, "vba/detectIndentation", Parameters(2));
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("result").ValueKind);
        await process.ShutdownAsync(6);
    }

    [Fact]
    public async Task Omitted_indent_size_links_the_detected_space_width_to_tab_size()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/Worker.bas";
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version = 1,
                text = "Public Sub Run()\n  Debug.Print 1\nEnd Sub\n"
            }
        });

        var response = await process.SendRequestAsync(2, "vba/detectIndentation", new
        {
            textDocument = new { uri, version = 1 },
            options = new { tabSize = 4, insertSpaces = true }
        });

        var result = response.GetProperty("result");
        Assert.True(result.GetProperty("insertSpaces").GetBoolean());
        Assert.Equal(2, result.GetProperty("indentSize").GetInt32());
        Assert.Equal(2, result.GetProperty("tabSize").GetInt32());
        await process.ShutdownAsync(3);
    }

    [Fact]
    public async Task Detection_uses_code_instead_of_class_export_metadata_without_editing_source()
    {
        await using var process = await LanguageServerProcessHarness.StartAsync();
        await process.InitializeAsync();
        const string uri = "file:///C:/work/IToolSettings.cls";
        var source = string.Join("\r\n", [
            "VERSION 1.0 CLASS",
            "BEGIN",
            "  MultiUse = -1  'True",
            "END",
            "Attribute VB_Name = \"IToolSettings\"",
            "Option Explicit",
            "Private Sub Class_Initialize()",
            "    Err.Raise 5",
            "End Sub",
            ""
        ]);
        await process.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "vba", version = 1, text = source }
        });

        var response = await process.SendRequestAsync(2, "vba/detectIndentation", new
        {
            textDocument = new { uri, version = 1 },
            options = new { tabSize = 8, indentSize = 2, insertSpaces = true }
        });

        var result = response.GetProperty("result");
        Assert.Equal(uri, result.GetProperty("uri").GetString());
        Assert.Equal(1, result.GetProperty("version").GetInt32());
        Assert.True(result.GetProperty("insertSpaces").GetBoolean());
        Assert.Equal(4, result.GetProperty("indentSize").GetInt32());
        Assert.Equal(8, result.GetProperty("tabSize").GetInt32());
        var formatting = await process.SendRequestAsync(3, "textDocument/formatting", new
        {
            textDocument = new { uri },
            options = new { tabSize = 8, indentSize = 4, insertSpaces = true }
        });
        Assert.Empty(formatting.GetProperty("result").EnumerateArray());
        await process.ShutdownAsync(4);
    }
}
