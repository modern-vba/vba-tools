using VbaDevTools.App.Diagnostics;
using VbaDevTools.Composition;
using VbaDevTools.Domain;
using VbaDevTools.Infrastructure.Projects;
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
