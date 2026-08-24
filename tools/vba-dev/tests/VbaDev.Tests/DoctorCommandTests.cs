using VbaDev.App.Diagnostics;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Cli;
using VbaDev.Composition;
using VbaDev.Domain;
using VbaDev.Infrastructure.Diagnostics;
using VbaDev.Infrastructure.Projects;
using VbaTools.TypeLibRegistry;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Xunit;

namespace VbaDev.Tests;

public sealed class DoctorCommandTests
{
    [Fact]
    public async Task CheckValidatesStaticProjectFactsWithoutActiveProbes()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault(
            "Project",
            "Book1",
            root,
            commonModulesRepositoryPath: null);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("Selected Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var referenceResolver = new FakeVbaProjectReferenceResolver
        {
            ThrowOnResolve = true
        };
        var application = CommandLineTestFactory.Create(
            root,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort(),
            vbaProjectReferenceResolver: referenceResolver);

        var result = await application.RunAsync(["check"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "[FAIL] Source template (Book1)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Empty(referenceResolver.RequestedNames);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeDoesNotDiscoverAProject()
    {
        using var temp = TempDirectory.Create();
        File.WriteAllText(
            Path.Combine(temp.Path, ProjectManifest.ManifestFileName),
            "environment scope must not read this project");
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(),
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "environment",
            output.RootElement.GetProperty("scope").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            output.RootElement.GetProperty("project").ValueKind);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeReturnsRequiredChecksInStableOrder()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed."),
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            [
                "platform.windows",
                "excel.comStartup",
                "excel.processOwnership",
                "excel.vbideProjectAccess",
                "excel.processCleanup"
            ],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("id").GetString()!)
                .ToArray());
        Assert.All(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal(
                JsonValueKind.Object,
                check.GetProperty("details").ValueKind));
    }

    [Fact]
    public async Task DoctorEnvironmentScopeKeepsACompleteWarningAtExitZero()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Warn("excel.vbideProjectAccess", "VBIDE access needs attention."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("warning", output.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "warning",
            output.RootElement
                .GetProperty("checks")[3]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckIsMissing()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement
                .GetProperty("checks")[2]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckIsDuplicated()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed twice."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement
                .GetProperty("checks")[2]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenTheAdapterAddsAnUnknownCheck()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed."),
                DiagnosticResult.Fail("excel.unknown", "Unknown readiness failed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            DoctorDiagnosticPipeline.EnvironmentCheckIds,
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("id").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task DoctorCancellationDoesNotHideAnUnknownAdapterFailure()
    {
        using var temp = TempDirectory.Create();
        var environmentPort = new FakeEnvironmentDiagnosticPort(
            DiagnosticResult.Pass("platform.windows", "Windows passed."),
            DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
            DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
            DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
            DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed."),
            DiagnosticResult.Fail("excel.unknown", "Unknown readiness failed."))
        {
            Canceled = true
        };
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort);

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckHasNegativeDuration()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed.") with
                {
                    DurationMilliseconds = -1
                },
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        var ownership = output.RootElement.GetProperty("checks")[2];
        Assert.Equal("unverified", ownership.GetProperty("status").GetString());
        Assert.Equal(0, ownership.GetProperty("durationMilliseconds").GetInt64());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckExceedsJsonSafeDuration()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed.") with
                {
                    DurationMilliseconds = 9_007_199_254_740_992
                },
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        var ownership = output.RootElement.GetProperty("checks")[2];
        Assert.Equal("unverified", ownership.GetProperty("status").GetString());
        Assert.Equal(0, ownership.GetProperty("durationMilliseconds").GetInt64());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckHasBlankMessage()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "   "),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        var ownership = output.RootElement.GetProperty("checks")[2];
        Assert.Equal("unverified", ownership.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ownership.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenMachineDetailsContradictStatus()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed.") with
                {
                    Details = new Dictionary<string, object?>
                    {
                        ["ownedByInvocation"] = false
                    }
                },
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        var ownership = output.RootElement.GetProperty("checks")[2];
        Assert.Equal("unverified", ownership.GetProperty("status").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            ownership.GetProperty("details").GetProperty("ownedByInvocation").ValueKind);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenARequiredCheckIsSkippedWithoutAnEarlierBlocker()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Skip("excel.comStartup", "COM startup was skipped."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement
                .GetProperty("checks")[1]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeIsIncompleteWhenStartedExcelCleanupIsSkipped()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Fail("excel.vbideProjectAccess", "VBIDE access failed."),
                DiagnosticResult.Skip("excel.processCleanup", "Cleanup was skipped.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement
                .GetProperty("checks")[4]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeTreatsUnverifiedReadinessAsFailure()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Unverified("excel.comStartup", "COM startup was not verified."),
                DiagnosticResult.Skip("excel.processOwnership", "Ownership was skipped."),
                DiagnosticResult.Skip("excel.vbideProjectAccess", "VBIDE access was skipped."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "unverified",
            output.RootElement.GetProperty("status").GetString());
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement
                .GetProperty("checks")[1]
                .GetProperty("status")
                .GetString());
    }

    [Fact]
    public async Task DoctorEnvironmentScopeEmitsIncompleteJsonAfterInfrastructureFailure()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            5,
            output.RootElement.GetProperty("checks").GetArrayLength());
        Assert.Equal(
            [
                "isWindows",
                "dedicatedInstanceStarted",
                "ownedByInvocation",
                "projectAccessSucceeded",
                "ownedProcessReleased"
            ],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check
                    .GetProperty("details")
                    .EnumerateObject()
                    .Single()
                    .Name)
                .ToArray());
        Assert.All(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal(
                JsonValueKind.Null,
                check.GetProperty("details").EnumerateObject().Single().Value.ValueKind));
    }

