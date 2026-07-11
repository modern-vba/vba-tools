using VbaDev.App.Diagnostics;
using VbaDev.App.Workbooks;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using System.Text;
using Xunit;

namespace VbaDev.Tests;

public sealed class DoctorCommandTests
{
    [Fact]
    public void DoctorWithoutProjectReportsSkippedProjectChecks()
    {
        using var temp = TempDirectory.Create();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("Excel COM startup", "Excel automation probe succeeded.")));

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[SKIP] Project manifest", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[PASS] Excel COM startup", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWithProjectReportsPathWarningsAndFailures()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, Path.Combine(root, "..", "missing_common_modules_repo")));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] Project manifest", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Source template", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[WARN] CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWithProjectChecksEveryDocument()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifestTestData.TwoDocumentManifest(root));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] Source template (Book1)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Document source set (SecondBook)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Source template (SecondBook)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorMapsFakeEnvironmentDiagnosticStatuses()
    {
        using var temp = TempDirectory.Create();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            temp.Path,
            new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("Excel COM startup", "Excel is available."),
                DiagnosticResult.Warn("VBIDE project access", "Trust access is disabled."),
                DiagnosticResult.Fail("Macro workbook creation", "Could not create an xlsm workbook.")));

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] Excel COM startup", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[WARN] VBIDE project access", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Macro workbook creation", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultDoctorEnvironmentDiagnosticsAreSkippedWithoutExcel()
    {
        using var temp = TempDirectory.Create();
        var application = ToolingCompositionRoot.CreateCommandLineApplication(temp.Path);

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[SKIP] Excel COM startup", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] Macro-enabled workbook creation", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] VBIDE project access", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] Locked workbook detection", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFailsForUnknownCommonModulesManifestEntries()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        AddInstalledCommonModules(root, new InstalledCommonModule("Missing", Requested: true));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Missing)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("unknown", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorFailsForMissingDependenciesRequiredByRequestedRoots()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(
            commonRepo,
            ("Base.bas", "optional", ""),
            ("Feature.bas", "optional", "Base.bas"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        WriteModule(Path.Combine(root, "src", "Book1"), "Feature.bas", "feature");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", Requested: true));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("requires missing dependency 'Base'", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsForUnreachableDependencyInstalledEntries()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Base.bas", "optional", ""));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(Path.Combine(root, "src", "Book1"), "Base.bas", "base");
        AddInstalledCommonModules(root, new InstalledCommonModule("Base", Requested: false));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Base)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("unreachable", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorWarnsForCommonModulesSourceDrift()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "canonical");
        WriteModule(Path.Combine(root, "src", "Book1"), "Feature.bas", "local edit");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", Requested: true));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("differs from CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorReportsDuplicateRecursiveSourceNamesAndDisplacedSidecars()
    {
        using var temp = TempDirectory.Create();
        var (root, _) = CreateDoctorProject(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("first", "Feature.bas"), "first");
        WriteModule(sourceSet, Path.Combine("second", "feature.bas"), "second");
        WriteModule(sourceSet, Path.Combine("forms", "Dialog.frm"), "form");
        WriteBytes(Path.Combine(sourceSet, "legacy", "Dialog.frx"), [1, 2, 3]);
        WriteBytes(Path.Combine(sourceSet, "Orphan.frx"), [9, 9, 9]);
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] Document source identity (Book1/Feature.bas)", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("first", "Feature.bas"), result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("second", "feature.bas"), result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[WARN] Form sidecar (Book1/Dialog.frx)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("legacy", "Dialog.frx"), result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Orphan.frx", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFindsNestedCommonModulesForDriftAndDuplicateChecks()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "canonical");
        WriteModule(Path.Combine(root, "src", "Book1"), Path.Combine("nested", "Feature.bas"), "local edit");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", Requested: true));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(root, new FakeEnvironmentDiagnosticPort());

        var drift = application.Run(["doctor"]);

        Assert.Equal(0, drift.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Feature)", drift.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("nested", "Feature.bas"), drift.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Manifest-listed source file was not found", drift.StandardOutput, StringComparison.Ordinal);

        WriteModule(Path.Combine(root, "src", "Book1"), Path.Combine("other", "feature.bas"), "other edit");
        var duplicate = application.Run(["doctor"]);

        Assert.Equal(1, duplicate.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Feature)", duplicate.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("multiple", duplicate.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorValidatesManifestReferencesForEveryDocument()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "src", "SecondBook"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        File.WriteAllText(Path.Combine(root, "src", "SecondBook", "SecondBook.xlsm"), string.Empty);
        var manifest = ProjectManifestTestData.TwoDocumentManifest(root);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Scripting Runtime"));
        manifest.Documents["SecondBook"].References.Add(new VbaProjectReference("Missing Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Scripting Runtime", "{420B2830-E718-11CF-893D-00A0C9054228}", 1, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] VbaProjectReferences (Book1/Microsoft Scripting Runtime)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] VbaProjectReferences (SecondBook/Missing Library)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorTreatsTemplateWorkbookReferencesAsResolvedBeforeRegistryValidation()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("OLE Automation"));
        new JsonProjectManifestStore().Save(root, manifest);
        var automation = new FakeWorkbookBuildAutomation();
        automation.References.Add(new WorkbookReference("OLE Automation", IsRemovable: false));
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 1, 0),
            new ResolvedVbaProjectReference("OLE Automation", "{00020430-0000-0000-C000-000000000046}", 2, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[PASS] VbaProjectReferences (Book1/OLE Automation)", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ambiguous", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(resolver.RequestedNames);
    }

    [Fact]
    public void DoctorWarnsWhenExcelDocumentOmitsMainVbaProjectReference()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] VbaProjectReferences (Book1)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Microsoft Excel 16.0 Object Library", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Host definitions will not be activated implicitly", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnWhenExcelDocumentListsMainVbaProjectReference()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Excel 16.0 Object Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Excel 16.0 Object Library", "{00020813-0000-0000-C000-000000000046}", 1, 9));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[PASS] VbaProjectReferences (Book1/Microsoft Excel 16.0 Object Library)", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("missing expected main reference", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsWhenReferenceCatalogMetadataIsUnavailable()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Microsoft Excel 16.0 Object Library"));
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Uncataloged Reference Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference("Microsoft Excel 16.0 Object Library", "{00020813-0000-0000-C000-000000000046}", 1, 9),
            new ResolvedVbaProjectReference("Uncataloged Reference Library", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}", 1, 0));
        var application = ToolingCompositionRoot.CreateCommandLineApplication(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] VbaProjectReferenceCatalog (Book1/Uncataloged Reference Library)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("No bundled or cached VbaProjectReferenceCatalog metadata is available", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[PASS] VbaProjectReferences (Book1/Uncataloged Reference Library)", result.StandardOutput, StringComparison.Ordinal);
    }

    private static (string Root, string CommonRepo) CreateDoctorProject(TempDirectory temp)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "publish", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, commonRepo));
        return (root, commonRepo);
    }

    private static void AddInstalledCommonModules(string root, params InstalledCommonModule[] modules)
    {
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(Path.Combine(root, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].CommonModules.AddRange(modules);
        store.Save(root, manifest);
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
        var path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static void WriteBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }
}

internal sealed class FakeEnvironmentDiagnosticPort : IEnvironmentDiagnosticPort
{
    private readonly IReadOnlyList<DiagnosticResult> results;

    public FakeEnvironmentDiagnosticPort(params DiagnosticResult[] results)
    {
        this.results = results;
    }

    public IReadOnlyList<DiagnosticResult> RunEnvironmentDiagnostics() => results;
}
