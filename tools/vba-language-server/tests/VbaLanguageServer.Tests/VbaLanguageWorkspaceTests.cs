using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VbaLanguageServer.Lsp;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLanguageWorkspaceTests
{
    [Fact]
    public void DocumentMutationInterfaceDoesNotExposeParserImplementationMetadata()
    {
        var workspaceType = typeof(VbaLanguageWorkspace);

        Assert.Equal(
            typeof(void),
            workspaceType.GetMethod(nameof(VbaLanguageWorkspace.UpdateDocument))!
                .ReturnType);
        Assert.Equal(
            typeof(void),
            workspaceType.GetMethod(nameof(VbaLanguageWorkspace.OpenDocument))!
                .ReturnType);
        Assert.Equal(
            typeof(bool),
            workspaceType.GetMethod(nameof(VbaLanguageWorkspace.ChangeDocument))!
                .ReturnType);
    }

    [Fact]
    public void Warm_project_capture_reuses_the_immutable_workspace_state()
    {
        const string uri = "file:///C:/work/WarmState.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(
            uri,
            "Attribute VB_Name = \"WarmState\"\nPublic Sub Run()\nEnd Sub\n");
        var copyWorkspaceState = Assert.IsAssignableFrom<MethodInfo>(
            typeof(VbaLanguageWorkspace).GetMethod(
                "CopyWorkspaceState",
                BindingFlags.Instance | BindingFlags.NonPublic));

        var first = copyWorkspaceState.Invoke(workspace, null);
        var second = copyWorkspaceState.Invoke(workspace, null);

        Assert.Same(first, second);
    }

    [Fact]
    public void Workspace_snapshot_capture_deduplicates_scopes_before_provider_capture()
    {
        var observer = new CountingProjectSnapshotBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            observer);
        for (var index = 0; index < 24; index++)
        {
            workspace.UpdateDocument(
                $"file:///C:/work/SameScope/Module{index:D2}.bas",
                $"Attribute VB_Name = \"Module{index:D2}\"\n"
                + $"Public Sub Run{index:D2}()\nEnd Sub\n");
        }

        var snapshots = workspace.CreateProjectSnapshots();

        Assert.Single(snapshots);
        Assert.Equal(1, observer.CaptureCount);
    }

    [Fact]
    public void Warm_workspace_snapshot_capture_reuses_the_known_project_scope()
    {
        var lifecycleObserver = new CountingSnapshotManifestResolveObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            lifecycleObserver);
        var uris = Enumerable.Range(0, 24)
            .Select(index => $"file:///C:/work/KnownScope/Module{index:D2}.bas")
            .ToArray();
        for (var index = 0; index < uris.Length; index++)
        {
            workspace.UpdateDocument(
                uris[index],
                $"Attribute VB_Name = \"Module{index:D2}\"\n"
                + $"Public Sub Run{index:D2}()\nEnd Sub\n");
        }

        workspace.CreateProjectSnapshot(uris[0]);
        var resolveCountAfterWarmup = lifecycleObserver.ManifestResolveCount;

        var snapshots = workspace.CreateProjectSnapshots();

        Assert.Single(snapshots);
        Assert.Equal(
            resolveCountAfterWarmup,
            lifecycleObserver.ManifestResolveCount);
    }

    [Fact]
    public void Project_snapshot_exposes_semantic_inventory_without_raw_source_index()
    {
        Assert.NotNull(
            typeof(VbaProjectSnapshot).GetProperty(
                nameof(VbaProjectSnapshot.SemanticInventory)));
        Assert.Null(typeof(VbaProjectSnapshot).GetProperty("SourceIndex"));
    }

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
        Assert.Equal(
            firstSnapshot.DiagnosticsOwnership?.CacheIdentity,
            refreshedSnapshot.DiagnosticsOwnership?.CacheIdentity);
        Assert.Equal(
            firstSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity,
            refreshedSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity);
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                firstSnapshot.Resolution,
                out var firstAuthority));
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyAuthority(
                refreshedSnapshot.Resolution,
                out var refreshedAuthority));
        Assert.Equal(firstAuthority, refreshedAuthority);
    }

    [Fact]
    public void Source_edit_rebuilds_only_its_project_scope_snapshot()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-scope-revision-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectAUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var projectBUri = ToFileUri(
                Path.Combine(projectRoot, "src", "SecondBook", "Worker.bas"));
            var buildObserver = new CountingProjectSnapshotBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.UpdateDocument(
                projectAUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub BeforeEdit()\nEnd Sub\n");
            workspace.UpdateDocument(
                projectBUri,
                "Attribute VB_Name = \"ProjectB\"\nPublic Sub Unchanged()\nEnd Sub\n");

            var beforeA = workspace.CreateProjectSnapshot(projectAUri);
            var beforeB = workspace.CreateProjectSnapshot(projectBUri);
            workspace.UpdateDocument(
                projectAUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub AfterEdit()\nEnd Sub\n");
            var afterA = workspace.CreateProjectSnapshot(projectAUri);
            var afterB = workspace.CreateProjectSnapshot(projectBUri);

            Assert.NotSame(beforeA, afterA);
            Assert.Same(beforeB, afterB);
            Assert.Equal(3, buildObserver.BuildCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Intrinsic_catalog_revision_rebuilds_every_project_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-intrinsic-catalog-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var firstUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Dialog.frm"));
            var secondUri = ToFileUri(
                Path.Combine(projectRoot, "src", "SecondBook", "Other.frm"));
            var observer = new CountingProjectSnapshotBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                observer);
            workspace.UpdateDocument(
                firstUri,
                "VERSION 5.00\nBegin VB.Form Dialog\nEnd\n"
                + "Attribute VB_Name = \"Dialog\"\n");
            workspace.UpdateDocument(
                secondUri,
                "VERSION 5.00\nBegin VB.Form Other\nEnd\n"
                + "Attribute VB_Name = \"Other\"\n");

            var beforeFirst = workspace.CreateProjectSnapshot(firstUri);
            var beforeSecond = workspace.CreateProjectSnapshot(secondUri);
            Assert.True(new VbaIntrinsicHostEventCatalogHandler(workspace)
                .TryApply(CreateCatalogPayload(revision: 1)));
            var afterFirst = workspace.CreateProjectSnapshot(firstUri);
            var afterSecond = workspace.CreateProjectSnapshot(secondUri);

            Assert.NotSame(beforeFirst, afterFirst);
            Assert.NotSame(beforeSecond, afterSecond);
            Assert.Equal(4, observer.BuildCount);
            Assert.Equal(
                "Initialize",
                Assert.Single(afterFirst.SemanticInventory
                    .IntrinsicHostEventCatalog!.Events).Name);
            Assert.Equal(
                "Initialize",
                Assert.Single(afterSecond.SemanticInventory
                    .IntrinsicHostEventCatalog!.Events).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Intrinsic_catalog_atomically_replaces_clears_and_rejects_stale_revisions()
    {
        const string uri = "file:///C:/work/Dialog.frm";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(
            uri,
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\n"
            + "Attribute VB_Name = \"Dialog\"\n");
        var handler = new VbaIntrinsicHostEventCatalogHandler(workspace);

        Assert.True(handler.TryApply(
            CreateCatalogPayload(revision: 2, eventName: "Initialize")));
        Assert.False(handler.TryApply(
            CreateCatalogPayload(revision: 2, eventName: "QueryClose")));
        Assert.False(handler.TryApply(
            CreateCatalogPayload(revision: 1, eventName: "QueryClose")));
        Assert.Equal(
            "Initialize",
            Assert.Single(workspace.CreateProjectSnapshot(uri)
                .SemanticInventory.IntrinsicHostEventCatalog!.Events).Name);

        Assert.True(handler.TryApply(CreateClearedCatalogPayload(revision: 3)));
        Assert.Null(workspace.CreateProjectSnapshot(uri)
            .SemanticInventory.IntrinsicHostEventCatalog);
        Assert.False(handler.TryApply(
            CreateCatalogPayload(revision: 2, eventName: "QueryClose")));
        Assert.Null(workspace.CreateProjectSnapshot(uri)
            .SemanticInventory.IntrinsicHostEventCatalog);
    }

    [Fact]
    public void Intrinsic_catalog_notification_preserves_the_full_structured_contract()
    {
        const string uri = "file:///C:/work/Dialog.frm";
        const string formsGuid = "0d452ee1-e08f-101a-852e-02608c4d0bb4";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(
            uri,
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\n"
            + "Attribute VB_Name = \"Dialog\"\n");
        var payload = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = "1.0",
            revision = 7,
            catalog = new
            {
                sourceKind = "userForm",
                intrinsicEventSourceName = "UserForm",
                events = new object[]
                {
                    new
                    {
                        identity = new
                        {
                            sourceName = "UserForm",
                            name = "QueryClose"
                        },
                        signature = new
                        {
                            parameters = new object[]
                            {
                                new
                                {
                                    name = "Cancel",
                                    type = new
                                    {
                                        kind = "intrinsic",
                                        name = "Boolean"
                                    },
                                    passing = "byRef",
                                    arrayShape = "scalar",
                                    optional = false,
                                    paramArray = false
                                },
                                new
                                {
                                    name = "CloseMode",
                                    type = new
                                    {
                                        kind = "typeLib",
                                        name = "VbQueryClose",
                                        libraryGuid = formsGuid,
                                        majorVersion = 2,
                                        minorVersion = 0,
                                        lcid = 0
                                    },
                                    passing = "byVal",
                                    arrayShape = "scalar",
                                    optional = false,
                                    paramArray = false
                                }
                            },
                            documentation = "Occurs before a UserForm closes."
                        },
                        authoringAvailable = true,
                        existingHandlerRecognizable = true
                    }
                },
                baseTypeProvenance = new
                {
                    name = "UserForm",
                    libraryGuid = formsGuid,
                    majorVersion = 2,
                    minorVersion = 0,
                    lcid = 0
                }
            }
        });

        Assert.True(new VbaIntrinsicHostEventCatalogHandler(workspace)
            .TryApply(payload));
        var catalog = workspace.CreateProjectSnapshot(uri)
            .SemanticInventory.IntrinsicHostEventCatalog;
        var hostEvent = Assert.Single(catalog!.Events);
        var cancel = hostEvent.Parameters[0];
        var closeMode = hostEvent.Parameters[1];

        Assert.Equal(VbaIntrinsicHostEventSourceKind.UserForm, catalog.SourceKind);
        Assert.Equal("UserForm", hostEvent.Identity.SourceName);
        Assert.Equal("QueryClose", hostEvent.Identity.Name);
        Assert.Equal("Occurs before a UserForm closes.", hostEvent.Documentation);
        Assert.True(hostEvent.AuthoringAvailable);
        Assert.True(hostEvent.ExistingHandlerRecognizable);
        Assert.Equal(VbaHostEventParameterPassing.ByRef, cancel.Passing);
        Assert.IsType<VbaIntrinsicHostEventParameterType>(cancel.Type);
        Assert.Equal(VbaHostEventParameterPassing.ByVal, closeMode.Passing);
        Assert.IsType<VbaTypeLibraryHostEventParameterType>(closeMode.Type);
        Assert.Equal(formsGuid, catalog.BaseTypeProvenance?.LibraryGuid);
    }

    [Fact]
    public void Intrinsic_catalog_store_commits_a_deeply_immutable_value()
    {
        const string uri = "file:///C:/work/Dialog.frm";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.UpdateDocument(
            uri,
            "VERSION 5.00\nBegin VB.Form Dialog\nEnd\n"
            + "Attribute VB_Name = \"Dialog\"\n");
        var parameters = new List<VbaHostEventParameter>();
        var events = new List<VbaIntrinsicHostEvent>
        {
            new(
                new VbaIntrinsicHostEventIdentity("UserForm", "Initialize"),
                new VbaIntrinsicHostEventSignature(
                    parameters,
                    "Initial documentation."),
                AuthoringAvailable: true,
                ExistingHandlerRecognizable: true)
        };
        var catalog = new VbaIntrinsicHostEventCatalog(
            VbaIntrinsicHostEventSourceKind.UserForm,
            "UserForm",
            events);

        Assert.True(workspace.TryApplyIntrinsicHostEventCatalog(
            new VbaIntrinsicHostEventCatalogUpdate(1, catalog)));
        events[0] = events[0] with
        {
            Identity = new VbaIntrinsicHostEventIdentity(
                "UserForm",
                "QueryClose")
        };
        parameters.Add(new VbaHostEventParameter(
            "Cancel",
            new VbaIntrinsicHostEventParameterType("Boolean"),
            VbaHostEventParameterPassing.ByRef,
            VbaHostEventParameterArrayShape.Scalar,
            Optional: false,
            ParamArray: false));

        var committed = workspace.CreateProjectSnapshot(uri)
            .SemanticInventory.IntrinsicHostEventCatalog;
        var committedEvent = Assert.Single(committed!.Events);
        Assert.Equal("Initialize", committedEvent.Name);
        Assert.Empty(committedEvent.Parameters);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VbaIntrinsicHostEvent>)committed.Events)[0] =
                events[0]);
    }

    [Fact]
    public void Intrinsic_catalog_rejects_unknown_versions_and_fields_without_mutation()
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var handler = new VbaIntrinsicHostEventCatalogHandler(workspace);
        var unsupportedVersion = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = "2.0",
            revision = 1,
            catalog = CreateCatalogValue("Initialize")
        });
        var unknown = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = "1.0",
            revision = 1,
            catalog = CreateCatalogValue("Initialize"),
            futureField = true
        });

        Assert.False(handler.TryApply(unsupportedVersion));
        Assert.False(handler.TryApply(unknown));
    }

    private static JsonNode CreateCatalogPayload(
        long revision,
        string eventName = "Initialize")
        => JsonSerializer.SerializeToNode(new
        {
            schemaVersion = "1.0",
            revision,
            catalog = CreateCatalogValue(eventName)
        })!;

    private static JsonNode CreateClearedCatalogPayload(long revision)
        => JsonSerializer.SerializeToNode(new
        {
            schemaVersion = "1.0",
            revision,
            catalog = (object?)null
        })!;

    private static object CreateCatalogValue(string eventName)
        => new
        {
            sourceKind = "userForm",
            intrinsicEventSourceName = "UserForm",
            events = new object[]
            {
                new
                {
                    identity = new { sourceName = "UserForm", name = eventName },
                    signature = new
                    {
                        parameters = Array.Empty<object>(),
                        documentation = "Intrinsic UserForm Event."
                    },
                    authoringAvailable = true,
                    existingHandlerRecognizable = true
                }
            }
        };
    [Fact]
    public void Manifest_change_does_not_rebuild_an_unrelated_project_scope()
    {
        var firstRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-scope-a-").FullName;
        var secondRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-scope-b-").FullName;
        try
        {
            WriteProjectManifest(firstRoot);
            WriteProjectManifest(secondRoot);
            var firstUri = ToFileUri(
                Path.Combine(firstRoot, "src", "Book1", "Worker.bas"));
            var secondUri = ToFileUri(
                Path.Combine(secondRoot, "src", "Book1", "Worker.bas"));
            var buildObserver = new CountingProjectSnapshotBuildObserver();
            var lifecycleObserver = new CountingSnapshotManifestResolveObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                lifecycleObserver,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.UpdateDocument(
                firstUri,
                "Attribute VB_Name = \"First\"\nPublic Sub RunFirst()\nEnd Sub\n");
            workspace.UpdateDocument(
                secondUri,
                "Attribute VB_Name = \"Second\"\nPublic Sub RunSecond()\nEnd Sub\n");
            workspace.CreateProjectSnapshot(firstUri);
            var beforeSecond = workspace.CreateProjectSnapshot(secondUri);
            var manifestResolveCount = lifecycleObserver.ManifestResolveCount;

            var firstManifestPath = Path.Combine(firstRoot, "vba-project.json");
            var opened = workspace.ManifestWorkspace.OpenManifest(
                ToFileUri(firstManifestPath),
                documentVersion: 1,
                File.ReadAllText(firstManifestPath));
            var afterSecond = workspace.CreateProjectSnapshot(secondUri);

            Assert.True(opened.Accepted);
            Assert.Same(beforeSecond, afterSecond);
            Assert.Equal(2, buildObserver.BuildCount);
            Assert.Equal(
                manifestResolveCount,
                lifecycleObserver.ManifestResolveCount);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Unrelated_source_edit_does_not_discard_an_in_flight_project_snapshot()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-scope-build-").FullName;
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        try
        {
            WriteProjectManifest(projectRoot);
            var projectAUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var projectBUri = ToFileUri(
                Path.Combine(projectRoot, "src", "SecondBook", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.UpdateDocument(
                projectAUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub BeforeEdit()\nEnd Sub\n");
            workspace.UpdateDocument(
                projectBUri,
                "Attribute VB_Name = \"ProjectB\"\nPublic Sub Unchanged()\nEnd Sub\n");

            var projectBBuild = Task.Run(
                () => workspace.CreateProjectSnapshot(projectBUri));
            await buildObserver.FirstBuildWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workspace.UpdateDocument(
                projectAUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub AfterEdit()\nEnd Sub\n");
            buildObserver.ReleaseFirstBuild();
            var projectBSnapshot =
                await projectBBuild.WaitAsync(TimeSpan.FromSeconds(5));
            var reusedProjectBSnapshot =
                workspace.CreateProjectSnapshot(projectBUri);

            Assert.Same(projectBSnapshot, reusedProjectBSnapshot);
            Assert.Equal(1, buildObserver.BuildCount);
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ProjectSnapshotReflectsSafeDocumentChanges()
    {
        const string uri = "file:///C:/work/Worker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));

        workspace.UpdateDocument(uri, string.Join('\n', [
            "Attribute VB_Name = \"Worker\"",
            "Public Function BuildValue() As String",
            "    BuildValue = \"old\"",
            "End Function",
            "",
            "Public Sub Run()",
            "    BuildValue",
            "End Sub"
        ]));
        workspace.UpdateDocument(uri, string.Join('\n', [
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
        var definition = snapshot.SemanticInventory.ResolveDefinition(
            uri,
            line: 6,
            character: "    ".Length);

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
            var definition = snapshot.SemanticInventory.ResolveDefinition(
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
                .SemanticInventory
                .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
            workspace.UpdateDocument(helperUri, string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                "Public Function BuildReplacement() As String",
                "End Function"
            ]));
            var overlaySnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Equal(helperUri, diskDefinition?.Uri);
            Assert.DoesNotContain(
                overlaySnapshot.SemanticInventory.GetWorkspaceSymbols("BuildValue"),
                symbol => symbol.Uri == helperUri);
            Assert.Contains(
                overlaySnapshot.SemanticInventory.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void ManifestBackedProjectSnapshotIdentityDoesNotDependOnActiveDocumentUri()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-scope-identity-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Helper.bas"));
            var callerUri = ToFileUri(Path.Combine(projectRoot, "src", "Book1", "Caller.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(helperUri, string.Join('\n', [
                "Attribute VB_Name = \"Helper\"",
                "Public Function BuildValue() As String",
                "End Function"
            ]));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var helperSnapshot = workspace.CreateProjectSnapshot(helperUri);
            var callerSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(helperSnapshot, callerSnapshot);
            Assert.Equal("Book1", callerSnapshot.Resolution.DocumentName);
            Assert.Contains(helperUri, callerSnapshot.SourceDocuments.Keys);
            Assert.Contains(callerUri, callerSnapshot.SourceDocuments.Keys);
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
                .SemanticInventory
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
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: true,
                    activeCodePage: 932));
            workspace.UpdateDocument(callerUri, string.Join('\n', [
                "Attribute VB_Name = \"Caller\"",
                "Public Sub Run()",
                "    BuildValue",
                "End Sub"
            ]));

            var semanticInventory = workspace
                .CreateProjectSnapshot(callerUri)
                .SemanticInventory;
            var definition = semanticInventory.ResolveSourceDefinition(
                callerUri,
                line: 2,
                character: "    ".Length);
            var classDefinition = semanticInventory
                .GetDocumentDefinitions(classUri)
                .Single(definition => definition.Name == "BuildClassValue");

            Assert.NotNull(definition);
            Assert.Equal(documentation, definition.Documentation);
            Assert.Equal("Function BuildValue() As String", definition.Signature?.Label);
            Assert.Equal(classDocumentation, classDefinition.Documentation);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Disk_encoding_and_active_acp_do_not_limit_identifier_forms()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-encoding-identifier-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var helperPath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Helper.bas");
            var helperUri = ToFileUri(helperPath);
            File.WriteAllText(
                helperPath,
                "Attribute VB_Name = \"Helper\"\n"
                + "Public Function 日本語() As String\n"
                + "End Function\n",
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: true,
                    activeCodePage: 1252));

            var symbols = workspace
                .CreateProjectSnapshot(helperUri)
                .SemanticInventory
                .GetWorkspaceSymbols("日本語");

            Assert.Contains(
                symbols,
                symbol => symbol.Name == "日本語"
                    && symbol.Uri == helperUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Open_unicode_text_overrides_invalid_equivalent_disk_bytes()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-open-invalid-disk-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var sourcePath = Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Worker.bas");
            var sourceUri = ToFileUri(sourcePath);
            File.WriteAllBytes(sourcePath, [0xC3, 0x28]);
            const string openText =
                "Attribute VB_Name = \"Worker\"\n"
                + "Public Sub 日本語()\n"
                + "End Sub\n";
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                new DiskSourceDecoding(
                    supportsLegacyFallback: false,
                    activeCodePage: 65001));
            workspace.OpenDocument(sourceUri, version: 1, openText);

            var snapshot = workspace.CreateProjectSnapshot(sourceUri);

            Assert.Equal(openText, snapshot.SourceDocuments[sourceUri]);
            Assert.Null(workspace.GetDiskSourceFailure(sourceUri));
            Assert.Contains(
                snapshot.SemanticInventory.GetWorkspaceSymbols("日本語"),
                symbol => symbol.Uri == sourceUri);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void WatcherLessDiskSourceWriteStaysStaleUntilReload()
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
            var staleSnapshot = workspace.CreateProjectSnapshot(callerUri);
            workspace.ReloadSourceDocument(helperUri, File.ReadAllText(helperPath));
            var refreshedSnapshot = workspace.CreateProjectSnapshot(callerUri);

            Assert.Same(initialSnapshot, reusedSnapshot);
            Assert.Same(initialSnapshot, staleSnapshot);
            Assert.NotSame(initialSnapshot, refreshedSnapshot);
            Assert.Contains(
                refreshedSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildReplacement"),
                symbol => symbol.Uri == helperUri);
            Assert.DoesNotContain(
                refreshedSnapshot.SemanticInventory.GetWorkspaceSymbols("BuildValue"),
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
            var definition = snapshot.SemanticInventory.ResolveDefinition(
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

        var initialSnapshot = workspace.CreateProjectSnapshot(callerUri);
        var initialDefinition = initialSnapshot.SemanticInventory
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.RemoveDocument(helperUri);
        var removedSnapshot = workspace.CreateProjectSnapshot(callerUri);
        var removedDefinition = removedSnapshot.SemanticInventory
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.UpdateDocument(renamedHelperUri, helperText);
        var renamedSnapshot = workspace.CreateProjectSnapshot(callerUri);
        var renamedDefinition = renamedSnapshot.SemanticInventory
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);

        Assert.Equal(helperUri, initialDefinition?.Uri);
        Assert.Null(removedDefinition);
        Assert.Equal(renamedHelperUri, renamedDefinition?.Uri);
        Assert.Equal(
            initialSnapshot.DiagnosticsOwnership?.CacheIdentity,
            removedSnapshot.DiagnosticsOwnership?.CacheIdentity);
        Assert.Equal(
            initialSnapshot.DiagnosticsOwnership?.CacheIdentity,
            renamedSnapshot.DiagnosticsOwnership?.CacheIdentity);
        Assert.Equal(
            initialSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity,
            renamedSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity);
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
                book1References: ["Microsoft Scripting Runtime"],
                secondBookSourcePath: "src/RetiredBook");
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
            workspace.UpdateDocument(uri, string.Join('\n', [
                "Attribute VB_Name = \"Worker\"",
                "Public Sub Run()",
                "    Dim value As ",
                "    result = Gen",
                "    result = Generated.",
                "End Sub"
            ]));

            var beforeRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var reusedBeforeRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var beforeRefresh = beforeRefreshSnapshot
                .SemanticInventory
                .GetCompletionResult(uri, line: 2, character: "    Dim value As ".Length)
                .Definitions
                .Select(definition => definition.Name)
                .ToArray();
            var beforeRoot = beforeRefreshSnapshot.SemanticInventory
                .GetCompletionResult(uri, line: 3, character: "    result = Gen".Length)
                .Candidates
                .Select(candidate => candidate.Label)
                .ToArray();
            var beforeQualified = beforeRefreshSnapshot.SemanticInventory
                .GetCompletionResult(uri, line: 4, character: "    result = Generated.".Length)
                .Candidates
                .Select(candidate => candidate.Label)
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
                            VbaSourceDefinitionKind.Class),
                        new VbaProjectReferenceDefinition(
                            "Generated Library",
                            "GeneratedValue",
                            VbaSourceDefinitionKind.Property,
                            TypeReference: new VbaTypeReference("Variant"),
                            PropertyAccess: VbaPropertyAccess.Readable,
                            GlobalExposure: ReferenceDefinitionGlobalExposure.LibraryGlobal)
                    ])));
            var afterRefreshSnapshot = workspace.CreateProjectSnapshot(uri);
            var afterRefresh = afterRefreshSnapshot
                .SemanticInventory
                .GetCompletionResult(uri, line: 2, character: "    Dim value As ".Length)
                .Definitions
                .Select(definition => definition.Name)
                .ToArray();
            var afterRoot = afterRefreshSnapshot.SemanticInventory
                .GetCompletionResult(uri, line: 3, character: "    result = Gen".Length)
                .Candidates
                .Select(candidate => candidate.Label)
                .ToArray();
            var afterQualified = afterRefreshSnapshot.SemanticInventory
                .GetCompletionResult(uri, line: 4, character: "    result = Generated.".Length)
                .Candidates
                .Select(candidate => candidate.Label)
                .ToArray();

            Assert.Same(beforeRefreshSnapshot, reusedBeforeRefreshSnapshot);
            Assert.NotSame(beforeRefreshSnapshot, afterRefreshSnapshot);
            Assert.DoesNotContain("GeneratedType", beforeRefresh);
            Assert.Contains("GeneratedType", afterRefresh);
            Assert.DoesNotContain("Generated", beforeRoot);
            Assert.DoesNotContain("GeneratedValue", beforeRoot);
            Assert.Empty(beforeQualified);
            Assert.Contains("Generated", afterRoot);
            Assert.Contains("GeneratedValue", afterRoot);
            Assert.Contains("GeneratedValue", afterQualified);
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
                return snapshot.SemanticInventory.ResolveDefinition(
                    uri,
                    line: 5,
                    character: "    ".Length);
            }))
            .ToArray();

        var definitions = await Task.WhenAll(tasks);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.All(definitions, Assert.NotNull);
        Assert.Throws<OperationCanceledException>(() => workspace.CreateProjectSnapshot(uri, cancellation.Token));
    }

    [Fact]
    public async Task Project_snapshot_build_completed_after_invalidation_cannot_replace_newer_cache()
    {
        const string uri = "file:///C:/work/SnapshotRace.bas";
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        workspace.UpdateDocument(
            uri,
            "Attribute VB_Name = \"SnapshotRace\"\nPublic Sub OldProcedure()\nEnd Sub\n");
        var oldBuild = Task.Run(() => workspace.CreateProjectSnapshot(uri));
        await buildObserver.FirstBuildWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        workspace.UpdateDocument(
            uri,
            "Attribute VB_Name = \"SnapshotRace\"\nPublic Sub NewProcedure()\nEnd Sub\n");
        var newSnapshot = workspace.CreateProjectSnapshot(uri);
        buildObserver.ReleaseFirstBuild();
        var oldSnapshot = await oldBuild.WaitAsync(TimeSpan.FromSeconds(5));
        var reusedSnapshot = workspace.CreateProjectSnapshot(uri);

        Assert.Contains(
            oldSnapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "OldProcedure");
        Assert.Contains(
            newSnapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "NewProcedure");
        Assert.Same(newSnapshot, reusedSnapshot);
    }

    [Fact]
    public void OpenDocumentChangesRequireIncreasingVersions()
    {
        const string uri = "file:///C:/work/VersionedWorker.bas";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            uri,
            version: 3,
            "Public Sub CurrentVersion()\nEnd Sub\n");
        var currentSnapshot = workspace.CreateProjectSnapshot(uri);

        var olderUpdate = workspace.ChangeDocument(
            uri,
            version: 2,
            "Public Sub OlderVersion()\nEnd Sub\n");
        var equalUpdate = workspace.ChangeDocument(
            uri,
            version: 3,
            "Public Sub EqualVersion()\nEnd Sub\n");
        var unchangedSnapshot = workspace.CreateProjectSnapshot(uri);
        var newerUpdate = workspace.ChangeDocument(
            uri,
            version: 4,
            "Public Sub NewerVersion()\nEnd Sub\n");
        var newerSnapshot = workspace.CreateProjectSnapshot(uri);

        Assert.False(olderUpdate);
        Assert.False(equalUpdate);
        Assert.Same(currentSnapshot, unchangedSnapshot);
        Assert.Contains(
            unchangedSnapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "CurrentVersion");
        Assert.True(newerUpdate);
        Assert.Contains(
            newerSnapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "NewerVersion");
        Assert.DoesNotContain(
            newerSnapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "CurrentVersion");
    }

    [Fact]
    public void WatchedReloadPreservesEquivalentOpenBufferAndCloseFallsBackToDisk()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-authority-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var canonicalUri = ToFileUri(sourcePath);
            var encodedUri = ToEncodedDriveFileUri(sourcePath);
            File.WriteAllText(sourcePath, "Public Sub InitialDisk()\nEnd Sub\n");
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(
                encodedUri,
                version: 1,
                "Public Sub UnsavedBuffer()\nEnd Sub\n");
            const string latestDiskText = "Public Sub LatestDisk()\nEnd Sub\n";
            File.WriteAllText(sourcePath, latestDiskText);

            var diskBecameAuthoritative = workspace.ReloadSourceDocument(canonicalUri, latestDiskText);
            var openSnapshot = workspace.CreateProjectSnapshot(encodedUri);
            var closed = workspace.CloseDocument(canonicalUri);
            var diskSnapshot = workspace.CreateProjectSnapshot(canonicalUri);

            Assert.False(diskBecameAuthoritative);
            Assert.Contains(
                openSnapshot.SemanticInventory.GetDocumentDefinitions(encodedUri),
                definition => definition.Name == "UnsavedBuffer");
            Assert.DoesNotContain(
                openSnapshot.SemanticInventory.GetWorkspaceSymbols("LatestDisk"),
                symbol => VbaProjectResolver.TryGetLocalPath(symbol.Uri) == Path.GetFullPath(sourcePath));
            Assert.True(closed);
            Assert.Contains(
                diskSnapshot.SemanticInventory.GetDocumentDefinitions(canonicalUri),
                definition => definition.Name == "LatestDisk");
            Assert.Single(
                diskSnapshot.SourceDocuments.Keys,
                uri => string.Equals(
                    VbaProjectResolver.TryGetLocalPath(uri),
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Disk_reload_adopts_the_latest_equivalent_input_uri()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-reload-uri-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var canonicalUri = ToFileUri(sourcePath);
            var encodedUri = ToEncodedDriveFileUri(sourcePath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            Assert.True(workspace.ReloadSourceDocument(
                encodedUri,
                "Public Sub Encoded()\nEnd Sub\n"));

            Assert.True(workspace.ReloadSourceDocument(
                canonicalUri,
                "Public Sub Canonical()\nEnd Sub\n"));

            Assert.Equal(canonicalUri, Assert.Single(workspace.GetDocumentUris()));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void New_open_lifecycle_adopts_the_latest_equivalent_input_uri()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-update-uri-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var canonicalUri = ToFileUri(sourcePath);
            var encodedUri = ToEncodedDriveFileUri(sourcePath);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            Assert.True(workspace.ReloadSourceDocument(
                encodedUri,
                "Public Sub Disk()\nEnd Sub\n"));

            workspace.UpdateDocument(
                canonicalUri,
                "Public Sub OpenBuffer()\nEnd Sub\n");

            Assert.Equal(canonicalUri, Assert.Single(workspace.GetDocumentUris()));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Non_file_document_identity_normalizes_equivalent_uris_without_merging_distinct_documents()
    {
        const string openedUri =
            "untitled://WORKSPACE/Folder/../Module.bas";
        const string equivalentUri =
            "untitled://workspace/Module.bas";
        const string distinctUri =
            "untitled://workspace/Other.bas";
        const string changedText =
            "Public Sub ChangedBuffer()\nEnd Sub\n";
        const string distinctText =
            "Public Sub DistinctBuffer()\nEnd Sub\n";
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(
            openedUri,
            version: 1,
            "Public Sub InitialBuffer()\nEnd Sub\n");
        var initialSnapshot = workspace.CreateProjectSnapshot(openedUri);

        var changed = workspace.ChangeDocument(
            equivalentUri,
            version: 2,
            changedText);
        var changedSnapshot = workspace.CreateProjectSnapshot(equivalentUri);
        workspace.OpenDocument(
            distinctUri,
            version: 1,
            distinctText);
        var distinctSnapshot = workspace.CreateProjectSnapshot(distinctUri);

        Assert.True(changed);
        Assert.Equal(changedText, workspace.GetDocumentText(openedUri));
        Assert.Equal(changedText, workspace.GetDocumentText(equivalentUri));
        Assert.Equal(2, workspace.GetDocumentUris().Count);
        Assert.NotSame(initialSnapshot, changedSnapshot);
        Assert.Equal(
            initialSnapshot.DiagnosticsOwnership?.CacheIdentity,
            changedSnapshot.DiagnosticsOwnership?.CacheIdentity);
        Assert.Equal(
            initialSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity,
            changedSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity);
        Assert.NotEqual(
            changedSnapshot.DiagnosticsOwnership?.CacheIdentity,
            distinctSnapshot.DiagnosticsOwnership?.CacheIdentity);
        Assert.NotEqual(
            changedSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity,
            distinctSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity);

        Assert.True(workspace.CloseDocument(equivalentUri));
        Assert.Null(workspace.GetDocumentText(openedUri));
        Assert.Equal(
            distinctText,
            workspace.GetDocumentText(distinctUri));
    }

    [Fact]
    public void WatchedDeletePreservesOpenBufferUntilCloseAndReloadClearsExclusion()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-delete-authority-").FullName;
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Worker.bas");
            var canonicalUri = ToFileUri(sourcePath);
            var encodedUri = ToEncodedDriveFileUri(sourcePath);
            File.WriteAllText(sourcePath, "Public Sub DiskVersion()\nEnd Sub\n");
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.OpenDocument(
                encodedUri,
                version: 1,
                "Public Sub OpenAfterDelete()\nEnd Sub\n");
            File.Delete(sourcePath);

            var shouldClearWhileOpen = workspace.DeleteSourceDocument(canonicalUri);
            var openSnapshot = workspace.CreateProjectSnapshot(encodedUri);
            workspace.CloseDocument(canonicalUri);
            var deletedSnapshot = workspace.CreateProjectSnapshot(canonicalUri);
            const string recreatedText = "Public Sub RecreatedDisk()\nEnd Sub\n";
            File.WriteAllText(sourcePath, recreatedText);
            var reloaded = workspace.ReloadSourceDocument(encodedUri, recreatedText);
            var recreatedSnapshot = workspace.CreateProjectSnapshot(canonicalUri);

            Assert.False(shouldClearWhileOpen);
            Assert.Contains(
                openSnapshot.SemanticInventory.GetDocumentDefinitions(encodedUri),
                definition => definition.Name == "OpenAfterDelete");
            Assert.Empty(deletedSnapshot.SourceDocuments);
            Assert.True(reloaded);
            Assert.Equal(
                openSnapshot.DiagnosticsOwnership?.CacheIdentity,
                deletedSnapshot.DiagnosticsOwnership?.CacheIdentity);
            Assert.Equal(
                openSnapshot.DiagnosticsOwnership?.CacheIdentity,
                recreatedSnapshot.DiagnosticsOwnership?.CacheIdentity);
            Assert.Equal(
                openSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity,
                recreatedSnapshot.DiagnosticsOwnership?.ActiveDocumentIdentity);
            Assert.Contains(
                recreatedSnapshot.SemanticInventory.GetWorkspaceSymbols("RecreatedDisk"),
                symbol => string.Equals(
                    VbaProjectResolver.TryGetLocalPath(symbol.Uri),
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void WriteProjectManifest(
        string projectRoot,
        string book1SourcePath = "src/Book1",
        IReadOnlyList<string>? book1References = null,
        string secondBookSourcePath = "src/SecondBook")
    {
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        IReadOnlyList<string> references =
            book1References ?? ["Microsoft Excel 16.0 Object Library"];
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
                    commonModules = Array.Empty<object>(),
                    references = references
                        .Where(reference => !VbaProjectReferenceName.IsStandardLibrary(reference))
                        .Select(reference => new { name = reference, requested = true })
                        .ToArray()
                },
                ["SecondBook"] = new
                {
                    kind = "excel",
                    sourcePath = secondBookSourcePath,
                    templatePath = "src/SecondBook/SecondBook.xlsm",
                    binPath = "bin/SecondBook/SecondBook.xlsm",
                    publishPath = "publish/SecondBook/SecondBook.xlsm",
                    commonModules = Array.Empty<object>(),
                    references = Array.Empty<object>()
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

    private sealed class BlockingFirstProjectSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();
        private int observedBuilds;

        public TaskCompletionSource FirstBuildWaiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int BuildCount => Volatile.Read(ref observedBuilds);

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref observedBuilds) != 1)
            {
                return;
            }

            FirstBuildWaiting.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void ReleaseFirstBuild()
            => release.Set();
    }

    private sealed class CountingProjectSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        public int CaptureCount { get; private set; }

        public int BuildCount { get; private set; }

        public void BeforeCapture(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
        }

        public void BeforeStore(long workspaceVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuildCount++;
        }
    }

    private sealed class CountingSnapshotManifestResolveObserver
        : IVbaProjectReferenceCatalogLifecycleObserver
    {
        public int ManifestResolveCount { get; private set; }

        public void Record(VbaProjectReferenceCatalogLifecycleEvent lifecycleEvent)
        {
            if (lifecycleEvent.Operation
                == VbaProjectReferenceCatalogLifecycleOperation.ProjectSnapshotManifestResolve)
            {
                ManifestResolveCount++;
            }
        }
    }

    private static byte[] AddPreamble(byte[] preamble, byte[] bytes)
        => preamble.Concat(bytes).ToArray();
}
