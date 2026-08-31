using System.Text;
using System.Runtime.InteropServices;
using VbaDev.App.Build;
using VbaDev.App.References;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class BuildCommandTests
{
    [Fact]
    public void BuildRejectsOverlappingDocumentSourceRootsBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifestTestData.TwoDocumentManifest(root);
        manifest.Documents["SecondBook"] = manifest.Documents["SecondBook"] with
        {
            SourcePath = "src/Book1"
        };
        File.WriteAllBytes(
            Path.Combine(root, ProjectManifest.ManifestFileName),
            ProjectManifestCanonicalSerializer.SerializeToUtf16LeBytes(manifest));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("document source roots overlap", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Book1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("SecondBook", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public async Task BuildUsesSelectedDocumentPathsAndFlushesImportableComponentsOnly()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        CreateWorkbookSource(root, "SecondBook", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(
            new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule),
            new WorkbookModule("Class1", WorkbookModuleKind.ClassModule),
            new WorkbookModule("Form1", WorkbookModuleKind.Form),
            new WorkbookModule("ThisWorkbook", WorkbookModuleKind.Document));
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                workbookBuildAutomation: automation));
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        var exitCode = await application.InvokeAsync(
            ["build", "--project", root, "--document", "SecondBook"],
            standardOutput,
            standardError,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(standardError.ToString());
        var expectedBin = Path.Combine(root, "bin", "SecondBook.xlsm");
        Assert.True(File.Exists(expectedBin));
        Assert.Equal("template:SecondBook", File.ReadAllText(expectedBin, Encoding.UTF8));
        Assert.Single(automation.OpenedWorkbooks);
        Assert.NotEqual(expectedBin, automation.OpenedWorkbooks[0]);
        Assert.Contains(Path.Combine(root, "bin"), automation.OpenedWorkbooks[0], StringComparison.Ordinal);
        Assert.Equal(
            [
                "remove:Standard1",
                "remove:Class1",
                "remove:Form1",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void SnapshotBuildUsesCallerInventoryAndExplicitOutputWithoutReadingPersistentSource()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var persistentSourcePath = Path.Combine(root, "missing-source", "Book1");
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        var publishPath = Path.Combine(root, "publish", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = new ProjectDocument(
            ProjectDocument.ExcelKind,
            Path.GetRelativePath(root, persistentSourcePath),
            Path.GetRelativePath(root, templatePath),
            Path.GetRelativePath(root, binPath),
            Path.GetRelativePath(root, publishPath),
            commonModules: [],
            references: [new VbaProjectReference("Snapshot Reference")]);
        new JsonProjectManifestStore().Save(root, manifest);
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        var templateBytes = Encoding.UTF8.GetBytes("snapshot-template");
        File.WriteAllBytes(templatePath, templateBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "existing-bin", Encoding.UTF8);
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        File.WriteAllText(publishPath, "existing-publish", Encoding.UTF8);
        var snapshotPath = temp.CreateDirectory("snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "nested", "Snapshot.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(snapshotSourcePath)!);
        File.WriteAllText(
            snapshotSourcePath,
            "Attribute VB_Name = \"Snapshot\"",
            Encoding.UTF8);
        var outputPath = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous-output", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        automation.AdoptedReferenceNamespaces["Snapshot Reference"] = "SnapshotReference";
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                new ResolvedVbaProjectReference(
                    "Snapshot Reference",
                    "{11111111-1111-1111-1111-111111111111}",
                    1,
                    0)));

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Equal("snapshot-template", File.ReadAllText(outputPath, Encoding.UTF8));
        Assert.Equal("existing-bin", File.ReadAllText(binPath, Encoding.UTF8));
        Assert.Equal("existing-publish", File.ReadAllText(publishPath, Encoding.UTF8));
        Assert.Equal(templateBytes, File.ReadAllBytes(templatePath));
        Assert.False(Directory.Exists(persistentSourcePath));
        Assert.Equal(
            "Attribute VB_Name = \"Snapshot\"",
            File.ReadAllText(snapshotSourcePath, Encoding.UTF8));
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));
        Assert.Equal(
            [
                "add-ref:Snapshot Reference",
                "import:Snapshot.bas",
                "save"
            ],
            automation.Events);
        var importedSource = Assert.Single(automation.ImportedSources);
        Assert.DoesNotContain(snapshotPath, importedSource.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(importedSource.SourcePath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(outputPath)!,
            ".Book1.*.tmp.xlsm",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("--source-snapshot")]
    [InlineData("--output")]
    public void SnapshotBuildOptionsMustBeSuppliedTogether(string suppliedOption)
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var optionValue = suppliedOption == "--source-snapshot"
            ? temp.CreateDirectory("snapshot")
            : Path.Combine(temp.Path, "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(["build", suppliedOption, optionValue]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--source-snapshot", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--output", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void SnapshotBuildRejectsCaseInsensitiveFlatSourceNameCollisionsBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        var firstPath = Path.Combine(snapshotPath, "feature", "Shared.bas");
        var secondPath = Path.Combine(snapshotPath, "legacy", "shared.bas");
        Directory.CreateDirectory(Path.GetDirectoryName(firstPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        File.WriteAllText(firstPath, "Attribute VB_Name = \"Shared\"", Encoding.UTF8);
        File.WriteAllText(secondPath, "Attribute VB_Name = \"shared\"", Encoding.UTF8);
        var outputPath = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Shared.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("feature", "Shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("legacy", "shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void SnapshotBuildReportsIdentityConflictsInDeterministicFlatFileNameOrder()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        var zetaPath = Path.Combine(snapshotPath, "Zeta.bas");
        var alphaPath = Path.Combine(snapshotPath, "nested", "Alpha.bas");
        File.WriteAllText(
            zetaPath,
            "Attribute VB_Name = \"CollisionName\"\r\n",
            Encoding.UTF8);
        Directory.CreateDirectory(Path.GetDirectoryName(alphaPath)!);
        File.WriteAllText(
            alphaPath,
            "Attribute VB_Name = \"collisionname\"\r\n",
            Encoding.UTF8);
        var alphaBytes = File.ReadAllBytes(alphaPath);
        var zetaBytes = File.ReadAllBytes(zetaPath);
        var outputPath = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        File.WriteAllText(outputPath, "previous-output", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        var alphaIndex = result.StandardError.IndexOf(alphaPath, StringComparison.OrdinalIgnoreCase);
        var zetaIndex = result.StandardError.IndexOf(zetaPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(alphaIndex >= 0);
        Assert.True(zetaIndex > alphaIndex);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal("previous-output", File.ReadAllText(outputPath, Encoding.UTF8));
        Assert.Equal(alphaBytes, File.ReadAllBytes(alphaPath));
        Assert.Equal(zetaBytes, File.ReadAllBytes(zetaPath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputInsideCallerSnapshotBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "Module1.bas");
        var snapshotBytes = new UTF8Encoding(false).GetBytes(
            "Attribute VB_Name = \"Module1\"\r\n");
        File.WriteAllBytes(snapshotSourcePath, snapshotBytes);
        var outputPath = Path.Combine(snapshotPath, "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("snapshot", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(File.Exists(outputPath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotSourcePath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputInsideAnyManifestDocumentSourceSetBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifestTestData.TwoDocumentManifest(root));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var outputPath = Path.Combine(
            root,
            "src",
            "SecondBook",
            "caller-output.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("source set", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputThatIsAManifestTemplateBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sourceSetPath = Path.Combine(root, "source", "Book1");
        var templatePath = Path.Combine(root, "templates", "Book1.xlsm");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"] = new ProjectDocument(
            ProjectDocument.ExcelKind,
            Path.GetRelativePath(root, sourceSetPath),
            Path.GetRelativePath(root, templatePath),
            Path.Combine("bin", "Book1.xlsm"),
            Path.Combine("publish", "Book1.xlsm"),
            commonModules: [],
            references: []);
        new JsonProjectManifestStore().Save(root, manifest);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        var templateBytes = Encoding.UTF8.GetBytes("source-template");
        File.WriteAllBytes(templatePath, templateBytes);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            templatePath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("template", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(templateBytes, File.ReadAllBytes(templatePath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputThatIsTheProjectManifestBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var manifestPath = Path.Combine(root, ProjectManifest.ManifestFileName);
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            manifestPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("manifest", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputThatIsAManifestBinWorkbookBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        var binBytes = Encoding.UTF8.GetBytes("persistent-bin");
        File.WriteAllBytes(binPath, binBytes);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            binPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("bin", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(binBytes, File.ReadAllBytes(binPath));
    }

    [Fact]
    public void SnapshotBuildRejectsOutputThatIsAManifestPublishWorkbookBeforeExcel()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var publishPath = Path.Combine(root, "publish", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        var publishBytes = Encoding.UTF8.GetBytes("persistent-publish");
        File.WriteAllBytes(publishPath, publishBytes);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            publishPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("publish", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(publishBytes, File.ReadAllBytes(publishPath));
    }

    [Fact]
    public void SnapshotBuildRejectsMissingOutputBelowFilesystemAliasToSnapshotBeforeExcel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var aliasPath = Path.Combine(temp.Path, "snapshot-alias");
        Directory.CreateSymbolicLink(aliasPath, snapshotPath);
        var outputPath = Path.Combine(aliasPath, "missing", "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("snapshot", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(Directory.Exists(Path.Combine(snapshotPath, "missing")));
    }

    [Fact]
    public void SnapshotBuildFailurePreservesPreviousCallerOutputAndCallerSnapshot()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "Module1.bas");
        var snapshotBytes = new UTF8Encoding(false).GetBytes(
            "Attribute VB_Name = \"Module1\"\r\n");
        File.WriteAllBytes(snapshotSourcePath, snapshotBytes);
        var outputDirectory = temp.CreateDirectory("caller-output");
        var outputPath = Path.Combine(outputDirectory, "Book1.xlsm");
        var outputBytes = Encoding.UTF8.GetBytes("previous-output");
        File.WriteAllBytes(outputPath, outputBytes);
        var automation = new FakeWorkbookBuildAutomation
        {
            ThrowOnImport = true
        };
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(outputBytes, File.ReadAllBytes(outputPath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotSourcePath));
        Assert.Empty(Directory.EnumerateFiles(
            outputDirectory,
            ".Book1.*.tmp.xlsm",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SnapshotBuildCancellationPreservesPreviousCallerOutputAndCallerSnapshot()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        var snapshotSourcePath = Path.Combine(snapshotPath, "Module1.bas");
        var snapshotBytes = new UTF8Encoding(false).GetBytes(
            "Attribute VB_Name = \"Module1\"\r\n");
        File.WriteAllBytes(snapshotSourcePath, snapshotBytes);
        var outputDirectory = temp.CreateDirectory("caller-output");
        var outputPath = Path.Combine(outputDirectory, "Book1.xlsm");
        var outputBytes = Encoding.UTF8.GetBytes("previous-output");
        File.WriteAllBytes(outputPath, outputBytes);
        using var cancellation = new CancellationTokenSource();
        var automation = new FakeWorkbookBuildAutomation
        {
            OnImport = cancellation.Cancel
        };
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = await application.RunAsync(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ], cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        Assert.Contains("cancel", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(outputBytes, File.ReadAllBytes(outputPath));
        Assert.Equal(snapshotBytes, File.ReadAllBytes(snapshotSourcePath));
        Assert.Empty(Directory.EnumerateFiles(
            outputDirectory,
            ".Book1.*.tmp.xlsm",
            SearchOption.TopDirectoryOnly));
        var importedSource = Assert.Single(automation.ImportedSources);
        Assert.False(File.Exists(importedSource.SourcePath));
    }

    [Fact]
    public void SnapshotBuildFailsClosedWhenOutputAliasCannotBeResolved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var missingTargetPath = Path.Combine(temp.Path, "missing-alias-target");
        var brokenAliasPath = Path.Combine(temp.Path, "broken-alias");
        Directory.CreateSymbolicLink(brokenAliasPath, missingTargetPath);
        var outputPath = Path.Combine(brokenAliasPath, "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Filesystem-canonical path identity",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.False(Directory.Exists(missingTargetPath));
    }

    [Fact]
    public void SnapshotBuildRejectsFilesystemAliasToManifestBinWorkbookBeforeExcel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var binDirectory = Path.Combine(root, "bin");
        Directory.CreateDirectory(binDirectory);
        var binPath = Path.Combine(binDirectory, "Book1.xlsm");
        var binBytes = Encoding.UTF8.GetBytes("persistent-bin");
        File.WriteAllBytes(binPath, binBytes);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var aliasPath = Path.Combine(temp.Path, "bin-alias");
        Directory.CreateSymbolicLink(aliasPath, binDirectory);
        var outputPath = Path.Combine(aliasPath, "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("bin", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(binBytes, File.ReadAllBytes(binPath));
    }

    [Fact]
    public void SnapshotBuildRejectsHardLinkAliasToManifestBinWorkbookBeforeExcel()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        var binBytes = Encoding.UTF8.GetBytes("persistent-bin");
        File.WriteAllBytes(binPath, binBytes);
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var outputPath = Path.Combine(
            temp.CreateDirectory("caller-output"),
            "Book1.xlsm");
        Assert.True(
            CreateHardLinkW(outputPath, binPath, IntPtr.Zero),
            $"Could not create test hard link. Win32 error: {Marshal.GetLastWin32Error()}");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("bin", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(binBytes, File.ReadAllBytes(binPath));
    }

    [Fact]
    public void SnapshotBuildAllowsCallerOutputInSafeSiblingWithSnapshotNamePrefix()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1");
        var snapshotPath = temp.CreateDirectory("snapshot");
        File.WriteAllText(
            Path.Combine(snapshotPath, "Module1.bas"),
            "Attribute VB_Name = \"Module1\"",
            Encoding.UTF8);
        var outputDirectory = temp.CreateDirectory("snapshot-copy");
        var outputPath = Path.Combine(outputDirectory, "Book1.xlsm");
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);

        var result = application.Run(
        [
            "build",
            "--source-snapshot",
            snapshotPath,
            "--output",
            outputPath
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.True(File.Exists(outputPath));
        Assert.Single(automation.OpenedWorkbooks);
    }

    [Fact]
    public void BuildLeavesExistingBinUntouchedWhenAutomationFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "old-bin", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule))
        {
            ThrowOnImport = true
        };
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old-bin", File.ReadAllText(binPath, Encoding.UTF8));
        Assert.DoesNotContain(binPath, automation.OpenedWorkbooks);
    }

    [Fact]
    public void CancelledGenerationDeletesItsTemporaryWorkbookAndPreservesThePreviousBin()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var sourcePath = Path.Combine(root, "DebugModule.bas");
        var templatePath = Path.Combine(root, "Template.xlsm");
        var binDirectory = Path.Combine(root, "bin");
        var binPath = Path.Combine(binDirectory, "Book1.xlsm");
        File.WriteAllText(sourcePath, "Attribute VB_Name = \"DebugModule\"", Encoding.UTF8);
        File.WriteAllText(templatePath, "new-template", Encoding.UTF8);
        Directory.CreateDirectory(binDirectory);
        File.WriteAllText(binPath, "last-known-good", Encoding.UTF8);
        using var cancellation = new CancellationTokenSource();
        var automation = new FakeWorkbookBuildAutomation
        {
            OnImport = cancellation.Cancel
        };
        var pipeline = new WorkbookGenerationPipeline(
            automation,
            new WorkbookReferenceNormalizer(
                new VbaProjectReferencePlanner(new FakeVbaProjectReferenceResolver())));

        Assert.ThrowsAny<OperationCanceledException>(() => pipeline.Generate(
            "Book1",
            templatePath,
            binPath,
            [],
            [new VbaSourceFile(sourcePath, VbaSourceKind.StandardModule, null)],
            cancellation.Token));

        Assert.Equal("last-known-good", File.ReadAllText(binPath, Encoding.UTF8));
        Assert.Empty(Directory.EnumerateFiles(
            binDirectory,
            ".Book1.*.tmp.xlsm",
            SearchOption.TopDirectoryOnly));
        Assert.DoesNotContain("save", automation.Events);
    }

    [Fact]
    public void BuildImportsRecordedCommonModulesIncludingTestOnlyInManifestOrderWithoutRepositoryLookup()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            "malformed repository metadata",
            Encoding.UTF8);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, commonModulesRepository);
        manifest.Documents["Book1"].CommonModules.AddRange(
        [
            new InstalledCommonModule("Base", "Base.bas", Requested: false, TestOnly: false, Orphaned: true),
            new InstalledCommonModule("Feature", "Feature.bas", Requested: true, TestOnly: true, Orphaned: true)
        ]);
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(
            root,
            "Book1",
            (Path.Combine("nested", "Zeta.cls"), "VERSION 1.0 CLASS\r\nAttribute VB_Name = \"Zeta\""),
            (Path.Combine("shared", "Feature.bas"), "Attribute VB_Name = \"Feature\""),
            ("Alpha.bas", "Attribute VB_Name = \"Alpha\""),
            (Path.Combine("shared", "Base.bas"), "Attribute VB_Name = \"Base\""),
            (Path.Combine("forms", "Dialog.frm"), "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\""));
        File.WriteAllBytes(Path.Combine(root, "src", "Book1", "forms", "Orphan.frx"), [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "import:Base.bas",
                "import:Feature.bas",
                "import:Alpha.bas",
                "import:Dialog.frm",
                "import:Zeta.cls",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void BuildLeavesMissingRecordedCommonModuleSourceForDoctorAndImportsAvailableSources()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].CommonModules.Add(
            new InstalledCommonModule("Missing", "Missing.bas", Requested: true, TestOnly: false));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Local.bas", "save"], automation.Events);
    }

    [Fact]
    public void BuildFailsBeforeSourceImportWhenNestedSourceNamesCollide()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            (Path.Combine("feature", "Shared.bas"), "Attribute VB_Name = \"Shared\""),
            (Path.Combine("legacy", "shared.bas"), "Attribute VB_Name = \"shared\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Shared.bas", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("feature", "Shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("legacy", "shared.bas"), result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
    }

    [Fact]
    public void BuildTreatsFormAndMatchingFrxAsOneSourceUnit()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Dialog.frm", "VERSION 5.00\r\nBegin VB.Form Dialog\r\nEnd\r\nAttribute VB_Name = \"Dialog\""));
        var frxPath = Path.Combine(root, "src", "Book1", "Dialog.frx");
        File.WriteAllBytes(frxPath, [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        var importedForm = Assert.Single(automation.ImportedSources);
        Assert.Equal(VbaSourceKind.Form, importedForm.Kind);
        Assert.Equal("Dialog.frm", importedForm.FileName);
        Assert.NotEqual(Path.Combine(root, "src", "Book1", "Dialog.frm"), importedForm.SourcePath);
        Assert.NotNull(importedForm.BinaryPath);
        Assert.Equal(Path.GetDirectoryName(importedForm.SourcePath), Path.GetDirectoryName(importedForm.BinaryPath));
        Assert.Equal("Dialog.frx", Path.GetFileName(importedForm.BinaryPath));
        Assert.False(File.Exists(importedForm.SourcePath));
        Assert.False(File.Exists(importedForm.BinaryPath));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(frxPath));
    }

    [Fact]
    public void BuildRemovesReplaceableModulesBeforeNormalizingReferencesAndImportingSource()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true, NamespaceName: "UnlistedLibrary"));
        automation.AdoptedReferenceNamespaces["Microsoft Scripting Runtime"] = "Scripting";
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "remove:Standard1",
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void BuildTreatsExistingDesiredWorkbookReferencesAsSatisfiedBeforeRegistryResolution()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("OLE Automation"));
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("OLE Automation", IsRemovable: false, NamespaceName: "stdole"));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true, NamespaceName: "UnlistedLibrary"));
        automation.AdoptedReferenceNamespaces["Microsoft Scripting Runtime"] = "Scripting";
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 1, 0),
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 2, 0),
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("OLE Automation", resolver.RequestedNames);
        Assert.Contains("Microsoft Scripting Runtime", resolver.RequestedNames);
        Assert.Equal(
            [
                "remove:Standard1",
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void BuildFailsBeforeSourceImportWhenManifestReferenceIsMissing()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Missing Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver());

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Book1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Missing Library", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
    }

    [Fact]
    public void BuildReportsComReferenceErrorsAsUsageErrors()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation
        {
            ReferenceError = new COMException("0x800A801C", unchecked((int)0x800A801C))
        };
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Excel COM build automation failed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("coding agent", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("outside the sandbox", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("import:", automation.Events);
    }

    [Fact]
    public void BuildFailsBeforeSourceImportWhenManifestReferenceIsAmbiguous()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Ambiguous Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Ambiguous Library", "{11111111-1111-1111-1111-111111111111}", 1, 0),
            new ResolvedVbaProjectReference("Ambiguous Library", "{22222222-2222-2222-2222-222222222222}", 1, 0));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Ambiguous Library", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("ambiguous", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("import:", automation.Events);
    }

    [Fact]
    public void BuildWarnsWhenUnlistedProtectedReferenceRemains()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        automation.References.Add(new WorkbookReference("Protected Library", IsRemovable: false, NamespaceName: "ProtectedLibrary"));
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("WARN", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Book1/Protected Library", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildKeepsSuccessOutputExactAndEmitsRecasingWarningsOnStandardError()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(
            root,
            ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        automation.References.Add(new WorkbookReference(
            "Protected Library",
            IsRemovable: false,
            NamespaceName: "ProtectedLibrary"));
        var application = CommandLineTestFactory.Create(
            root,
            workbookBuildAutomation: automation);
        var baseline = application.Run(["build"]);
        automation.VerificationReport = new VbeImportVerificationReport(
        [
            new VbeIdentifierRecasingWarning(
                "Local",
                [
                    new VbeIdentifierRecasingPair("FileName", "Filename"),
                    new VbeIdentifierRecasingPair("OtherName", "Othername")
                ])
        ]);

        var warned = application.Run(["build"]);

        Assert.Equal(0, warned.ExitCode);
        Assert.Equal(baseline.StandardOutput, warned.StandardOutput);
        Assert.Contains(
            "Book1/Protected Library",
            warned.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, baseline.StandardError);
        Assert.Equal(
            "[WARN] vbeIdentifierRecased: Imported component 'Local' identifier casing " +
            "(source -> VBE): 'FileName' -> 'Filename'; 'OtherName' -> 'Othername'." +
            Environment.NewLine,
            warned.StandardError);
    }

    [Fact]
    public void BuildReportsLockedTargetWithoutOpeningTargetWorkbook()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var binPath = Path.Combine(root, "bin", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "locked-bin", Encoding.UTF8);
        using var lockStream = new FileStream(binPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var automation = new FakeWorkbookBuildAutomation();
        var application = CommandLineTestFactory.Create(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Target workbook is locked or unavailable", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(binPath, automation.OpenedWorkbooks);
        Assert.Single(automation.OpenedWorkbooks);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static void CreateWorkbookSource(string root, string documentName, params (string FileName, string Content)[] sources)
    {
        var sourceDirectory = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var source in sources)
        {
            var sourcePath = Path.Combine(sourceDirectory, source.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, source.Content, Encoding.UTF8);
        }
    }
}

