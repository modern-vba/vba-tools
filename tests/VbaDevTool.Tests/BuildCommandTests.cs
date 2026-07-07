using System.Text;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using Xunit;

namespace VbaDevTools.Tests;

public sealed class BuildCommandTests
{
    [Fact]
    public void BuildUsesSelectedDocumentPathsAndFlushesImportableComponentsOnly()
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build", "--project", root, "--document", "SecondBook"]);

        Assert.Equal(0, result.ExitCode);
        var expectedBin = Path.Combine(root, "bin", "SecondBook", "SecondBook.xlsm");
        Assert.True(File.Exists(expectedBin));
        Assert.Equal("template:SecondBook", File.ReadAllText(expectedBin, Encoding.UTF8));
        Assert.Single(automation.OpenedWorkbooks);
        Assert.NotEqual(expectedBin, automation.OpenedWorkbooks[0]);
        Assert.Contains(Path.Combine(root, "bin", "SecondBook"), automation.OpenedWorkbooks[0], StringComparison.Ordinal);
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
    public void BuildLeavesExistingBinUntouchedWhenAutomationFails()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "old-bin", Encoding.UTF8);
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule))
        {
            ThrowOnImport = true
        };
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("import failed", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("old-bin", File.ReadAllText(binPath, Encoding.UTF8));
        Assert.DoesNotContain(binPath, automation.OpenedWorkbooks);
    }

    [Fact]
    public void BuildImportsCommonModulesInDependencyOrderThenLocalSourcesInStableOrder()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var commonModulesRepository = temp.CreateDirectory("common_modules_repo");
        File.WriteAllText(
            Path.Combine(commonModulesRepository, "common-modules-manifest.tsv"),
            "ModuleFile\tCategories\tDependencies\nFeature.bas\tRuntime\tBase.bas\nBase.bas\tRuntime\t\n",
            Encoding.UTF8);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, commonModulesRepository);
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(
            root,
            "Book1",
            ("Zeta.cls", "VERSION 1.0 CLASS"),
            ("Feature.bas", "Attribute VB_Name = \"Feature\""),
            ("Alpha.bas", "Attribute VB_Name = \"Alpha\""),
            ("Base.bas", "Attribute VB_Name = \"Base\""));
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "import:Base.bas",
                "import:Feature.bas",
                "import:Alpha.bas",
                "import:Zeta.cls",
                "save"
            ],
            automation.Events);
    }

    [Fact]
    public void BuildTreatsFormAndMatchingFrxAsOneSourceUnit()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Dialog.frm", "VERSION 5.00"));
        var frxPath = Path.Combine(root, "src", "Book1", "Dialog.frx");
        File.WriteAllBytes(frxPath, [1, 2, 3]);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        var importedForm = Assert.Single(automation.ImportedSources);
        Assert.Equal(VbaSourceKind.Form, importedForm.Kind);
        Assert.Equal(Path.Combine(root, "src", "Book1", "Dialog.frm"), importedForm.SourcePath);
        Assert.Equal(frxPath, importedForm.BinaryPath);
    }

    [Fact]
    public void BuildReportsLockedTargetWithoutOpeningTargetWorkbook()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var binPath = Path.Combine(root, "bin", "Book1", "Book1.xlsm");
        Directory.CreateDirectory(Path.GetDirectoryName(binPath)!);
        File.WriteAllText(binPath, "locked-bin", Encoding.UTF8);
        using var lockStream = new FileStream(binPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var automation = new FakeWorkbookBuildAutomation();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Target workbook is locked or unavailable", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(binPath, automation.OpenedWorkbooks);
        Assert.Single(automation.OpenedWorkbooks);
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

internal sealed class FakeWorkbookBuildAutomation : IWorkbookBuildAutomation
{
    private readonly IReadOnlyList<WorkbookModule> modules;

    public FakeWorkbookBuildAutomation(params WorkbookModule[] modules)
    {
        this.modules = modules;
    }

    public bool ThrowOnImport { get; init; }

    public List<string> OpenedWorkbooks { get; } = [];

    public List<string> Events { get; } = [];

    public List<VbaSourceFile> ImportedSources { get; } = [];

    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
    {
        OpenedWorkbooks.Add(workbookPath);
        return new FakeWorkbookBuildSession(this, modules);
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

        public IReadOnlyList<WorkbookModule> GetModules() => modules;

        public void RemoveModule(string moduleName)
        {
            owner.Events.Add($"remove:{moduleName}");
        }

        public void ImportModule(VbaSourceFile sourceFile)
        {
            if (owner.ThrowOnImport)
            {
                throw new InvalidOperationException("import failed");
            }

            owner.ImportedSources.Add(sourceFile);
            owner.Events.Add($"import:{Path.GetFileName(sourceFile.SourcePath)}");
        }

        public void Save()
        {
            owner.Events.Add("save");
        }

        public void Dispose()
        {
        }
    }
}
