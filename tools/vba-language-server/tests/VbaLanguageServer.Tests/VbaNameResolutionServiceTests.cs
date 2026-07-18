using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaNameResolutionServiceTests
{
    [Theory]
    [InlineData(true, true, true, "local")]
    [InlineData(false, true, true, "current")]
    [InlineData(false, false, true, "project")]
    [InlineData(false, false, false, "reference")]
    public void PreservesUnqualifiedResolutionRankMatrix(
        bool includeLocal,
        bool includeCurrentModule,
        bool includeProject,
        string expectedOrigin)
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string referenceName = "Rank Matrix Reference";
        var procedureRange = new VbaRange(new VbaPosition(1, 0), new VbaPosition(5, 7));
        var currentDefinitions = new List<VbaSourceDefinition>();
        if (includeLocal)
        {
            currentDefinitions.Add(Definition(
                "RankedName",
                VbaSourceDefinitionKind.Variable,
                currentUri,
                "Worker",
                VbaSourceDefinitionVisibility.Local,
                2,
                parentProcedureName: "Run",
                parentProcedureRange: procedureRange));
        }

        if (includeCurrentModule)
        {
            currentDefinitions.Add(Definition(
                "RankedName",
                VbaSourceDefinitionKind.Procedure,
                currentUri,
                "Worker",
                VbaSourceDefinitionVisibility.Public,
                6));
        }

        var projectDefinitions = includeProject
            ? new[]
            {
                Definition(
                    "RankedName",
                    VbaSourceDefinitionKind.Procedure,
                    helperUri,
                    "Helpers",
                    VbaSourceDefinitionVisibility.Public,
                    1)
            }
            : [];
        var referenceDefinition = ReferenceDefinition(
            referenceName,
            "RankedName",
            VbaSourceDefinitionKind.Procedure);
        var selection = VbaProjectReferenceSelection.Create(
            "word",
            [new VbaProjectReference(referenceName)]);
        var resolver = new VbaNameResolutionService(
            [
                new VbaSourceDocument(currentUri, "", "Worker", currentDefinitions),
                new VbaSourceDocument(helperUri, "", "Helpers", projectDefinitions)
            ],
            selection,
            VbaProjectReferenceCatalogSet.Empty,
            [referenceDefinition]);

        var resolved = resolver.Resolve(currentUri, new VbaPosition(3, 4), null, "rankedname");

        Assert.NotNull(resolved);
        switch (expectedOrigin)
        {
            case "local":
                Assert.Equal(VbaSourceDefinitionVisibility.Local, resolved.Visibility);
                break;
            case "current":
                Assert.Equal(currentUri, resolved.Uri);
                Assert.Null(resolved.ParentProcedureName);
                break;
            case "project":
                Assert.Equal(helperUri, resolved.Uri);
                break;
            case "reference":
                Assert.Equal(referenceName, resolved.ModuleName);
                break;
        }
    }

    [Fact]
    public void PreservesSameRankAmbiguityAndMainReferenceTieBreak()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        const string firstUri = "file:///C:/work/First.bas";
        const string secondUri = "file:///C:/work/Second.bas";
        const string mainReferenceName = "Microsoft Excel 16.0 Object Library";
        const string secondaryReferenceName = "Secondary Reference";
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [
                new VbaProjectReference(mainReferenceName),
                new VbaProjectReference(secondaryReferenceName)
            ]);
        var documents = new[]
        {
            new VbaSourceDocument(currentUri, "", "Worker", []),
            new VbaSourceDocument(firstUri, "", "First", [
                Definition("ProjectTie", VbaSourceDefinitionKind.Procedure, firstUri, "First", VbaSourceDefinitionVisibility.Public, 1)
            ]),
            new VbaSourceDocument(secondUri, "", "Second", [
                Definition("ProjectTie", VbaSourceDefinitionKind.Procedure, secondUri, "Second", VbaSourceDefinitionVisibility.Public, 1)
            ])
        };
        var referenceDefinitions = new[]
        {
            ReferenceDefinition(mainReferenceName, "ReferenceTie", VbaSourceDefinitionKind.Procedure),
            ReferenceDefinition(secondaryReferenceName, "ReferenceTie", VbaSourceDefinitionKind.Procedure)
        };
        var resolver = new VbaNameResolutionService(
            documents,
            selection,
            VbaProjectReferenceCatalogSet.Empty,
            referenceDefinitions);

        Assert.Null(resolver.Resolve(currentUri, new VbaPosition(1, 0), null, "ProjectTie"));
        Assert.Equal(
            mainReferenceName,
            resolver.Resolve(currentUri, new VbaPosition(1, 0), null, "ReferenceTie")?.ModuleName);
    }

    [Fact]
    public void PreservesUnqualifiedResolutionOfExternalMembersWithParentTypes()
    {
        const string currentUri = "file:///C:/work/Worker.bas";
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]);
        var resolver = new VbaNameResolutionService(
            [new VbaSourceDocument(currentUri, "", "Worker", [])],
            selection,
            VbaProjectReferenceCatalogSet.CreateBundled());

        var resolved = resolver.Resolve(currentUri, new VbaPosition(1, 0), null, "Run");

        Assert.Equal("Application", resolved?.ParentTypeName);
        Assert.Equal("Microsoft Excel 16.0 Object Library", resolved?.ModuleName);
    }

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
    {
        var range = new VbaRange(new VbaPosition(line, 0), new VbaPosition(line, name.Length));
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForSource(uri, name, range),
            new VbaDefinitionLocation(uri, range),
            name,
            kind,
            visibility,
            moduleName,
            parentProcedureName,
            parentProcedureRange);
    }

    private static VbaSourceDefinition ReferenceDefinition(
        string referenceName,
        string name,
        VbaSourceDefinitionKind kind,
        string? parentTypeName = null)
    {
        var range = new VbaRange(new VbaPosition(0, 0), new VbaPosition(0, name.Length));
        return new VbaSourceDefinition(
            VbaDefinitionIdentity.ForProjectReference(referenceName, parentTypeName, kind, name),
            new VbaDefinitionLocation(
                $"{VbaProjectReferenceCatalogSet.ExternalDefinitionUriPrefix}{Uri.EscapeDataString(referenceName)}/{Uri.EscapeDataString(name)}",
                range),
            name,
            kind,
            VbaSourceDefinitionVisibility.Public,
            referenceName,
            ParentTypeName: parentTypeName,
            ReferenceGlobalExposure: parentTypeName is null
                ? ReferenceDefinitionGlobalExposure.LibraryGlobal
                : ReferenceDefinitionGlobalExposure.None);
    }
}
