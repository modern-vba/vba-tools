using VbaDev.App.Build;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Domain;
using VbaDev.Infrastructure.FileSystem;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class SnapshotTestExecutionWorkspaceTests
{
    [Fact]
    public void CleanupPreservesForeignWorkspaceContents()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        using var workspace = fixture.Create();
        var foreign = Path.Combine(workspace.WorkspacePath, "foreign.txt");
        File.WriteAllText(foreign, "foreign content");

        var result = workspace.Cleanup();

        Assert.False(result.Deleted);
        Assert.Equal("foreign content", File.ReadAllText(foreign));
        Assert.Contains(workspace.WorkspacePath, result.Warning);
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspacePath, "source")));
    }

    [Fact]
    public void CleanupRemovesOwnedWorkbookAndUnconsumedCaptureButPreservesCallerAndSiblings()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        using var workspace = fixture.Create();
        var sibling = Path.Combine(fixture.ScratchRoot, "sibling.txt");
        File.WriteAllText(sibling, "keep");
        File.WriteAllText(workspace.WorkbookPath, "completed build");
        workspace.RegisterCommittedWorkbook(workspace.WorkbookPath);

        var result = workspace.Cleanup();

        Assert.True(result.Deleted);
        Assert.Null(result.Warning);
        Assert.Equal(InvocationScratchCleanupStatus.Removed, result.Evidence.Status);
        Assert.False(Directory.Exists(workspace.WorkspacePath));
        Assert.Equal("keep", File.ReadAllText(sibling));
        Assert.True(File.Exists(Path.Combine(fixture.Snapshot, "Module1.bas")));
        Assert.Same(result, workspace.Cleanup());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupPreservesChangedOrReplacedRegisteredWorkbook(bool replaced)
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        File.WriteAllText(workspace.WorkbookPath, "completed build");
        workspace.RegisterCommittedWorkbook(workspace.WorkbookPath);
        if (replaced) File.Move(workspace.WorkbookPath, Path.Combine(temp.Path, "original.xlsm"));
        File.WriteAllText(workspace.WorkbookPath, "external workbook");

        var result = workspace.Cleanup();

        Assert.False(result.Deleted);
        Assert.Contains(workspace.WorkbookPath, result.Evidence.RetainedPaths);
        Assert.Equal("external workbook", File.ReadAllText(workspace.WorkbookPath));
    }

    [Fact]
    public void CleanupCannotAdoptAnUnregisteredWorkbook()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        File.WriteAllText(workspace.WorkbookPath, "unobserved content");
        var result = workspace.Cleanup();
        Assert.False(result.Deleted);
        Assert.Equal("unobserved content", File.ReadAllText(workspace.WorkbookPath));
    }

    [Fact]
    public void CleanupCombinesNestedSourceEvidenceWithoutDeletingItsChangedContent()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var capture = workspace.TakeSourceCapture();
        var source = Assert.Single(capture.SourceFiles).SourcePath;
        File.WriteAllText(source, "external source");
        Assert.Throws<InvalidOperationException>(capture.Dispose);

        var result = workspace.Cleanup();

        Assert.False(result.Deleted);
        Assert.Contains(source, result.Evidence.RetainedPaths);
        Assert.Contains(workspace.WorkspacePath, result.Evidence.RetainedPaths);
        Assert.Equal("external source", File.ReadAllText(source));
    }

    [Fact]
    public async Task LockedWorkbookProducesBoundedStableInconclusiveEvidence()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        File.WriteAllText(workspace.WorkbookPath, "completed build");
        workspace.RegisterCommittedWorkbook(workspace.WorkbookPath);
        SnapshotTestWorkspaceCleanupResult result;
        using (File.Open(workspace.WorkbookPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await Task.Run(workspace.Cleanup).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(InvocationScratchCleanupStatus.Inconclusive, result.Evidence.Status);
        Assert.Contains(workspace.WorkbookPath, result.Evidence.InconclusivePaths);
        Assert.Same(result, workspace.Cleanup());
        Assert.True(File.Exists(workspace.WorkbookPath));
    }

    [Fact]
    public void MissingRegisteredWorkbookCountsAsRemoved()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        File.WriteAllText(workspace.WorkbookPath, "completed build");
        workspace.RegisterCommittedWorkbook(workspace.WorkbookPath);
        File.Delete(workspace.WorkbookPath);
        Assert.True(workspace.Cleanup().Deleted);
    }

    [Fact]
    public void ForeignDirectoryLinkDoesNotGrantAuthorityOverItsTarget()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var outside = temp.CreateDirectory("outside");
        var foreign = Path.Combine(outside, "foreign.txt");
        File.WriteAllText(foreign, "foreign target");
        var alias = Path.Combine(workspace.WorkspacePath, "foreign-link");
        Directory.CreateSymbolicLink(alias, outside);
        Assert.False(workspace.Cleanup().Deleted);
        Assert.Equal("foreign target", File.ReadAllText(foreign));
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
    }

    [Fact]
    public void WorkspaceRejectsWorkbookOutsideItsOwnedLeaf()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var capture = workspace.TakeSourceCapture();
        Assert.Throws<InvalidOperationException>(() => SnapshotTestExecutionWorkspace.ValidateLayout(
            workspace.WorkspacePath, capture, Path.Combine(temp.Path, "outside.xlsm")));
    }

    [Fact]
    public void FactoryRollbackNeverDisposesAnUntrustedExternalSourceCapture()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        var outside = temp.CreateDirectory("outside");
        using var external = new ExternalSnapshotSourceCaptureFactory(outside);
        var factory = new SnapshotTestExecutionWorkspaceFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            new FileSystemPathIdentityResolver(), fixture.ScratchRoot, sourceCaptureFactory: external);

        var error = Assert.Throws<SnapshotTestWorkspacePreparationException>(() =>
            factory.Create(fixture.Context, fixture.Snapshot, "Book1.xlsm", CancellationToken.None));

        Assert.Contains("source capture", error.Message);
        Assert.Empty(Directory.EnumerateDirectories(fixture.ScratchRoot));
        Assert.True(Directory.Exists(external.Capture!.StagingPath));
    }

    private sealed record Fixture(ResolvedProjectContext Context, string Snapshot, string ScratchRoot)
    {
        internal SnapshotTestExecutionWorkspace Create()
            => new SnapshotTestExecutionWorkspaceFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
                new FileSystemPathIdentityResolver(), ScratchRoot)
                .Create(Context, Snapshot, "Book1.xlsm", CancellationToken.None);
    }

    private static Fixture CreateFixture(TempDirectory temp)
    {
        var snapshot = temp.CreateDirectory("snapshot");
        File.WriteAllText(Path.Combine(snapshot, "Module1.bas"), "Attribute VB_Name = \"Module1\"\n");
        var project = temp.CreateDirectory("Project");
        var store = new JsonProjectManifestStore();
        store.Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var context = new ProjectContextResolver(store).Resolve(new ProjectResolutionRequest(project, null, project));
        return new(context, snapshot, temp.CreateDirectory("scratch"));
    }

    private sealed class ExternalSnapshotSourceCaptureFactory(string outside) : ISnapshotSourceCaptureFactory, IDisposable
    {
        internal BuildSourceSnapshotCapture? Capture { get; private set; }
        public BuildSourceSnapshotCapture Create(string scratchRoot, string sourceSnapshotPath, CancellationToken cancellationToken)
            => Capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), outside)
                .Create(sourceSnapshotPath, cancellationToken);
        public void Dispose() => Capture?.Dispose();
    }
}
