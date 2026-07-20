using System.Text;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaProjectManifestWorkspaceConcurrencyTests
{
    [Fact]
    public void Cold_invalid_disk_manifest_throws_once_then_uses_known_fallback()
    {
        var root = Path.GetFullPath("manifest-invalid-once");
        var activeUri = new Uri(
            Path.Combine(root, "Module1.bas")).AbsoluteUri;
        var fileSystem = new InvalidManifestFileSystem(
            Path.Combine(root, "vba-project.json"));
        var workspace = new VbaProjectManifestWorkspace(fileSystem);

        Assert.Throws<VbaProjectManifestException>(
            () => workspace.Resolve(activeUri));
        var fallback = workspace.Resolve(activeUri);

        Assert.Equal(VbaProjectResolutionKind.AdHoc, fallback.Kind);
        Assert.Equal(1, fileSystem.ManifestReadCount);
    }

    [Fact]
    public async Task Invalid_open_overlay_disk_fallback_does_not_hold_the_manifest_gate()
    {
        var root = Path.GetFullPath("manifest-lock-free");
        var manifestPath = Path.Combine(root, "vba-project.json");
        var manifestUri = new Uri(manifestPath).AbsoluteUri;
        var activeUri = new Uri(Path.Combine(root, "Module1.bas")).AbsoluteUri;
        var fileSystem = new BlockingManifestFileSystem();
        var workspace = new VbaProjectManifestWorkspace(fileSystem);
        var openTask = Task.Run(
            () => workspace.OpenManifest(
                manifestUri,
                documentVersion: 1,
                "{ invalid"));
        await fileSystem.ReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        try
        {
            var barriers = await Task.Run(
                    () => workspace.CaptureScopeBarriers(
                        activeUri,
                        new VbaProjectResolution(
                            VbaProjectResolutionKind.AdHoc,
                            root)))
                .WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(0, barriers.Revision);
        }
        finally
        {
            fileSystem.ReleaseRead();
        }

        var opened = await openTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(opened.Accepted);
        Assert.NotNull(opened.Error);
    }

    private sealed class InvalidManifestFileSystem(string manifestPath)
        : IVbaProjectFileSystem
    {
        public int ManifestReadCount { get; private set; }

        public bool FileExists(string path)
            => Path.GetFullPath(path).Equals(
                Path.GetFullPath(manifestPath),
                StringComparison.OrdinalIgnoreCase);

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => [];

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public string ReadManifestText(string path)
        {
            ManifestReadCount++;
            return "{";
        }

        public byte[] ReadSourceBytes(string path)
            => Encoding.UTF8.GetBytes("");
    }

    private sealed class BlockingManifestFileSystem : IVbaProjectFileSystem
    {
        private readonly ManualResetEventSlim releaseRead = new(false);

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FileExists(string path) => true;

        public bool DirectoryExists(string path) => true;

        public IEnumerable<string> EnumerateSourceFiles(
            string rootPath,
            string searchPattern,
            SearchOption searchOption)
            => [];

        public bool TryGetSourceMetadata(
            string path,
            out VbaProjectSourceFileMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public string ReadManifestText(string path)
        {
            ReadStarted.TrySetResult();
            releaseRead.Wait();
            return """
                {
                  "schemaVersion": 1,
                  "projectName": "LockFree",
                  "primaryDocument": "Book1",
                  "documents": {
                    "Book1": {
                      "kind": "excel",
                      "sourcePath": "src",
                      "templatePath": "Book1.xlsm",
                      "binPath": "bin/Book1.xlsm",
                      "publishPath": "publish/Book1.xlsm"
                    }
                  }
                }
                """;
        }

        public byte[] ReadSourceBytes(string path)
            => Encoding.UTF8.GetBytes("");

        public void ReleaseRead()
            => releaseRead.Set();
    }
}
