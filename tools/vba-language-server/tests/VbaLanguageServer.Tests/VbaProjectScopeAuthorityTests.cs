using VbaLanguageServer;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectScopeAuthorityTests
{
    [Fact]
    public void Warm_project_scope_reuses_one_snapshot_from_another_module_without_disk_or_rebuild_work()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-warm-scope-authority-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteManifest(projectRoot, "src/Book1");
            var firstPath = Path.Combine(sourceRoot, "First.bas");
            var secondPath = Path.Combine(sourceRoot, "Second.bas");
            File.WriteAllText(
                firstPath,
                "Attribute VB_Name = \"First\"\n"
                + "Public Sub RunFirst()\n"
                + "End Sub\n");
            File.WriteAllText(
                secondPath,
                "Attribute VB_Name = \"Second\"\n"
                + "Public Sub RunSecond()\n"
                + "End Sub\n");
            var fileSystem = new CountingProjectFileSystem(
                SystemVbaProjectFileSystem.Instance);
            var lifecycleObserver = new CountingLifecycleObserver();
            var buildObserver = new CountingSnapshotBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                lifecycleObserver,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver,
                fileSystem);
            var firstUri = new Uri(firstPath).AbsoluteUri;
            var secondUri = new Uri(secondPath).AbsoluteUri;

            var first = workspace.CreateProjectSnapshot(firstUri);
            var coldCounts = ReadCounts(
                fileSystem,
                lifecycleObserver,
                buildObserver);
            var second = workspace.CreateProjectSnapshot(secondUri);

            Assert.Same(first, second);
            Assert.Equal(
                coldCounts,
                ReadCounts(fileSystem, lifecycleObserver, buildObserver));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Warm_workspace_capture_assigns_an_inner_module_only_to_its_nearest_project_scope()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-nearest-scope-authority-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "src");
            WriteManifest(innerProjectRoot, "src/Inner");
            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            const string innerText =
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n";
            File.WriteAllText(
                outerPath,
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n");
            File.WriteAllText(innerPath, innerText);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var outerUri = new Uri(outerPath).AbsoluteUri;
            var innerUri = new Uri(innerPath).AbsoluteUri;
            workspace.OpenDocument(innerUri, version: 1, innerText);

            var inner = workspace.CreateProjectSnapshot(innerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            var snapshots = workspace.CreateProjectSnapshots();

            Assert.Same(inner, Assert.Single(snapshots));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Outer_scope_does_not_claim_an_unseen_source_owned_by_a_nested_project()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-outer-first-scope-authority-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "src");
            WriteManifest(innerProjectRoot, "src/Inner");
            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            File.WriteAllText(
                outerPath,
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n");
            File.WriteAllText(
                innerPath,
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n");
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var outerUri = new Uri(outerPath).AbsoluteUri;
            var innerUri = new Uri(innerPath).AbsoluteUri;

            var outer = workspace.CreateProjectSnapshot(outerUri);
            var inner = workspace.CreateProjectSnapshot(innerUri);

            Assert.NotSame(outer, inner);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    innerProjectRoot,
                    "vba-project.json")),
                inner.Resolution.ManifestPath);
            Assert.Contains(
                inner.SemanticInventory.GetDocumentDefinitions(innerUri),
                definition => definition.Name == "RunInner");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Invalidated_nested_authority_survives_an_outer_scope_store()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-stable-nested-authority-").FullName;
        try
        {
            var outerSourceRoot = Path.Combine(projectRoot, "src");
            var innerProjectRoot = Path.Combine(
                outerSourceRoot,
                "NestedProject");
            var innerSourceRoot = Path.Combine(
                innerProjectRoot,
                "src",
                "Inner");
            Directory.CreateDirectory(innerSourceRoot);
            WriteManifest(projectRoot, "src");
            WriteManifest(innerProjectRoot, "src/Inner");
            var outerPath = Path.Combine(outerSourceRoot, "Outer.bas");
            var innerPath = Path.Combine(innerSourceRoot, "Inner.bas");
            const string outerText =
                "Attribute VB_Name = \"Outer\"\n"
                + "Public Sub RunOuter()\n"
                + "End Sub\n";
            const string innerText =
                "Attribute VB_Name = \"Inner\"\n"
                + "Public Sub RunInner()\n"
                + "End Sub\n";
            File.WriteAllText(outerPath, outerText);
            File.WriteAllText(innerPath, innerText);
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()));
            var outerUri = new Uri(outerPath).AbsoluteUri;
            var innerUri = new Uri(innerPath).AbsoluteUri;
            _ = workspace.CreateProjectSnapshot(innerUri);
            _ = workspace.CreateProjectSnapshot(outerUri);
            workspace.UpdateDocument(
                innerUri,
                innerText.Replace("RunInner", "RunInnerAfterEdit"));
            workspace.UpdateDocument(
                outerUri,
                outerText.Replace("RunOuter", "RunOuterAfterEdit"));

            _ = workspace.CreateProjectSnapshot(outerUri);
            var rebuiltInner = workspace.CreateProjectSnapshot(innerUri);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    innerProjectRoot,
                    "vba-project.json")),
                rebuiltInner.Resolution.ManifestPath);
            Assert.Contains(
                rebuiltInner.SemanticInventory.GetDocumentDefinitions(innerUri),
                definition => definition.Name == "RunInnerAfterEdit");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Source_invalidation_rebuilds_the_snapshot_without_forgetting_its_project_authority()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-invalidated-scope-authority-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteManifest(projectRoot, "src/Book1");
            var firstPath = Path.Combine(sourceRoot, "First.bas");
            var secondPath = Path.Combine(sourceRoot, "Second.bas");
            File.WriteAllText(
                firstPath,
                "Attribute VB_Name = \"First\"\n"
                + "Public Sub RunFirst()\n"
                + "End Sub\n");
            File.WriteAllText(
                secondPath,
                "Attribute VB_Name = \"Second\"\n"
                + "Public Sub BeforeEdit()\n"
                + "End Sub\n");
            var lifecycleObserver = new CountingLifecycleObserver();
            var buildObserver = new CountingSnapshotBuildObserver();
            var workspace = new VbaLanguageWorkspace(
                new VbaProjectReferenceCatalogCache(
                    VbaProjectReferenceCatalogSet.CreateBundled()),
                lifecycleObserver,
                NullVbaDocumentAnalysisBuildObserver.Instance,
                buildObserver);
            var firstUri = new Uri(firstPath).AbsoluteUri;
            var secondUri = new Uri(secondPath).AbsoluteUri;

            var first = workspace.CreateProjectSnapshot(firstUri);
            var manifestResolveCount = lifecycleObserver.ManifestResolveCount;
            workspace.OpenDocument(
                secondUri,
                version: 1,
                "Attribute VB_Name = \"Second\"\n"
                + "Public Sub AfterEdit()\n"
                + "End Sub\n");
            var second = workspace.CreateProjectSnapshot(secondUri);

            Assert.NotSame(first, second);
            Assert.Equal(
                manifestResolveCount,
                lifecycleObserver.ManifestResolveCount);
            Assert.Equal(2, buildObserver.ProjectSnapshotBuildCount);
            Assert.Contains(
                second.SemanticInventory.GetDocumentDefinitions(secondUri),
                definition => definition.Name == "AfterEdit");
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Manifest_resolution_retries_when_revision_changes_after_resolution()
    {
        const string activeUri = "file:///atomic-manifest-capture/Module.bas";
        var initial = new VbaProjectResolution(
            VbaProjectResolutionKind.ManifestDocument,
            "",
            "/atomic-manifest-capture/vba-project.json",
            "Book1",
            "initial",
            []);
        var current = initial with { DocumentKind = "current" };
        var manifestSource = new RevisionRaceManifestResolutionSource(
            initial,
            current);
        var provider = new VbaProjectSnapshotProvider(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            new VbaFileSystemProjectDiskInventory(),
            new VbaProjectSourceDocumentCache(),
            manifestSource);

        var snapshot = provider.CreateProjectSnapshot(
            activeUri,
            new VbaWorkspaceSnapshotState(
                new Dictionary<string, VbaTrackedDocument>(
                    StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Version: 0),
            CancellationToken.None);

        Assert.Equal("current", snapshot.Resolution.DocumentKind);
        Assert.True(manifestSource.ResolveCount >= 2);
    }

    [Fact]
    public async Task Deleted_manifest_does_not_regain_last_known_good_from_an_older_disk_read()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-deleted-read-race-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteManifest(projectRoot, "src/Book1");
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var activeUri = new Uri(Path.Combine(
                sourceRoot,
                "Module.bas")).AbsoluteUri;
            var fileSystem = new BlockingManifestReadFileSystem(
                SystemVbaProjectFileSystem.Instance);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace(fileSystem);

            var staleCapture = Task.Run(
                () => manifestWorkspace.CaptureResolution(activeUri));
            await fileSystem.ReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(
                manifestWorkspace.DeleteManifest(manifestUri));
            fileSystem.ReleaseRead();

            var deletedCapture = await staleCapture
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                deletedCapture.Resolution.Kind);
            Assert.Equal(
                0,
                manifestWorkspace.RetainedLastKnownGoodCount);

            File.WriteAllText(manifestPath, "{\"schemaVersion\":");
            Assert.True(
                manifestWorkspace.ReloadManifest(manifestUri));
            Assert.False(
                manifestWorkspace.TryGetEffectiveManifest(
                    manifestUri,
                    out _,
                    out _,
                    out var validationError));
            Assert.NotNull(validationError);

            Assert.Equal(
                VbaProjectResolutionKind.AdHoc,
                manifestWorkspace
                    .CaptureResolution(activeUri)
                    .Resolution
                    .Kind);
            Assert.Equal(
                0,
                manifestWorkspace.RetainedLastKnownGoodCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Retired_manifest_history_is_not_reintroduced_by_an_in_flight_resolution()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-retirement-read-race-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteManifest(projectRoot, "src/Book1");
            var activeUri = new Uri(Path.Combine(
                sourceRoot,
                "Module.bas")).AbsoluteUri;
            var fileSystem = new BlockingManifestReadFileSystem(
                SystemVbaProjectFileSystem.Instance,
                blockedReadNumber: 2);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace(fileSystem);
            _ = manifestWorkspace.CaptureResolution(activeUri);
            Assert.Equal(
                1,
                manifestWorkspace.RetainedLastKnownGoodCount);

            var staleCapture = Task.Run(
                () => manifestWorkspace.CaptureResolution(activeUri));
            await fileSystem.ReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            manifestWorkspace.RetireInactiveState([], []);
            Assert.Equal(
                0,
                manifestWorkspace.RetainedLastKnownGoodCount);
            fileSystem.ReleaseRead();

            var retiredCapture = await staleCapture
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                retiredCapture.Resolution.Kind);
            Assert.Equal(
                1,
                manifestWorkspace.RetainedLastKnownGoodCount);

            _ = manifestWorkspace.CaptureResolution(activeUri);
            Assert.Equal(
                1,
                manifestWorkspace.RetainedLastKnownGoodCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Observed_overlay_delete_is_not_undone_by_an_older_disk_fallback_read()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-overlay-delete-read-race-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            WriteManifest(projectRoot, "src/Book1");
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var fileSystem = new BlockingManifestReadFileSystem(
                SystemVbaProjectFileSystem.Instance,
                blockedReadNumber: 2);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace(fileSystem);
            _ = manifestWorkspace.CaptureResolution(
                new Uri(Path.Combine(
                    sourceRoot,
                    "Module.bas")).AbsoluteUri);
            var opened = manifestWorkspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                File.ReadAllText(manifestPath));
            Assert.True(opened.Accepted);
            var capturedRevision =
                manifestWorkspace.GetReconciliationRevision(
                    manifestUri);

            var staleOpen = Task.Run(
                () => manifestWorkspace.OpenManifest(
                    manifestUri,
                    documentVersion: 2,
                    "{\"schemaVersion\":"));
            await fileSystem.ReadStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            var deletion =
                manifestWorkspace.DeleteReconciledManifest(
                    manifestUri,
                    capturedRevision);
            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Observed,
                deletion.Status);
            fileSystem.ReleaseRead();

            var invalidOverlay = await staleOpen
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(invalidOverlay.Accepted);
            Assert.NotNull(invalidOverlay.Error);
            Assert.False(
                manifestWorkspace
                    .GetReconciliationBaseline(manifestUri)
                    .Exists);
            Assert.Equal(
                0,
                manifestWorkspace.RetainedLastKnownGoodCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Compound_manifest_replacement_rejects_all_changes_when_any_revision_is_stale()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-compound-stale-").FullName;
        try
        {
            var innerRoot = Path.Combine(projectRoot, "Inner");
            Directory.CreateDirectory(innerRoot);
            WriteManifest(projectRoot, "Inner");
            WriteManifest(innerRoot, "src");
            var outerManifestUri = new Uri(Path.Combine(
                projectRoot,
                "vba-project.json")).AbsoluteUri;
            var innerManifestUri = new Uri(Path.Combine(
                innerRoot,
                "vba-project.json")).AbsoluteUri;
            var manifestWorkspace =
                new VbaProjectManifestWorkspace();
            Assert.True(
                manifestWorkspace.TryGetEffectiveManifest(
                    outerManifestUri,
                    out _,
                    out _,
                    out _));
            Assert.True(
                manifestWorkspace.TryGetEffectiveManifest(
                    innerManifestUri,
                    out _,
                    out _,
                    out _));
            var capturedInnerRevision =
                manifestWorkspace.GetReconciliationRevision(
                    innerManifestUri);
            var capturedOuterRevision =
                manifestWorkspace.GetReconciliationRevision(
                    outerManifestUri);
            Assert.True(
                manifestWorkspace.ReloadManifest(
                    outerManifestUri));

            var replacement =
                manifestWorkspace
                    .ReplaceDeletedReconciledManifestAuthority(
                    [
                        new(
                            innerManifestUri,
                            capturedInnerRevision),
                        new(
                            outerManifestUri,
                            capturedOuterRevision)
                    ],
                    reloadedManifest: null,
                    reloadedText: null);

            Assert.False(replacement.Accepted);
            Assert.True(
                manifestWorkspace
                    .GetReconciliationBaseline(innerManifestUri)
                    .Exists);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Fact]
    public void Capture_resolution_seeds_with_the_reconciliation_revision_not_the_effective_revision()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-revision-map-").FullName;
        var otherRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-revision-map-other-").FullName;
        try
        {
            WriteManifest(projectRoot, "src");
            WriteManifest(otherRoot, "src");
            var manifestUri = new Uri(Path.Combine(
                projectRoot,
                "vba-project.json")).AbsoluteUri;
            var otherManifestPath = Path.Combine(
                otherRoot,
                "vba-project.json");
            var otherManifestUri =
                new Uri(otherManifestPath).AbsoluteUri;
            var activeUri = new Uri(Path.Combine(
                projectRoot,
                "src",
                "Module.bas")).AbsoluteUri;
            var manifestWorkspace =
                new VbaProjectManifestWorkspace();
            _ = manifestWorkspace.CaptureResolution(activeUri);
            var otherOverlay = manifestWorkspace.OpenManifest(
                otherManifestUri,
                documentVersion: 1,
                File.ReadAllText(otherManifestPath));
            Assert.True(otherOverlay.Accepted);
            Assert.True(
                manifestWorkspace.CloseManifest(
                    otherManifestUri));
            Assert.True(
                manifestWorkspace.ReloadManifest(manifestUri));

            Assert.Equal(
                VbaProjectResolutionKind.ManifestDocument,
                manifestWorkspace.CaptureResolution(activeUri)
                    .Resolution
                    .Kind);

            Assert.True(
                manifestWorkspace
                    .GetReconciliationBaseline(manifestUri)
                    .Exists);
            Assert.Equal(
                1,
                manifestWorkspace.RetainedLastKnownGoodCount);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
            Directory.Delete(otherRoot, recursive: true);
        }
    }

    [Fact]
    public void Newer_direct_disk_seed_rejects_an_older_reconciliation_result()
    {
        var projectRoot = Directory.CreateTempSubdirectory(
            "vba-ls-manifest-seed-revision-").FullName;
        try
        {
            WriteManifest(projectRoot, "src/Old");
            var manifestPath = Path.Combine(
                projectRoot,
                "vba-project.json");
            var manifestUri = new Uri(manifestPath).AbsoluteUri;
            var staleText = File.ReadAllText(manifestPath);
            var manifestWorkspace =
                new VbaProjectManifestWorkspace();
            var capturedRevision =
                manifestWorkspace.GetReconciliationRevision(
                    manifestUri);
            WriteManifest(projectRoot, "src/New");
            var activeUri = new Uri(Path.Combine(
                projectRoot,
                "src",
                "New",
                "Module.bas")).AbsoluteUri;
            var current = manifestWorkspace
                .CaptureResolution(activeUri)
                .Resolution;

            var staleUpdate =
                manifestWorkspace.ReloadReconciledManifest(
                    manifestUri,
                    staleText,
                    capturedRevision);

            Assert.Equal(
                VbaProjectManifestReconciliationStatus.Rejected,
                staleUpdate.Status);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(
                    projectRoot,
                    "src",
                    "New")),
                current.RootPath);
            Assert.NotEqual(
                staleText,
                manifestWorkspace
                    .GetReconciliationBaseline(manifestUri)
                    .Text);
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static void WriteManifest(
        string projectRoot,
        string sourcePath)
        => File.WriteAllText(
            Path.Combine(projectRoot, "vba-project.json"),
            $$"""
            {
              "schemaVersion": 1,
              "projectName": "ScopeAuthority",
              "primaryDocument": "Book1",
              "documents": {
                "Book1": {
                  "kind": "excel",
                  "sourcePath": "{{sourcePath}}",
                  "templatePath": "Book1.xlsm",
                  "binPath": "bin/Book1.xlsm",
                  "publishPath": "publish/Book1.xlsm",
                  "references": []
                }
              }
            }
            """);

    private static CaptureCounts ReadCounts(
        CountingProjectFileSystem fileSystem,
        CountingLifecycleObserver lifecycleObserver,
        CountingSnapshotBuildObserver buildObserver)
        => new(
            fileSystem.OperationCount,
            lifecycleObserver.ManifestResolveCount,
            buildObserver.ProjectSnapshotBuildCount,
            buildObserver.SemanticInventoryBuildCount);

    private sealed class CountingProjectFileSystem(
        IVbaProjectFileSystem inner)
        : IVbaProjectFileSystem
    {
        public int OperationCount { get; private set; }

        public bool FileExists(string path)
        {
            OperationCount++;
            return inner.FileExists(path);
        }

        public bool DirectoryExists(string path)
        {
            OperationCount++;
            return inner.DirectoryExists(path);
        }

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            OperationCount++;
            return inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            OperationCount++;
            return inner.TryGetSourceMetadata(path, out metadata);
        }

        public string ReadManifestText(string path)
        {
            OperationCount++;
            return inner.ReadManifestText(path);
        }

        public byte[] ReadSourceBytes(string path)
        {
            OperationCount++;
            return inner.ReadSourceBytes(path);
        }
    }

    private sealed class BlockingManifestReadFileSystem(
        IVbaProjectFileSystem inner,
        int blockedReadNumber = 1)
        : IVbaProjectFileSystem
    {
        private readonly TaskCompletionSource releaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int blocked;
        private int readCount;

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseRead()
            => releaseRead.TrySetResult();

        public bool FileExists(string path)
            => inner.FileExists(path);

        public bool DirectoryExists(string path)
            => inner.DirectoryExists(path);

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => inner.EnumerateSourceFiles(
                rootPath,
                searchPattern,
                searchOption);

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
            => inner.TryGetSourceMetadata(path, out metadata);

        public string ReadManifestText(string path)
        {
            var text = inner.ReadManifestText(path);
            if (Interlocked.Increment(ref readCount)
                    == blockedReadNumber
                && Interlocked.CompareExchange(
                    ref blocked,
                    1,
                    0) == 0)
            {
                ReadStarted.TrySetResult();
                releaseRead.Task.GetAwaiter().GetResult();
            }

            return text;
        }

        public byte[] ReadSourceBytes(string path)
            => inner.ReadSourceBytes(path);
    }

    private sealed class CountingLifecycleObserver
        : IVbaProjectReferenceCatalogLifecycleObserver
    {
        public int ManifestResolveCount { get; private set; }

        public void Record(
            VbaProjectReferenceCatalogLifecycleEvent lifecycleEvent)
        {
            if (lifecycleEvent.Operation
                == VbaProjectReferenceCatalogLifecycleOperation
                    .ProjectSnapshotManifestResolve)
            {
                ManifestResolveCount++;
            }
        }
    }

    private sealed class CountingSnapshotBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        public int ProjectSnapshotBuildCount { get; private set; }

        public int SemanticInventoryBuildCount { get; private set; }

        public void BeforeBuildProjectSnapshot(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectSnapshotBuildCount++;
        }

        public void BeforeBuildSemanticInventory(
            string activeUri,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticInventoryBuildCount++;
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
            => cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class RevisionRaceManifestResolutionSource(
        VbaProjectResolution initial,
        VbaProjectResolution current)
        : IVbaProjectManifestResolutionSource
    {
        private long version;
        private int revisionCaptureCount;
        private int resolveCount;

        public long Version => Volatile.Read(ref version);

        public int ResolveCount => Volatile.Read(ref resolveCount);

        public long GetRevision(string authorityUri)
        {
            if (Interlocked.Increment(ref revisionCaptureCount) == 1)
            {
                Interlocked.Exchange(ref version, 1);
            }

            return Volatile.Read(ref version);
        }

        public VbaProjectResolution Resolve(string activeUri)
        {
            Interlocked.Increment(ref resolveCount);
            return Volatile.Read(ref version) == 0
                ? initial
                : current;
        }
    }

    private sealed record CaptureCounts(
        int FileSystemOperations,
        int ManifestResolves,
        int ProjectSnapshotBuilds,
        int SemanticInventoryBuilds);
}
