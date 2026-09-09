using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.App.Testing;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class TestBuildSourceDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationStopsEveryMacroDespiteOldOutputAndRecoversForTheSelectedTest(bool snapshot)
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        var source = snapshot ? temp.CreateDirectory("caller-snapshot") : saved;
        var template = Path.Combine(saved, "Book1.xlsm");
        File.WriteAllText(template, "original-template", Encoding.UTF8);
        var bin = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "bin")).FullName, "Book1.xlsm");
        File.WriteAllText(bin, "previous-completed-workbook", Encoding.UTF8);
        const string validCaller = "Attribute VB_Name = \"Caller\"\r\nPublic Sub Run()\r\n    Dim item As Long\r\n    AcceptValue item\r\nEnd Sub\r\n";
        var caller = Path.Combine(Directory.CreateDirectory(Path.Combine(source, "nested", "日本語")).FullName, "Caller.bas");
        var target = Path.Combine(source, "Target.bas");
        var duplicate = Path.Combine(source, "Other.bas");
        File.WriteAllText(caller, validCaller.Replace("As Long", "As Integer"), Encoding.UTF8);
        File.WriteAllText(target, "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n", Encoding.UTF8);
        File.WriteAllText(duplicate, "Attribute VB_Name = \"Other\"\nPublic Sub Bad(ByVal name As String, ByVal name As String)\nEnd Sub\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(source, "Test_Module.bas"), "Attribute VB_Name = \"Test_Module\"\nPublic Sub Test_Passes()\nEnd Sub\n", Encoding.UTF8);
        if (snapshot) File.WriteAllText(Path.Combine(saved, "Caller.bas"), validCaller, Encoding.UTF8);
        var bytes = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var macros = 0;
        var runner = new FakeWorkbookTestRunner(new WorkbookTestResultRow("Test_Module", "Test_Passes", "OK", ""))
        { OnRun = () => macros++ };
        var automation = new FakeWorkbookGenerationAutomation();
        var app = CommandLineTestFactory.Create(project, workbookTestRunner: runner, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);
        string[] args = snapshot
            ? ["test", "--source-snapshot", source, "--module", "Test_Module", "--procedure", "Test_Passes", "--format", "ndjson"]
            : ["test", "--module", "Test_Module", "--procedure", "Test_Passes", "--format", "ndjson"];

        var failed = await app.RunAsync(args);

        Assert.Equal(1, failed.ExitCode);
        Assert.Empty(failed.StandardOutput);
        var report = ReadReport(failed.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        var diagnostics = report.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Contains(diagnostics, d => d.GetProperty("code").GetString() == "validation.duplicateCallableParameterName");
        var call = Assert.Single(diagnostics, d => d.GetProperty("code").GetString() == "validation.incompatibleCallArgumentList");
        Assert.Equal(new Uri(caller).AbsoluteUri, call.GetProperty("uri").GetString());
        Assert.Equal(3, call.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(new Uri(target).AbsoluteUri, Assert.Single(call.GetProperty("relatedInformation").EnumerateArray())
            .GetProperty("location").GetProperty("uri").GetString());
        Assert.Equal(0, macros);
        Assert.Empty(runner.Workbooks);
        Assert.Empty(automation.OpenedWorkbooks);
        foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));

        // Existing-workbook mode intentionally ignores source errors and omits source authority.
        var noBuild = await app.RunAsync(["test", "--no-build", "--format", "ndjson"]);
        Assert.Equal(0, noBuild.ExitCode);
        Assert.Equal(1, macros);
        Assert.Equal(bin, Assert.Single(runner.Workbooks));
        Assert.DoesNotContain("sourceAnalysis", noBuild.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("\"location\"", noBuild.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("without a proved source capture", noBuild.StandardError, StringComparison.Ordinal);

        File.WriteAllText(caller, validCaller, Encoding.UTF8);
        File.Delete(duplicate);
        var succeeded = await app.RunAsync(args);
        Assert.Equal(0, succeeded.ExitCode);
        Assert.Equal(2, macros);
        Assert.Contains("\"type\":\"testFinished\"", succeeded.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceAnalysis", succeeded.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceAnalysis", succeeded.StandardError, StringComparison.Ordinal);
        Assert.Equal(new WorkbookTestSelector("Test_Module", "Test_Passes"), runner.Selectors[^1]);
        Assert.Equal(bytes[template], File.ReadAllBytes(template));
        if (snapshot) Assert.Equal(bytes[bin], File.ReadAllBytes(bin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteAnalysisRetainsAllRecoverableFindingsAndNeverRunsMacros(bool snapshot)
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(saved, "Book1.xlsm"), "template", Encoding.UTF8);
        var source = snapshot ? temp.CreateDirectory("caller-snapshot") : saved;
        File.WriteAllBytes(Path.Combine(source, "Broken.bas"), [0xff, 0xfe, 0, 0, 65, 0, 0, 0]);
        foreach (var name in new[] { "One", "Two" })
            File.WriteAllText(Path.Combine(source, name + ".bas"),
                $"Attribute VB_Name = \"{name}\"\nPublic Sub {name}(ByVal name As String, ByVal name As String)\nEnd Sub\n", Encoding.UTF8);
        var runner = new FakeWorkbookTestRunner();
        var automation = new FakeWorkbookGenerationAutomation();
        var app = CommandLineTestFactory.Create(project, workbookTestRunner: runner, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: new MissingRequiredMetadata());
        var bytes = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

        var result = await app.RunAsync(snapshot ? ["test", "--source-snapshot", source, "--format", "ndjson"] : ["test", "--format", "ndjson"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(2, report.GetProperty("diagnostics").EnumerateArray().Count(d => d.GetProperty("code").GetString() == "validation.duplicateCallableParameterName"));
        Assert.Equal(2, report.GetProperty("failures").GetArrayLength());
        Assert.Contains(report.GetProperty("failures").EnumerateArray(), failure => failure.GetProperty("message").GetString() == "Required reference metadata unavailable.");
        Assert.Empty(runner.Workbooks);
        Assert.Empty(automation.OpenedWorkbooks);
        foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static JsonElement ReadReport(string stderr) => JsonSerializer.Deserialize<JsonElement>(
        Assert.Single(stderr.Split('\n'), line => line.StartsWith("{\"type\":\"sourceAnalysis\"", StringComparison.Ordinal)));

    private sealed class MissingRequiredMetadata : IProjectSemanticInputProvider
    {
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context, CapturedWorkbookTemplate template,
            IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
            => throw new IOException("Required reference metadata unavailable.");
    }
}
