using VbaDevTools.App.Diagnostics;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
using System.Text;
using Xunit;

namespace VbaDevTools.Tests;

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
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] VbaProjectReferences (Book1/Microsoft Scripting Runtime)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] VbaProjectReferences (SecondBook/Missing Library)", result.StandardOutput, StringComparison.Ordinal);
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
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
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