internal sealed class FakeWorkbookBuildAutomation : IWorkbookBuildAutomation
{
    private readonly IReadOnlyList<WorkbookModule> modules;
    private readonly TaskCompletionSource cancelableOpenStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeWorkbookBuildAutomation(params WorkbookModule[] modules)
    {
        this.modules = modules;
    }

    public bool ThrowOnImport { get; init; }

    public bool ThrowOnRemove { get; init; }

    public bool ThrowOnVerify { get; init; }

    public bool ThrowOnSave { get; init; }

    public bool WaitForCancellationOnOpen { get; init; }

    public bool CancellationRequestedAtOpen { get; private set; }

    public bool CancellationObserved { get; private set; }

    public Task CancelableOpenStarted => cancelableOpenStarted.Task;

    public Action? OnImport { get; set; }

    public COMException? ReferenceError { get; init; }

    public string ProjectName { get; init; } = "VbaProject";

    public List<string> OpenedWorkbooks { get; } = [];

    public List<string> Events { get; } = [];

    public List<VbeImportSourceFile> ImportedSources { get; } = [];

    public List<WorkbookReference> References { get; } = [];

    public Dictionary<string, string> AdoptedReferenceNamespaces { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public VbeImportVerificationReport VerificationReport { get; set; } =
        VbeImportVerificationReport.Empty;

    public int VerifyCalls { get; private set; }

    public int SaveCalls { get; private set; }

    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
    {
        OpenedWorkbooks.Add(workbookPath);
        return new FakeWorkbookBuildSession(this, modules);
    }

    public IWorkbookBuildSession OpenWorkbook(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        CancellationRequestedAtOpen = cancellationToken.IsCancellationRequested;
        if (WaitForCancellationOnOpen)
        {
            cancelableOpenStarted.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)))
            {
                throw new InvalidOperationException("Import did not observe cancellation.");
            }

