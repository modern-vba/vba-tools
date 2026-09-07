using System.Text;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectManifestFreshResolutionTests
{
    [Fact]
    public void Fresh_resolution_observes_changed_manifest_without_watcher_notification()
    {
        var root = Path.GetFullPath("manifest-fresh-resolution");
        var manifest = Identify(Path.Combine(root, "vba-project.json"));
        var active = Identify(Path.Combine(root, "src", "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[manifest.Identity.CanonicalValue] = Manifest("Before");
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        var initial = workspace.CaptureResolution(active.Uri);
        workspace.ReloadReconciledManifest(
            manifest,
            Manifest("Before"),
            workspace.GetReconciliationRevision(manifest));
        fileSystem.Manifests[manifest.Identity.CanonicalValue] = Manifest("After");

        var fresh = ((IVbaProjectManifestResolutionSource)workspace)
            .CaptureFreshResolution(active, initial.Resolution.RootPath, CancellationToken.None);

        Assert.Equal("After", fresh.Resolution.DocumentName);
    }

    [Fact]
    public void Fresh_resolution_releases_a_silently_deleted_known_nested_manifest_barrier()
    {
        var root = Path.GetFullPath("manifest-fresh-nested-barrier");
        var manifest = Identify(Path.Combine(root, "vba-project.json"));
        var nested = Identify(Path.Combine(root, "src", "nested", "vba-project.json"));
        var active = Identify(Path.Combine(root, "src", "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[manifest.Identity.CanonicalValue] = Manifest("Parent");
        fileSystem.Manifests[nested.Identity.CanonicalValue] = Manifest("Nested", ".");
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        var initial = workspace.CaptureResolution(active.Uri);
        workspace.ReloadReconciledManifest(nested, Manifest("Nested", "."), 0);
        fileSystem.Manifests.Remove(nested.Identity.CanonicalValue);

        var fresh = workspace.CaptureFreshResolution(
            active, initial.Resolution.RootPath, CancellationToken.None);

        Assert.Equal("Parent", fresh.Resolution.DocumentName);
        Assert.False(fresh.Barriers.Overrides[nested.Identity]);
    }

    [Fact]
    public void Fresh_resolution_does_not_refresh_a_parent_project_shadowed_by_nested_authority()
    {
        var root = Path.GetFullPath("manifest-fresh-isolated-parent");
        var parent = Identify(Path.Combine(root, "vba-project.json"));
        var parentActive = Identify(Path.Combine(root, "src", "Parent.bas"));
        var nestedRoot = Path.Combine(root, "src", "nested");
        var nested = Identify(Path.Combine(nestedRoot, "vba-project.json"));
        var active = Identify(Path.Combine(nestedRoot, "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[parent.Identity.CanonicalValue] = Manifest("Parent");
        fileSystem.Manifests[nested.Identity.CanonicalValue] = Manifest("Nested", ".");
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        workspace.CaptureResolution(parentActive.Uri);
        workspace.ReloadReconciledManifest(
            parent, Manifest("Parent"), workspace.GetReconciliationRevision(parent));
        var initial = workspace.CaptureResolution(active.Uri);
        var parentRevision = workspace.GetRevision(parent);
        fileSystem.Manifests[parent.Identity.CanonicalValue] = Manifest("ChangedParent");

        var fresh = workspace.CaptureFreshResolution(
            active, initial.Resolution.RootPath, CancellationToken.None);

        Assert.Equal("Nested", fresh.Resolution.DocumentName);
        Assert.Equal(parentRevision, workspace.GetRevision(parent));
        Assert.Equal("Parent", workspace.CaptureResolution(parentActive.Uri).Resolution.DocumentName);
    }

    [Fact]
    public void Fresh_resolution_preserves_the_cold_invalid_manifest_failure_before_known_fallback()
    {
        var root = Path.GetFullPath("manifest-fresh-invalid-cold");
        var manifest = Identify(Path.Combine(root, "vba-project.json"));
        var active = Identify(Path.Combine(root, "src", "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[manifest.Identity.CanonicalValue] = "{ invalid";
        var workspace = new VbaProjectManifestWorkspace(fileSystem);

        Assert.Throws<VbaProjectManifestException>(() => workspace.CaptureFreshResolution(
            active, Path.Combine(root, "src"), CancellationToken.None));
        var fallback = workspace.CaptureFreshResolution(
            active, Path.Combine(root, "src"), CancellationToken.None);

        Assert.Equal(VbaProjectResolutionKind.AdHoc, fallback.Resolution.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_resolution_tracks_created_or_deleted_nested_authority(bool initiallyNested)
    {
        var root = Path.GetFullPath("manifest-fresh-authority-transition");
        var parent = Identify(Path.Combine(root, "vba-project.json"));
        var nestedRoot = Path.Combine(root, "src", "nested");
        var nested = Identify(Path.Combine(nestedRoot, "vba-project.json"));
        var active = Identify(Path.Combine(nestedRoot, "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[parent.Identity.CanonicalValue] = Manifest("Parent");
        if (initiallyNested)
        {
            fileSystem.Manifests[nested.Identity.CanonicalValue] = Manifest("Nested", ".");
        }
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        var initial = workspace.CaptureResolution(active.Uri);
        if (initiallyNested)
        {
            fileSystem.Manifests.Remove(nested.Identity.CanonicalValue);
        }
        else
        {
            fileSystem.Manifests[nested.Identity.CanonicalValue] = Manifest("Nested", ".");
        }

        var fresh = workspace.CaptureFreshResolution(
            active, initial.Resolution.RootPath, CancellationToken.None);

        Assert.Equal(initiallyNested ? "Parent" : "Nested", fresh.Resolution.DocumentName);
        Assert.Equal(
            initiallyNested ? parent.Identity.CanonicalValue : nested.Identity.CanonicalValue,
            fresh.Resolution.ManifestPath,
            ignoreCase: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_resolution_preserves_unchanged_effective_authority_without_invalidation(bool openOverlay)
    {
        var root = Path.GetFullPath("manifest-fresh-unchanged-authority");
        var manifest = Identify(Path.Combine(root, "vba-project.json"));
        var active = Identify(Path.Combine(root, "src", "Module1.bas"));
        var fileSystem = new ManifestFileSystem();
        fileSystem.Manifests[manifest.Identity.CanonicalValue] = Manifest("Disk");
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        var initial = workspace.CaptureResolution(active.Uri);
        if (openOverlay)
        {
            workspace.OpenManifest(manifest.Uri, 1, Manifest("Overlay"));
            fileSystem.Manifests[manifest.Identity.CanonicalValue] = Manifest("ChangedDisk");
        }
        var version = workspace.Version;
        var revision = workspace.GetRevision(manifest);

        var fresh = workspace.CaptureFreshResolution(
            active, initial.Resolution.RootPath, CancellationToken.None);

        Assert.Equal(openOverlay ? "Overlay" : "Disk", fresh.Resolution.DocumentName);
        Assert.Equal(version, workspace.Version);
        Assert.Equal(revision, workspace.GetRevision(manifest));
    }

    private static VbaIdentifiedDocument Identify(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        Assert.True(VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity));
        return new VbaIdentifiedDocument(identity, uri);
    }

    private static string Manifest(string documentName, string sourcePath = "src")
        => $$"""
            {
              "schemaVersion": 1,
              "projectName": "FreshResolution",
              "primaryDocument": "{{documentName}}",
              "documents": {
                "{{documentName}}": {
                  "kind": "excel",
                  "sourcePath": "{{sourcePath}}",
                  "templatePath": "Book1.xlsm",
                  "binPath": "bin/Book1.xlsm",
                  "publishPath": "publish/Book1.xlsm",
                  "commonModules": [],
                  "references": []
                }
              }
            }
            """;

    private sealed class ManifestFileSystem : IVbaProjectFileSystem
    {
        public Dictionary<string, string> Manifests { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Manifests.ContainsKey(Path.GetFullPath(path));

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption) => [];

        public bool TryGetSourceMetadata(string path, out VbaProjectSourceFileMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public string ReadManifestText(string path) => Manifests[Path.GetFullPath(path)];

        public byte[] ReadSourceBytes(string path) => Encoding.UTF8.GetBytes("");
    }
}
