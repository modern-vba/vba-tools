using VbaLanguageServer.ProjectModel;
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

    [Fact]
    public void FormatDocumentNormalizesResolvedCurrentModuleLocalAndParameterReferences()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub buildValue(ByVal inputValue as string)",
            "dim localValue as string",
            "localvalue = inputvalue",
            "buildvalue localvalue",
            "unresolvedname = localvalue",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub buildValue(ByVal inputValue As String)",
            "    Dim localValue As String",
            "    localValue = inputValue",
            "    buildValue localValue",
            "    unresolvedname = localValue",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentNormalizesSiblingReferencesAndLeavesAmbiguousOrShadowedReferencesClosed()
    {
        const string builderUri = "file:///C:/work/Builder.bas";
        var builderSource = string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Public Sub SharedAction()",
            "End Sub",
            "Public Sub SharedThing()",
            "End Sub"
        ]);
        const string firstUri = "file:///C:/work/First.bas";
        var firstSource = string.Join('\n', [
            "Attribute VB_Name = \"First\"",
            "Public Sub DuplicateValue()",
            "End Sub"
        ]);
        const string secondUri = "file:///C:/work/Second.bas";
        var secondSource = string.Join('\n', [
            "Attribute VB_Name = \"Second\"",
            "Public Sub DuplicateValue()",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.bas";
        var workerSource = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "dim sharedThing as string",
            "sharedaction",
            "duplicATEvalue",
            "SHAREDTHING = \"x\"",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim sharedThing As String",
            "    SharedAction",
            "    duplicATEvalue",
            "    sharedThing = \"x\"",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [builderUri] = builderSource,
            [firstUri] = firstSource,
            [secondUri] = secondSource,
            [workerUri] = workerSource
        });

        var edit = index.FormatDocument(workerUri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentNormalizesResolvedQualifiedSourceReferences()
    {
        const string builderUri = "file:///C:/work/Builder.bas";
        var builderSource = string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Public Sub SharedAction()",
            "End Sub"
        ]);
        const string widgetUri = "file:///C:/work/Widget.cls";
        var widgetSource = string.Join('\n', [
            "Attribute VB_Name = \"Widget\"",
            "Public Sub CreateValue()",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.bas";
        var workerSource = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "builder.sharedaction",
            "widget . createvalue",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Builder.SharedAction",
            "    Widget . CreateValue",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [builderUri] = builderSource,
            [widgetUri] = widgetSource,
            [workerUri] = workerSource
        });

        var edit = index.FormatDocument(workerUri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentLeavesUnresolvedAmbiguousAndPrivateQualifiedSourceReferencesClosed()
    {
        const string builderUri = "file:///C:/work/Builder.bas";
        var builderSource = string.Join('\n', [
            "Attribute VB_Name = \"Builder\"",
            "Private Sub HiddenAction()",
            "End Sub"
        ]);
        const string firstUri = "file:///C:/work/First.bas";
        var firstSource = string.Join('\n', [
            "Attribute VB_Name = \"DuplicateModule\"",
            "Public Sub SameName()",
            "End Sub"
        ]);
        const string secondUri = "file:///C:/work/Second.bas";
        var secondSource = string.Join('\n', [
            "Attribute VB_Name = \"DuplicateModule\"",
            "Public Sub SameName()",
            "End Sub"
        ]);
        const string workerUri = "file:///C:/work/Worker.bas";
        var workerSource = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "missing.sharedaction",
            "builder.missingaction",
            "builder.hiddenaction",
            "duplicatemodule.samename",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    missing.sharedaction",
            "    builder.missingaction",
            "    builder.hiddenaction",
            "    duplicatemodule.samename",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [builderUri] = builderSource,
            [firstUri] = firstSource,
            [secondUri] = secondSource,
            [workerUri] = workerSource
        });

        var edit = index.FormatDocument(workerUri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentNormalizesActiveReferenceRootsAliasesAndMembers()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "option explicit",
            "public sub Run()",
            "application.run",
            "excel.application",
            "dim app as excel.application",
            "app.workbooks.open",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Application.Run",
            "    Excel.Application",
            "    Dim app As Excel.Application",
            "    app.Workbooks.Open",
            "End Sub"
        ]);
        var index = BuildExcelReferenceIndex(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentLeavesReferenceNamesUnchangedWhenCatalogIsMissing()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "application.run",
            "excel.application",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    application.run",
            "    excel.application",
            "End Sub"
        ]);
        var index = BuildExcelReferenceIndex(
            new Dictionary<string, string> { [uri] = source },
            VbaProjectReferenceCatalogSet.Empty);

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    [Fact]
    public void FormatDocumentStopsReferenceMemberChainCasingWhenResolutionFails()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var source = string.Join('\n', [
            "attribute vb_name = \"Worker\"",
            "public sub Run()",
            "dim app as excel.application",
            "app.unknown.open",
            "dim unknown as MissingType",
            "unknown.run",
            "end sub"
        ]);
        var expected = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    app.unknown.open",
            "    Dim unknown As MissingType",
            "    unknown.run",
            "End Sub"
        ]);
        var index = BuildExcelReferenceIndex(new Dictionary<string, string> { [uri] = source });

        var edit = index.FormatDocument(uri, tabSize: 4);

        Assert.NotNull(edit);
        Assert.Equal(expected, edit.NewText);
    }

    private static VbaSourceIndex BuildExcelReferenceIndex(
        IReadOnlyDictionary<string, string> sourceDocuments,
        VbaProjectReferenceCatalogSet? referenceCatalogs = null)
        => VbaSourceIndex.Build(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [
                    new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                    new VbaProjectReference("Microsoft Office 16.0 Object Library"),
                    new VbaProjectReference("Microsoft Scripting Runtime")
                ]),
            referenceCatalogs ?? VbaProjectReferenceCatalogSet.CreateBundled());
}
