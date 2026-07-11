using System.Text;
using VbaDev.App.Export;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class ExportCommandTests
{
    [Fact]
    public void DefaultExportReadsFromBinAndCleansDocumentSourceSetOnly()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateDocumentSourceSet(root, "SecondBook", ("Other.bas", "Attribute VB_Name = \"Other\""));
        var binPath = CreateWorkbook(root, "bin", "Book1");
        File.WriteAllText(Path.Combine(sourceSet, "Old.bas"), "'#ExcludePublish\nold", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "Old.cls"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceSet, "OldForm.frm"), "old", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(sourceSet, "OldForm.frx"), [9, 9, 9]);
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "Attribute VB_Name = \"Module1\""),
            ("Dialog.frm", "VERSION 5.00"),
            ("Dialog.frx", "frx"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(binPath, sourceSet)], exporter.Calls);
        Assert.False(File.Exists(Path.Combine(sourceSet, "Old.bas")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Old.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "OldForm.frm")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "OldForm.frx")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Book1.xlsm")));
        Assert.True(File.Exists(Path.Combine(root, ProjectManifest.ManifestFileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(root, "src", "SecondBook", "Other.bas")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
        Assert.DoesNotContain("#ExcludePublish", File.ReadAllText(Path.Combine(sourceSet, "Module1.bas"), Encoding.UTF8), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sourceSet, "Dialog.frm")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Dialog.frx")));
    }

    [Fact]
    public void ExplicitWorkbookExportDefaultsToWorkingDirectoryWithoutCleaning()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Old.bas"), "old", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(explicitWorkbook, temp.Path)], exporter.Calls);
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "Old.bas"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
    }

    [Fact]
    public void ExplicitWorkbookExportDoesNotRequireProjectContext()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var explicitDestination = temp.CreateDirectory("explicit-export");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook, "--to", explicitDestination]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(explicitWorkbook, explicitDestination)], exporter.Calls);
        Assert.Equal("new", File.ReadAllText(Path.Combine(explicitDestination, "Module1.bas"), Encoding.UTF8));
    }

    [Theory]
    [InlineData("--project")]
    [InlineData("--document")]
    public void ExplicitWorkbookExportRejectsProjectContextOptions(string optionName)
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var optionValue = optionName.Equals("--project", StringComparison.Ordinal)
            ? temp.Path
            : "Book1";
        var result = application.Run(["export", "--from", explicitWorkbook, optionName, optionValue]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"{optionName} cannot be used with --from.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExportToOptionCleansSpecifiedDirectory()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateDocumentSourceSet(root, "Book1");
        var binPath = CreateWorkbook(root, "bin", "Book1");
        var explicitDestination = temp.CreateDirectory("explicit-export");
        File.WriteAllText(Path.Combine(explicitDestination, "Old.bas"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(explicitDestination, "Old.frm"), "old", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(explicitDestination, "Old.frx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(explicitDestination, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--to", explicitDestination]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(binPath, explicitDestination)], exporter.Calls);
        Assert.False(File.Exists(Path.Combine(explicitDestination, "Old.bas")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "Old.frm")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "Old.frx")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(explicitDestination, "notes.txt"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(explicitDestination, "Module1.bas"), Encoding.UTF8));
    }

    [Fact]
    public void ExportToOptionStillRequiresProjectContext()
    {
        using var temp = TempDirectory.Create();
        var explicitDestination = temp.CreateDirectory("explicit-export");
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--to", explicitDestination]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Project manifest was not found", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExplicitWorkbookExportRejectsDestinationFile()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        var destinationFile = Path.Combine(temp.Path, "Module1.bas");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(destinationFile, "old", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook, "--to", destinationFile]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Export destination is not a directory: {destinationFile}", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExplicitWorkbookExportRejectsBlankFromPath()
    {
        using var temp = TempDirectory.Create();
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from="]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--from requires a workbook path.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(exporter.Calls);
    }

    [Fact]
    public void ExportOptionsResolveRelativePathsFromWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("work");
        var relativeWorkbook = Path.Combine(workingDirectory, "relative.xlsm");
        File.WriteAllText(relativeWorkbook, "workbook", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(workingDirectory, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", "relative.xlsm", "--to", "out"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(relativeWorkbook, Path.Combine(workingDirectory, "out"))], exporter.Calls);
    }

    private static string CreateDocumentSourceSet(string root, string documentName, params (string FileName, string Content)[] files)
    {
        var sourceSet = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(sourceSet, file.FileName), file.Content, Encoding.UTF8);
        }

        return sourceSet;
    }

    private static string CreateWorkbook(string root, string outputDirectory, string documentName)
    {
        var workbookPath = Path.Combine(root, outputDirectory, documentName, $"{documentName}.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
        File.WriteAllText(workbookPath, $"workbook:{documentName}", Encoding.UTF8);
        return workbookPath;
    }
}

internal sealed class FakeWorkbookModuleExporter : IWorkbookModuleExporter
{
    private readonly IReadOnlyList<(string FileName, string Content)> exports;

    public FakeWorkbookModuleExporter(params (string FileName, string Content)[] exports)
    {
        this.exports = exports;
    }

    public List<(string WorkbookPath, string DestinationDirectory)> Calls { get; } = [];

    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        Calls.Add((workbookPath, destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        foreach (var export in exports)
        {
            File.WriteAllText(Path.Combine(destinationDirectory, export.FileName), export.Content, Encoding.UTF8);
        }
    }
}
