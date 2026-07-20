using VbaLanguageServer;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectSnapshotHotPathGateTests
{
    [Fact]
    public async Task Semantic_inventory_build_does_not_hold_the_workspace_state_lock()
    {
        const string uri = "file:///C:/work/SemanticBuild.bas";
        const string source =
            "Attribute VB_Name = \"SemanticBuild\"\n"
            + "Public Sub Run()\n"
            + "End Sub\n";
        var buildObserver = new BlockingSemanticInventoryBuildObserver();
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(
                VbaProjectReferenceCatalogSet.CreateBundled()),
            NullVbaProjectReferenceCatalogLifecycleObserver.Instance,
            NullVbaDocumentAnalysisBuildObserver.Instance,
            buildObserver);
        workspace.UpdateDocument(uri, source);

        var snapshotBuild = Task.Run(
            () => workspace.CreateProjectSnapshot(uri));
        try
        {
            await buildObserver.SemanticInventoryBuildStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));

            var concurrentRead = Task.Run(
                () => workspace.GetDocumentText(uri));
            var capturedText = await concurrentRead
                .WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(source, capturedText);
        }
        finally
        {
            buildObserver.Release();
        }

        var snapshot = await snapshotBuild.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Contains(
            snapshot.SemanticInventory.GetDocumentDefinitions(uri),
            definition => definition.Name == "Run");
    }

    [Fact]
    public void Warm_interactive_project_captures_reuse_committed_inputs_without_file_io_or_rebuilds()
    {
        var projectRoot = Directory.CreateTempSubdirectory("vba-ls-hot-path-").FullName;
        try
        {
            var sourceRoot = Path.Combine(projectRoot, "src", "Book1");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(
                Path.Combine(projectRoot, "vba-project.json"),
                """
                {
                  "schemaVersion": 1,
                  "projectName": "HotPath",
                  "primaryDocument": "Book1",
                  "documents": {
                    "Book1": {
                      "kind": "excel",
                      "sourcePath": "src/Book1",
                      "templatePath": "src/Book1/Book1.xlsm",
                      "binPath": "bin/Book1/Book1.xlsm",
                      "publishPath": "publish/Book1/Book1.xlsm",
                      "references": []
                    }
                  }
                }
                """);
            var activePath = Path.Combine(sourceRoot, "Main.bas");
            const string activeText =
                "Attribute VB_Name = \"Main\"\n"
                + "Public Sub Run()\n"
                + "End Sub\n";
            File.WriteAllText(activePath, activeText);
            File.WriteAllText(
                Path.Combine(sourceRoot, "Helper.bas"),
                "Attribute VB_Name = \"Helper\"\n"
                + "Public Function BuildValue() As String\n"
                + "End Function\n");

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
            var activeUri = new Uri(activePath).AbsoluteUri;
            workspace.OpenDocument(activeUri, version: 1, activeText);

            var cold = workspace.CreateProjectSnapshot(activeUri);

            Assert.NotEmpty(cold.SourceDocuments);
            Assert.True(fileSystem.FileExistsCount > 0);
            Assert.True(fileSystem.DirectoryExistsCount > 0);
            Assert.True(fileSystem.SourceEnumerationCount > 0);
            Assert.True(fileSystem.SourceMetadataCount > 0);
            Assert.True(fileSystem.ManifestReadCount > 0);
            Assert.True(fileSystem.SourceReadCount > 0);
            Assert.True(lifecycleObserver.ManifestResolveCount > 0);
            Assert.True(buildObserver.ProjectSnapshotBuildCount > 0);
            Assert.True(buildObserver.SemanticInventoryBuildCount > 0);
            var coldCounts = CaptureCounts(
                fileSystem,
                lifecycleObserver,
                buildObserver);

            var firstWarm = workspace.CreateProjectSnapshot(activeUri);
            var secondWarm = workspace.CreateProjectSnapshot(activeUri);
            var thirdWarm = workspace.CreateProjectSnapshot(activeUri);
            var workspaceWarm = workspace.CreateProjectSnapshots();

            Assert.Same(cold, firstWarm);
            Assert.Same(cold, secondWarm);
            Assert.Same(cold, thirdWarm);
            Assert.Same(cold, Assert.Single(workspaceWarm));
            Assert.Equal(
                coldCounts,
                CaptureCounts(fileSystem, lifecycleObserver, buildObserver));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static InteractiveCaptureCounts CaptureCounts(
        CountingProjectFileSystem fileSystem,
        CountingLifecycleObserver lifecycleObserver,
        CountingSnapshotBuildObserver buildObserver)
        => new(
            fileSystem.FileExistsCount,
            fileSystem.DirectoryExistsCount,
            fileSystem.SourceEnumerationCount,
            fileSystem.SourceMetadataCount,
            fileSystem.ManifestReadCount,
            fileSystem.SourceReadCount,
            lifecycleObserver.ManifestResolveCount,
            buildObserver.ProjectSnapshotBuildCount,
            buildObserver.SemanticInventoryBuildCount);

    private sealed class CountingProjectFileSystem(
        IVbaProjectFileSystem inner)
        : IVbaProjectFileSystem
    {
        public int FileExistsCount { get; private set; }

        public int DirectoryExistsCount { get; private set; }

        public int SourceEnumerationCount { get; private set; }

        public int SourceMetadataCount { get; private set; }

        public int ManifestReadCount { get; private set; }

        public int SourceReadCount { get; private set; }

        public bool FileExists(string path)
        {
            FileExistsCount++;
            return inner.FileExists(path);
        }

        public bool DirectoryExists(string path)
        {
            DirectoryExistsCount++;
            return inner.DirectoryExists(path);
        }

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
        {
            SourceEnumerationCount++;
            return inner.EnumerateSourceFiles(rootPath, searchPattern, searchOption);
        }

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            SourceMetadataCount++;
            return inner.TryGetSourceMetadata(path, out metadata);
        }

        public string ReadManifestText(string path)
        {
            ManifestReadCount++;
            return inner.ReadManifestText(path);
        }

        public byte[] ReadSourceBytes(string path)
        {
            SourceReadCount++;
            return inner.ReadSourceBytes(path);
        }
    }

    private sealed class CountingLifecycleObserver
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
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class BlockingSemanticInventoryBuildObserver
        : IVbaProjectSnapshotBuildObserver
    {
        private readonly ManualResetEventSlim release = new();

        public TaskCompletionSource SemanticInventoryBuildStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void BeforeBuildSemanticInventory(
            string activeUri,
            CancellationToken cancellationToken)
        {
            SemanticInventoryBuildStarted.TrySetResult();
            release.Wait(cancellationToken);
        }

        public void BeforeStore(
            long workspaceVersion,
            CancellationToken cancellationToken)
            => cancellationToken.ThrowIfCancellationRequested();

        public void Release()
            => release.Set();
    }

    private sealed record InteractiveCaptureCounts(
        int FileExists,
        int DirectoryExists,
        int SourceEnumeration,
        int SourceMetadata,
        int ManifestRead,
        int SourceRead,
        int ManifestResolve,
        int ProjectSnapshotBuild,
        int SemanticInventoryBuild);
}
