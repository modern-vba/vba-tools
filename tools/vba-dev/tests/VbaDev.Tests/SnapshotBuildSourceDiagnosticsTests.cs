using System.Text;
using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Projects;
using VbaDev.Domain;
using VbaDev.Infrastructure.Projects;
using VbaTools.Semantics;
using VbaTools.Syntax;
using Xunit;

namespace VbaDev.Tests;

public sealed class SnapshotBuildSourceDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterAuthoringChangesCannotSwitchTheAnalyzedOrGeneratedSnapshot(bool initiallyInvalid)
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        var templatePath = Path.Combine(saved, "Book1.xlsm");
        var templateBytes = Encoding.UTF8.GetBytes("captured-template");
        File.WriteAllBytes(templatePath, templateBytes);
        var snapshot = temp.CreateDirectory("snapshot");
        var callerPath = Path.Combine(snapshot, "Caller.bas");
        var targetPath = Path.Combine(snapshot, "Target.bas");
        const string validCaller = "Attribute VB_Name = \"Caller\"\r\nPublic Sub Run()\r\n    Dim item As Long\r\n    AcceptValue item\r\nEnd Sub\r\n";
        var invalidCaller = validCaller.Replace("As Long", "As Integer");
        File.WriteAllText(callerPath, initiallyInvalid ? invalidCaller : validCaller, Encoding.UTF8);
        File.WriteAllText(targetPath, "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n", Encoding.UTF8);
        var output = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        File.WriteAllText(output, "previous-output", Encoding.UTF8);
        var automation = new FakeWorkbookGenerationAutomation();
        var provider = new SemanticInputProvider((_, _, _, _) =>
        {
            File.WriteAllText(callerPath, initiallyInvalid ? validCaller : invalidCaller, Encoding.UTF8);
            File.Delete(targetPath);
            File.WriteAllText(templatePath, "later-template", Encoding.UTF8);
            return Task.FromResult(VbaProjectSemanticInputs.Empty);
        });
        var application = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: provider);

        var result = await application.RunAsync(["build", "--source-snapshot", snapshot, "--output", output]);

        Assert.Equal(initiallyInvalid ? 1 : 0, result.ExitCode);
        Assert.Equal(1, provider.Calls);
        if (initiallyInvalid)
        {
            Assert.Contains("validation.incompatibleCallArgumentList", result.StandardError, StringComparison.Ordinal);
            Assert.Empty(automation.OpenedWorkbooks);
            Assert.Equal("previous-output", File.ReadAllText(output));
        }
        else
        {
            Assert.Empty(result.StandardError);
            Assert.Equal(templateBytes, File.ReadAllBytes(output));
            var caller = Assert.Single(automation.ImportedSources, source => source.FileName == "Caller.bas");
            Assert.Contains("    Dim item As Long", caller.ImportVerification.CodeModuleLines);
            Assert.Equal(2, automation.ImportedSources.Count);
        }
        Assert.Equal(initiallyInvalid ? validCaller : invalidCaller, File.ReadAllText(callerPath));
        Assert.False(File.Exists(targetPath));
        Assert.Equal("later-template", File.ReadAllText(templatePath));
        Assert.False(File.Exists(Path.Combine(project, "bin", "Book1.xlsm")));
    }

    [Fact]
    public async Task SnapshotBuildAggregatesReadableSourceFindingsWithDecodeAndRequiredInputFailures()
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(saved, "Book1.xlsm"), "original-template", Encoding.UTF8);
        var snapshot = temp.CreateDirectory("snapshot");
        var brokenPath = Path.Combine(snapshot, "Broken.bas");
        File.WriteAllBytes(brokenPath, [0xff, 0xfe, 0, 0, 65, 0, 0, 0]);
        foreach (var name in new[] { "One", "Two" })
            File.WriteAllText(Path.Combine(snapshot, name + ".bas"),
                $"Attribute VB_Name = \"{name}\"\nPublic Sub Run(ByVal name As String, ByVal name As Long)\nEnd Sub\n", Encoding.UTF8);
        var output = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        File.WriteAllText(output, "previous-output", Encoding.UTF8);
        var originalBytes = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var provider = new SemanticInputProvider((_, _, sources, _) =>
        {
            Assert.Equal(2, sources.Count);
            throw new IOException("Required TypeLib unavailable for this snapshot.");
        });
        var application = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: provider);

        var result = await application.RunAsync(["build", "--source-snapshot", snapshot, "--output", output]);

        Assert.Equal(1, result.ExitCode);
        var report = JsonSerializer.Deserialize<JsonElement>(Assert.Single(result.StandardError.Split('\n'),
            line => line.StartsWith("{\"type\":\"sourceAnalysis\"", StringComparison.Ordinal)));
        Assert.False(report.GetProperty("complete").GetBoolean());
        var findings = report.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(2, findings.Count(finding => finding.GetProperty("code").GetString() == "validation.duplicateCallableParameterName"));
        Assert.All(findings, finding => Assert.Contains(finding.GetProperty("uri").GetString(),
            new[] { new Uri(Path.Combine(snapshot, "One.bas")).AbsoluteUri, new Uri(Path.Combine(snapshot, "Two.bas")).AbsoluteUri }));
        var failures = report.GetProperty("failures").EnumerateArray().ToArray();
        Assert.Equal(2, failures.Length);
        Assert.Contains(failures, failure => failure.GetProperty("uri").GetString() == new Uri(brokenPath).AbsoluteUri);
        Assert.Contains(failures, failure => failure.GetProperty("message").GetString() == "Required TypeLib unavailable for this snapshot.");
        var inputFailure = Assert.Single(failures, failure => failure.GetProperty("scope").GetString() == "project");
        Assert.Equal("semanticInputAcquisition", inputFailure.GetProperty("phase").GetString());
        Assert.Equal(typeof(IOException).FullName, inputFailure.GetProperty("exceptionType").GetString());
        Assert.Contains(nameof(SnapshotBuildAggregatesReadableSourceFindingsWithDecodeAndRequiredInputFailures),
            inputFailure.GetProperty("exception").GetString());
        Assert.Equal(1, provider.Calls);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Equal(0, automation.SaveCalls);
        foreach (var (path, bytes) in originalBytes) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SnapshotBuildDiagnosesItsCapturedTypeInsteadOfTheSavedSourceType()
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        var template = Path.Combine(saved, "Book1.xlsm");
        File.WriteAllText(template, "original-template", Encoding.UTF8);
        const string caller = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Dim item As Integer\n    AcceptValue item\nEnd Sub\n";
        const string target = "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n";
        File.WriteAllText(Path.Combine(saved, "Caller.bas"), caller.Replace("As Integer", "As Long"), Encoding.UTF8);
        File.WriteAllText(Path.Combine(saved, "Target.bas"), target, Encoding.UTF8);
        var snapshot = temp.CreateDirectory("snapshot");
        var nested = Directory.CreateDirectory(Path.Combine(snapshot, "nested", "日本語")).FullName;
        var callerPath = Path.Combine(nested, "Caller.bas");
        var targetPath = Path.Combine(snapshot, "Target.bas");
        File.WriteAllText(callerPath, caller, Encoding.UTF8);
        File.WriteAllText(targetPath, target, Encoding.UTF8);
        var bin = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "bin")).FullName, "Book1.xlsm");
        var output = Path.Combine(temp.CreateDirectory("session"), "Book1.xlsm");
        File.WriteAllText(bin, "previous-bin", Encoding.UTF8);
        File.WriteAllText(output, "previous-caller-output", Encoding.UTF8);
        var originalBytes = Directory.GetFiles(project, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories)).Append(output)
            .ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var application = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await application.RunAsync(["build", "--source-snapshot", snapshot, "--output", output]);

        Assert.Equal(1, result.ExitCode);
        var report = JsonSerializer.Deserialize<JsonElement>(Assert.Single(result.StandardError.Split('\n'),
            line => line.StartsWith("{\"type\":\"sourceAnalysis\"", StringComparison.Ordinal)));
        Assert.Equal("3.0", report.GetProperty("schemaVersion").GetString());
        Assert.True(report.GetProperty("complete").GetBoolean());
        var diagnostic = Assert.Single(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.GetProperty("code").GetString());
        Assert.Equal(new Uri(callerPath).AbsoluteUri, diagnostic.GetProperty("uri").GetString());
        Assert.Equal(3, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(16, diagnostic.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        var related = Assert.Single(diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(new Uri(targetPath).AbsoluteUri, related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Contains("ByRef type: expected Long, found Integer.", related.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(automation.ImportedSources);
        Assert.Equal(0, automation.SaveCalls);
        foreach (var (path, bytes) in originalBytes) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private sealed class SemanticInputProvider(
        Func<ResolvedProjectContext, CapturedWorkbookTemplate, IReadOnlyList<VbaSyntaxTree>, CancellationToken, Task<VbaProjectSemanticInputs>> acquire)
        : IProjectSemanticInputProvider
    {
        internal int Calls { get; private set; }
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context, CapturedWorkbookTemplate template,
            IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
        {
            Calls++;
            return acquire(context, template, sources, cancellationToken);
        }
    }
}
