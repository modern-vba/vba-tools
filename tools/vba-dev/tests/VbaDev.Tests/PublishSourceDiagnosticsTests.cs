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

public sealed class PublishSourceDiagnosticsTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ExclusionPrecedesValidationWhileOrdinaryBuildStillValidatesTheFullSet(bool commonModule, bool excluded, bool semantic)
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        var manifest = ProjectManifest.CreateDefault("Project", "Book1", project, null);
        if (commonModule) manifest.Documents["Book1"].CommonModules.Add(new("Bad", "Bad.bas", Requested: true, TestOnly: excluded));
        new JsonProjectManifestStore().Save(project, manifest);
        var source = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(source, "Book1.xlsm"), "template", Encoding.UTF8);
        var bad = Path.Combine(source, "Bad.bas");
        // Included CommonModules ignore local markers; only their testOnly bit excludes them.
        var invalidBody = semantic
            ? "Public Sub Broken()\n    Dim item As Integer\n    AcceptValue item\nEnd Sub\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n"
            : "Public Sub Broken()\n    value = \"unterminated\nEnd Sub\n";
        var expectedCode = semantic ? "validation.incompatibleCallArgumentList" : "syntax.unterminatedStringLiteral";
        var text = (commonModule || excluded ? "'#ExcludePublish\n" : "")
            + "Attribute VB_Name = \"Bad\"\n" + invalidBody;
        File.WriteAllText(bad, text, Encoding.UTF8);
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "publish")).FullName, "Book1.xlsm");
        File.WriteAllText(output, "previous-publication", Encoding.UTF8);
        var original = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var app = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation);

        var published = await app.RunAsync(["publish"]);

        Assert.Equal(excluded ? 0 : 1, published.ExitCode);
        if (excluded)
        {
            Assert.Empty(published.StandardError);
            Assert.Empty(automation.ImportedSources);
            Assert.Equal("template", File.ReadAllText(output));
        }
        else
        {
            Assert.Contains(ReadReport(published.StandardError).GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == expectedCode);
            Assert.Empty(automation.OpenedWorkbooks);
            Assert.Equal(original[output], File.ReadAllBytes(output));
        }
        foreach (var (path, bytes) in original.Where(entry => entry.Key != output)) Assert.Equal(bytes, File.ReadAllBytes(path));
        var built = await app.RunAsync(["build"]);
        Assert.Equal(1, built.ExitCode);
        Assert.Contains(ReadReport(built.StandardError).GetProperty("diagnostics").EnumerateArray(),
            diagnostic => diagnostic.GetProperty("code").GetString() == expectedCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExcludedDeclarationsDoNotBindAndIncludedInputsMatchTheSharedAnalyzer(bool excludeTarget)
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var source = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(source, "Book1.xlsm"), "template", Encoding.UTF8);
        var caller = Path.Combine(source, "Caller.bas");
        var target = Path.Combine(source, "Target.bas");
        const string callerText = "Attribute VB_Name = \"Caller\"\nPublic Sub Run()\n    Dim item As Integer\n    AcceptValue item\nEnd Sub\n";
        var targetText = (excludeTarget ? "'#ExcludePublish\n" : "")
            + "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n";
        File.WriteAllText(caller, callerText, Encoding.UTF8);
        File.WriteAllText(target, targetText, Encoding.UTF8);
        var expectedTrees = new List<VbaSyntaxTree> { VbaSyntaxTree.ParseModule(new Uri(caller).AbsoluteUri, callerText) };
        if (!excludeTarget) expectedTrees.Add(VbaSyntaxTree.ParseModule(new Uri(target).AbsoluteUri, targetText));
        var expected = VbaProjectSourceAnalysis.Analyze(expectedTrees);
        var provider = new SemanticInputProvider(sources =>
        {
            Assert.Equal(expectedTrees.Select(tree => tree.Uri), sources.Select(tree => tree.Uri));
            return VbaProjectSemanticInputs.Empty;
        });
        var automation = new FakeWorkbookGenerationAutomation();
        var app = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation, projectSemanticInputProvider: provider);

        var result = await app.RunAsync(["publish"]);

        Assert.Equal(excludeTarget ? 0 : 1, result.ExitCode);
        if (excludeTarget)
        {
            Assert.Empty(expected);
            Assert.Empty(result.StandardError);
            Assert.Equal("Caller.bas", Assert.Single(automation.ImportedSources).FileName);
        }
        else
        {
            Assert.Equal(expected.Select(d => d.Code), ReadReport(result.StandardError).GetProperty("diagnostics").EnumerateArray()
                .Select(d => d.GetProperty("code").GetString()));
            Assert.Empty(automation.OpenedWorkbooks);
        }
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task RequiredMetadataFailureRetainsIncludedErrorsAndPreservesThePreviousPublication()
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var source = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(source, "Book1.xlsm"), "template", Encoding.UTF8);
        foreach (var name in new[] { "One", "Two" }) File.WriteAllText(Path.Combine(source, name + ".bas"),
            $"Attribute VB_Name = \"{name}\"\nPublic Sub {name}(ByVal name As String, ByVal name As Long)\nEnd Sub\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(source, "Excluded.bas"), "'#ExcludePublish\nPublic Sub Bad(\n", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(source, "Broken.bas"), [0xff, 0xfe, 0, 0, 65, 0, 0, 0]);
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "publish")).FullName, "Book1.xlsm");
        File.WriteAllText(output, "previous-publication", Encoding.UTF8);
        var bytes = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var provider = new SemanticInputProvider(sources =>
        {
            Assert.Equal(2, sources.Count);
            throw new IOException("Required publish reference evidence unavailable.");
        });
        var app = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation, projectSemanticInputProvider: provider);

        var result = await app.RunAsync(["publish"]);

        Assert.Equal(1, result.ExitCode);
        var report = ReadReport(result.StandardError);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal(2, report.GetProperty("diagnostics").EnumerateArray().Count(d => d.GetProperty("code").GetString() == "validation.duplicateCallableParameterName"));
        Assert.Equal(2, report.GetProperty("failures").GetArrayLength());
        Assert.Contains(report.GetProperty("failures").EnumerateArray(), failure => failure.GetProperty("message").GetString() == "Required publish reference evidence unavailable.");
        Assert.Empty(automation.OpenedWorkbooks);
        foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PublishRejectsIncludedSemanticErrorAndKeepsPreviousPublication()
    {
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project, ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var source = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        File.WriteAllText(Path.Combine(source, "Book1.xlsm"), "original-template", Encoding.UTF8);
        var caller = Path.Combine(Directory.CreateDirectory(Path.Combine(source, "nested", "日本語")).FullName, "Caller.bas");
        var target = Path.Combine(source, "Target.bas");
        File.WriteAllText(caller, "Attribute VB_Name = \"Caller\"\r\nPublic Sub Run()\r\n    Dim item As Integer\r\n    AcceptValue item\r\nEnd Sub\r\n", Encoding.UTF8);
        File.WriteAllText(target, "Attribute VB_Name = \"Target\"\nPublic Sub AcceptValue(ByRef value As Long)\nEnd Sub\n", Encoding.UTF8);
        var output = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "publish")).FullName, "Book1.xlsm");
        File.WriteAllText(output, "previous-publication", Encoding.UTF8);
        var bytes = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var automation = new FakeWorkbookGenerationAutomation();
        var app = CommandLineTestFactory.Create(project, workbookGenerationAutomation: automation,
            projectSemanticInputProvider: FakeProjectSemanticInputProvider.Empty);

        var result = await app.RunAsync(["publish"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        var report = ReadReport(result.StandardError);
        Assert.True(report.GetProperty("complete").GetBoolean());
        var diagnostic = Assert.Single(report.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("validation.incompatibleCallArgumentList", diagnostic.GetProperty("code").GetString());
        Assert.Equal(new Uri(caller).AbsoluteUri, diagnostic.GetProperty("uri").GetString());
        Assert.Equal(3, diagnostic.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        var related = Assert.Single(diagnostic.GetProperty("relatedInformation").EnumerateArray());
        Assert.Equal(new Uri(target).AbsoluteUri, related.GetProperty("location").GetProperty("uri").GetString());
        Assert.Empty(automation.OpenedWorkbooks);
        Assert.Empty(automation.ImportedSources);
        foreach (var (path, original) in bytes) Assert.Equal(original, File.ReadAllBytes(path));
    }

    private static JsonElement ReadReport(string stderr) => JsonSerializer.Deserialize<JsonElement>(
        Assert.Single(stderr.Split('\n'), line => line.StartsWith("{\"type\":\"sourceAnalysis\"", StringComparison.Ordinal)));

    private sealed class SemanticInputProvider(Func<IReadOnlyList<VbaSyntaxTree>, VbaProjectSemanticInputs> acquire) : IProjectSemanticInputProvider
    {
        internal int Calls { get; private set; }
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context, CapturedWorkbookTemplate template,
            IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(acquire(sources));
        }
    }
}
