using VbaDev.Infrastructure.FileSystem;
using System.Text;
using VbaDev.App.Build;
using VbaDev.App.Testing;
using Xunit;

namespace VbaDev.Tests;

public sealed class SnapshotTestExecutionWorkspaceTests
{
    [Fact]
    public void CleanupRetriesOnlyTheOwnedWorkspaceLeafAndPreservesSiblings()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("scratch");
        var siblingPath = Path.Combine(scratchRoot, "sibling");
        Directory.CreateDirectory(siblingPath);
        var siblingSentinel = Path.Combine(siblingPath, "sentinel.txt");
        File.WriteAllText(siblingSentinel, "keep", Encoding.UTF8);
        var fileSystem = new RetryingSnapshotWorkspaceFileSystem(failuresBeforeDelete: 2);
        var factory = new SnapshotTestExecutionWorkspaceFactory(
            new FileSystemPathIdentityResolver(),
            scratchRoot,
            fileSystem,
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero);
        var projectRoot = temp.CreateDirectory("Project");
        new VbaDev.Infrastructure.Projects.JsonProjectManifestStore().Save(
            projectRoot,
            VbaDev.Domain.ProjectManifest.CreateDefault(
                "Project",
                "Book1",
                projectRoot,
                null));
        var context = new VbaDev.App.Projects.ProjectContextResolver(
                new VbaDev.Infrastructure.Projects.JsonProjectManifestStore())
            .Resolve(new VbaDev.App.Projects.ProjectResolutionRequest(
                projectRoot,
                null,
                projectRoot));
        var workspace = factory.Create(
            context,
            snapshotPath,
            "Book1.xlsm",
            CancellationToken.None);

        var result = workspace.Cleanup();

