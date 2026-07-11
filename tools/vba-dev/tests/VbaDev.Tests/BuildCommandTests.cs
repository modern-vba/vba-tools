using System.Text;
using System.Runtime.InteropServices;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using Xunit;

namespace VbaDev.Tests;

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
    public void BuildNormalizesReferencesBeforeFlushingAndImportingSource()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        new JsonProjectManifestStore().Save(root, manifest);
        CreateWorkbookSource(root, "Book1", ("Local.bas", "Attribute VB_Name = \"Local\""));
        var automation = new FakeWorkbookBuildAutomation(new WorkbookModule("Standard1", WorkbookModuleKind.StandardModule));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "remove:Standard1",
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
        automation.References.Add(new WorkbookReference("OLE Automation", IsRemovable: false));
        automation.References.Add(new WorkbookReference("Unlisted Library", IsRemovable: true));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 1, 0),
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 2, 0),
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("OLE Automation", resolver.RequestedNames);
        Assert.Contains("Microsoft Scripting Runtime", resolver.RequestedNames);
        Assert.Equal(
            [
                "remove-ref:Unlisted Library",
                "add-ref:Microsoft Scripting Runtime",
                "remove:Standard1",
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

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
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
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
        automation.References.Add(new WorkbookReference("Protected Library", IsRemovable: false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, workbookBuildAutomation: automation);

        var result = application.Run(["build"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("WARN", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Book1/Protected Library", result.StandardOutput, StringComparison.Ordinal);
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

    public bool ThrowOnRemove { get; init; }

    public COMException? ReferenceError { get; init; }

    public List<string> OpenedWorkbooks { get; } = [];

    public List<string> Events { get; } = [];

    public List<VbaSourceFile> ImportedSources { get; } = [];

    public List<WorkbookReference> References { get; } = [];

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
            owner.References.Add(new WorkbookReference(reference.Name, IsRemovable: true));
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
