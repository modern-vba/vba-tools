using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSemanticTokenTests
{
    [Fact]
    public void SemanticTokensCoverSourceDefinitionsActiveReferencesAndSkipUnresolvedNames()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Function BuildValue() As String",
            "End Function",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    BuildValue",
            "    app.Run(",
            "    MissingIdentifier",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        var tokens = index.GetSemanticTokens(uri);

        Assert.Contains(tokens, token =>
            token.Text == "BuildValue"
            && token.TokenType == "function"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "BuildValue"
            && token.TokenType == "function"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "Run"
            && token.TokenType == "method"
            && token.TokenModifiers.Contains("defaultLibrary"));
        Assert.DoesNotContain(tokens, token => token.Text == "MissingIdentifier");
    }

    [Fact]
    public void SemanticTokensSkipInactiveReferenceCatalogNames()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Scripting.Dictionary",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.DoesNotContain(index.GetSemanticTokens(uri), token => token.Text == "Dictionary");
    }
}
