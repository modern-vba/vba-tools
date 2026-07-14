using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaWorkspaceSymbolAndReferenceTests
{
    [Fact]
    public void DefinitionIdentityEqualityIsCaseInsensitiveAndPreservesDiscriminators()
    {
        var range = new VbaRange(new VbaPosition(1, 2), new VbaPosition(1, 6));
        var source = VbaDefinitionIdentity.ForSource("file:///C:/work/Module.bas", "Run", range);
        var equivalentSource = VbaDefinitionIdentity.ForSource("FILE:///c:/WORK/module.bas", "run", range);
        var movedSource = VbaDefinitionIdentity.ForSource(
            "file:///C:/work/Module.bas",
            "Run",
            new VbaRange(new VbaPosition(2, 2), new VbaPosition(2, 6)));
        var reference = VbaDefinitionIdentity.ForProjectReference(
            "Generated Library",
            "FirstType",
            VbaSourceDefinitionKind.Property,
            "Name");
        var equivalentReference = VbaDefinitionIdentity.ForProjectReference(
            "generated library",
            "firsttype",
            VbaSourceDefinitionKind.Property,
            "name");
        var differentParent = VbaDefinitionIdentity.ForProjectReference(
            "Generated Library",
            "SecondType",
            VbaSourceDefinitionKind.Property,
            "Name");
        var differentKind = VbaDefinitionIdentity.ForProjectReference(
            "Generated Library",
            "FirstType",
            VbaSourceDefinitionKind.Procedure,
            "Name");

        Assert.Equal(source, equivalentSource);
        Assert.Equal(source.GetHashCode(), equivalentSource.GetHashCode());
        Assert.NotEqual(source, movedSource);
        Assert.Equal(reference, equivalentReference);
        Assert.Equal(reference.GetHashCode(), equivalentReference.GetHashCode());
        Assert.NotEqual(reference, differentParent);
        Assert.NotEqual(reference, differentKind);
        Assert.Equal(default, default(VbaDefinitionIdentity));
        Assert.Equal(2, new HashSet<VbaDefinitionIdentity> { source, equivalentSource, movedSource }.Count);
    }

    [Fact]
    public void WorkspaceSymbolsReturnSourceDefinitionsMatchingTheQuery()
    {
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [helperUri] = string.Join('\n', [
                "Attribute VB_Name = \"Helpers\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]),
            [callerUri] = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "End Sub"
            ])
        });

        var symbols = index.GetWorkspaceSymbols("build");

        var symbol = Assert.Single(symbols);
        Assert.Equal("BuildValue", symbol.Name);
        Assert.Equal(helperUri, symbol.Uri);
    }

    [Fact]
    public void FindReferencesReturnsResolvedSourceAndReferenceOccurrencesOnly()
    {
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string>
            {
                [helperUri] = string.Join('\n', [
                    "Attribute VB_Name = \"Helpers\"",
                    "Public Function BuildValue() As String",
                    "End Function",
                    "Public Function DuplicateValue() As String",
                    "End Function"
                ]),
                [callerUri] = string.Join('\n', [
                    "Attribute VB_Name = \"Caller\"",
                    "Public Sub Run()",
                    "    BuildValue",
                    "    Application",
                    "    DuplicateValue",
                    "End Sub"
                ]),
                ["file:///C:/work/Duplicate.bas"] = string.Join('\n', [
                    "Attribute VB_Name = \"Duplicate\"",
                    "Public Function DuplicateValue() As String",
                    "End Function"
                ])
            },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        var sourceReferences = index.FindReferences(helperUri, 1, "Public Function ".Length);
        var externalReferences = index.FindReferences(callerUri, 3, "    ".Length);
        var ambiguousReferences = index.FindReferences(callerUri, 4, "    ".Length);

        Assert.Equal(2, sourceReferences.Count);
        Assert.Contains(sourceReferences, reference => reference.Uri == helperUri);
        Assert.Contains(sourceReferences, reference => reference.Uri == callerUri);
        var externalReference = Assert.Single(externalReferences);
        Assert.Equal(callerUri, externalReference.Uri);
        Assert.Empty(ambiguousReferences);
    }

    [Fact]
    public void ReferenceIdentitySeparatesSameNamedMembersOwnedByDifferentTypes()
    {
        const string uri = "file:///C:/work/Caller.bas";
        var selection = VbaProjectReferenceSelection.Create(
            ProjectDocument.ExcelKind,
            [new VbaProjectReference("Generated Library")]);
        var catalog = new VbaProjectReferenceCatalog(
            "Generated Library",
            ["Generated"],
            [
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "FirstType",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "SecondType",
                    VbaSourceDefinitionKind.Class),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "Name",
                    VbaSourceDefinitionKind.Property,
                    ParentTypeName: "FirstType"),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "Name",
                    VbaSourceDefinitionKind.Property,
                    ParentTypeName: "SecondType")
            ]);
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string>
            {
                [uri] = string.Join('\n', [
                    "Attribute VB_Name = \"Caller\"",
                    "Public Sub Run()",
                    "    Dim first As FirstType",
                    "    Dim second As SecondType",
                    "    first.Name",
                    "    first.Name",
                    "    second.Name",
                    "    first.",
                    "End Sub"
                ])
            },
            selection,
            VbaProjectReferenceCatalogSet.Empty.WithCatalog(catalog));

        var firstDefinition = Assert.IsType<VbaSourceDefinition>(
            index.ResolveSourceDefinition(uri, 4, "    first.".Length));
        var secondDefinition = Assert.IsType<VbaSourceDefinition>(
            index.ResolveSourceDefinition(uri, 6, "    second.".Length));
        var completionDefinition = Assert.Single(
            index.GetCompletionDefinitions(uri, 7, "    first.".Length),
            definition => definition.Name == "Name");

        Assert.Equal(firstDefinition.Location, secondDefinition.Location);
        Assert.NotEqual(firstDefinition.Identity, secondDefinition.Identity);
        Assert.Equal(
            firstDefinition.Identity,
            VbaDefinitionIdentity.ForProjectReference(
                "generated library",
                "firsttype",
                VbaSourceDefinitionKind.Property,
                "name"));
        Assert.NotEqual(
            firstDefinition.Identity,
            VbaDefinitionIdentity.ForProjectReference(
                "Generated Library",
                "FirstType",
                VbaSourceDefinitionKind.Procedure,
                "Name"));
        Assert.Equal(firstDefinition.Identity, completionDefinition.Identity);
        Assert.Equal(2, index.FindReferences(uri, 4, "    first.".Length).Count);
        Assert.Single(index.FindReferences(uri, 6, "    second.".Length));
    }

    [Fact]
    public void FindReferencesRespectsManifestDocumentBoundaries()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-references-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "vba-project.json"), ProjectManifestFixtureText("multi-document.json"));
            var book1Uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helpers.bas"));
            var book1CallerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var secondBookUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Caller.bas"));
            var workspace = new VbaLanguageWorkspace(new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(book1Uri, string.Join('\n', [
                "Attribute VB_Name = \"Helpers\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(book1CallerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));
            workspace.UpdateDocument(secondBookUri, string.Join('\n', [
                "Attribute VB_Name = \"SecondCaller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var snapshot = workspace.CreateProjectSnapshot(book1Uri);
            var references = snapshot.SourceIndex.FindReferences(book1Uri, 1, "Public Function ".Length);

            Assert.Equal(2, references.Count);
            Assert.Contains(references, reference => reference.Uri == book1Uri);
            Assert.Contains(references, reference => reference.Uri == book1CallerUri);
            Assert.DoesNotContain(references, reference => reference.Uri == secondBookUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void PrepareRenameRejectsExternalReferenceDefinitions()
    {
        const string uri = "file:///C:/work/Caller.bas";
        var index = VbaSourceIndex.Build(
            new Dictionary<string, string>
            {
                [uri] = string.Join('\n', [
                    "Attribute VB_Name = \"Caller\"",
                    "Public Sub Run()",
                    "    Application",
                    "End Sub"
                ])
            },
            VbaProjectReferenceSelection.Create(
                ProjectDocument.ExcelKind,
                [new VbaProjectReference("Microsoft Excel 16.0 Object Library")]),
            VbaProjectReferenceCatalogSet.CreateBundled());

        var definition = index.ResolveSourceDefinition(uri, 2, "    ".Length);

        Assert.NotNull(definition);
        Assert.Equal("Microsoft Excel 16.0 Object Library", definition.ModuleName);
        Assert.Null(index.PrepareRename(uri, 2, "    ".Length));
        Assert.Null(index.CreateRenamePlan(uri, 2, "    ".Length, "RenamedApplication"));
    }

    private static string ToFileUri(string path)
        => new Uri(path).AbsoluteUri;

    private static string ProjectManifestFixtureText(string fixtureName)
        => File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-manifest",
            fixtureName)));
}
