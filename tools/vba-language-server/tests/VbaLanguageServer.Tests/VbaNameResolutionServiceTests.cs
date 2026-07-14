using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaNameResolutionServiceTests
{
    [Fact]
    public void ResolvesUnqualifiedNamesByLocalCurrentProjectAndReferenceRank()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string helperUri = "file:///C:/work/Helpers.bas";
        var procedureRange = new VbaRange(new VbaPosition(1, 0), new VbaPosition(5, 7));
        var currentDocument = new VbaSourceDocument(
            currentUri,
            "",
            "Worker",
            [
                Definition("SharedName", VbaSourceDefinitionKind.Variable, currentUri, "Worker", VbaSourceDefinitionVisibility.Local, 2, parentProcedureName: "Run", parentProcedureRange: procedureRange),
                Definition("SharedName", VbaSourceDefinitionKind.Procedure, currentUri, "Worker", VbaSourceDefinitionVisibility.Public, 6),
                Definition("CurrentOnly", VbaSourceDefinitionKind.Procedure, currentUri, "Worker", VbaSourceDefinitionVisibility.Public, 8)
            ]);
        var helperDocument = new VbaSourceDocument(
            helperUri,
            "",
            "Helpers",
            [
                Definition("SharedName", VbaSourceDefinitionKind.Procedure, helperUri, "Helpers", VbaSourceDefinitionVisibility.Public, 1),
                Definition("ProjectOnly", VbaSourceDefinitionKind.Procedure, helperUri, "Helpers", VbaSourceDefinitionVisibility.Public, 3)
            ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                new VbaProjectReference("Microsoft Scripting Runtime")
            ]);
        var resolver = new VbaNameResolutionService(
            [currentDocument, helperDocument],
            selection,
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.Equal(VbaSourceDefinitionVisibility.Local, resolver.Resolve(currentUri, new VbaPosition(3, 4), null, "SharedName")?.Visibility);
        Assert.Equal(currentUri, resolver.Resolve(currentUri, new VbaPosition(9, 0), null, "SharedName")?.Uri);
        Assert.Equal(helperUri, resolver.Resolve(currentUri, new VbaPosition(9, 0), null, "ProjectOnly")?.Uri);
        Assert.Equal("Microsoft Scripting Runtime", resolver.Resolve(currentUri, new VbaPosition(9, 0), null, "Dictionary")?.ModuleName);
    }

    [Fact]
    public void ReferencesUseSourcePrecedenceMainReferenceAndAmbiguityRules()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        var sourceApplication = new VbaSourceDocument(
            currentUri,
            "",
            "Worker",
            [
                Definition("Application", VbaSourceDefinitionKind.Procedure, currentUri, "Worker", VbaSourceDefinitionVisibility.Public, 1)
            ]);
        var excelSelection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference("Microsoft Excel 16.0 Object Library"),
                new VbaProjectReference("Microsoft Office 16.0 Object Library")
            ]);
        var sourceResolver = new VbaNameResolutionService(
            [sourceApplication],
            excelSelection,
            VbaProjectReferenceCatalogSet.CreateBundled());
        var referenceResolver = new VbaNameResolutionService(
            [new VbaSourceDocument(currentUri, "", "Worker", [])],
            excelSelection,
            VbaProjectReferenceCatalogSet.CreateBundled());
        var ambiguousSelection = VbaProjectReferenceSelection.Create(
            "word",
            [
                new VbaProjectReference("Microsoft Office 16.0 Object Library"),
                new VbaProjectReference("Microsoft Outlook 16.0 Object Library")
            ]);
        var ambiguousResolver = new VbaNameResolutionService(
            [new VbaSourceDocument(currentUri, "", "Worker", [])],
            ambiguousSelection,
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.Equal(currentUri, sourceResolver.Resolve(currentUri, new VbaPosition(9, 0), null, "Application")?.Uri);
        Assert.Equal("Microsoft Excel 16.0 Object Library", referenceResolver.Resolve(currentUri, new VbaPosition(9, 0), null, "Application")?.ModuleName);
        Assert.Null(ambiguousResolver.Resolve(currentUri, new VbaPosition(9, 0), null, "Application"));
    }

    [Fact]
    public void ResolvesQualifiedSourceAndReferenceNamesWithoutInactiveReferences()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string excelSourceUri = "file:///C:/work/Excel.bas";
        var currentDocument = new VbaSourceDocument(currentUri, "", "Worker", []);
        var excelSourceDocument = new VbaSourceDocument(
            excelSourceUri,
            "",
            "Excel",
            [
                Definition("Application", VbaSourceDefinitionKind.Procedure, excelSourceUri, "Excel", VbaSourceDefinitionVisibility.Public, 1)
            ]);
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference("Microsoft Excel 16.0 Object Library")
            ]);
        var resolver = new VbaNameResolutionService(
            [currentDocument, excelSourceDocument],
            selection,
            VbaProjectReferenceCatalogSet.CreateBundled());
        var referenceOnlyResolver = new VbaNameResolutionService(
            [currentDocument],
            selection,
            VbaProjectReferenceCatalogSet.CreateBundled());

        Assert.Equal(excelSourceUri, resolver.Resolve(currentUri, new VbaPosition(1, 0), "Excel", "Application")?.Uri);
        Assert.Equal("Microsoft Excel 16.0 Object Library", referenceOnlyResolver.Resolve(currentUri, new VbaPosition(1, 0), "Excel", "Application")?.ModuleName);
        Assert.Null(referenceOnlyResolver.Resolve(currentUri, new VbaPosition(1, 0), "Scripting", "Dictionary"));
    }

    private static VbaSourceDefinition Definition(
        string name,
        VbaSourceDefinitionKind kind,
        string uri,
        string moduleName,
        VbaSourceDefinitionVisibility visibility,
        int line,
        string? parentProcedureName = null,
        VbaRange? parentProcedureRange = null)
        => new(
            name,
            kind,
            visibility,
            uri,
            moduleName,
            new VbaRange(new VbaPosition(line, 0), new VbaPosition(line, name.Length)),
            parentProcedureName,
            parentProcedureRange);
}