            CancellationObserved = true;
            throw new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen),
                cancellationToken);
        }

        return OpenWorkbook(workbookPath);
    }

    private sealed class FakeWorkbookBuildSession : IWorkbookBuildSession
    {
        private readonly FakeWorkbookBuildAutomation owner;
        private readonly IReadOnlyList<WorkbookModule> modules;

        public FakeWorkbookBuildSession(FakeWorkbookBuildAutomation owner, IReadOnlyList<WorkbookModule> modules)
        {
            this.owner = owner;
            this.modules = modules;
        }

        public string GetProjectName() => owner.ProjectName;

        public IReadOnlyList<WorkbookModule> GetModules() => modules;

        public IReadOnlyList<WorkbookReference> GetReferences()
        {
            if (owner.ReferenceError is not null)
            {
                throw owner.ReferenceError;
            }

            return owner.References;
        }

        public bool RemoveReference(string referenceName)
        {
            var reference = owner.References.FirstOrDefault(item => item.Name.Equals(referenceName, StringComparison.OrdinalIgnoreCase));
            if (reference is null || !reference.IsRemovable)
            {
                return false;
            }

            owner.References.Remove(reference);
            owner.Events.Add($"remove-ref:{reference.Name}");
            return true;
        }

        public void AddReference(ResolvedVbaProjectReference reference)
        {
            owner.AdoptedReferenceNamespaces.TryGetValue(
                reference.Name,
                out var namespaceName);
            owner.References.Add(new WorkbookReference(
                reference.Name,
                IsRemovable: true,
                NamespaceName: namespaceName));
            owner.Events.Add($"add-ref:{reference.Name}");
        }

        public void RemoveModule(string moduleName)
        {
            if (owner.ThrowOnRemove)
            {
                throw new InvalidOperationException("remove failed");
            }

            owner.Events.Add($"remove:{moduleName}");
        }

        public void ImportModule(VbeImportSourceFile sourceFile)
        {
            if (owner.ThrowOnImport)
            {
                throw new InvalidOperationException("import failed");
            }

            owner.ImportedSources.Add(sourceFile);
            owner.Events.Add($"import:{Path.GetFileName(sourceFile.SourcePath)}");
            owner.OnImport?.Invoke();
        }

        public void ExportModule(string moduleName, string destinationPath)
            => throw new NotSupportedException();

        public VbeImportVerificationReport VerifyImportedModules()
        {
            owner.VerifyCalls++;
            if (owner.ThrowOnVerify)
            {
                throw new InvalidOperationException("verification failed");
            }

            return owner.VerificationReport;
        }

        public void Save()
        {
            owner.SaveCalls++;
            if (owner.ThrowOnSave)
            {
                throw new InvalidOperationException("save failed");
            }

            owner.Events.Add("save");
        }

        public void Dispose()
        {
        }
    }
}
