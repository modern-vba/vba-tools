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
    public void Host_class_snapshot_rebuilds_only_its_matching_project_document_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-snapshot-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectAUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var projectBUri = ToFileUri(
                Path.Combine(projectRoot, "src", "SecondBook", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectAUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            workspace.UpdateDocument(
                projectBUri,
                "Attribute VB_Name = \"ProjectB\"\nPublic Sub RunB()\nEnd Sub\n");
            var beforeA = workspace.CreateProjectSnapshot(projectAUri);
            var beforeB = workspace.CreateProjectSnapshot(projectBUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = true,
                classes = Array.Empty<object>()
            }))!;

            var accepted = new VbaHostClassProjectionSnapshotHandler(workspace)
                .TryApply(payload);
            var afterA = workspace.CreateProjectSnapshot(projectAUri);
            var afterB = workspace.CreateProjectSnapshot(projectBUri);

            Assert.True(accepted);
            Assert.NotSame(beforeA, afterA);
            Assert.Same(beforeB, afterB);
            Assert.Equal(
                1,
                afterA.SemanticInventory.HostClassProjectionSnapshot?.Revision);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Host_class_snapshot_revision_invalidates_an_in_flight_older_project_cache_build()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-snapshot-race-").FullName;
        var buildObserver = new BlockingFirstProjectSnapshotBuildObserver();
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));

            var olderBuild = Task.Run(
                () => workspace.CreateProjectSnapshot(projectUri));
            await buildObserver.FirstBuildWaiting.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 2)));
            var newerSnapshot = workspace.CreateProjectSnapshot(projectUri);
            buildObserver.ReleaseFirstBuild();
            var olderSnapshot = await olderBuild.WaitAsync(TimeSpan.FromSeconds(5));
            var reusedSnapshot = workspace.CreateProjectSnapshot(projectUri);

            Assert.Equal(
                1,
                olderSnapshot.SemanticInventory.HostClassProjectionSnapshot?.Revision);
            Assert.Equal(
                2,
                newerSnapshot.SemanticInventory.HostClassProjectionSnapshot?.Revision);
            Assert.Same(newerSnapshot, reusedSnapshot);
        }
        finally
        {
            buildObserver.ReleaseFirstBuild();
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_replay_after_manifest_sync_precedes_source_open()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-replay-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(
                Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            Assert.True(workspace.ManifestWorkspace.OpenManifest(
                ToFileUri(manifestPath),
                documentVersion: 1,
                File.ReadAllText(manifestPath)).Accepted);

            var accepted = new VbaHostClassProjectionSnapshotHandler(workspace)
                .TryApply(CreateEmptyHostSnapshotPayload(
                    projectRoot,
                    sourceTemplate,
                    revision: 1));

            Assert.True(accepted);
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            Assert.Equal(
                1,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_clear_removes_the_matching_document_projection()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-clear-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var context = new
            {
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm"))
            };
            var present = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                context.project,
                context.document,
                context.sourceTemplate,
                state = "present",
                classEnumerationComplete = true,
                classes = Array.Empty<object>()
            }))!;
            var cleared = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 2,
                context.project,
                context.document,
                context.sourceTemplate,
                state = "cleared"
            }))!;
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);

            Assert.True(handler.TryApply(present));
            var beforeClear = workspace.CreateProjectSnapshot(projectUri);
            Assert.Equal(
                1,
                beforeClear.SemanticInventory.HostClassProjectionSnapshot?.Revision);

            Assert.True(handler.TryApply(cleared));
            var afterClear = workspace.CreateProjectSnapshot(projectUri);

            Assert.NotSame(beforeClear, afterClear);
            Assert.Null(afterClear.SemanticInventory.HostClassProjectionSnapshot);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_rejects_malformed_cleared_context_without_throwing()
    {
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()));
        var payload = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision = 1,
            project = "C:\\work\0bad",
            document = "Book1",
            sourceTemplate = "C:\\work\\Book1.xlsm",
            state = "cleared"
        }))!;

        var accepted = new VbaHostClassProjectionSnapshotHandler(workspace)
            .TryApply(payload);

        Assert.False(accepted);
    }

    [Fact]
    public void Host_class_snapshot_rejects_noncanonical_equivalent_context()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-noncanonical-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var payload = CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1);
            payload["project"] = Path.Combine(projectRoot, ".");

            var accepted = new VbaHostClassProjectionSnapshotHandler(workspace)
                .TryApply(payload);

            Assert.False(accepted);
            Assert.Null(workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_rejects_equal_and_stale_revisions_without_replacing_current()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-stale-revision-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);

            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 2)));
            Assert.False(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 2)));
            Assert.False(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));

            Assert.Equal(
                2,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_rejects_fresh_mismatched_source_template_without_replacing_current()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-context-mismatch-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var mismatchedTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Other.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);

            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));
            Assert.False(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                mismatchedTemplate,
                revision: 2)));

            var retained = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(retained);
            Assert.Equal(1, retained.Revision);
            Assert.Equal(sourceTemplate, retained.Context.SourceTemplate);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_rejects_unknown_nested_schema_without_replacing_current()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-nested-schema-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));
            var invalid = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 2,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate,
                state = "present",
                classEnumerationComplete = true,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "Sheet1", kind = "document" },
                        authority = "current",
                        projection = new
                        {
                            intrinsicEventSourceName = "Worksheet",
                            events = Array.Empty<object>(),
                            unexpected = true
                        }
                    }
                }
            }))!;

            Assert.False(handler.TryApply(invalid));
            Assert.Equal(
                1,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_rejects_case_insensitive_duplicate_identities_without_replacing_current()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-duplicate-identity-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var sourceTemplate = Path.GetFullPath(Path.Combine(
                projectRoot,
                "src",
                "Book1",
                "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));
            var invalid = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 2,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate,
                state = "present",
                classEnumerationComplete = false,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "Sheet1", kind = "document" },
                        authority = "indeterminate"
                    },
                    new
                    {
                        identity = new { name = "sheet1", kind = "document" },
                        authority = "indeterminate"
                    }
                }
            }))!;

            Assert.False(handler.TryApply(invalid));
            Assert.Equal(
                1,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_exposes_current_projection_event_evidence()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-event-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = true,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "Sheet1", kind = "document" },
                        authority = "current",
                        projection = new
                        {
                            intrinsicEventSourceName = "Worksheet",
                            events = new object[]
                            {
                                new
                                {
                                    name = "Change",
                                    parameters = new object[]
                                    {
                                        new
                                        {
                                            name = "Target",
                                            type = new { kind = "intrinsic", name = "Variant" },
                                            passing = "byVal",
                                            arrayShape = "scalar",
                                            optional = false,
                                            paramArray = false
                                        }
                                    },
                                    documentation = "Occurs when cells change.",
                                    authoringAvailable = true,
                                    existingHandlerRecognizable = true
                                }
                            }
                        }
                    }
                }
            }))!;

            Assert.True(new VbaHostClassProjectionSnapshotHandler(workspace).TryApply(payload));
            var snapshot = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(snapshot);
            var entry = Assert.IsType<VbaCurrentHostClassProjectionEntry>(
                Assert.Single(snapshot.Classes));
            var hostEvent = Assert.Single(entry.Projection.Events);
            var parameter = Assert.Single(hostEvent.Parameters);

            Assert.Equal("Sheet1", entry.Identity.Name);
            Assert.Equal(VbaHostClassKind.Document, entry.Identity.Kind);
            Assert.Equal("Worksheet", entry.Projection.IntrinsicEventSourceName);
            Assert.Equal("Change", hostEvent.Name);
            Assert.Equal("Occurs when cells change.", hostEvent.Documentation);
            Assert.True(hostEvent.AuthoringAvailable);
            Assert.True(hostEvent.ExistingHandlerRecognizable);
            Assert.Equal("Target", parameter.Name);
            Assert.Equal(VbaHostEventParameterPassing.ByVal, parameter.Passing);
            Assert.Equal(VbaHostEventParameterArrayShape.Scalar, parameter.ArrayShape);
            Assert.False(parameter.Optional);
            Assert.False(parameter.ParamArray);
            Assert.Equal(
                "Variant",
                Assert.IsType<VbaIntrinsicHostEventParameterType>(parameter.Type).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_commits_a_deeply_immutable_value()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-host-immutable-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var context = new VbaHostClassProjectionContext(
                Path.GetFullPath(projectRoot),
                "Book1",
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "src",
                    "Book1",
                    "Book1.xlsm")));
            var parameter = new VbaHostEventParameter(
                "Target",
                new VbaIntrinsicHostEventParameterType("Variant"),
                VbaHostEventParameterPassing.ByVal,
                VbaHostEventParameterArrayShape.Scalar,
                Optional: false,
                ParamArray: false);
            var hostEvent = new VbaHostEventSignature(
                "Change",
                new[] { parameter },
                Documentation: null,
                AuthoringAvailable: true,
                ExistingHandlerRecognizable: true);
            var entry = new VbaCurrentHostClassProjectionEntry(
                new VbaHostClassIdentity(
                    "Sheet1",
                    VbaHostClassKind.Document),
                new VbaHostClassProjection(
                    "Worksheet",
                    new[] { hostEvent }));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);

            Assert.True(workspace.TryApplyHostClassProjectionSnapshot(
                new VbaHostClassProjectionSnapshotUpdate(
                    context,
                    Revision: 1,
                    new VbaHostClassProjectionSnapshot(
                        Revision: 1,
                        context,
                        ClassEnumerationComplete: true,
                        new VbaHostClassProjectionEntry[] { entry }))));
            var committed = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(committed);
            var committedEntry = Assert.IsType<VbaCurrentHostClassProjectionEntry>(
                Assert.Single(committed!.Classes));
            var committedEvent = Assert.Single(committedEntry.Projection.Events);

            Assert.Throws<NotSupportedException>(() =>
            {
                ((IList<VbaHostClassProjectionEntry>)committed.Classes)[0] =
                    new VbaIndeterminateHostClassProjectionEntry(
                        committedEntry.Identity);
            });
            Assert.Throws<NotSupportedException>(() =>
            {
                ((IList<VbaHostEventSignature>)committedEntry.Projection.Events)[0] =
                    committedEvent with { Name = "Mutated" };
            });
            Assert.Throws<NotSupportedException>(() =>
            {
                ((IList<VbaHostEventParameter>)committedEvent.Parameters)[0] =
                    parameter with { Name = "Mutated" };
            });

            var recaptured = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(recaptured);
            Assert.Equal(
                "Change",
                Assert.Single(Assert.IsType<VbaCurrentHostClassProjectionEntry>(
                    Assert.Single(recaptured!.Classes)).Projection.Events).Name);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_retains_last_known_good_as_advisory_evidence()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-lkg-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = false,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "InvoiceForm", kind = "form" },
                        authority = "lastKnownGood",
                        projection = new
                        {
                            intrinsicEventSourceName = "UserForm",
                            events = Array.Empty<object>()
                        }
                    }
                }
            }))!;

            Assert.True(new VbaHostClassProjectionSnapshotHandler(workspace).TryApply(payload));
            var snapshot = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(snapshot);
            var entry = Assert.IsType<VbaLastKnownGoodHostClassProjectionEntry>(
                Assert.Single(snapshot.Classes));

            Assert.Equal("InvoiceForm", entry.Identity.Name);
            Assert.Equal(VbaHostClassKind.Form, entry.Identity.Kind);
            Assert.Equal("UserForm", entry.Projection.IntrinsicEventSourceName);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_revision_does_not_resurrect_an_old_template_projection()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-template-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var manifestPath = Path.Combine(projectRoot, "vba-project.json");
            var manifestUri = ToFileUri(manifestPath);
            var originalManifest = File.ReadAllText(manifestPath);
            var alternateManifest = originalManifest.Replace(
                "src/Book1/Book1.xlsm",
                "src/Book1/Alternate.xlsm",
                StringComparison.Ordinal);
            var originalTemplate = Path.GetFullPath(
                Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm"));
            var alternateTemplate = Path.GetFullPath(
                Path.Combine(projectRoot, "src", "Book1", "Alternate.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);

            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                originalTemplate,
                revision: 10)));
            Assert.Equal(
                10,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);

            Assert.True(workspace.ManifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                alternateManifest).Accepted);
            Assert.Null(workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot);
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                alternateTemplate,
                revision: 11)));
            Assert.Equal(
                11,
                workspace.CreateProjectSnapshot(projectUri)
                    .SemanticInventory.HostClassProjectionSnapshot?.Revision);

            Assert.True(workspace.ManifestWorkspace.ChangeManifest(
                manifestUri,
                documentVersion: 2,
                originalManifest).Accepted);

            Assert.Null(workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot);
            Assert.False(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                originalTemplate,
                revision: 10)));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_keeps_indeterminate_identity_without_projection()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-indeterminate-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = false,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "Sheet1", kind = "document" },
                        authority = "indeterminate"
                    }
                }
            }))!;

            Assert.True(new VbaHostClassProjectionSnapshotHandler(workspace).TryApply(payload));
            var snapshot = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(snapshot);
            var entry = Assert.IsType<VbaIndeterminateHostClassProjectionEntry>(
                Assert.Single(snapshot.Classes));

            Assert.Equal("Sheet1", entry.Identity.Name);
            Assert.Equal(VbaHostClassKind.Document, entry.Identity.Kind);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_accepts_an_exact_terminal_clear_after_document_removal()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-remove-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var source =
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n";
            var sourceTemplate = Path.GetFullPath(
                Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(projectUri, source);
            _ = workspace.CreateProjectSnapshot(projectUri);
            var handler = new VbaHostClassProjectionSnapshotHandler(workspace);
            Assert.True(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));
            Assert.True(workspace.RemoveDocument(projectUri));
            var cleared = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 2,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate,
                state = "cleared"
            }))!;

            Assert.True(handler.TryApply(cleared));

            workspace.UpdateDocument(projectUri, source);
            Assert.Null(workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot);
            Assert.False(handler.TryApply(CreateEmptyHostSnapshotPayload(
                projectRoot,
                sourceTemplate,
                revision: 1)));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_preserves_type_library_provenance()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-typelib-").FullName;
        try
        {
            const string excelGuid = "00020813-0000-0000-c000-000000000046";
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = true,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "Sheet1", kind = "document" },
                        authority = "current",
                        projection = new
                        {
                            intrinsicEventSourceName = "Worksheet",
                            baseTypeProvenance = new
                            {
                                name = "Worksheet",
                                libraryGuid = excelGuid,
                                majorVersion = 1,
                                minorVersion = 9,
                                lcid = 0
                            },
                            events = new object[]
                            {
                                new
                                {
                                    name = "SelectionChange",
                                    parameters = new object[]
                                    {
                                        new
                                        {
                                            name = "Target",
                                            type = new
                                            {
                                                kind = "typeLib",
                                                name = "Range",
                                                libraryGuid = excelGuid,
                                                majorVersion = 1,
                                                minorVersion = 9,
                                                lcid = 0
                                            },
                                            passing = "byRef",
                                            arrayShape = "array",
                                            optional = true,
                                            paramArray = false
                                        }
                                    },
                                    authoringAvailable = true,
                                    existingHandlerRecognizable = true
                                }
                            }
                        }
                    }
                }
            }))!;

            Assert.True(new VbaHostClassProjectionSnapshotHandler(workspace).TryApply(payload));
            var snapshot = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(snapshot);
            var entry = Assert.IsType<VbaCurrentHostClassProjectionEntry>(
                Assert.Single(snapshot.Classes));
            Assert.NotNull(entry.Projection.BaseTypeProvenance);
            var provenance = entry.Projection.BaseTypeProvenance;
            var parameterType = Assert.IsType<VbaTypeLibraryHostEventParameterType>(
                Assert.Single(Assert.Single(entry.Projection.Events).Parameters).Type);

            Assert.Equal("Worksheet", provenance.Name);
            Assert.Equal(excelGuid, provenance.LibraryGuid);
            Assert.Equal(1, provenance.MajorVersion);
            Assert.Equal(9, provenance.MinorVersion);
            Assert.Equal(0, provenance.Lcid);
            Assert.Equal("Range", parameterType.Name);
            Assert.Equal(excelGuid, parameterType.LibraryGuid);
            Assert.Equal(1, parameterType.MajorVersion);
            Assert.Equal(9, parameterType.MinorVersion);
            Assert.Equal(0, parameterType.Lcid);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Host_class_snapshot_preserves_unresolved_parameter_type_without_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-host-unresolved-").FullName;
        try
        {
            WriteProjectManifest(projectRoot);
            var projectUri = ToFileUri(
                Path.Combine(projectRoot, "src", "Book1", "Worker.bas"));
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
            workspace.UpdateDocument(
                projectUri,
                "Attribute VB_Name = \"ProjectA\"\nPublic Sub RunA()\nEnd Sub\n");
            _ = workspace.CreateProjectSnapshot(projectUri);
            var payload = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                revision = 1,
                project = Path.GetFullPath(projectRoot),
                document = "Book1",
                sourceTemplate = Path.GetFullPath(
                    Path.Combine(projectRoot, "src", "Book1", "Book1.xlsm")),
                state = "present",
                classEnumerationComplete = true,
                classes = new object[]
                {
                    new
                    {
                        identity = new { name = "InvoiceForm", kind = "form" },
                        authority = "current",
                        projection = new
                        {
                            intrinsicEventSourceName = "UserForm",
                            events = new object[]
                            {
                                new
                                {
                                    name = "CustomEvent",
                                    parameters = new object[]
                                    {
                                        new
                                        {
                                            name = "Value",
                                            type = new
                                            {
                                                kind = "unresolved",
                                                displayName = "Vendor.Widget"
                                            },
                                            passing = "byVal",
                                            arrayShape = "scalar",
                                            optional = false,
                                            paramArray = false
                                        }
                                    },
                                    authoringAvailable = false,
                                    existingHandlerRecognizable = true
                                }
                            }
                        }
                    }
                }
            }))!;

            Assert.True(new VbaHostClassProjectionSnapshotHandler(workspace).TryApply(payload));
            var snapshot = workspace.CreateProjectSnapshot(projectUri)
                .SemanticInventory.HostClassProjectionSnapshot;
            Assert.NotNull(snapshot);
            var entry = Assert.IsType<VbaCurrentHostClassProjectionEntry>(
                Assert.Single(snapshot.Classes));
            var parameter = Assert.Single(
                Assert.Single(entry.Projection.Events).Parameters);

            Assert.Equal(
                "Vendor.Widget",
                Assert.IsType<VbaUnresolvedHostEventParameterType>(parameter.Type)
                    .DisplayName);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

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
                new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
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

        var initialDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SemanticInventory
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.RemoveDocument(helperUri);
        var removedDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SemanticInventory
            .ResolveDefinition(callerUri, line: 2, character: "    ".Length);
        workspace.UpdateDocument(renamedHelperUri, helperText);
        var renamedDefinition = workspace
            .CreateProjectSnapshot(callerUri)
            .SemanticInventory
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

    private static JsonNode CreateEmptyHostSnapshotPayload(
        string projectRoot,
        string sourceTemplate,
        long revision)
        => JsonNode.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            revision,
            project = Path.GetFullPath(projectRoot),
            document = "Book1",
            sourceTemplate = Path.GetFullPath(sourceTemplate),
            state = "present",
            classEnumerationComplete = true,
            classes = Array.Empty<object>()
        }))!;

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
