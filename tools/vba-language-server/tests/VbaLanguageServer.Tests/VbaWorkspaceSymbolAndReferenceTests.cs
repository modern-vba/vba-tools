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
                    ParentTypeName: "FirstType",
                    PropertyAccess: VbaPropertyAccess.Readable),
                new VbaProjectReferenceDefinition(
                    "Generated Library",
                    "Name",
                    VbaSourceDefinitionKind.Property,
                    ParentTypeName: "SecondType",
                    PropertyAccess: VbaPropertyAccess.Readable)
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
    public void ReferencesRenameAndSemanticTokensShareDefinitionIdentityRanges()
    {
        const string helperUri = "file:///C:/work/Helpers.bas";
        const string callerUri = "file:///C:/work/Caller.bas";
        var helperText = string.Join('\n', [
            "Attribute VB_Name = \"Helpers\"",
            "Public Function BuildValue() As String",
            "    buildvalue = \"value\"",
            "End Function"
        ]);
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    BUILDVALUE",
            "    Helpers.BuildValue",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string>
        {
            [helperUri] = helperText,
            [callerUri] = callerText
        });
        var target = Assert.IsType<VbaSourceDefinition>(index.ResolveSourceDefinition(
            helperUri,
            1,
            "Public Function ".Length));

        var references = index.FindReferences(helperUri, 1, "Public Function ".Length);
        var renamePlan = Assert.IsType<VbaRenamePlan>(index.CreateRenamePlan(
            helperUri,
            1,
            "Public Function ".Length,
            "CreateValue"));
        var semanticTokenLocations = new[]
        {
            (Uri: helperUri, Tokens: index.GetSemanticTokens(helperUri)),
            (Uri: callerUri, Tokens: index.GetSemanticTokens(callerUri))
        }
            .SelectMany(document => document.Tokens
                .Where(token => token.Text.Equals("BuildValue", StringComparison.OrdinalIgnoreCase))
                .Select(token => LocationKey(document.Uri, token.Range)))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referenceLocations = references
            .Select(reference => LocationKey(reference.Uri, reference.Range))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var renameLocations = renamePlan.Changes
            .SelectMany(change => change.Value.Select(edit => LocationKey(change.Key, edit.Range)))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            VbaDefinitionIdentity.ForSource(helperUri, "buildvalue", target.Range),
            target.Identity);
        Assert.Equal(4, referenceLocations.Length);
        Assert.Equal(referenceLocations, renameLocations);
        Assert.Equal(referenceLocations, semanticTokenLocations);
    }

    [Fact]
    public async Task SemanticOccurrenceFeaturesRemainStableDuringConcurrentFirstUse()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var text = string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "    Dim SharedValue As Long",
            "    sharedvalue = sharedVALUE + 1",
            "End Sub"
        ]);
        var index = VbaSourceIndex.Build(new Dictionary<string, string> { [uri] = text });
        var expectedLocations = new[]
        {
            LocationKey(uri, new VbaRange(new VbaPosition(2, 8), new VbaPosition(2, 19))),
            LocationKey(uri, new VbaRange(new VbaPosition(3, 4), new VbaPosition(3, 15))),
            LocationKey(uri, new VbaRange(new VbaPosition(3, 18), new VbaPosition(3, 29)))
        }
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tasks = Enumerable.Range(0, 24)
            .Select(iteration => Task.Run(() =>
            {
                switch (iteration % 4)
                {
                    case 0:
                        return index.FindReferences(uri, 2, 8)
                            .Select(reference => LocationKey(reference.Uri, reference.Range))
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    case 1:
                        return Assert.IsType<VbaRenamePlan>(index.CreateRenamePlan(uri, 2, 8, "CurrentValue"))
                            .Changes
                            .SelectMany(change => change.Value.Select(edit => LocationKey(change.Key, edit.Range)))
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    case 2:
                        return index.GetSemanticTokens(uri)
                            .Where(token => token.Text.Equals("SharedValue", StringComparison.OrdinalIgnoreCase))
                            .Select(token => LocationKey(uri, token.Range))
                            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                    default:
                        var edit = Assert.IsType<VbaTextEdit>(index.FormatDocument(uri, tabSize: 4));
                        Assert.Contains("    SharedValue = SharedValue + 1", edit.NewText, StringComparison.Ordinal);
                        return expectedLocations;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, locations => Assert.Equal(expectedLocations, locations));
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

    private static string LocationKey(string uri, VbaRange range)
        => $"{uri}:{range.Start.Line}:{range.Start.Character}:{range.End.Line}:{range.End.Character}";

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
