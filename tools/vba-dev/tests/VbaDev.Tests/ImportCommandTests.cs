using System.Text;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using Xunit;

namespace VbaDev.Tests;

public sealed class ImportCommandTests
{
    [Fact]
    public void ImportCommandDoesNotRequireProjectContextAndSavesAfterSuccessfulImport()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(
            new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule),
            new WorkbookModule("OldClass", WorkbookModuleKind.ClassModule),
            new WorkbookModule("OldForm", WorkbookModuleKind.Form),
            new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document),
            new WorkbookModule("Sheet1", WorkbookModuleKind.Document),
            new WorkbookModule("Other", WorkbookModuleKind.Other));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Imported 1 source file", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(sourceDirectory, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(targetWorkbook, result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal([targetWorkbook], automation.OpenedWorkbooks);
        Assert.Equal(
            [
                "remove:OldModule",
                "remove:OldClass",
                "remove:OldForm",
                "import:Module1.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void ImportCommandResolvesRelativePathsFromWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var workingDirectory = temp.CreateDirectory("work");
        var sourceDirectory = Path.Combine(workingDirectory, "src");
        var targetWorkbook = Path.Combine(workingDirectory, "target.xlsm");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(workingDirectory, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", "src", "--to", "target.xlsm"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([targetWorkbook], automation.OpenedWorkbooks);
        var importedSource = Assert.Single(automation.ImportedSources);
        Assert.Equal(Path.Combine(sourceDirectory, "Module1.bas"), importedSource.SourcePath);
    }

    [Fact]
    public void ImportCommandSelectsTopLevelSourcesInStableNameOrderAndPairsFormFrx()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var nestedDirectory = Path.Combine(sourceDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Zeta.cls"), "VERSION 1.0 CLASS", Encoding.UTF8);
        File.WriteAllText(Path.Combine(sourceDirectory, "Dialog.frm"), "VERSION 5.00", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "Dialog.frx"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(sourceDirectory, "Alpha.bas"), "Attribute VB_Name = \"Alpha\"", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "Orphan.frx"), [9, 9, 9]);
        File.WriteAllText(Path.Combine(nestedDirectory, "Nested.bas"), "Attribute VB_Name = \"Nested\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["Alpha.bas", "Dialog.frm", "Zeta.cls"], automation.ImportedSources.Select(source => source.FileName));
        var importedForm = Assert.Single(automation.ImportedSources, source => source.Kind == VbaSourceKind.Form);
        Assert.Equal(Path.Combine(sourceDirectory, "Dialog.frx"), importedForm.BinaryPath);
    }

    [Fact]
    public void ImportCommandDoesNotApplyBuildOrPublishManifestBehavior()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(
            Path.Combine(sourceDirectory, "Excluded.bas"),
            "'#ExcludePublish\nAttribute VB_Name = \"Excluded\"",
            Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Excluded.bas", "save"], automation.Events);
        Assert.Contains("Unlisted Library", automation.References.Select(reference => reference.Name));
    }

    [Theory]
    [InlineData(new[] { "import", "--to", "target.xlsm" }, "--from is required.")]
    [InlineData(new[] { "import", "--from", "src" }, "--to is required.")]
    [InlineData(new[] { "import", "--from=", "--to", "target.xlsm" }, "--from requires a source directory path.")]
    [InlineData(new[] { "import", "--from", "src", "--to=" }, "--to requires a target workbook path.")]
    [InlineData(new[] { "import", "-f", "src", "--to", "target.xlsm" }, "Unknown option '-f'")]
    [InlineData(new[] { "import", "--from", "src", "-t", "target.xlsm" }, "Unknown option '-t'")]
    [InlineData(new[] { "import", "--from", "src", "--to", "target.xlsm", "--project", "." }, "Unknown option '--project'")]
    [InlineData(new[] { "import", "--from", "src", "--to", "target.xlsm", "--document", "Book1" }, "Unknown option '--document'")]
    public void ImportCommandRejectsInvalidArgumentContract(string[] args, string expectedError)
    {
        using var temp = TempDirectory.Create();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        var result = application.Run(args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportCommandRejectsInvalidSourceAndTargetPathsBeforeOpeningWorkbook()
    {
        using var temp = TempDirectory.Create();
        var validSource = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        var sourceFile = Path.Combine(temp.Path, "Source.bas");
        var targetDirectory = temp.CreateDirectory("target-dir");
        File.WriteAllText(Path.Combine(validSource, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        File.WriteAllText(sourceFile, "Attribute VB_Name = \"Source\"", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var missingSource = application.Run(["import", "--from", Path.Combine(temp.Path, "missing-src"), "--to", targetWorkbook]);
        var fileSource = application.Run(["import", "--from", sourceFile, "--to", targetWorkbook]);
        var missingTarget = application.Run(["import", "--from", validSource, "--to", Path.Combine(temp.Path, "missing.xlsm")]);
        var directoryTarget = application.Run(["import", "--from", validSource, "--to", targetDirectory]);

        Assert.Equal(1, missingSource.ExitCode);
        Assert.Contains("Import source directory was not found", missingSource.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, fileSource.ExitCode);
        Assert.Contains($"Import source path is not a directory: {sourceFile}", fileSource.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, missingTarget.ExitCode);
        Assert.Contains("Import target workbook was not found", missingTarget.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, directoryTarget.ExitCode);
        Assert.Contains($"Import target workbook is not a file: {targetDirectory}", directoryTarget.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void ImportCommandFailsBeforeOpeningWorkbookWhenNoImportableSourcesExist()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "notes.txt"), "notes", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "Orphan.frx"), [1, 2, 3]);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"No importable VBA source files were found in: {sourceDirectory}", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void ImportCommandDoesNotSaveWhenFlushFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule))
        {
            ThrowOnRemove = true
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("remove failed", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void ImportCommandDoesNotSaveWhenImportFails()
    {
        using var temp = TempDirectory.Create();
        var sourceDirectory = temp.CreateDirectory("src");
        var targetWorkbook = Path.Combine(temp.Path, "target.xlsm");
        File.WriteAllText(Path.Combine(sourceDirectory, "Module1.bas"), "Attribute VB_Name = \"Module1\"", Encoding.UTF8);
        File.WriteAllText(targetWorkbook, "workbook", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule))
        {
            ThrowOnImport = true
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path, workbookBuildAutomation: automation);

        var result = application.Run(["import", "--from", sourceDirectory, "--to", targetWorkbook]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("remove:OldModule", automation.Events);
        Assert.DoesNotContain("save", automation.Events);
    }
}
