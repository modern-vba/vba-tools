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
