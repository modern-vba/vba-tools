using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaWorkspaceSymbolAndReferenceTests
{
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
