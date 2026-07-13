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

    [Fact]
    public void SemanticTokensSkipVbaKeywordsWhenReferenceCatalogContainsMatchingNames()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Function BuildValue() As String",
            "End Function",
            "Public Property Get Value() As String",
            "End Property",
            "Public Sub Run()",
            "    If True Then",
            "    End If",
            "End Sub"
        ]);
        const string referenceName = "Keyword Collision Library";
        var referenceCatalogs = VbaProjectReferenceCatalogSet.Empty.WithCatalog(new VbaProjectReferenceCatalog(
            referenceName,
            ["KeywordCollision"],
            [
                KeywordCollisionDefinition(referenceName, "End"),
                KeywordCollisionDefinition(referenceName, "Function"),
                KeywordCollisionDefinition(referenceName, "If"),
                KeywordCollisionDefinition(referenceName, "Property"),
                KeywordCollisionDefinition(referenceName, "Sub")
            ]));
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string> { [uri] = text },
            VbaProjectReferenceSelection.Create(
                "keyword-collision",
                [new VbaProjectReference(referenceName)]),
            referenceCatalogs);

        var tokens = index.GetSemanticTokens(uri);

        Assert.DoesNotContain(tokens, token => token.Text == "End");
        Assert.DoesNotContain(tokens, token => token.Text == "Function");
        Assert.DoesNotContain(tokens, token => token.Text == "If");
        Assert.DoesNotContain(tokens, token => token.Text == "Property");
        Assert.DoesNotContain(tokens, token => token.Text == "Sub");
    }

    [Fact]
    public void SemanticTokenDataIsCachedWithinSourceIndexSnapshot()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim value As Long",
            "    value = 1",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = text });

        var firstData = index.GetSemanticTokenData(uri);
        var secondData = index.GetSemanticTokenData(uri);

        Assert.Same(firstData, secondData);
    }

    [Fact]
    public void SemanticTokensCoverProjectTypeAnnotationsVariablesAndMembers()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string rangeBoundsUri = "file:///C:/work/WorksheetRangeBounds.cls";
        const string dialogFormUri = "file:///C:/work/DialogForm.frm";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run( _",
            "    Optional ByVal inputValue As Long = 1 _",
            ")",
            "    aaaa = inputValue",
            "    Dim range_obj As WorksheetRangeBounds",
            "    aaaa = range_obj.Column",
            "    aaaa = range_obj.StartColumn",
            "    Dim rangeInfo As RangeInfo",
            "    aaaa = rangeInfo.FirstColumn",
            "    Dim dialog As DialogForm",
            "    Set dialog = New DialogForm",
            "End Sub"
        ]);
        var rangeBoundsText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"WorksheetRangeBounds\"",
            "Private pColumn As Long",
            "Public StartColumn As Long",
            "Public Property Get Column() As Long",
            "    Column = pColumn",
            "End Property"
        ]);
        var rangeInfoText = string.Join('\n', [
            "Attribute VB_Name = \"RangeInfo\"",
            "Public Type RangeInfo",
            "    FirstColumn As Long",
            "End Type"
        ]);
        var dialogFormText = string.Join('\n', [
            "VERSION 5.00",
            "Begin VB.Form DialogForm",
            "End",
            "Attribute VB_Name = \"DialogForm\"",
            "Option Explicit"
        ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [rangeBoundsUri] = rangeBoundsText,
                ["file:///C:/work/RangeInfo.bas"] = rangeInfoText,
                [dialogFormUri] = dialogFormText
            });

        var tokens = index.GetSemanticTokens(workerUri);
        var rangeBoundsTokens = index.GetSemanticTokens(rangeBoundsUri);
        var rangeInfoTokens = index.GetSemanticTokens("file:///C:/work/RangeInfo.bas");

        Assert.Contains(tokens, token =>
            token.Text == "WorksheetRangeBounds"
            && token.TokenType == "class"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Equal(
            2,
            tokens.Count(token =>
                token.Text == "DialogForm"
                && token.TokenType == "class"
                && !token.TokenModifiers.Contains("declaration")));
        Assert.Contains(tokens, token =>
            token.Text == "range_obj"
            && token.TokenType == "variable"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "inputValue"
            && token.TokenType == "parameter"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "inputValue"
            && token.TokenType == "parameter"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "range_obj"
            && token.TokenType == "variable"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "Column"
            && token.TokenType == "property"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "StartColumn"
            && token.TokenType == "field"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(rangeBoundsTokens, token =>
            token.Text == "pColumn"
            && token.TokenType == "field"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(rangeBoundsTokens, token =>
            token.Text == "pColumn"
            && token.TokenType == "field"
            && !token.TokenModifiers.Contains("declaration"));
        Assert.Contains(rangeInfoTokens, token =>
            token.Text == "FirstColumn"
            && token.TokenType == "field"
            && token.TokenModifiers.Contains("declaration"));
        Assert.Contains(tokens, token =>
            token.Text == "FirstColumn"
            && token.TokenType == "field"
            && !token.TokenModifiers.Contains("declaration"));
    }

    private static VbaProjectReferenceDefinition KeywordCollisionDefinition(string referenceName, string name)
        => new(
            referenceName,
            name,
            VbaSourceDefinitionKind.Property,
            $"Reference member named {name}.",
            ParentTypeName: "Application");
}
