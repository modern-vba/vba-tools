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
    public void DefaultExportUsesTempCleanupAndPreservesExistingLayout()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        CreateDocumentSourceSet(root, "SecondBook", ("Other.bas", "Attribute VB_Name = \"Other\""));
        var binPath = CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Module1.bas"), "'#ExcludePublish\nold");
        WriteText(Path.Combine(sourceSet, "old", "Old.cls"), "old");
        WriteText(Path.Combine(sourceSet, "forms", "Dialog.frm"), "old");
        WriteBytes(Path.Combine(sourceSet, "forms", "Dialog.frx"), [9, 9, 9]);
        WriteBytes(Path.Combine(sourceSet, "Dialog.frx"), [8, 8, 8]);
        File.WriteAllText(Path.Combine(sourceSet, "notes.txt"), "keep", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(sourceSet, "empty"));
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "Attribute VB_Name = \"Module1\""),
            ("Dialog.frm", "VERSION 5.00"),
            ("Dialog.frx", "frx"),
            ("NewModule.cls", "VERSION 1.0 CLASS"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(sourceSet, call.DestinationDirectory);
        Assert.False(File.Exists(Path.Combine(sourceSet, "old", "Old.cls")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Dialog.frx")));
        Assert.True(Directory.Exists(Path.Combine(sourceSet, "old")));
        Assert.True(Directory.Exists(Path.Combine(sourceSet, "empty")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "Book1.xlsm")));
        Assert.True(File.Exists(Path.Combine(root, ProjectManifest.ManifestFileName)));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sourceSet, "notes.txt"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(root, "src", "SecondBook", "Other.bas")));
        Assert.True(File.Exists(Path.Combine(sourceSet, "modules", "Module1.bas")));
        Assert.DoesNotContain("#ExcludePublish", File.ReadAllText(Path.Combine(sourceSet, "modules", "Module1.bas"), Encoding.UTF8), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(sourceSet, "forms", "Dialog.frm")));
        Assert.Equal("frx", File.ReadAllText(Path.Combine(sourceSet, "forms", "Dialog.frx"), Encoding.UTF8));
        Assert.Equal("VERSION 1.0 CLASS", File.ReadAllText(Path.Combine(sourceSet, "NewModule.cls"), Encoding.UTF8));
    }

    [Fact]
    public void ExplicitWorkbookExportDefaultsToWorkingDirectoryWithoutCleaning()
    {
        using var temp = TempDirectory.Create();
        var explicitWorkbook = Path.Combine(temp.Path, "explicit.xlsm");
        File.WriteAllText(explicitWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Old.bas"), "old", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temp.Path, "Module1.bas"), "old module", Encoding.UTF8);
        Directory.CreateDirectory(Path.Combine(temp.Path, "forms"));
        File.WriteAllBytes(Path.Combine(temp.Path, "forms", "Dialog.frx"), [9, 9, 9]);
        var exporter = new FakeWorkbookModuleExporter(
            ("Module1.bas", "new"),
            ("Dialog.frm", "VERSION 5.00"),
            ("Dialog.frx", "frx"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--from", explicitWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([(explicitWorkbook, temp.Path)], exporter.Calls);
        Assert.Equal("old", File.ReadAllText(Path.Combine(temp.Path, "Old.bas"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(temp.Path, "Module1.bas"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(temp.Path, "forms", "Dialog.frx")));
        Assert.Equal("frx", File.ReadAllText(Path.Combine(temp.Path, "Dialog.frx"), Encoding.UTF8));
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
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(explicitWorkbook, call.WorkbookPath);
        Assert.NotEqual(explicitDestination, call.DestinationDirectory);
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
        WriteText(Path.Combine(explicitDestination, "nested", "Old.frm"), "old");
        WriteBytes(Path.Combine(explicitDestination, "nested", "Old.frx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(explicitDestination, "notes.txt"), "keep", Encoding.UTF8);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookModuleExporter: exporter);

        var result = application.Run(["export", "--to", explicitDestination]);

        Assert.Equal(0, result.ExitCode);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(explicitDestination, call.DestinationDirectory);
        Assert.False(File.Exists(Path.Combine(explicitDestination, "Old.bas")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "nested", "Old.frm")));
        Assert.False(File.Exists(Path.Combine(explicitDestination, "nested", "Old.frx")));
        Assert.True(Directory.Exists(Path.Combine(explicitDestination, "nested")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(explicitDestination, "notes.txt"), Encoding.UTF8));
        Assert.Equal("new", File.ReadAllText(Path.Combine(explicitDestination, "Module1.bas"), Encoding.UTF8));
    }

    [Fact]
    public void CleanupEnabledExportLeavesDestinationUntouchedWhenExporterFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var sourceSet = CreateDocumentSourceSet(root, "Book1");
        var binPath = CreateWorkbook(root, "bin", "Book1");
        WriteText(Path.Combine(sourceSet, "modules", "Old.bas"), "old");
        WriteBytes(Path.Combine(sourceSet, "forms", "Old.frx"), [1, 2, 3]);
        var exporter = new FakeWorkbookModuleExporter(("Module1.bas", "new"))
        {
            ThrowOnExport = true
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookModuleExporter: exporter);

        var result = application.Run(["export"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("export failed", result.StandardError, StringComparison.Ordinal);
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(binPath, call.WorkbookPath);
        Assert.NotEqual(sourceSet, call.DestinationDirectory);
        Assert.Equal("old", File.ReadAllText(Path.Combine(sourceSet, "modules", "Old.bas"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(sourceSet, "forms", "Old.frx")));
        Assert.False(File.Exists(Path.Combine(sourceSet, "Module1.bas")));
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
        var call = Assert.Single(exporter.Calls);
        Assert.Equal(relativeWorkbook, call.WorkbookPath);
        Assert.NotEqual(Path.Combine(workingDirectory, "out"), call.DestinationDirectory);
        Assert.Equal("new", File.ReadAllText(Path.Combine(workingDirectory, "out", "Module1.bas"), Encoding.UTF8));
    }

    private static string CreateDocumentSourceSet(string root, string documentName, params (string FileName, string Content)[] files)
    {
        var sourceSet = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceSet);
        File.WriteAllText(Path.Combine(sourceSet, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var file in files)
        {
            var filePath = Path.Combine(sourceSet, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, file.Content, Encoding.UTF8);
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

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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

    public bool ThrowOnExport { get; init; }

    public void ExportModules(string workbookPath, string destinationDirectory)
    {
        Calls.Add((workbookPath, destinationDirectory));
        if (ThrowOnExport)
        {
            throw new InvalidOperationException("export failed");
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var export in exports)
        {
            var exportPath = Path.Combine(destinationDirectory, export.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            File.WriteAllText(exportPath, export.Content, Encoding.UTF8);
        }
    }
}
