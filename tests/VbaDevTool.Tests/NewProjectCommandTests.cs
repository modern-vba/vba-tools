using System.Text;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class NewProjectCommandTests
{
    [Fact]
    public void NewCreatesProjectLayoutWorkbookAndUtf16Manifest()
    {
        using var temp = TempDirectory.Create();
        var workbookCreator = new FakeInitialWorkbookCreator();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            initialWorkbookCreator: workbookCreator);

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("CommonModulesRepository was not found", result.StandardError, StringComparison.Ordinal);
        var projectRoot = Path.Combine(temp.Path, "SampleProject");
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "src", "SampleProject")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "bin", "SampleProject")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "publish", "SampleProject")));
        Assert.True(File.Exists(Path.Combine(projectRoot, "src", "SampleProject", "SampleProject.xlsm")));
        Assert.Contains(Path.Combine(projectRoot, "src", "SampleProject", "SampleProject.xlsm"), workbookCreator.CreatedPaths);

        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        var bytes = File.ReadAllBytes(manifestPath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        var manifest = new JsonProjectManifestStore().Load(manifestPath);
        Assert.Equal("SampleProject", manifest.ProjectName);
        Assert.Equal("SampleProject", manifest.PrimaryDocument);
        Assert.Null(manifest.CommonModulesRepository);
    }

    [Fact]
    public void NewExcelRequiresNameOption()
    {
        using var temp = TempDirectory.Create();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("new requires a project name", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void NewAcceptsEmptyDirectoryAndRejectsNonEmptyDirectoryWithoutDeletingFiles()
    {
        using var temp = TempDirectory.Create();
        var emptyProject = Path.Combine(temp.Path, "EmptyProject");
        Directory.CreateDirectory(emptyProject);
        var nonEmptyProject = Path.Combine(temp.Path, "NonEmptyProject");
        Directory.CreateDirectory(nonEmptyProject);
        var existingFile = Path.Combine(nonEmptyProject, "keep.txt");
        File.WriteAllText(existingFile, "keep", new UTF8Encoding(false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var emptyResult = application.Run(["new", "excel", "-n", "EmptyProject"]);
        var nonEmptyResult = application.Run(["new", "excel", "-n", "NonEmptyProject"]);

        Assert.Equal(0, emptyResult.ExitCode);
        Assert.Equal(1, nonEmptyResult.ExitCode);
        Assert.True(File.Exists(existingFile));
        Assert.Contains("not empty", nonEmptyResult.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRejectsExistingProjectManifestWithoutDeletingFiles()
    {
        using var temp = TempDirectory.Create();
        var projectRoot = Path.Combine(temp.Path, "ExistingProject");
        Directory.CreateDirectory(projectRoot);
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);
        File.WriteAllText(manifestPath, "{}", new UTF8Encoding(false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "ExistingProject"]);

        Assert.Equal(1, result.ExitCode);
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("project.json already exists", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void NewCopiesRuntimeBaselineAndTestFoundationFromCommonModulesManifest()
    {
        using var temp = TempDirectory.Create();
        var commonModulesRepository = Path.Combine(temp.Path, "common_modules_repo");
        Directory.CreateDirectory(commonModulesRepository);
        WriteCommonModulesManifest(commonModulesRepository);
        File.WriteAllText(Path.Combine(commonModulesRepository, "Runtime.bas"), "runtime", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(commonModulesRepository, "UnitTest.bas"), "test", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(commonModulesRepository, "Optional.bas"), "optional", new UTF8Encoding(false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            initialWorkbookCreator: new FakeInitialWorkbookCreator());

        var result = application.Run(["new", "excel", "--name", "SampleProject"]);

        Assert.Equal(0, result.ExitCode);
        var sourceSet = Path.Combine(temp.Path, "SampleProject", "src", "SampleProject");
        Assert.True(File.Exists(Path.Combine(sourceSet, "Runtime.bas")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "UnitTest.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Optional.bas")));
        var manifest = new JsonProjectManifestStore().Load(Path.Combine(temp.Path, "SampleProject", ProjectManifest.ManifestFileName));
        Assert.Equal("../common_modules_repo", manifest.CommonModulesRepository);
    }

    private static void WriteCommonModulesManifest(string commonModulesRepository)
    {
        var text = string.Join(
            "\n",
            "# test manifest",
            "ModuleFile\tCategories\tDependencies",
            "Runtime.bas\truntime-baseline\t",
            "UnitTest.bas\ttest-foundation\t",
            "Optional.bas\toptional\t") + "\n";
        File.WriteAllText(Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"), text, new UTF8Encoding(false));
    }
}

internal sealed class FakeInitialWorkbookCreator : IInitialWorkbookCreator
{
    public List<string> CreatedPaths { get; } = [];

    public void CreateInitialWorkbook(string workbookPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        File.WriteAllText(workbookPath, "fake xlsm", new UTF8Encoding(false));
        CreatedPaths.Add(workbookPath);
    }
}
