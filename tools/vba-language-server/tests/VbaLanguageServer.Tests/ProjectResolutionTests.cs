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
        var relation = VbaProjectIdentityModel.Relate(
            IdentifyDocument(new Uri(sourcePath).AbsoluteUri),
            resolution,
            resolution);
        Assert.True(relation.Ownership.PreviousOwnsSubject);
        Assert.True(relation.Ownership.CurrentOwnsSubject);

        var physicalSpelling = resolution with
        {
            RootPath = sourceRoot
        };
        var sameBoundary = VbaProjectIdentityModel.Relate(
            IdentifyDocument(new Uri(sourcePath).AbsoluteUri),
            resolution,
            physicalSpelling);
        Assert.True(sameBoundary.Ownership.SameSourceOwnershipBoundary);

        var nestedProjectRoot = Directory.CreateDirectory(
            Path.Combine(sourceRoot, "NestedProject")).FullName;
        var nestedSourceRoot = Directory.CreateDirectory(
            Path.Combine(nestedProjectRoot, "src")).FullName;
        var nestedSourcePath = Path.Combine(
            nestedSourceRoot,
            "NestedModule.bas");
        File.WriteAllText(
            nestedSourcePath,
            "Attribute VB_Name = \"NestedModule\"",
            Encoding.UTF8);
        var nestedManifestPath = Path.Combine(
            nestedProjectRoot,
            ProjectManifest.ManifestFileName);
        File.WriteAllText(nestedManifestPath, "{}", Encoding.UTF8);
        var nestedResolution = new VbaProjectResolution(
            VbaProjectResolutionKind.ManifestDocument,
            nestedSourceRoot,
            nestedManifestPath,
            "NestedBook")
        {
            RootIdentity = VbaProjectResolver.ResolvePathIdentity(
                nestedSourceRoot)
        };
        var nestedRelation = VbaProjectIdentityModel.Relate(
            IdentifyDocument(new Uri(nestedSourcePath).AbsoluteUri),
            resolution,
            nestedResolution);
        Assert.Equal(
            VbaProjectAuthorityRelationKind.RetainPrevious,
            nestedRelation.Kind);
        var aliasNestedResolution = nestedResolution with
        {
            ManifestPath = Path.Combine(
                aliasRoot,
                "NestedProject",
                ProjectManifest.ManifestFileName)
        };
        Assert.True(
            VbaProjectIdentityModel.TryIdentifyDocument(
                new Uri(nestedSourcePath).AbsoluteUri,
                out var nestedSourceIdentity));
        Assert.True(
            VbaProjectIdentityModel.OwnsTransferredProjectDocument(
                aliasNestedResolution,
                nestedSourceIdentity));
    }

    private static void WriteManifest(string projectRoot, ProjectManifest manifest)
        => File.WriteAllBytes(
            Path.Combine(projectRoot, ProjectManifest.ManifestFileName),
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest));

    private static VbaDocumentIdentity IdentifyDocument(string uri)
        => VbaProjectIdentityModel.TryIdentifyDocument(uri, out var identity)
            ? identity
            : throw new InvalidOperationException(
                "The test document must have a typed identity.");

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
