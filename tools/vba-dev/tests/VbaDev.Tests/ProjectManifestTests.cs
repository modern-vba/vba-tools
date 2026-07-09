using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ProjectManifestTests
{
    [Fact]
    public void SaveWritesUtf16LeBomAndRelativeCommonModulesRepository()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("SampleProject");
        var commonModulesRepository = Path.GetFullPath(Path.Combine(projectRoot, "..", "common_modules_repo"));
        var manifest = ProjectManifest.CreateDefault(
            "SampleProject",
            "Book1",
            projectRoot,
            commonModulesRepository,
            [
                new InstalledCommonModule("Runtime", Requested: true),
                new InstalledCommonModule("CommonDependency", Requested: false)
            ],
            [new VbaProjectReference("Microsoft Scripting Runtime")]);
        var store = new JsonProjectManifestStore();

        store.Save(projectRoot, manifest);

        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var bytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        using var document = JsonDocument.Parse(Encoding.Unicode.GetString(bytes[2..]));
        Assert.Equal("../common_modules_repo", document.RootElement.GetProperty("commonModulesRepository").GetString());
        var book = document.RootElement.GetProperty("documents").GetProperty("Book1");
        var commonModules = book.GetProperty("commonModules");
        Assert.Equal("Runtime", commonModules[0].GetProperty("name").GetString());
        Assert.True(commonModules[0].GetProperty("requested").GetBoolean());
        Assert.Equal("CommonDependency", commonModules[1].GetProperty("name").GetString());
        Assert.False(commonModules[1].GetProperty("requested").GetBoolean());
        Assert.Equal("Microsoft Scripting Runtime", book.GetProperty("references")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void LoadAcceptsUtf16LeBomAndUtf8Inputs()
    {
        using var temp = TempDirectory.Create();
        var utf16Root = temp.CreateDirectory("Utf16Project");
        var utf8Root = temp.CreateDirectory("Utf8Project");
        var store = new JsonProjectManifestStore();
        var manifest = ProjectManifest.CreateDefault("SampleProject", "Book1", utf16Root, null);
        store.Save(utf16Root, manifest);
        File.WriteAllText(Path.Combine(utf8Root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("Utf8Project"), new UTF8Encoding(false));

        var utf16Manifest = store.Load(Path.Combine(utf16Root, ProjectManifest.ManifestFileName));
        var utf8Manifest = store.Load(Path.Combine(utf8Root, ProjectManifest.ManifestFileName));

        Assert.Equal("SampleProject", utf16Manifest.ProjectName);
        Assert.Equal("Utf8Project", utf8Manifest.ProjectName);
        Assert.Empty(utf8Manifest.Documents["Book1"].CommonModules);
        Assert.Empty(utf8Manifest.Documents["Book1"].References);
    }

    [Fact]
    public void UnknownSchemaVersionIsRejectedAsUsageError()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("BadSchema");
        File.WriteAllText(Path.Combine(root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("BadSchema").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), new UTF8Encoding(false));
        var store = new JsonProjectManifestStore();

        var ex = Assert.Throws<ProjectManifestException>(() => store.Load(Path.Combine(root, ProjectManifest.ManifestFileName)));

        Assert.Contains("schemaVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverUsesExplicitProjectAndDocumentOptions()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var nested = temp.CreateDirectory("OtherLocation");
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var resolver = new ProjectContextResolver(store);

        var context = resolver.Resolve(new ProjectResolutionRequest(ProjectRoot: root, DocumentName: "SecondBook", StartDirectory: nested));

        Assert.Equal(root, context.ProjectRoot);
        Assert.Equal("SecondBook", context.DocumentName);
        Assert.Equal(Path.Combine(root, "src", "SecondBook"), context.DocumentSourceSetPath);
        Assert.Equal(Path.Combine(root, "bin", "SecondBook", "SecondBook.xlsm"), context.BinDocumentPath);
        Assert.Equal(Path.Combine(root, "publish", "SecondBook", "SecondBook.xlsm"), context.PublishDocumentPath);
    }

    [Fact]
    public void ResolverWalksUpToNearestProjectManifestAndUsesPrimaryDocument()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var startDirectory = Directory.CreateDirectory(Path.Combine(root, "src", "Book1", "nested")).FullName;
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var resolver = new ProjectContextResolver(store);

        var context = resolver.Resolve(new ProjectResolutionRequest(ProjectRoot: null, DocumentName: null, StartDirectory: startDirectory));

        Assert.Equal(root, context.ProjectRoot);
        Assert.Equal("Book1", context.DocumentName);
        Assert.Equal(Path.Combine(root, "src", "Book1"), context.DocumentSourceSetPath);
    }

    [Fact]
    public void CommandDefaultResolutionPrefersOptionThenManifestThenBuiltInDefault()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null) with
        {
            CommandDefaults = new CommandDefaults(Test: new TestCommandDefaults(Format: "text"))
        };

        Assert.Equal("ndjson", CommandDefaultResolver.ResolveTestFormat(manifest, "ndjson"));
        Assert.Equal("text", CommandDefaultResolver.ResolveTestFormat(manifest, null));
        Assert.Equal("text", CommandDefaultResolver.ResolveTestFormat(ProjectManifest.CreateDefault("Project", "Book1", root, null), null));

        var unsupportedManifest = manifest with
        {
            CommandDefaults = new CommandDefaults(Test: new TestCommandDefaults(Format: "json"))
        };
        var ex = Assert.Throws<ProjectManifestException>(() => CommandDefaultResolver.ResolveTestFormat(unsupportedManifest, null));
        Assert.Contains("Unsupported test format default 'json'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CliRejectsInvalidManifestAsUsageErrorBeforePlaceholderAction()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("BadSchema");
        File.WriteAllText(Path.Combine(root, ProjectManifest.ManifestFileName), ProjectManifestTestData.ValidJson("BadSchema").Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal), new UTF8Encoding(false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("schemaVersion", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("primary-document.json", "PrimaryDocumentProject", "Book1", 1)]
    [InlineData("document-source-set.json", "DocumentSourceSetProject", "Book1", 1)]
    [InlineData("references.json", "ReferencesProject", "Book1", 1)]
    [InlineData("source-template.json", "SourceTemplateProject", "Book1", 1)]
    [InlineData("multi-document.json", "MultiDocumentProject", "Book1", 2)]
    public void SharedFixturesLoadAsVbaDevProjectManifests(
        string fixtureName,
        string expectedProjectName,
        string expectedPrimaryDocument,
        int expectedDocumentCount)
    {
        var manifest = new JsonProjectManifestStore().Load(ProjectManifestFixturePath(fixtureName));

        Assert.Equal(expectedProjectName, manifest.ProjectName);
        Assert.Equal(expectedPrimaryDocument, manifest.PrimaryDocument);
        Assert.Equal(expectedDocumentCount, manifest.Documents.Count);
    }

    [Theory]
    [InlineData("invalid-missing-primary-document.json", "primaryDocument")]
    [InlineData("invalid-primary-document-not-defined.json", "primaryDocument")]
    [InlineData("invalid-empty-reference-name.json", "reference name")]
    public void SharedInvalidFixturesFailVbaDevManifestValidation(string fixtureName, string expectedMessage)
    {
        var ex = Assert.Throws<ProjectManifestException>(
            () => new JsonProjectManifestStore().Load(ProjectManifestFixturePath(fixtureName)));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ProjectManifestFixturePath(string fixtureName)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "..",
            "fixtures",
            "project-manifest",
            fixtureName));
}

internal static class ProjectManifestTestData
{
    public static string ValidJson(string projectName)
        => $$"""
        {
          "schemaVersion": 1,
          "projectName": "{{projectName}}",
          "primaryDocument": "Book1",
          "documents": {
            "Book1": {
              "kind": "excel",
              "sourcePath": "src/Book1",
              "templatePath": "src/Book1/Book1.xlsm",
              "binPath": "bin/Book1/Book1.xlsm",
              "publishPath": "publish/Book1/Book1.xlsm"
            }
          },
          "commonModulesRepository": "../common_modules_repo",
          "commandDefaults": {
            "test": {
              "format": "ndjson"
            }
          }
        }
        """;

    public static ProjectManifest TwoDocumentManifest(string projectRoot)
        => ProjectManifest.CreateDefault("Project", "Book1", projectRoot, null) with
        {
            Documents = new Dictionary<string, ProjectDocument>(StringComparer.OrdinalIgnoreCase)
            {
                ["Book1"] = ProjectDocument.CreateExcel("Book1"),
                ["SecondBook"] = ProjectDocument.CreateExcel("SecondBook")
            }
        };
}

internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vba-devtools-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
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