        Assert.True(result.Deleted);
        Assert.Null(result.Warning);
        Assert.Equal(3, fileSystem.DeletePaths.Count);
        Assert.All(
            fileSystem.DeletePaths,
            path => Assert.Equal(workspace.WorkspacePath, path));
        Assert.False(Directory.Exists(workspace.WorkspacePath));
        Assert.Equal("keep", File.ReadAllText(siblingSentinel, Encoding.UTF8));
    }

    [Fact]
    public void CleanupDoesNotTreatAnInaccessibleWorkspaceAsDeleted()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("scratch");
        var fileSystem = new InaccessibleSnapshotWorkspaceFileSystem();
        var factory = new SnapshotTestExecutionWorkspaceFactory(
            new FileSystemPathIdentityResolver(),
            scratchRoot,
            fileSystem,
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero);
        var projectRoot = temp.CreateDirectory("Project");
        new VbaDev.Infrastructure.Projects.JsonProjectManifestStore().Save(
            projectRoot,
            VbaDev.Domain.ProjectManifest.CreateDefault(
                "Project",
                "Book1",
                projectRoot,
                null));
        var context = new VbaDev.App.Projects.ProjectContextResolver(
                new VbaDev.Infrastructure.Projects.JsonProjectManifestStore())
            .Resolve(new VbaDev.App.Projects.ProjectResolutionRequest(
                projectRoot,
                null,
                projectRoot));
        var workspace = factory.Create(
            context,
            snapshotPath,
            "Book1.xlsm",
            CancellationToken.None);

        var result = workspace.Cleanup();

        Assert.False(result.Deleted);
        Assert.NotNull(result.Warning);
        Assert.Equal(3, fileSystem.DeletePaths.Count);
        Assert.All(
            fileSystem.DeletePaths,
            path => Assert.Equal(workspace.WorkspacePath, path));
        Assert.Contains(workspace.WorkspacePath, result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(workspace.WorkspacePath));
    }

    [Fact]
    public void WorkspaceRejectsWorkbookOutsideItsOwnedGuidLeaf()
    {
        using var temp = TempDirectory.Create();
        var scratchRoot = temp.CreateDirectory("scratch");
        var workspacePath = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(workspacePath, "source");
        Directory.CreateDirectory(sourcePath);
        using var sourceCapture = new BuildSourceSnapshotCapture(sourcePath, []);
        var outsideWorkbookPath = Path.Combine(temp.Path, "Book1.xlsm");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SnapshotTestExecutionWorkspace(
                scratchRoot,
                workspacePath,
                sourceCapture,
                outsideWorkbookPath,
                new SnapshotTestWorkspaceFileSystem(),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        Assert.Contains("workbook", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(workspacePath, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspaceRejectsSourceCaptureOutsideItsOwnedGuidLeaf()
    {
        using var temp = TempDirectory.Create();
        var scratchRoot = temp.CreateDirectory("scratch");
        var workspacePath = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        var outsideSourcePath = temp.CreateDirectory("outside-source");
        var outsideSentinel = Path.Combine(outsideSourcePath, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "keep", Encoding.UTF8);
        var sourceCapture = new BuildSourceSnapshotCapture(outsideSourcePath, []);
        var workbookPath = Path.Combine(workspacePath, "Book1.xlsm");

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SnapshotTestExecutionWorkspace(
                scratchRoot,
                workspacePath,
                sourceCapture,
                workbookPath,
                new SnapshotTestWorkspaceFileSystem(),
                cleanupAttempts: 3,
                retryDelay: TimeSpan.Zero));

        Assert.Contains("source capture", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(workspacePath, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep", File.ReadAllText(outsideSentinel, Encoding.UTF8));
    }

    [Fact]
    public void FactoryRollbackNeverDisposesAnUntrustedExternalSourceCapture()
    {
        using var temp = TempDirectory.Create();
        var snapshotPath = temp.CreateDirectory("caller-snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Test_Module.bas"),
            "Attribute VB_Name = \"Test_Module\"",
            Encoding.UTF8);
        var scratchRoot = temp.CreateDirectory("scratch");
        var outsideSourcePath = temp.CreateDirectory("outside-source");
        var outsideSentinel = Path.Combine(outsideSourcePath, "sentinel.txt");
        File.WriteAllText(outsideSentinel, "keep", Encoding.UTF8);
        var factory = new SnapshotTestExecutionWorkspaceFactory(
            new FileSystemPathIdentityResolver(),
            scratchRoot,
            new SnapshotTestWorkspaceFileSystem(),
            cleanupAttempts: 3,
            retryDelay: TimeSpan.Zero,
            sourceCaptureFactory: new ExternalSnapshotSourceCaptureFactory(
                outsideSourcePath));
        var projectRoot = temp.CreateDirectory("Project");
        new VbaDev.Infrastructure.Projects.JsonProjectManifestStore().Save(
            projectRoot,
            VbaDev.Domain.ProjectManifest.CreateDefault(
                "Project",
                "Book1",
                projectRoot,
                null));
        var context = new VbaDev.App.Projects.ProjectContextResolver(
                new VbaDev.Infrastructure.Projects.JsonProjectManifestStore())
            .Resolve(new VbaDev.App.Projects.ProjectResolutionRequest(
                projectRoot,
                null,
                projectRoot));

        var error = Assert.Throws<SnapshotTestWorkspacePreparationException>(() =>
            factory.Create(
                context,
                snapshotPath,
                "Book1.xlsm",
                CancellationToken.None));

        Assert.Contains("source capture", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep", File.ReadAllText(outsideSentinel, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateDirectories(scratchRoot));
    }

    private sealed class RetryingSnapshotWorkspaceFileSystem(int failuresBeforeDelete)
        : ISnapshotTestWorkspaceFileSystem
    {
        public List<string> DeletePaths { get; } = [];

        public void DeleteDirectory(string path)
        {
            DeletePaths.Add(path);
            if (DeletePaths.Count <= failuresBeforeDelete)
            {
                throw new IOException("synthetic deletion failure");
            }

            Directory.Delete(path, recursive: true);
        }

        public void Delay(TimeSpan delay)
        {
        }
    }

    private sealed class InaccessibleSnapshotWorkspaceFileSystem
        : ISnapshotTestWorkspaceFileSystem
    {
        public List<string> DeletePaths { get; } = [];

        public void DeleteDirectory(string path)
        {
            DeletePaths.Add(path);
            throw new UnauthorizedAccessException("synthetic inaccessible workspace");
        }

        public void Delay(TimeSpan delay)
        {
        }
    }

    private sealed class ExternalSnapshotSourceCaptureFactory(string outsideSourcePath)
        : ISnapshotSourceCaptureFactory
    {
        public BuildSourceSnapshotCapture Create(
            string scratchRoot,
            string sourceSnapshotPath,
            CancellationToken cancellationToken)
            => new(outsideSourcePath, []);
    }
}
