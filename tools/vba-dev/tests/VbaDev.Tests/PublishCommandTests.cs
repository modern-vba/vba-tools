using System.Text;
using VbaDev.App.Testing;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

public sealed class PublishCommandTests
{
    [Fact]
    public void PublishUsesSelectedDocumentPublishPathAndTemporaryGeneration()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        CreateWorkbookSource(root, "SecondBook", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule));
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Fails", "NG", "should not run"));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: automation,
            workbookTestRunner: runner);

        var result = application.Run(["publish", "--project", root, "--document", "SecondBook"]);

        Assert.Equal(0, result.ExitCode);
        var expectedPublish = Path.Combine(root, "publish", "SecondBook", "SecondBook.xlsm");
        Assert.True(File.Exists(expectedPublish));
        Assert.Equal("template:SecondBook", File.ReadAllText(expectedPublish, Encoding.UTF8));
        Assert.Single(automation.OpenedWorkbooks);
        Assert.NotEqual(expectedPublish, automation.OpenedWorkbooks[0]);
        Assert.Contains(Path.Combine(root, "publish", "SecondBook"), automation.OpenedWorkbooks[0], StringComparison.Ordinal);
        Assert.Equal(["remove:OldModule", "import:Local.bas", "save"], automation.Events);
        Assert.Empty(runner.Workbooks);
    }

    [Fact]
    public void PublishExcludesCommonModulesTestOnlyClassifications()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\nRuntime.bas\truntime\t\nLib_UnitTest.bas\ttest-foundation\tRuntime.bas\nWorkbookServiceTestDouble.cls\ttest-double\tRuntime.bas\n",
            Encoding.UTF8);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, commonModulesRepository));
        CreateWorkbookSource(
            root,
            "Book1",
            ("WorkbookServiceTestDouble.cls", "VERSION 1.0 CLASS"),
            ("Lib_UnitTest.bas", "Attribute VB_Name = \"Lib_UnitTest\""),
            ("Runtime.bas", "Attribute VB_Name = \"Runtime\""),
            ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Runtime.bas", "import:Local.bas", "save"], automation.Events);
    }

    [Fact]
    public void PublishExcludesProjectLocalMarkerNearTopWithoutFilenameOnlyTestPattern()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(
            root,
            "Book1",
            ("Hidden.bas", "Attribute VB_Name = \"Hidden\"\n'#ExcludePublish\nPublic Sub Hidden()\nEnd Sub\n"),
            ("Test_Local.bas", "Attribute VB_Name = \"Test_Local\"\nPublic Sub Test_StillPublishable()\nEnd Sub\n"),
            ("Keep.bas", "Attribute VB_Name = \"Keep\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["import:Keep.bas", "import:Test_Local.bas", "save"], automation.Events);
    }

    [Fact]
    public void PublishTreatsIncludedFormAndFrxAsOneSourceUnit()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Dialog.frm", "VERSION 5.00"));
        var frxPath = Path.Combine(root, "src", "Book1", "Dialog.frx");
        File.WriteAllBytes(frxPath, [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        var importedForm = Assert.Single(automation.ImportedSources);
        Assert.Equal(VbaSourceKind.Form, importedForm.Kind);
        Assert.Equal(Path.Combine(root, "src", "Book1", "Dialog.frm"), importedForm.SourcePath);
        Assert.Equal(frxPath, importedForm.BinaryPath);
    }

    [Fact]
    public void PublishNormalizesReferencesBeforeImportingSource()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("OldModule", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["publish"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "remove:OldModule",
                "import:Local.bas",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void PublishLeavesExistingOutputUntouchedWhenGenerationFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var publishPath = Path.Combine(root, "publish", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(publishPath)!);
        File.WriteAllText(publishPath, "old-publish", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation
        {
            ThrowOnImport = true
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old-publish", File.ReadAllText(publishPath, Encoding.UTF8));
        Assert.DoesNotContain(publishPath, automation.OpenedWorkbooks);
    }

    private static void CreateWorkbookSource(string root, string documentName, params (string FileName, string Content)[] sources)
    {
        var sourceDirectory = Path.Combine(root, "src", documentName);
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, $"{documentName}.xlsm"), $"template:{documentName}", Encoding.UTF8);
        foreach (var source in sources)
        {
            File.WriteAllText(Path.Combine(sourceDirectory, source.FileName), source.Content, Encoding.UTF8);
        }
    }
}
