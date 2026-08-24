using System.Text;
using VbaDev.Domain;
using VbaLanguageServer.ProjectModel;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class ProjectResolutionTests
{
    [Fact]
    public void ActiveFileAliasIntoDeclaredSourceRootResolvesTheManifestDocument()
    {
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "src", "Book1")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Module1.bas");
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        var aliasRoot = Path.Combine(root, "ActiveAlias");
        Directory.CreateSymbolicLink(aliasRoot, sourceRoot);
        WriteManifest(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));

        var resolution = VbaProjectResolver.Resolve(
            new Uri(Path.Combine(aliasRoot, "Module1.bas")).AbsoluteUri);

        Assert.Equal(VbaProjectResolutionKind.ManifestDocument, resolution.Kind);
        Assert.Equal("Book1", resolution.DocumentName);
        Assert.Equal(sourceRoot, resolution.RootPath, ignoreCase: true);
    }

    [Fact]
    public void DeclaredSourceRootAliasResolvesAnActiveFileThroughItsPhysicalPath()
    {
        using var temp = TestDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sourceRoot = Directory.CreateDirectory(Path.Combine(root, "src", "Book1")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Module1.bas");
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        var aliasRoot = Path.Combine(root, "DeclaredAlias");
        Directory.CreateSymbolicLink(aliasRoot, sourceRoot);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = manifest.Documents["Book1"] with
        {
            SourcePath = "DeclaredAlias"
        };
        WriteManifest(root, manifest);

        var resolution = VbaProjectResolver.Resolve(new Uri(sourcePath).AbsoluteUri);

        Assert.Equal(VbaProjectResolutionKind.ManifestDocument, resolution.Kind);
        Assert.Equal("Book1", resolution.DocumentName);
        Assert.Equal(aliasRoot, resolution.RootPath, ignoreCase: true);
        Assert.True(resolution.ContainsUri(new Uri(sourcePath).AbsoluteUri));
    }

    private static void WriteManifest(string projectRoot, ProjectManifest manifest)
        => File.WriteAllBytes(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName),
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest));

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vba-language-server-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
