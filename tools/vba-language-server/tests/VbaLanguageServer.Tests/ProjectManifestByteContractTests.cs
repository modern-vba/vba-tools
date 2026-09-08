using System.Text.Json;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.Workspace;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ProjectManifestByteContractTests
{
    [Fact]
    public void OpenUnicodeManifestOverlayRetainsAuthorityOverInvalidDiskBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vba-manifest-overlay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        var path = Path.Combine(root, "vba-project.json");
        try
        {
            File.WriteAllBytes(path, [0xff, 0xfe, 0, 0]);
            var text = (string)ConformanceCases().Single(entry => (string)entry[0] == "utf8")[3];
            var workspace = new VbaProjectManifestWorkspace();
            workspace.OpenManifest(new Uri(path).AbsoluteUri, 1, text);

            var resolution = workspace.Resolve(new Uri(Path.Combine(root, "src", "Book1", "Module1.bas")).AbsoluteUri);

            Assert.Equal(VbaProjectResolutionKind.ManifestDocument, resolution.Kind);
            Assert.Equal("Book1", resolution.DocumentName);
            Assert.Throws<VbaProjectManifestException>(() => SystemVbaProjectFileSystem.Instance.ReadManifestText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static IEnumerable<object[]> ConformanceCases()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-manifest-encoding", "cases.json")));
        foreach (var entry in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [entry.GetProperty("name").GetString()!,
                Convert.FromBase64String(entry.GetProperty("base64").GetString()!),
                entry.GetProperty("accepted").GetBoolean(),
                entry.TryGetProperty("text", out var text) ? text.GetString()! : string.Empty];
        }
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void DiskManifestReadsAndProjectResolutionObeyTheSharedByteContract(
        string name, byte[] bytes, bool accepted, string expectedText)
    {
        var root = Path.Combine(Path.GetTempPath(), "vba-manifest-bytes", Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        var path = Path.Combine(root, "vba-project.json");
        var sourceUri = new Uri(Path.Combine(root, "src", "Book1", "Module1.bas")).AbsoluteUri;
        try
        {
            File.WriteAllBytes(path, bytes);
            var fileSystem = SystemVbaProjectFileSystem.Instance;
            if (accepted)
            {
                Assert.Equal(expectedText, fileSystem.ReadManifestText(path));
                Assert.Equal(expectedText, fileSystem.ReadManifestText(path, CancellationToken.None));
                Assert.Equal(VbaProjectResolutionKind.ManifestDocument, VbaProjectResolver.Resolve(sourceUri).Kind);
            }
            else
            {
                Assert.Throws<VbaProjectManifestException>(() => fileSystem.ReadManifestText(path));
                Assert.Throws<VbaProjectManifestException>(() => fileSystem.ReadManifestText(path, CancellationToken.None));
                Assert.Throws<VbaProjectManifestException>(() => VbaProjectResolver.Resolve(sourceUri));
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }
}
