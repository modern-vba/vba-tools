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

public sealed class SnapshotTestInputTests
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
    public void CleanupRemovesOwnedInputAndUnconsumedCaptureButPreservesCallerAndSiblings()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        using var workspace = fixture.Create();
        var sibling = Path.Combine(fixture.ScratchRoot, "sibling.txt");
        File.WriteAllText(sibling, "keep");

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
    public void CleanupPreservesChangedOrReplacedInput(bool replaced)
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var source = Assert.Single(workspace.TakeSourceCapture().SourceFiles).SourcePath;
        if (replaced) File.Move(source, Path.Combine(temp.Path, "original.bas"));
        File.WriteAllText(source, "external input");

        var result = workspace.Cleanup();

        Assert.False(result.Deleted);
        Assert.Contains(source, result.Evidence.RetainedPaths);
        Assert.Equal("external input", File.ReadAllText(source));
    }

    [Fact]
    public void CleanupCannotAdoptUnregisteredInput()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var source = Path.Combine(workspace.TakeSourceCapture().StagingPath, "Unobserved.bas");
        File.WriteAllText(source, "unobserved content");
        var result = workspace.Cleanup();
        Assert.False(result.Deleted);
        Assert.Equal("unobserved content", File.ReadAllText(source));
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
    public async Task LockedInputProducesBoundedStableInconclusiveEvidence()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var source = Assert.Single(workspace.TakeSourceCapture().SourceFiles).SourcePath;
        SnapshotTestInputCleanupResult result;
        using (File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            result = await Task.Run(workspace.Cleanup).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(InvocationScratchCleanupStatus.Inconclusive, result.Evidence.Status);
        Assert.Contains(source, result.Evidence.InconclusivePaths);
        Assert.Same(result, workspace.Cleanup());
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void MissingOwnedInputCountsAsRemoved()
    {
        using var temp = TempDirectory.Create();
        using var workspace = CreateFixture(temp).Create();
        var source = Assert.Single(workspace.TakeSourceCapture().SourceFiles).SourcePath;
        File.Delete(source);
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
    public void FictionalWorkbookOutputAliasDoesNotRejectInputCapture()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        var resolver = new FormerOutputAliasResolver(fixture.ScratchRoot, fixture.Context.TemplateDocumentPath);
        using var input = new SnapshotTestInputFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            resolver, fixture.ScratchRoot).Create(fixture.Context, fixture.Snapshot, CancellationToken.None);
        Assert.Single(input.TakeSourceCapture().SourceFiles);
        Assert.False(resolver.ResolvedFictionalOutput);
        Assert.Empty(Directory.EnumerateFiles(input.WorkspacePath, "*.xlsm", SearchOption.AllDirectories));
        Assert.True(input.Cleanup().Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalScratchAliasCannotPlaceInputInsideCallerOrPersistentSource(bool callerSnapshot)
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        var target = callerSnapshot ? fixture.Snapshot : fixture.Context.DocumentSourceSetPath;
        Directory.CreateDirectory(target);
        var protectedFile = Path.Combine(target, "Protected.bas");
        File.WriteAllText(protectedFile, "protected source");
        var alias = Path.Combine(temp.Path, "scratch-alias");
        Directory.CreateSymbolicLink(alias, target);
        var factory = new SnapshotTestInputFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            new FileSystemPathIdentityResolver(), alias);
        Assert.Throws<SnapshotTestInputPreparationException>(() =>
            factory.Create(fixture.Context, fixture.Snapshot, CancellationToken.None));
        Assert.Equal("protected source", File.ReadAllText(protectedFile));
        Assert.Empty(Directory.EnumerateDirectories(target));
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
    }

    private sealed class FormerOutputAliasResolver(string scratchRoot, string sourceWorkbook) : IFileSystemPathIdentityResolver
    {
        private readonly FileSystemPathIdentityResolver inner = new();
        internal bool ResolvedFictionalOutput { get; private set; }
        public FileSystemPathIdentity Resolve(string path)
        {
            if (path.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path) == "Book1.xlsm")
            {
                ResolvedFictionalOutput = true;
                return inner.Resolve(sourceWorkbook);
            }
            return inner.Resolve(path);
        }
    }

    [Fact]
    public void CaptureWithExpectedSpellingCannotAdoptACanonicalOutsideTarget()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        var outside = temp.CreateDirectory("outside");
        var protectedFile = Path.Combine(outside, "protected.txt");
        File.WriteAllText(protectedFile, "foreign content");
        using var captures = new RecordingCaptureFactory();
        var factory = new SnapshotTestInputFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            new CaptureAliasResolver(outside), fixture.ScratchRoot, sourceCaptureFactory: captures);
        var error = Assert.Throws<SnapshotTestInputPreparationException>(() =>
            factory.Create(fixture.Context, fixture.Snapshot, CancellationToken.None));
        Assert.Contains("canonical input container", error.Message);
        Assert.True(File.Exists(Path.Combine(captures.Capture!.StagingPath, "Module1.bas")));
        Assert.Equal("foreign content", File.ReadAllText(protectedFile));
    }

    private sealed class CaptureAliasResolver(string outside) : IFileSystemPathIdentityResolver
    {
        private readonly FileSystemPathIdentityResolver inner = new();
        public FileSystemPathIdentity Resolve(string path)
        {
            var identity = inner.Resolve(path);
            return Path.GetFileName(Path.GetDirectoryName(path)) == "source"
                && Guid.TryParseExact(Path.GetFileName(path), "N", out _)
                    ? new(Path.Combine(outside, Path.GetFileName(path)), identity.OperationPath, null)
                    : identity;
        }
    }

    private sealed class RecordingCaptureFactory : ISnapshotSourceCaptureFactory, IDisposable
    {
        internal BuildSourceSnapshotCapture? Capture { get; private set; }
        public BuildSourceSnapshotCapture Create(string scratchRoot, string sourceSnapshotPath, CancellationToken token)
            => Capture = new BuildSourceSnapshotCaptureFactory(new WindowsExactFileSystemObjectOwnershipFactory(), scratchRoot)
                .Create(sourceSnapshotPath, token);
        public void Dispose() => Capture?.Dispose();
    }
    [Fact]
    public void FactoryRollbackNeverDisposesAnUntrustedExternalSourceCapture()
    {
        using var temp = TempDirectory.Create();
        var fixture = CreateFixture(temp);
        var outside = temp.CreateDirectory("outside");
        using var external = new ExternalSnapshotSourceCaptureFactory(outside);
        var factory = new SnapshotTestInputFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
            new FileSystemPathIdentityResolver(), fixture.ScratchRoot, sourceCaptureFactory: external);

        var error = Assert.Throws<SnapshotTestInputPreparationException>(() =>
            factory.Create(fixture.Context, fixture.Snapshot, CancellationToken.None));

        Assert.Contains("source capture", error.Message);
        Assert.Empty(Directory.EnumerateDirectories(fixture.ScratchRoot));
        Assert.True(Directory.Exists(external.Capture!.StagingPath));
    }

    private sealed record Fixture(ResolvedProjectContext Context, string Snapshot, string ScratchRoot)
    {
        internal SnapshotTestInput Create()
            => new SnapshotTestInputFactory(new WindowsExactFileSystemObjectOwnershipFactory(),
                new FileSystemPathIdentityResolver(), ScratchRoot)
                .Create(Context, Snapshot, CancellationToken.None);
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
