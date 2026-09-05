using System.Text;
using VbaDev.App.Projects;
using VbaDev.Domain;
using Xunit;

namespace VbaDev.Tests;

public sealed class NewProjectInitialManifestStagerTests
{
    [Fact]
    public void StageWritesCanonicalSiblingAndRegistersItInTheTargetInventory()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var leaseMarkerPath = manifestPath + ".vba-dev.lock";
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        File.WriteAllText(leaseMarkerPath, "owned lease", new UTF8Encoding(false));
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);

        var stage = NewProjectInitialManifestStager.Stage(manifestPath, manifest, tracker);

        Assert.Equal(projectRoot, Path.GetDirectoryName(stage.TemporaryPath));
        Assert.Equal(
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest),
            File.ReadAllBytes(stage.TemporaryPath));
        Assert.True(
            tracker.ProveCompleteTargetInventory(projectRoot, leaseMarkerPath).IsComplete);
    }

    [Fact]
    public void CommitCreateOnlyMovesTheStagedBytesToTheInitialManifestPath()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        var stage = NewProjectInitialManifestStager.Stage(manifestPath, manifest, tracker);

        stage.CommitCreateOnly();

        Assert.True(stage.IsCommitted);
        Assert.False(File.Exists(stage.TemporaryPath));
        Assert.Equal(
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest),
            File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void CommitCreateOnlyPreservesAnExternalManifestAndTheTrackedStage()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "Project");
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        using var tracker = new NewProjectArtifactTracker();
        tracker.EnsureDirectory(projectRoot);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null);
        var stage = NewProjectInitialManifestStager.Stage(manifestPath, manifest, tracker);
        var externalBytes = Encoding.UTF8.GetBytes("external manifest");
        File.WriteAllBytes(manifestPath, externalBytes);

        Assert.Throws<IOException>(() => stage.CommitCreateOnly());

        Assert.False(stage.IsCommitted);
        Assert.Equal(externalBytes, File.ReadAllBytes(manifestPath));
        Assert.True(File.Exists(stage.TemporaryPath));
    }
}
