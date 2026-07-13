using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaSemanticResolutionTests
{
    [Fact]
    public void ResolvesTypedReferenceMembersAndMissingMetadataFailsClosed()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    app.",
            "    app.Run(",
            "    Dim dict As Scripting.Dictionary",
            "    dict.",
            "    Dim unknown As MissingType",
            "    unknown.",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var appCompletionLabels = index.GetCompletionDefinitions(uri, 4, "    app.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Run", appCompletionLabels);
        Assert.Contains("Workbooks", appCompletionLabels);
        Assert.DoesNotContain("Dictionary", appCompletionLabels);

        var runDefinition = index.ResolveSourceDefinition(uri, 5, "    app.".Length);
        Assert.Equal("Microsoft Excel 16.0 Object Library", runDefinition?.ModuleName);
        Assert.Equal("Application", runDefinition?.ParentTypeName);
        Assert.Equal("Run(Macro, Arg1)", index.GetSignatureHelp(uri, 5, "    app.Run(".Length)?.Signature.Label);

        var dictionaryCompletionLabels = index.GetCompletionDefinitions(uri, 7, "    dict.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Exists", dictionaryCompletionLabels);
        Assert.Empty(index.GetCompletionDefinitions(uri, 9, "    unknown.".Length));
    }

    [Fact]
    public void SourceTypesOutrankReferencesUnlessTypeAnnotationIsReferenceQualified()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string applicationUri = "file:///C:/work/Application.cls";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim sourceApp As Application",
            "    sourceApp.",
            "    Dim excelApp As Excel.Application",
            "    excelApp.",
            "End Sub"
        ]);
        var sourceApplicationText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Application\"",
            "Public Function SourceOnly() As String",
            "End Function"
        ]);
        var index = BuildIndex(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [applicationUri] = sourceApplicationText
            });

        var sourceLabels = index.GetCompletionDefinitions(workerUri, 4, "    sourceApp.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("SourceOnly", sourceLabels);
        Assert.DoesNotContain("Run", sourceLabels);

        var referenceLabels = index.GetCompletionDefinitions(workerUri, 6, "    excelApp.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Run", referenceLabels);
        Assert.DoesNotContain("SourceOnly", referenceLabels);
    }

    [Fact]
    public void ResolvesMemberChainsContinuationsAndNestedWithReceivers()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim app As Excel.Application",
            "    app.Workbooks.",
            "    app _",
            "        .Run(",
            "    app.Run( _",
            "        ",
            "    With app",
            "        With .Workbooks",
            "            .Open(",
            "        End With",
            "    End With",
            "    With app _",
            "        .Workbooks",
            "        .Open(",
            "    End With",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var workbookLabels = index.GetCompletionDefinitions(uri, 4, "    app.Workbooks.".Length)
            .Select(definition => definition.Name)
            .ToArray();
        Assert.Contains("Open", workbookLabels);

        Assert.Equal("Run(Macro, Arg1)", index.GetSignatureHelp(uri, 6, "        .Run(".Length)?.Signature.Label);
        Assert.Equal("Run(Macro, Arg1)", index.GetSignatureHelp(uri, 8, "        ".Length)?.Signature.Label);
        Assert.Equal("Open(FileName)", index.GetSignatureHelp(uri, 11, "            .Open(".Length)?.Signature.Label);
        Assert.Equal("Open(FileName)", index.GetSignatureHelp(uri, 16, "        .Open(".Length)?.Signature.Label);
    }

    [Fact]
    public void MemberAndTypeCompletionUseSourceTypeContext()
    {
        const string workerUri = "file:///C:/work/Worker.bas";
        const string rangeBoundsUri = "file:///C:/work/WorksheetRangeBounds.cls";
        const string helperUri = "file:///C:/work/Helper.bas";
        var workerText = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    Dim bare As ",
            "    Dim typed As WorksheetRan",
            "    Dim range_obj As WorksheetRangeBounds",
            "    range_obj.",
            "    range_obj.Col",
            "    aaaa = range_obj.Column ",
            "    aaaa = range_obj. ",
            "End Sub"
        ]);
        var rangeBoundsText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"WorksheetRangeBounds\"",
            "Public Property Get Column() As Long",
            "End Property",
            "Public Property Get ColumnCount() As Long",
            "End Property"
        ]);
        var helperText = string.Join('\n', [
            "Attribute VB_Name = \"Helper\"",
            "Public Function BuildValue() As String",
            "End Function"
        ]);
        var index = BuildIndex(
            new Dictionary<string, string>
            {
                [workerUri] = workerText,
                [rangeBoundsUri] = rangeBoundsText,
                [helperUri] = helperText
            });

        var dotCompletion = index.GetCompletionResult(workerUri, 6, "    range_obj.".Length);
        var dotLabels = dotCompletion.Definitions.Select(definition => definition.Name).ToArray();
        Assert.Equal(VbaCompletionVocabularyKind.None, dotCompletion.VocabularyKind);
        Assert.Contains("Column", dotLabels);
        Assert.Contains("ColumnCount", dotLabels);
        Assert.DoesNotContain("BuildValue", dotLabels);

        var partialCompletion = index.GetCompletionResult(workerUri, 7, "    range_obj.Col".Length);
        var partialLabels = partialCompletion.Definitions.Select(definition => definition.Name).ToArray();
        Assert.Equal(VbaCompletionVocabularyKind.None, partialCompletion.VocabularyKind);
        Assert.Contains("Column", partialLabels);
        Assert.Contains("ColumnCount", partialLabels);
        Assert.DoesNotContain("BuildValue", partialLabels);

        var completedMemberCompletion = index.GetCompletionResult(workerUri, 8, "    aaaa = range_obj.Column ".Length);
        Assert.Equal(VbaCompletionVocabularyKind.None, completedMemberCompletion.VocabularyKind);
        Assert.Empty(completedMemberCompletion.Definitions);

        var spacedDotCompletion = index.GetCompletionResult(workerUri, 9, "    aaaa = range_obj. ".Length);
        Assert.Equal(VbaCompletionVocabularyKind.None, spacedDotCompletion.VocabularyKind);
        Assert.Empty(spacedDotCompletion.Definitions);

        var bareTypeCompletion = index.GetCompletionResult(workerUri, 3, "    Dim bare As ".Length);
        Assert.Equal(VbaCompletionVocabularyKind.TypeName, bareTypeCompletion.VocabularyKind);
        Assert.Contains(
            bareTypeCompletion.Definitions,
            definition => definition.Name == "WorksheetRangeBounds" && definition.Kind == VbaSourceDefinitionKind.Class);

        var typeCompletion = index.GetCompletionResult(workerUri, 4, "    Dim typed As WorksheetRan".Length);
        Assert.Equal(VbaCompletionVocabularyKind.TypeName, typeCompletion.VocabularyKind);
        Assert.Contains(
            typeCompletion.Definitions,
            definition => definition.Name == "WorksheetRangeBounds" && definition.Kind == VbaSourceDefinitionKind.Class);
    }

    [Fact]
    public void SignatureHelpUsesActiveNamedArgumentWhenParameterNameMatches()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function ReadValue(ByVal Key As String, ByVal Fallback As String) As String",
            "End Function",
            "Public Sub Run()",
            "    ReadValue(Fallback:=",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 4, "    ReadValue(Fallback:=".Length);

        Assert.Equal("ReadValue(Key, Fallback) As String", signatureHelp?.Signature.Label);
        Assert.Equal(1, signatureHelp?.ActiveParameter);
    }

    [Fact]
    public void SignatureHelpIncludesArrayParametersAndLaterParametersInOrder()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function Search(ByRef Values() As String, ByVal Fallback As String) As Long",
            "End Function",
            "Public Sub Run()",
            "    Search(",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var signatureHelp = index.GetSignatureHelp(uri, 4, "    Search(".Length);

        Assert.Equal("Search(Values, Fallback) As Long", signatureHelp?.Signature.Label);
        Assert.Equal(["Values", "Fallback"], signatureHelp!.Signature.Parameters.Select(parameter => parameter.Name).ToArray());
    }

    private static VbaSourceIndex BuildIndex(string uri, string text)
        => BuildIndex(new Dictionary<string, string> { [uri] = text });

    private static VbaSourceIndex BuildIndex(IReadOnlyDictionary<string, string> sourceDocuments)
        => VbaSourceIndex.Build(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [
                    new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                    new VbaProjectReference("Microsoft Scripting Runtime")
                ]),
            VbaProjectReferenceCatalogSet.CreateBundled());
}
