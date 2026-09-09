using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectResolutionRequestTests
{
    [Theory]
    [InlineData("", null, "ProjectRoot")]
    [InlineData(" ", null, "ProjectRoot")]
    [InlineData("\t\r\n", null, "ProjectRoot")]
    [InlineData("\u00a0\u3000", null, "ProjectRoot")]
    [InlineData(null, "", "DocumentName")]
    [InlineData(null, " ", "DocumentName")]
    [InlineData(null, "\t\r\n", "DocumentName")]
    [InlineData(null, "\u00a0\u3000", "DocumentName")]
    public void DirectResolutionRejectsBlankSelectorsBeforeDiscovery(
        string? projectRoot,
        string? documentName,
        string parameterName)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var startDirectory = Directory.CreateDirectory(
            Path.Combine(root, "src", "Book1", "nested")).FullName;
        var jsonStore = new JsonProjectManifestStore();
        jsonStore.Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var store = new CountingManifestStore(jsonStore);
        var resolver = new ProjectContextResolver(store);
        ProjectResolutionRequest? request = null;

        var error = Assert.Throws<ArgumentException>(() =>
        {
            request = new ProjectResolutionRequest(
                ProjectRoot: projectRoot,
                DocumentName: documentName,
                StartDirectory: startDirectory);
            resolver.Resolve(request);
        });

        Assert.Equal(parameterName, error.ParamName);
        Assert.Null(request);
        Assert.Equal(0, store.LoadCount);
    }

    [Theory]
    [InlineData("ProjectRoot", false)]
    [InlineData("DocumentName", false)]
    [InlineData("ProjectRoot", true)]
    [InlineData("DocumentName", true)]
    public void InitializerAndWithCannotIntroduceBlankSelectors(
        string parameterName,
        bool useWith)
    {
        var original = new ProjectResolutionRequest("../Project", "Book1", "working-directory");
        foreach (var blank in new[] { "", " ", "\t\r\n", "\u00a0\u3000" })
        {
            ProjectResolutionRequest? updated = null;

            var error = Assert.Throws<ArgumentException>(() =>
            {
                if (useWith)
                {
                    updated = parameterName == "ProjectRoot"
                        ? original with { ProjectRoot = blank }
                        : original with { DocumentName = blank };
                }
                else
                {
                    updated = parameterName == "ProjectRoot"
                        ? new ProjectResolutionRequest(null, null, original.StartDirectory)
                        {
                            ProjectRoot = blank
                        }
                        : new ProjectResolutionRequest(null, null, original.StartDirectory)
                        {
                            DocumentName = blank
                        };
                }
            });

            Assert.Equal(parameterName, error.ParamName);
            Assert.Null(updated);
            Assert.Equal(new ProjectResolutionRequest("../Project", "Book1", "working-directory"), original);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" .\\MiXeD\\..\\Project ", " BoOk1 ")]
    [InlineData("../MiXeD/Project", "bOoK1")]
    public void ValidSelectorsPreserveSpellingAndPublicRecordOperations(
        string? projectRoot,
        string? documentName)
    {
        const string startDirectory = " .\\Working\\..\\Here ";
        var request = new ProjectResolutionRequest(
            ProjectRoot: projectRoot,
            DocumentName: documentName,
            StartDirectory: startDirectory);

        var (actualProjectRoot, actualDocumentName, actualStartDirectory) = request;

        Assert.Equal(projectRoot, actualProjectRoot);
        Assert.Equal(documentName, actualDocumentName);
        Assert.Equal(startDirectory, actualStartDirectory);
        var initialized = new ProjectResolutionRequest(null, null, "initial")
        {
            ProjectRoot = projectRoot,
            DocumentName = documentName,
            StartDirectory = startDirectory
        };
        var copied = new ProjectResolutionRequest("old-root", "old-document", "old-start") with
        {
            ProjectRoot = projectRoot,
            DocumentName = documentName,
            StartDirectory = startDirectory
        };
        Assert.Equal(request, initialized);
        Assert.Equal(request, copied);
        Assert.Equal(request, request with { });
        Assert.NotEqual(request, request with { ProjectRoot = "another-root" });
        Assert.NotEqual(request, request with { DocumentName = "another-document" });
        Assert.NotEqual(request, request with { StartDirectory = "another-start" });
    }

    [Fact]
    public void RelativeProjectResolutionKeepsTheSuppliedStartDirectoryBasis()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory(Path.Combine("Projects", "MiXeD"));
        var startDirectory = temp.CreateDirectory(Path.Combine("Elsewhere", "Nested"));
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var resolver = new ProjectContextResolver(store);
        var relativeProject = Path.Combine("..", "..", "Projects", "MiXeD");
        var request = new ProjectResolutionRequest(
            ProjectRoot: relativeProject,
            DocumentName: "sEcOnDbOoK",
            StartDirectory: startDirectory);

        var context = resolver.Resolve(request);

        Assert.Equal(root, context.ProjectRoot);
        Assert.Equal("SecondBook", context.DocumentName);
        Assert.Equal(Path.Combine(root, "src", "SecondBook"), context.DocumentSourceSetPath);
        Assert.Equal(relativeProject, request.ProjectRoot);
        Assert.Equal("sEcOnDbOoK", request.DocumentName);
        Assert.Equal(startDirectory, request.StartDirectory);
    }

    private sealed class CountingManifestStore(IProjectManifestStore inner) : IProjectManifestStore
    {
        public int LoadCount { get; private set; }

        public ProjectManifest Load(string manifestPath)
        {
            LoadCount++;
            return inner.Load(manifestPath);
        }

        public void Save(string projectRoot, ProjectManifest manifest)
            => inner.Save(projectRoot, manifest);
    }
}
