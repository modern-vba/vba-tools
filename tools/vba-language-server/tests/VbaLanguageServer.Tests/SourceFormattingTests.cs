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
}
