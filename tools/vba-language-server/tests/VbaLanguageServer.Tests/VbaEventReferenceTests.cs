using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaEventReferenceTests
{
    [Fact]
    public void RaiseEventResolvesCurrentModuleEventDefinition()
    {
        const string uri = "file:///C:/work/Worker.cls";
        var text = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Public Event Saved()",
            "Public Sub Run()",
            "    RaiseEvent Saved",
            "End Sub"
        ]);
        var index = BuildIndex(uri, text);

        var definition = index.ResolveSourceDefinition(uri, 4, "    RaiseEvent ".Length);

        Assert.Equal("Saved", definition?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, definition?.Kind);
        Assert.Equal(uri, definition?.Uri);
    }

    [Fact]
    public void WithEventsHandlersResolveSourceAndReferenceEvents()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string publisherUri = "file:///C:/work/Publisher.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents publisher As Publisher",
            "Private WithEvents app As Excel.Application",
            "Private Sub publisher_Changed()",
            "End Sub",
            "Private Sub app_WorkbookOpen(ByVal Wb As Excel.Workbook)",
            "End Sub"
        ]);
        var publisherText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Publisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [publisherUri] = publisherText
        });

        var sourceEvent = index.ResolveSourceDefinition(workerUri, 4, "Private Sub ".Length);
        var referenceEvent = index.ResolveSourceDefinition(workerUri, 6, "Private Sub ".Length);

        Assert.Equal("Changed", sourceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, sourceEvent?.Kind);
        Assert.Equal(publisherUri, sourceEvent?.Uri);
        Assert.Equal("WorkbookOpen", referenceEvent?.Name);
        Assert.Equal(VbaSourceDefinitionKind.Event, referenceEvent?.Kind);
        Assert.Equal("Application", referenceEvent?.ParentTypeName);
        Assert.Equal("Microsoft Excel 16.0 Object Library", referenceEvent?.ModuleName);
    }

    [Fact]
    public void WithEventsHandlersFailClosedForMissingOrAmbiguousEventMetadata()
    {
        const string workerUri = "file:///C:/work/Worker.cls";
        const string duplicateAUri = "file:///C:/work/DuplicateA.cls";
        const string duplicateBUri = "file:///C:/work/DuplicateB.cls";
        var workerText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"Worker\"",
            "Private WithEvents missing As MissingPublisher",
            "Private WithEvents duplicate As DuplicatePublisher",
            "Private Sub missing_Changed()",
            "End Sub",
            "Private Sub duplicate_Changed()",
            "End Sub"
        ]);
        var duplicateText = string.Join('\n', [
            "VERSION 1.0 CLASS",
            "Attribute VB_Name = \"DuplicatePublisher\"",
            "Public Event Changed()"
        ]);
        var index = BuildIndex(new Dictionary<string, string>
        {
            [workerUri] = workerText,
            [duplicateAUri] = duplicateText,
            [duplicateBUri] = duplicateText
        });

        Assert.Null(index.ResolveSourceDefinition(workerUri, 4, "Private Sub ".Length));
        Assert.Null(index.ResolveSourceDefinition(workerUri, 6, "Private Sub ".Length));
    }

    private static VbaSourceIndex BuildIndex(string uri, string text)
        => BuildIndex(new Dictionary<string, string> { [uri] = text });

    private static VbaSourceIndex BuildIndex(IReadOnlyDictionary<string, string> sourceDocuments)
        => VbaSourceIndex.Build(
            sourceDocuments,
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());
}
