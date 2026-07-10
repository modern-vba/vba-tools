using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class SourceFormattingTests
{
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void FormatDocumentPreservesDominantLineEnding(string lineEnding)
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join(lineEnding, [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "if true then",
            "end if",
            "end sub"
        ]);
        var expected = string.Join(lineEnding, [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If True Then",
            "    End If",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentUsesDeterministicDominantLineEndingForMixedInput()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source =
            "attribute vb_name = \"Worker\"\n" +
            "public sub Run()\r\n" +
            "if true then\n" +
            "end if\n" +
            "end sub";
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If True Then",
            "    End If",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentReturnsNoEditWhenSourceIsAlreadyFormatted()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    If True Then",
            "    End If",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.Null(edit);
    }

    [Fact]
    public void FormatDocumentNormalizesVocabularyOnlyInCodeRanges()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "option explicit",
            "public sub Run()",
            "dim text as string",
            "text = \"public sub if true string false nothing\"",
            "' public sub if true string false nothing",
            "'* @brief public sub if true string false nothing",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim text As String",
            "    text = \"public sub if true string false nothing\"",
            "    ' public sub if true string false nothing",
            "    '* @brief public sub if true string false nothing",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentLeavesUnclosedStringLiteralProseUnchanged()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "option explicit",
            "public sub Run()",
            "dim value as string",
            "value = \"if true string",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Option Explicit",
            "Public Sub Run()",
            "    Dim value As String",
            "    value = \"if true string",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }
}
