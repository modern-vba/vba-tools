using System.Text;
using VbaDevTools.App.CommonModules;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class CommonModulesCommandTests
{
    [Fact]
    public void ManifestReaderRejectsMalformedRecordsAndUnknownDependencies()
    {
        using var temp = TempDirectory.Create();
        var malformedRepo = temp.CreateDirectory("malformed");
        File.WriteAllText(Path.Combine(malformedRepo, "common-modules-manifest.tsv"), "ModuleFile\tCategories\nBroken.bas\truntime-baseline\n", new UTF8Encoding(false));
        var unknownDependencyRepo = temp.CreateDirectory("unknown");
        WriteManifest(
            unknownDependencyRepo,
            ("Feature.bas", "optional", "Missing.bas"));

        var reader = new CommonModulesManifestReader();

        Assert.Contains("header", Assert.Throws<CommonModulesManifestException>(() => reader.Load(malformedRepo)).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown dependency", Assert.Throws<CommonModulesManifestException>(() => reader.Load(unknownDependencyRepo)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManifestReaderKeepsClassifications()
    {
        using var temp = TempDirectory.Create();
        var repo = temp.CreateDirectory("repo");
        WriteManifest(repo, ("Runtime.bas", "runtime-baseline,public-udf", ""));

        var entry = Assert.Single(new CommonModulesManifestReader().Load(repo));

        Assert.True(entry.HasCategory("runtime-baseline"));
        Assert.True(entry.HasCategory("public-udf"));
    }

    [Fact]
    public void AddCopiesRequestedModuleAndTransitiveDependenciesInOrder()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(commonRepo, "Feature.bas", "feature");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["add", "Feature"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        Assert.Equal("base", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        Assert.True(result.StandardOutput.IndexOf("Copied Base.bas", StringComparison.Ordinal) < result.StandardOutput.IndexOf("Copied Feature.bas", StringComparison.Ordinal));
    }

    [Fact]
    public void AddFailsWhenExtensionlessModuleNameIsAmbiguous()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Foo.bas", "optional", ""),
            ("Foo.cls", "optional", ""));
        WriteModule(commonRepo, "Foo.bas", "bas");
        WriteModule(commonRepo, "Foo.cls", "cls");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["add", "Foo"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ambiguous", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddFailsWhenCommonModulesRepositoryIsMissing()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        var missingRepo = Path.Combine(temp.Path, "missing_common_modules_repo");
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault("Project", "Book1", projectRoot, missingRepo));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["add", "Runtime.bas"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("CommonModulesRepository was not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateOverwritesInstalledModulesAddsDependenciesAndKeepsObsoleteFiles()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = CreateProjectWithCommonModules(temp, "Project");
        var commonRepo = Path.Combine(temp.Path, "common_modules_repo");
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var sourceSet = Path.Combine(projectRoot, "src", "Book1");
        WriteModule(sourceSet, "Feature.bas", "feature v1");
        WriteModule(sourceSet, "Obsolete.bas", "obsolete");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(sourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(sourceSet, "Feature.bas")));
        Assert.Equal("obsolete", File.ReadAllText(Path.Combine(sourceSet, "Obsolete.bas")));
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateAppliesToInstalledModulesAcrossAllDocumentSourceSets()
    {
        using var temp = TempDirectory.Create();
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "SecondBook"));
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifestTestData.TwoDocumentManifest(projectRoot) with
        {
            CommonModulesRepository = "../common_modules_repo"
        });
        WriteManifest(
            commonRepo,
            ("Base.bas", "runtime-baseline", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Base.bas", "base v2");
        WriteModule(commonRepo, "Feature.bas", "feature v2");
        var firstSourceSet = Path.Combine(projectRoot, "src", "Book1");
        var secondSourceSet = Path.Combine(projectRoot, "src", "SecondBook");
        WriteModule(firstSourceSet, "Feature.bas", "first feature v1");
        WriteModule(secondSourceSet, "Feature.bas", "second feature v1");
        var application = ToolingCompositionRoot.CreateCommandLineApplication(projectRoot);

        var result = application.Run(["update"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(firstSourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(firstSourceSet, "Feature.bas")));
        Assert.Equal("base v2", File.ReadAllText(Path.Combine(secondSourceSet, "Base.bas")));
        Assert.Equal("feature v2", File.ReadAllText(Path.Combine(secondSourceSet, "Feature.bas")));
        Assert.Contains("Updated Book1/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Updated SecondBook/Feature.bas", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string CreateProjectWithCommonModules(TempDirectory temp, string projectName)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var projectRoot = temp.CreateDirectory(projectName);
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "bin", "Book1"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "publish", "Book1"));
        new JsonProjectManifestStore().Save(projectRoot, ProjectManifest.CreateDefault(projectName, "Book1", projectRoot, commonRepo));
        return projectRoot;
    }

    private static void WriteManifest(string repo, params (string ModuleFile, string Categories, string Dependencies)[] rows)
    {
        Directory.CreateDirectory(repo);
        var lines = new List<string>
        {
            "ModuleFile\tCategories\tDependencies"
        };
        lines.AddRange(rows.Select(row => $"{row.ModuleFile}\t{row.Categories}\t{row.Dependencies}"));
        File.WriteAllText(Path.Combine(repo, "common-modules-manifest.tsv"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    private static void WriteModule(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
    }
}