    [Fact]
    public async Task DoctorEnvironmentScopeRejectsProjectSelectionBeforeDiagnostics()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            [
                "doctor",
                "--scope", "environment",
                "--project", temp.Path,
                "--format", "json"
            ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "--project cannot be used with --scope environment",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeDoesNotStartExcelOnUnsupportedPlatform()
    {
        using var temp = TempDirectory.Create();
        var automation = new RecordingEnvironmentWorkbookAutomation();
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => throw new InvalidOperationException("A probe workbook must not be created."),
            _ => throw new InvalidOperationException("A probe workbook must not be deleted."),
            () => false);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["fail", "skipped", "skipped", "skipped", "skipped"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.Equal(0, automation.RunCount);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeProbesVbideInOwnedExcelAndCleansUp()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new SuccessfulEnvironmentWorkbookAutomation();
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.All(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check => Assert.Equal("pass", check.GetProperty("status").GetString()));
        var checks = output.RootElement.GetProperty("checks");
        Assert.True(checks[0].GetProperty("details").GetProperty("isWindows").GetBoolean());
        Assert.True(checks[1].GetProperty("details").GetProperty("dedicatedInstanceStarted").GetBoolean());
        Assert.True(checks[2].GetProperty("details").GetProperty("ownedByInvocation").GetBoolean());
        Assert.True(checks[3].GetProperty("details").GetProperty("projectAccessSucceeded").GetBoolean());
        Assert.True(checks[4].GetProperty("details").GetProperty("ownedProcessReleased").GetBoolean());
        Assert.Equal(probeWorkbook, automation.WorkbookPath);
        Assert.Equal(1, automation.Session.GetModulesCount);
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeReportsVbideDenialAfterOwnedCleanup()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new SuccessfulEnvironmentWorkbookAutomation(
            new COMException("Programmatic access to the Visual Basic Project is not trusted."));
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["pass", "pass", "pass", "fail", "pass"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeClassifiesStartupTimeoutAfterCleanup()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new ThrowingEnvironmentWorkbookAutomation(
            new WorkbookAutomationTimeoutException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ExcelStartup),
                TimeSpan.FromSeconds(30)));
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["pass", "unverified", "skipped", "skipped", "pass"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeClassifiesVbideTimeoutAfterOwnedCleanup()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new ThrowingEnvironmentWorkbookAutomation(
            new WorkbookAutomationTimeoutException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookOpen),
                TimeSpan.FromSeconds(30)));
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["pass", "pass", "pass", "unverified", "pass"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.True(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeReportsIncompleteCleanupProof()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new CleanupFailingEnvironmentWorkbookAutomation();
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["pass", "pass", "pass", "pass", "unverified"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeReturns130OnlyAfterCanceledOwnedCleanup()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var deletedWorkbooks = new List<string>();
        var automation = new CancelingEnvironmentWorkbookAutomation(
            cancellation.Cancel);
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            deletedWorkbooks.Add,
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"],
            cancellation.Token);

        Assert.Equal(130, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "pass",
            output.RootElement
                .GetProperty("checks")[4]
                .GetProperty("status")
                .GetString());
        Assert.Equal([probeWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorEnvironmentScopeMarksTheInterruptedActiveProbeUnverified()
    {
        using var temp = TempDirectory.Create();
        var probeWorkbook = Path.Combine(temp.Path, "doctor-probe.xlsx");
        var automation = new ThrowingEnvironmentWorkbookAutomation(
            new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookOpen),
                CancellationToken.None));
        var environmentPort = new ExcelEnvironmentDiagnosticPort(
            automation,
            () => probeWorkbook,
            _ => { },
            () => true);
        var application = CommandLineTestFactory.Create(
            temp.Path,
            environmentDiagnosticPort: environmentPort,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--scope", "environment", "--format", "json"]);

        Assert.Equal(130, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            ["pass", "pass", "pass", "unverified", "pass"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void DoctorCancellationDoesNotHideAnObservedFailure()
    {
        var renderer = new DoctorReportRenderer();
        var result = renderer.Render(
            new DoctorDiagnosticRun(
            [
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Fail("excel.vbideProjectAccess", "VBIDE access failed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
            ],
            Project: null,
            Complete: false,
            Canceled: true),
            new DoctorCommandRequest(
                ProjectRoot: null,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Environment,
                Format: DoctorOutputFormat.Json));

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void DoctorCancellationBindsCleanupProofToTheMachineIdentity()
    {
        var renderer = new DoctorReportRenderer();
        var result = renderer.Render(
            new DoctorDiagnosticRun(
            [
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Unverified("excel.vbideProjectAccess", "VBIDE access was interrupted."),
                DiagnosticResult.Pass(
                    "excel.processCleanup",
                    "Owned Excel cleanup",
                    "Cleanup passed.")
            ],
            Project: null,
            Complete: false,
            Canceled: true),
            new DoctorCommandRequest(
                ProjectRoot: null,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Environment,
                Format: DoctorOutputFormat.Json));

        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public void DoctorLateCancellationDoesNotReplaceACompleteSuccessfulResult()
    {
        var renderer = new DoctorReportRenderer();
        var result = renderer.Render(
            new DoctorDiagnosticRun(
            [
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
            ],
            Project: null,
            Complete: true,
            Canceled: true),
            new DoctorCommandRequest(
                ProjectRoot: null,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Environment,
                Format: DoctorOutputFormat.Json));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void DoctorJsonNormalizesDuplicateProjectCheckIdsAsIncompleteEvidence()
    {
        var renderer = new DoctorReportRenderer();
        var result = renderer.Render(
            new DoctorDiagnosticRun(
            [
                DiagnosticResult.Pass("project.same", "First project check", "First passed."),
                DiagnosticResult.Pass("project.same", "Second project check", "Second passed."),
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
            ],
            Project: Path.GetFullPath(Environment.CurrentDirectory),
            Complete: true),
            new DoctorCommandRequest(
                ProjectRoot: Environment.CurrentDirectory,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Project,
                Format: DoctorOutputFormat.Json));

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        var checks = output.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        var ids = checks.Select(check => check.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(
            checks,
            check => check.GetProperty("status").GetString() == "unverified");
    }

    [Fact]
    public void DoctorCancellationDoesNotHideAFailingDuplicateCheck()
    {
        var renderer = new DoctorReportRenderer();
        var result = renderer.Render(
            new DoctorDiagnosticRun(
            [
                DiagnosticResult.Pass("project.same", "First project check", "First passed."),
                DiagnosticResult.Fail("project.same", "Second project check", "Second failed."),
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Unverified("excel.vbideProjectAccess", "VBIDE access was interrupted."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
            ],
            Project: Path.GetFullPath(Environment.CurrentDirectory),
            Complete: false,
            Canceled: true),
            new DoctorCommandRequest(
                ProjectRoot: Environment.CurrentDirectory,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Project,
                Format: DoctorOutputFormat.Json));

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("fail", output.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("status").GetString() == "fail");
    }

    [Fact]
    public void DoctorDuplicateCheckClassificationDoesNotDependOnOutputFormat()
    {
        var renderer = new DoctorReportRenderer();
        var run = new DoctorDiagnosticRun(
        [
            DiagnosticResult.Pass("project.same", "First project check", "First passed."),
            DiagnosticResult.Pass("project.same", "Second project check", "Second passed."),
            DiagnosticResult.Pass("platform.windows", "Windows passed."),
            DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
            DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
            DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
            DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
        ],
        Project: Path.GetFullPath(Environment.CurrentDirectory),
        Complete: true);
        var jsonResult = renderer.Render(
            run,
            new DoctorCommandRequest(
                ProjectRoot: Environment.CurrentDirectory,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Project,
                Format: DoctorOutputFormat.Json));
        var textResult = renderer.Render(
            run,
            new DoctorCommandRequest(
                ProjectRoot: Environment.CurrentDirectory,
                StartDirectory: Environment.CurrentDirectory,
                Scope: DoctorScope.Project,
                Format: DoctorOutputFormat.Text));

        Assert.Equal(1, jsonResult.ExitCode);
        Assert.Equal(jsonResult.ExitCode, textResult.ExitCode);
    }

    [Fact]
    public async Task DoctorProjectScopeReportsImplicitAbsoluteProjectIdentity()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var nestedDirectory = Directory.CreateDirectory(
            Path.Combine(root, "nested"));
        var application = CommandLineTestFactory.Create(
            nestedDirectory.FullName,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(root),
            output.RootElement.GetProperty("project").GetString());
        Assert.Equal(
            DoctorDiagnosticPipeline.EnvironmentCheckIds,
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .TakeLast(DoctorDiagnosticPipeline.EnvironmentCheckIds.Count)
                .Select(check => check.GetProperty("id").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task DoctorDefaultTextReportsProjectContextAndCompleteness()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(["doctor"]);

        Assert.Contains("Scope: project", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            $"Project: {Path.GetFullPath(root)}",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("Complete: true", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProjectScopePreservesImplicitIdentityForMalformedManifest()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        File.WriteAllText(
            Path.Combine(root, ProjectManifest.ManifestFileName),
            "not valid json");
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(root),
            output.RootElement.GetProperty("project").GetString());
    }

    [Fact]
    public async Task DoctorProjectScopeEmitsIncompleteJsonAfterInfrastructureFailure()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        File.WriteAllText(
            Path.Combine(root, ProjectManifest.ManifestFileName),
            "{}");
        var application = CommandLineTestFactory.Create(
            root,
            projectManifestStore: new ThrowingProjectManifestStore());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "project",
            output.RootElement.GetProperty("scope").GetString());
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            "unverified",
            output.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            Path.GetFullPath(root),
            output.RootElement.GetProperty("project").GetString());
        Assert.Equal(
            DoctorDiagnosticPipeline.EnvironmentCheckIds,
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .TakeLast(DoctorDiagnosticPipeline.EnvironmentCheckIds.Count)
                .Select(check => check.GetProperty("id").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task DoctorProjectScopePreservesProjectEvidenceAfterEnvironmentInfrastructureFailure()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var application = CommandLineTestFactory.Create(
            root,
            environmentDiagnosticPort: new ThrowingEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(root),
            output.RootElement.GetProperty("project").GetString());
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Contains(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check =>
                check.GetProperty("id").GetString() == "Project manifest" &&
                check.GetProperty("status").GetString() == "pass");
    }

    [Fact]
    public async Task DoctorProjectScopeTreatsSkippedEvidenceAsUnverifiedFailure()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var application = CommandLineTestFactory.Create(root);

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            "unverified",
            output.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DoctorProjectScopeReturnsEnvironmentEvidenceInStableOrder()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed."),
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed.")));

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            DoctorDiagnosticPipeline.EnvironmentCheckIds,
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("id").GetString()!)
                .TakeLast(5)
                .ToArray());
    }

    [Fact]
    public async Task DoctorProjectScopeReportsWorkbookMaterializationFailure()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var stagedWorkbook = Path.Combine(temp.Path, "staged-Book1.xlsm");
        var deletedWorkbooks = new List<string>();
        var materializationPort = new ExcelProjectMaterializationDiagnosticPort(
            new ThrowingEnvironmentWorkbookAutomation(
                new InvalidOperationException("Excel could not open the workbook.")),
            _ => stagedWorkbook,
            deletedWorkbooks.Add);
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(),
            projectMaterializationDiagnosticPort: materializationPort);

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Contains(
            output.RootElement.GetProperty("checks").EnumerateArray(),
            check =>
                check.GetProperty("id").GetString() ==
                    "project.workbookMaterialization/Book1" &&
                check.GetProperty("status").GetString() == "fail");
        Assert.Equal(
            DoctorDiagnosticPipeline.EnvironmentCheckIds,
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("id").GetString()!)
                .TakeLast(5)
                .ToArray());
        Assert.Equal([stagedWorkbook], deletedWorkbooks);
    }

    [Fact]
    public async Task DoctorProjectScopePropagatesCancellationToReferenceProbe()
    {
        using var temp = TempDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("Ambiguous Library"));
        store.Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Ambiguous Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0),
            new ResolvedVbaProjectReference(
                "Ambiguous Library",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                2,
                0));
        var probe = new CancelingDoctorAmbiguityProbe(cancellation.Cancel);
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(),
            vbaProjectReferenceResolver: resolver,
            vbaProjectReferenceAmbiguityProbe: probe);

        var result = await application.RunAsync(
            ["doctor", "--format", "json"],
            cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.True(probe.ReceivedCancelableToken);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(root),
            output.RootElement.GetProperty("project").GetString());
        Assert.False(output.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(
            ["unverified", "skipped", "skipped", "skipped", "skipped"],
            output.RootElement
                .GetProperty("checks")
                .EnumerateArray()
                .TakeLast(DoctorDiagnosticPipeline.EnvironmentCheckIds.Count)
                .Select(check => check.GetProperty("status").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task DoctorProjectScopeFailsWithoutProjectContext()
    {
        using var temp = TempDirectory.Create();
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                new FakeEnvironmentDiagnosticPort()));

        var result = await application.RunAsync(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] Project manifest", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[PASS] excel.comStartup", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProjectScopeReportsAbsoluteRequestIdentityWhenManifestIsMissing()
    {
        using var temp = TempDirectory.Create();
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                temp.Path,
                new FakeEnvironmentDiagnosticPort()));

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(
            Path.GetFullPath(temp.Path),
            output.RootElement.GetProperty("project").GetString());
    }

    [Fact]
    public void DoctorWithProjectReportsPathWarningsAndFailures()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var store = new JsonProjectManifestStore();
        store.Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, Path.Combine(root, "..", "missing_common_modules_repo")));
        var application = CommandLineTestFactory.Create(
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
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] Source template (Book1)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Document source set (SecondBook)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] Source template (SecondBook)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFailsThroughProjectManifestDiagnosticForMissingCommonModuleBaseMetadata()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        var json = """
            {
              "schemaVersion": 1,
              "projectName": "Project",
              "primaryDocument": "Book1",
              "documents": {
                "Book1": {
                  "kind": "excel",
                  "sourcePath": "src/Book1",
                  "templatePath": "src/Book1/Book1.xlsm",
                  "binPath": "bin/Book1.xlsm",
                  "publishPath": "publish/Book1.xlsm",
                  "commonModules": [
                    {
                      "name": "Feature",
                      "requested": true,
                      "testOnly": false
                    }
                  ]
                }
              }
            }
            """;
        File.WriteAllText(Path.Combine(root, ProjectManifest.ManifestFileName), json, new UTF8Encoding(false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] Project manifest", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("moduleFile", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorMapsFakeEnvironmentDiagnosticStatuses()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(
            temp.Path,
            new FakeEnvironmentDiagnosticPort(
                DiagnosticResult.Pass("platform.windows", "Windows is available."),
                DiagnosticResult.Pass("excel.comStartup", "Excel is available."),
                DiagnosticResult.Pass("excel.processOwnership", "Excel is owned."),
                DiagnosticResult.Warn("excel.vbideProjectAccess", "Trust access is disabled."),
                DiagnosticResult.Fail("excel.processCleanup", "Could not clean up Excel.")));

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[PASS] excel.comStartup", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[WARN] excel.vbideProjectAccess", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[FAIL] excel.processCleanup", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultDoctorDoesNotRunVisibleDebugEnvironmentProbes()
    {
        using var temp = TempDirectory.Create();
        var application = CommandLineTestFactory.Create(temp.Path);

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] Project manifest", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] excel.comStartup", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] excel.processOwnership", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[SKIP] excel.vbideProjectAccess", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("VBA debug capability", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Native VBE readiness", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFailsForUnknownCommonModulesManifestEntries()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        AddInstalledCommonModules(root, new InstalledCommonModule("Missing", "Missing.bas", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Missing)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("unknown", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoctorProjectScopeJsonUsesUniqueIdsForIndependentCommonModulesFaults()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        AddInstalledCommonModules(
            root,
            new InstalledCommonModule(
                "Missing",
                "Missing.bas",
                Requested: true,
                TestOnly: false));
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var ids = output.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DoctorFailsForMissingStoredCommonModuleSourceWithoutRepository()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.cls", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Feature.cls", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorFailsForAmbiguousStoredCommonModuleSourceWithoutRepository()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("first", "Feature.cls"), "first");
        WriteModule(sourceSet, Path.Combine("second", "feature.cls"), "second");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.cls", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("multiple source matches", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
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
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.bas", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("requires missing dependency 'Base'", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorProjectScopeJsonUsesUniqueIdsForDiamondCommonModulesDependencies()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(
            commonRepo,
            ("Base.bas", "optional", ""),
            ("Left.bas", "optional", "Base.bas"),
            ("Right.bas", "optional", "Base.bas"),
            ("Feature.bas", "optional", "Left.bas,Right.bas"));
        WriteModule(commonRepo, "Feature.bas", "feature");
        WriteModule(Path.Combine(root, "src", "Book1"), "Feature.bas", "feature");
        AddInstalledCommonModules(
            root,
            new InstalledCommonModule(
                "Feature",
                "Feature.bas",
                Requested: true,
                TestOnly: false));
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        Assert.Equal(1, result.ExitCode);
        using var output = JsonDocument.Parse(result.StandardOutput);
        var ids = output.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DoctorWarnsForUnreachableDependencyInstalledEntries()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Base.bas", "optional", ""));
        WriteModule(commonRepo, "Base.bas", "base");
        WriteModule(Path.Combine(root, "src", "Book1"), "Base.bas", "base");
        AddInstalledCommonModules(root, new InstalledCommonModule("Base", "Base.bas", Requested: false, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

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
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.bas", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("differs from CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoctorWarnsWhenOnlyOneCommonModulesFormHasSidecar(bool canonicalHasSidecar)
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "form");
        WriteModule(sourceSet, "Dialog.frm", "form");
        WriteBytes(
            Path.Combine(canonicalHasSidecar ? commonRepo : sourceSet, "Dialog.frx"),
            [1, 2, 3]);
        AddInstalledCommonModules(root, new InstalledCommonModule("Dialog", "Dialog.frm", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Dialog)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("differs from CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnWhenMatchingCommonModulesFormsHaveNoSidecars()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "form");
        WriteModule(sourceSet, "Dialog.frm", "form");
        AddInstalledCommonModules(root, new InstalledCommonModule("Dialog", "Dialog.frm", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("[WARN] CommonModules (Book1/Dialog)", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsWhenCommonModulesFormSidecarBytesDiffer()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteManifest(commonRepo, ("Dialog.frm", "optional", ""));
        WriteModule(commonRepo, "Dialog.frm", "form");
        WriteBytes(Path.Combine(commonRepo, "Dialog.frx"), [1, 2, 3]);
        WriteModule(sourceSet, "Dialog.frm", "form");
        WriteBytes(Path.Combine(sourceSet, "Dialog.frx"), [3, 2, 1]);
        AddInstalledCommonModules(root, new InstalledCommonModule("Dialog", "Dialog.frm", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Dialog)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("differs from CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorComparesStoredSourceWithCanonicalRepositoryEntryResolvedByName()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("runtime/Feature.cls", "optional", ""));
        WriteModule(commonRepo, Path.Combine("runtime", "Feature.cls"), "canonical");
        WriteModule(Path.Combine(root, "src", "Book1"), "Feature.cls", "local edit");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.cls", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[WARN] CommonModules (Book1/Feature)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("differs from CommonModulesRepository", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("source file was not found", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
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
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

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
    public async Task DoctorProjectScopeJsonUsesUniqueIdsForMultipleDisplacedFormSidecars()
    {
        using var temp = TempDirectory.Create();
        var (root, _) = CreateDoctorProject(temp);
        var sourceSet = Path.Combine(root, "src", "Book1");
        WriteModule(sourceSet, Path.Combine("forms", "Dialog.frm"), "form");
        WriteBytes(Path.Combine(sourceSet, "legacy", "Dialog.frx"), [1, 2, 3]);
        WriteBytes(Path.Combine(sourceSet, "archive", "Dialog.frx"), [4, 5, 6]);
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort());

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        using var output = JsonDocument.Parse(result.StandardOutput);
        var ids = output.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DoctorFindsNestedCommonModulesForDriftAndDuplicateChecks()
    {
        using var temp = TempDirectory.Create();
        var (root, commonRepo) = CreateDoctorProject(temp);
        WriteManifest(commonRepo, ("Feature.bas", "optional", ""));
        WriteModule(commonRepo, "Feature.bas", "canonical");
        WriteModule(Path.Combine(root, "src", "Book1"), Path.Combine("nested", "Feature.bas"), "local edit");
        AddInstalledCommonModules(root, new InstalledCommonModule("Feature", "Feature.bas", Requested: true, TestOnly: false));
        var application = CommandLineTestFactory.Create(root, new FakeEnvironmentDiagnosticPort());

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
        var application = CommandLineTestFactory.Create(
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
    public async Task DoctorProjectScopeJsonUsesUniqueIdsForDuplicateReferenceNames()
    {
        using var temp = TempDirectory.Create();
        var root = CreateDoctorProjectWithoutRepository(temp);
        var store = new JsonProjectManifestStore();
        var manifest = store.Load(
            Path.Combine(root, ProjectManifest.ManifestFileName));
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("Unique Library"));
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("Unique Library"));
        store.Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Unique Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0));
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(),
            vbaProjectReferenceResolver: resolver);

        var result = await application.RunAsync(
            ["doctor", "--format", "json"]);

        using var output = JsonDocument.Parse(result.StandardOutput);
        var ids = output.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DoctorDoesNotOpenTheSourceTemplateToOverrideARegistryUnavailableReference()
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
        var resolver = new FakeVbaProjectReferenceResolver();
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: automation,
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[FAIL] VbaProjectReferences (Book1/OLE Automation)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(["OLE Automation"], resolver.RequestedNames);
        Assert.Empty(automation.OpenedWorkbooks);
    }

    [Fact]
    public void DoctorUsesTheDocumentTemplateToResolveAMissingAmbiguousReference()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        var templatePath = Path.Combine(root, "src", "Book1", "Book1.xlsm");
        File.WriteAllText(templatePath, string.Empty);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(
            new VbaProjectReference("Ambiguous Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var resolvedIdentity = new ResolvedVbaProjectReference(
            "Ambiguous Library",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            2,
            0);
        var probe = new RecordingDoctorAmbiguityProbe(resolvedIdentity);
        var application = VbaDevCommandLine.Create(
            ToolingCompositionRoot.CreateApplicationComposition(
                root,
                environmentDiagnosticPort: new FakeEnvironmentDiagnosticPort(),
                workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
                vbaProjectReferenceResolver: new FakeVbaProjectReferenceResolver(
                    resolvedIdentity,
                    new ResolvedVbaProjectReference(
                        "Ambiguous Library",
                        "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                        3,
                        0)),
                vbaProjectReferenceAmbiguityProbe: probe));

        var result = application.Run(["doctor"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "[PASS] VbaProjectReferences (Book1/Ambiguous Library)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Equal([templatePath], probe.BaselineWorkbookPaths);
    }

    [Fact]
    public void DoctorFailsAnOtherwiseResolvedReferenceWhenTheSharedBatchIsIncomplete()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", root, null);
        manifest.Documents["Book1"].References.Add(new VbaProjectReference("Unique Library"));
        new JsonProjectManifestStore().Save(root, manifest);
        var resolver = new FakeVbaProjectReferenceResolver(
            new ResolvedVbaProjectReference(
                "Unique Library",
                "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                1,
                0))
        {
            Complete = false,
            Diagnostic = new TypeLibRegistryCatalogDiagnostic(
                "registryEnumerationFailure",
                "The reference catalog could not be enumerated completely.")
        };
        var application = CommandLineTestFactory.Create(
            root,
            new FakeEnvironmentDiagnosticPort(),
            workbookBuildAutomation: new FakeWorkbookBuildAutomation(),
            vbaProjectReferenceResolver: resolver);

        var result = application.Run(["doctor"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "[FAIL] VbaProjectReferences (Book1)",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains("registryEnumerationFailure", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[PASS] VbaProjectReferences (Book1/Unique Library)",
            result.StandardOutput,
            StringComparison.Ordinal);

        var jsonResult = application.Run(["doctor", "--format", "json"]);
        using var output = JsonDocument.Parse(jsonResult.StandardOutput);
        var ids = output.RootElement
            .GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DoctorWarnsWhenExcelDocumentOmitsMainVbaProjectReference()
    {
        using var temp = TempDirectory.Create();
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        var application = CommandLineTestFactory.Create(
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
        var application = CommandLineTestFactory.Create(
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
        var application = CommandLineTestFactory.Create(
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

    private sealed class RecordingDoctorAmbiguityProbe(
        ResolvedVbaProjectReference resolvedIdentity)
        : IVbaProjectReferenceAmbiguityProbe
    {
        public List<string> BaselineWorkbookPaths { get; } = [];

        public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
            VbaProjectReferenceProbeBaseline baseline,
            VbaProjectReferenceResolutionBatch registryResolution,
            CancellationToken cancellationToken)
        {
            BaselineWorkbookPaths.Add(baseline.WorkbookPath!);
            return Task.FromResult(registryResolution with
            {
                References = registryResolution.References
                    .Select(reference => reference with
                    {
                        Matches = [resolvedIdentity],
                        Candidates = [resolvedIdentity]
                    })
                    .ToArray()
            });
        }
    }

    private sealed class CancelingDoctorAmbiguityProbe(
        Action requestCancellation) : IVbaProjectReferenceAmbiguityProbe
    {
        public bool ReceivedCancelableToken { get; private set; }

        public Task<VbaProjectReferenceResolutionBatch> ResolveAsync(
            VbaProjectReferenceProbeBaseline baseline,
            VbaProjectReferenceResolutionBatch registryResolution,
            CancellationToken cancellationToken)
        {
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            requestCancellation();
            return Task.FromCanceled<VbaProjectReferenceResolutionBatch>(
                cancellationToken);
        }
    }

    private sealed class ThrowingProjectManifestStore : IProjectManifestStore
    {
        public ProjectManifest Load(string manifestPath)
            => throw new InvalidOperationException(
                "Environment Doctor must not load project state.");

        public void Save(string projectRoot, ProjectManifest manifest)
            => throw new InvalidOperationException(
                "Environment Doctor must not save project state.");
    }

    private sealed class ThrowingEnvironmentDiagnosticPort
        : IEnvironmentDiagnosticPort
    {
        public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "Environment diagnostic infrastructure failed.");
    }

    private sealed class RecordingEnvironmentWorkbookAutomation
        : IWorkbookGenerationAutomation
    {
        public int RunCount { get; private set; }

        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            RunCount++;
            throw new InvalidOperationException(
                "Workbook automation must not run on an unsupported platform.");
        }
    }

    private sealed class SuccessfulEnvironmentWorkbookAutomation
        : IWorkbookGenerationAutomation
    {
        public SuccessfulEnvironmentWorkbookAutomation(
            Exception? getModulesError = null)
        {
            Session = new RecordingEnvironmentWorkbookSession(getModulesError);
        }

        public RecordingEnvironmentWorkbookSession Session { get; }

        public string? WorkbookPath { get; private set; }

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            WorkbookPath = workbookPath;
            return await operation(Session, cancellationToken);
        }
    }

    private sealed class ThrowingEnvironmentWorkbookAutomation(
        Exception error) : IWorkbookGenerationAutomation
    {
        public Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
            => Task.FromException<TResult>(error);
    }

    private sealed class CleanupFailingEnvironmentWorkbookAutomation
        : IWorkbookGenerationAutomation
    {
        private readonly RecordingEnvironmentWorkbookSession session = new(null);

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            await operation(session, cancellationToken);
            throw new WorkbookAutomationCleanupException(
                "The owned Excel process release could not be proved.");
        }
    }

    private sealed class CancelingEnvironmentWorkbookAutomation(
        Action requestCancellation) : IWorkbookGenerationAutomation
    {
        private readonly RecordingEnvironmentWorkbookSession session = new(null);

        public async Task<TResult> RunAsync<TResult>(
            string workbookPath,
            WorkbookAutomationTimeouts timeouts,
            Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken)
        {
            await operation(session, cancellationToken);
            requestCancellation();
            throw new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ProcessCleanup),
                cancellationToken);
        }
    }

    private sealed class RecordingEnvironmentWorkbookSession
        : IWorkbookGenerationSession
    {
        private readonly Exception? getModulesError;

        public RecordingEnvironmentWorkbookSession(Exception? getModulesError)
        {
            this.getModulesError = getModulesError;
        }

        public int GetModulesCount { get; private set; }

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(
            CancellationToken cancellationToken)
        {
            GetModulesCount++;
            if (getModulesError is not null)
            {
                return Task.FromException<IReadOnlyList<WorkbookModule>>(
                    getModulesError);
            }

            return Task.FromResult<IReadOnlyList<WorkbookModule>>([]);
        }

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task RemoveModuleAsync(
            string moduleName,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task VerifyAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SaveAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static (string Root, string CommonRepo) CreateDoctorProject(TempDirectory temp)
    {
        var commonRepo = temp.CreateDirectory("common_modules_repo");
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "publish"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, commonRepo));
        return (root, commonRepo);
    }

    private static string CreateDoctorProjectWithoutRepository(TempDirectory temp)
    {
        var root = temp.CreateDirectory("Project");
        Directory.CreateDirectory(Path.Combine(root, "src", "Book1"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "publish"));
        File.WriteAllText(Path.Combine(root, "src", "Book1", "Book1.xlsm"), string.Empty);
        new JsonProjectManifestStore().Save(root, ProjectManifest.CreateDefault("Project", "Book1", root, null));
        return root;
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
        this.results = results.Length == 0
            ?
            [
                DiagnosticResult.Pass("platform.windows", "Windows passed."),
                DiagnosticResult.Pass("excel.comStartup", "COM startup passed."),
                DiagnosticResult.Pass("excel.processOwnership", "Ownership passed."),
                DiagnosticResult.Pass("excel.vbideProjectAccess", "VBIDE access passed."),
                DiagnosticResult.Pass("excel.processCleanup", "Cleanup passed.")
            ]
            : results;
    }

    public bool Complete { get; init; } = true;

    public bool Canceled { get; init; }

    public Task<EnvironmentDiagnosticRun> RunEnvironmentDiagnosticsAsync(
        CancellationToken cancellationToken)
        => Task.FromResult(new EnvironmentDiagnosticRun(
            results,
            Complete,
            Canceled));
}
