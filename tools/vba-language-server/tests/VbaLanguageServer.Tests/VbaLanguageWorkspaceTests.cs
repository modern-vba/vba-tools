using System.Text;
using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLanguageWorkspaceTests
{
    [Fact]
    public void ProjectSnapshotReusesCachedSnapshotUntilWorkspaceInputsChange()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub Run()",
            "End Sub"
        ]));

        var firstSnapshot = workspace.CreateProjectSnapshot(uri);
        var reusedSnapshot = workspace.CreateProjectSnapshot(uri);
        workspace.UpdateDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Sub RenamedRun()",
            "End Sub"
        ]));
        var refreshedSnapshot = workspace.CreateProjectSnapshot(uri);
        var reusedRefreshedSnapshot = workspace.CreateProjectSnapshot(uri);

        Assert.Same(firstSnapshot, reusedSnapshot);
        Assert.NotSame(firstSnapshot, refreshedSnapshot);
        Assert.Same(refreshedSnapshot, reusedRefreshedSnapshot);
    }

    [Fact]
    public void ProjectSnapshotReusesModuleMemberUpdatesForSafeDocumentChanges()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));

        var initialUpdate = workspace.UpdateDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]));
        var incrementalUpdate = workspace.UpdateDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"new\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]));

        var snapshot = workspace.CreateProjectSnapshot(uri);
        var definition = snapshot.SourceIndex.ResolveDefinition(
            uri,
            line: 6,
            character: "    ".Length);

        Assert.Equal(VbaSyntaxTreeParseUpdateKind.FullModule, initialUpdate);
        Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, incrementalUpdate);
        Assert.NotNull(definition);
    }

    [Fact]
    public void ProjectSnapshotScopesDocumentsAndReferenceSelectionForFeatureHandlers()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-workspace-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var book1HelperUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helper.bas"));
            var book1CallerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var secondBookHelperUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Helper.bas"));
            var callerText = string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(book1HelperUri, string.Join('\n', [
                "Attribute VB_Name = \"Book1Helper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(secondBookHelperUri, string.Join('\n', [
                "Attribute VB_Name = \"SecondBookHelper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(book1CallerUri, callerText);

            var snapshot = workspace.CreateProjectSnapshot(book1CallerUri);
            var definition = snapshot.SourceIndex.ResolveDefinition(
                book1CallerUri,
                line: 2,
                character: "    ".Length);

            Assert.NotNull(definition);
            Assert.Equal(book1HelperUri, definition.Uri);
            Assert.Equal("Book1", snapshot.Resolution.DocumentName);
            Assert.Equal("excel", snapshot.Resolution.DocumentKind);
            Assert.NotNull(snapshot.ReferenceSelection);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                snapshot.ReferenceSelection.MainVbaProjectReference?.Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotIncludesDiskSourceFilesAndOverlaysTrackedDocuments()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-inventory-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildValue() As String",
                    "End Function"
                ]));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var diskDefinition = workspace
                .CreateProjectSnapshot(callerUri)
                .SourceIndex
                .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
            workspace.UpdateDocument(helperUri, string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                "Public Function BuildReplacement() As String",
                "End Function"
            ]));
            var overlaySnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(helperUri, diskDefinition?.Uri);
            Assert.DoesNotContain(
                overlaySnapshot.SourceIndex.GetWorkspaceSymbols("BuildValue"),
                symbol => symbol.Uri == helperUri);
            Assert.Contains(
                overlaySnapshot.SourceIndex.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(BomAndUtf8EncodedSourceCases))]
    public void ProjectSnapshotDecodesBomAndUtf8DiskSourceDocumentation(byte[] helperBytes)
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-utf-source-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            const string documentation = "\u65e5\u672c\u8a9e\u306e\u8aac\u660e";
            File.WriteAllBytes(helperPath, helperBytes);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));

            var definition = workspace
                .CreateProjectSnapshot(helperUri)
                .SourceIndex
                .GetDocumentDefinitions(helperUri)
                .Single(definition => definition.Name == "BuildValue");

            Assert.Equal(documentation, definition.Documentation);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotDecodesCp932DiskSourceDocumentation()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-cp932-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var classPath = Path.Combine(projectRoot, "src", "Book1", "HelperClass.cls");
            var classUri = ToFileUri(classPath);
            var callerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            const string documentation = "\u65e5\u672c\u8a9e\u306e\u8aac\u660e";
            const string classDocumentation = "\u30af\u30e9\u30b9\u306e\u8aac\u660e";
            var helperText = string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                $"'* @brief {documentation}",
                "Public Function BuildValue() As String",
                "End Function"
            ]);
            var classText = string.Join('\n', [
                "VERSION 1.0 CLASS",
                "BEGIN",
                "  MultiUse = -1",
                "END",
                "Attribute VB_Name = \"HelperClass\"",
                $"'* @brief {classDocumentation}",
                "Public Function BuildClassValue() As String",
                "End Function"
            ]);
            File.WriteAllBytes(helperPath, Encoding.GetEncoding(932).GetBytes(helperText));
            File.WriteAllBytes(classPath, Encoding.GetEncoding(932).GetBytes(classText));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var sourceIndex = workspace
                .CreateProjectSnapshot(callerUri)
                .SourceIndex;
            var definition = sourceIndex.ResolveSourceDefinition(callerUri, line: 2, character: "    ".Length);
            var classDefinition = sourceIndex
                .GetDocumentDefinitions(classUri)
                .Single(definition => definition.Name == "BuildClassValue");

            Assert.NotNull(definition);
            Assert.Equal(documentation, definition.Documentation);
            Assert.Equal("BuildValue() As String", definition.Signature?.Label);
            Assert.Equal(classDocumentation, classDefinition.Documentation);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotCacheInvalidatesWhenDiskSourceChanges()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-disk-refresh-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            var callerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildValue() As String",
                    "End Function"
                ]));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var initialSnapshot = workspace.CreateProjectSnapshot(callerUri);
            var reusedSnapshot = workspace.CreateProjectSnapshot(callerUri);
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildReplacement() As String",
                    "End Function"
                ]));
            var refreshedSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(initialSnapshot, reusedSnapshot);
            Assert.NotSame(initialSnapshot, refreshedSnapshot);
            Assert.Contains(
                refreshedSnapshot.SourceIndex.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
            Assert.DoesNotContain(
                refreshedSnapshot.SourceIndex.GetWorkspaceSymbols("BuildValue"),
                symbol => symbol.Uri == helperUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotIncludesDiskSourceFilesForEncodedWindowsDriveUris()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-encoded-uri-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(projectRoot, "src", "Book1", "Helper.bas");
            var callerPath = Path.Combine(projectRoot, "src", "Book1", "Caller.bas");
            File.WriteAllText(
                helperPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Helper\"",
                    "Public Function BuildValue() As String",
                    "End Function"
                ]));
            File.WriteAllText(
                callerPath,
                string.Join('\n', [
                    "Attribute VB_Name = \"Caller\"",
                    "Public Sub OldRun()",
                    "End Sub"
                ]));

            var callerUri = ToEncodedDriveFileUri(callerPath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var snapshot = workspace.CreateProjectSnapshot(callerUri);
            var definition = snapshot.SourceIndex.ResolveDefinition(
                callerUri,
                line: 2,
                character: "    ".Length);
            var callerDocumentCount = snapshot.SourceDocuments.Keys
                .Select(VbaProjectResolver.TryGetLocalPath)
                .Count(path => path is not null
                    && string.Equals(path, Path.GetFullPath(callerPath), StringComparison.OrdinalIgnoreCase));

            Assert.Equal(VbaProjectResolutionKind.ManifestDocument, snapshot.Resolution.Kind);
            Assert.Equal("Book1", snapshot.Resolution.DocumentName);
            Assert.NotNull(definition);
            Assert.EndsWith("Helper.bas", VbaProjectResolver.TryGetLocalPath(definition.Uri), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, callerDocumentCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotInvalidatesRemovedAndRenamedSourceDocuments()
    {
        const string callerUri = "file:///C:/work/Caller.bas";
        const string helperUri = "file:///C:/work/Helper.bas";
        const string renamedHelperUri = "file:///C:/work/RenamedHelper.bas";
        var callerText = string.Join('\n', [
            "Attribute VB_Name = \"Caller\"",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]);
        var helperText = string.Join('\n', [
            "Attribute VB_Name = \"Helper\"",
            "Public Function BuildValue() As String",
            "End Function"
        ]);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(callerUri, callerText);
        workspace.UpdateDocument(helperUri, helperText);

        var initialDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SourceIndex
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.RemoveDocument(helperUri);
        var removedDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SourceIndex
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.UpdateDocument(renamedHelperUri, helperText);
        var renamedDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SourceIndex
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);

        Assert.Equal(helperUri, initialDefinition?.Uri);
        Assert.Null(removedDefinition);
        Assert.Equal(renamedHelperUri, renamedDefinition?.Uri);
    }

    [Fact]
    public void ProjectSnapshotUsesLatestManifestBoundariesAndReferenceSelection()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-manifest-refresh-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
            WriteProjectManifest(
                projectRoot,
                book1SourcePath: "src/Book1",
                book1References: ["Microsoft Excel 16.0 Object Library"]);
            var book1Uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var secondBookUri = ToFileUri(Path.Combine(projectRoot, "src", "SecondBook", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(book1Uri, "Attribute VB_Name = \"Book1Worker\"\nPublic Sub Run()\nEnd Sub");
            workspace.UpdateDocument(secondBookUri, "Attribute VB_Name = \"SecondBookWorker\"\nPublic Sub Run()\nEnd Sub");

            var firstSnapshot = workspace.CreateProjectSnapshot(book1Uri);
            WriteProjectManifest(
                projectRoot,
                book1SourcePath: "src/SecondBook",
                book1References: ["Microsoft Scripting Runtime"]);
            var refreshedSnapshot = workspace.CreateProjectSnapshot(secondBookUri);

            Assert.Equal("Book1", firstSnapshot.Resolution.DocumentName);
            Assert.Contains(book1Uri, firstSnapshot.SourceDocuments.Keys);
            Assert.DoesNotContain(secondBookUri, firstSnapshot.SourceDocuments.Keys);
            Assert.Equal(
                "Microsoft Excel 16.0 Object Library",
                firstSnapshot.ReferenceSelection?.References.Single().Name);
            Assert.Equal("Book1", refreshedSnapshot.Resolution.DocumentName);
            Assert.DoesNotContain(book1Uri, refreshedSnapshot.SourceDocuments.Keys);
            Assert.Contains(secondBookUri, refreshedSnapshot.SourceDocuments.Keys);
            Assert.Equal(
                "Microsoft Scripting Runtime",
                refreshedSnapshot.ReferenceSelection?.References.Single().Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotUsesRefreshedReferenceCatalogCacheForLaterRequests()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-catalog-cache-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
            WriteProjectManifest(
                projectRoot,
                book1SourcePath: "src/Book1",
                book1References: ["Generated Library"]);
            var uri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var cache = new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.Empty);
            var workspace = new VbaLanguageWorkspace(cache);
            workspace.UpdateDocument(uri, "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub");

            var beforeRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var reusedBeforeRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var beforeRefresh = beforeRefreshSnapshot
                .SourceIndex
                .GetCompletionDefinitions(uri, line: 1, character: 0)
                .Select(definition => definition.Name)
                .ToArray();
            cache.Store(VbaProjectReferenceCatalogDiscoveryResult.Success(
                new VbaProjectReferenceCatalogIdentity(
                    "Generated Library",
                    "{33333333-3333-3333-3333-333333333333}",
                    1,
                    0,
                    0,
                    @"C:\TypeLibs\Generated.tlb"),
                new VbaProjectReferenceCatalog(
                    "Generated Library",
                    ["Generated"],
                    [
                        new VbaProjectReferenceDefinition(
                            "Generated Library",
                            "GeneratedType",
                            VbaSourceDefinitionKind.Class)
                    ])));
            var afterRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var afterRefresh = afterRefreshSnapshot
                .SourceIndex
                .GetCompletionDefinitions(uri, line: 1, character: 0)
                .Select(definition => definition.Name)
                .ToArray();

            Assert.Same(beforeRefreshSnapshot, reusedBeforeRefreshSnapshot);
            Assert.NotSame(beforeRefreshSnapshot, afterRefreshSnapshot);
            Assert.DoesNotContain("GeneratedType", beforeRefresh);
            Assert.Contains("GeneratedType", afterRefresh);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectSnapshotsRemainConsistentAcrossConcurrentRequestsAndHonorCancellation()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(uri, "Attribute VB_Name = \"Worker\"\nPublic Sub Run()\nEnd Sub");

        var tasks = Enumerable.Range(0, 40)
            .Select(index => Task.Run(() =>
            {
                var text = string.Join('\n', [
                    "Attribute VB_Name = \"Worker\"",
                    "Public Function BuildValue() As String",
                    $"    BuildValue = \"{index}\"",
                    "End Function",
                    "Public Sub Run()",
                    "    BuildValue",
                    "End Sub"
                ]);
                workspace.UpdateDocument(uri, text);
                var snapshot = workspace.CreateProjectSnapshot(uri);
                return snapshot.SourceIndex.ResolveDefinition(uri, line: 5, character: "    ".Length);
            }))
            .ToArray();

        var definitions = await Task.WhenAll(tasks);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.All(definitions, Assert.NotNull);
        Assert.Throws<OperationCanceledException>(() => workspace.CreateProjectSnapshot(uri, cancellation.Token));
    }

    private static void WriteProjectManifest(
        string projectRoot,
        string book1SourcePath = "src/Book1",
        IReadOnlyList<string>? book1References = null)
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        IReadOnlyList<string> references =
            book1References ?? ["Visual Basic For Applications", "Microsoft Excel 16.0 Object Library"];
        var manifest = new
        {
            schemaVersion = 1,
            projectName = "WorkspaceSnapshotProject",
            primaryDocument = "Book1",
            documents = new Dictionary<string, object>
            {
                ["Book1"] = new
                {
                    kind = "excel",
                    sourcePath = book1SourcePath,
                    templatePath = "src/Book1/Book1.xlsm",
                    binPath = "bin/Book1/Book1.xlsm",
                    publishPath = "publish/Book1/Book1.xlsm",
                    references = references.Select(reference => new { name = reference }).ToArray()
                },
                ["SecondBook"] = new
                {
                    kind = "excel",
                    sourcePath = "src/SecondBook",
                    templatePath = "src/SecondBook/SecondBook.xlsm",
                    binPath = "bin/SecondBook/SecondBook.xlsm",
                    publishPath = "publish/SecondBook/SecondBook.xlsm",
                    references = new[]
                    {
                        new { name = "Visual Basic For Applications" }
                    }
                }
            }
        };
        File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ToFileUri(string path)
        => new Uri(path).AbsoluteUri;

    private static string ToEncodedDriveFileUri(string path)
    {
        var fullPath = Path.GetFullPath(path).Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Length >= 2 && fullPath[1] == Path.VolumeSeparatorChar
            ? $"file:///{char.ToLowerInvariant(fullPath[0])}%3A{fullPath[2..]}"
            : new Uri(path).AbsoluteUri;
    }

    public static IEnumerable<object[]> BomAndUtf8EncodedSourceCases()
    {
        const string documentation = "\u65e5\u672c\u8a9e\u306e\u8aac\u660e";
        var source = string.Join('\n', [
            "Attribute VB_Name = \"Helper\"",
            $"'* @brief {documentation}",
            "Public Function BuildValue() As String",
            "End Function"
        ]);
        yield return [AddPreamble(Encoding.UTF8.GetPreamble(), Encoding.UTF8.GetBytes(source))];
        yield return [AddPreamble(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(source))];
        yield return [new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(source)];
    }

    private static byte[] AddPreamble(byte[] preamble, byte[] bytes)
        => preamble.Concat(bytes).ToArray();
}
