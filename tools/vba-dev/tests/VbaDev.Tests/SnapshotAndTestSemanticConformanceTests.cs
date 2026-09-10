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

public sealed class SnapshotAndTestSemanticConformanceTests
{
    public static IEnumerable<object[]> ProjectSemanticCases()
        => ReadCases().SelectMany(fixture => new[] { "snapshot-build", "test", "snapshot-test" }
            .Select(route => new object[] { fixture.GetProperty("id").GetString()!, route }));

    [Theory]
    [MemberData(nameof(ProjectSemanticCases))]
    public async Task GenerationAndTestBuildGatesMatchTheNeutralLiteralCorpus(string caseId, string route)
    {
        var corpus = ReadCases().Single(item => item.GetProperty("id").GetString() == caseId);
        var snapshot = route != "test";
        var test = route != "snapshot-build";
        using var temp = TempDirectory.Create();
        var project = temp.CreateDirectory("Project");
        new JsonProjectManifestStore().Save(project,
            ProjectManifest.CreateDefault("Project", "Book1", project, null));
        var saved = Directory.CreateDirectory(Path.Combine(project, "src", "Book1")).FullName;
        var sourceRoot = snapshot ? temp.CreateDirectory("caller-snapshot") : saved;
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "nested")).FullName;
        var template = Path.Combine(saved, "Book1.xlsm");
        File.WriteAllText(template, "original-template", Encoding.UTF8);
        if (snapshot)
        {
            File.WriteAllText(Path.Combine(saved, "SavedOnly.bas"),
                "Attribute VB_Name = \"SavedOnly\"\nPublic Sub SavedProcedure()\nEnd Sub\n", Encoding.UTF8);
        }
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in corpus.GetProperty("sources").EnumerateArray())
        {
            var fileName = source.GetProperty("fileName").GetString()!;
            var path = Path.Combine(sourceDirectory, fileName);
            File.WriteAllText(path, source.GetProperty("source").GetString()!, Encoding.UTF8);
            sourcePaths.Add(fileName, path);
        }
        var bin = Path.Combine(Directory.CreateDirectory(Path.Combine(project, "bin")).FullName, "Book1.xlsm");
        var output = Path.Combine(temp.CreateDirectory("caller-output"), "Book1.xlsm");
        File.WriteAllText(bin, "previous-completed-bin", Encoding.UTF8);
        File.WriteAllText(output, "previous-caller-output", Encoding.UTF8);
        var originalFiles = Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
        var automation = new FakeWorkbookGenerationAutomation();
        var testInvocations = 0;
        var runner = new FakeWorkbookTestRunner { OnRun = () => testInvocations++ };
        var application = CommandLineTestFactory.Create(project,
            workbookGenerationAutomation: automation,
            workbookTestRunner: runner,
            projectSemanticInputProvider: new LiteralSemanticInputProvider(ReadInputs(corpus)));
        string[] arguments = route switch
        {
            "snapshot-build" => ["build", "--source-snapshot", sourceRoot, "--output", output],
            "test" => ["test", "--format", "ndjson"],
            "snapshot-test" => ["test", "--source-snapshot", sourceRoot, "--format", "ndjson"],
            _ => throw new InvalidOperationException($"Unknown conformance route: {route}.")
        };

        var result = await application.RunAsync(arguments);

        var expected = corpus.GetProperty("diagnostics").EnumerateArray().ToArray();
        if (expected.Length > 0)
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            AssertDiagnostics(expected, ReadReport(result.StandardError), sourcePaths);
            Assert.Empty(automation.OpenedWorkbooks);
            Assert.Empty(automation.ImportedSources);
            Assert.Equal(0, automation.VerifyCalls);
            Assert.Equal(0, automation.SaveCalls);
            Assert.Empty(runner.Workbooks);
            Assert.Equal(0, testInvocations);
            AssertFilesUnchanged(originalFiles);
        }
        else
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.StandardError);
            var stagingWorkbook = Assert.Single(automation.OpenedWorkbooks);
            Assert.False(File.Exists(stagingWorkbook));
            Assert.Equal(1, automation.VerifyCalls);
            Assert.Equal(1, automation.SaveCalls);
            Assert.Equal(sourcePaths.Keys.Order(StringComparer.Ordinal),
                automation.ImportedSources.Select(source => Path.GetFileName(source.SourcePath)).Order(StringComparer.Ordinal));
            Assert.All(automation.ImportedSources, source => Assert.False(File.Exists(source.SourcePath)));
            if (test)
            {
                Assert.Equal(1, testInvocations);
                var testedWorkbook = Assert.Single(runner.Workbooks);
                var events = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                Assert.Equal(new[] { "runStarted", "runFinished" },
                    events.Select(item => item.GetProperty("type").GetString()));
                Assert.Equal("passed", events[^1].GetProperty("outcome").GetString());
                Assert.Equal(0, events[^1].GetProperty("total").GetInt32());
                if (snapshot)
                {
                    Assert.NotEqual(bin, testedWorkbook);
                    Assert.Equal("Book1.xlsm", Path.GetFileName(testedWorkbook));
                    Assert.False(File.Exists(testedWorkbook));
                    AssertFilesUnchanged(originalFiles);
                }
                else
                {
                    Assert.Equal(bin, testedWorkbook);
                    Assert.Equal(originalFiles[template], File.ReadAllBytes(bin));
                    AssertFilesUnchanged(originalFiles, bin);
                }
            }
            else
            {
                Assert.Empty(runner.Workbooks);
                Assert.Equal(0, testInvocations);
                Assert.Contains($"Built {output}", result.StandardOutput, StringComparison.Ordinal);
                Assert.Equal(originalFiles[template], File.ReadAllBytes(output));
                AssertFilesUnchanged(originalFiles, output);
            }
        }
        Assert.Equal(originalFiles.Keys.Order(StringComparer.Ordinal),
            Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    private static void AssertDiagnostics(JsonElement[] expected, JsonElement report,
        IReadOnlyDictionary<string, string> sourcePaths)
    {
        Assert.Equal("3.0", report.GetProperty("schemaVersion").GetString());
        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Empty(report.GetProperty("failures").EnumerateArray());
        var actual = report.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var expectedItem = expected[index];
            var actualItem = actual[index];
            Assert.Equal("diagnostic", actualItem.GetProperty("type").GetString());
            Assert.Equal("vba-dev", actualItem.GetProperty("owner").GetString());
            Assert.Equal(SourceUri(expectedItem), actualItem.GetProperty("uri").GetString());
            Assert.Equal(expectedItem.GetProperty("code").GetString(), actualItem.GetProperty("code").GetString());
            Assert.Equal(expectedItem.GetProperty("cliMessage").GetString(), actualItem.GetProperty("message").GetString());
            Assert.Equal(expectedItem.GetProperty("severity").GetString(), actualItem.GetProperty("severity").GetString());
            Assert.True(JsonElement.DeepEquals(expectedItem.GetProperty("range"), actualItem.GetProperty("range")));
            var expectedRelated = expectedItem.GetProperty("relatedInformation").EnumerateArray().ToArray();
            var actualRelated = actualItem.TryGetProperty("relatedInformation", out var related)
                ? related.EnumerateArray().ToArray() : [];
            Assert.Equal(expectedRelated.Length, actualRelated.Length);
            for (var relatedIndex = 0; relatedIndex < expectedRelated.Length; relatedIndex++)
            {
                var expectedLocation = expectedRelated[relatedIndex];
                var actualLocation = actualRelated[relatedIndex].GetProperty("location");
                Assert.Equal(expectedLocation.GetProperty("message").GetString(),
                    actualRelated[relatedIndex].GetProperty("message").GetString());
                Assert.Equal(SourceUri(expectedLocation), actualLocation.GetProperty("uri").GetString());
                Assert.True(JsonElement.DeepEquals(expectedLocation.GetProperty("range"), actualLocation.GetProperty("range")));
            }
        }
        return;

        string SourceUri(JsonElement item)
            => new Uri(sourcePaths[item.GetProperty("fileName").GetString()!]).AbsoluteUri;
    }

    private static void AssertFilesUnchanged(IReadOnlyDictionary<string, byte[]> originalFiles, string? changedOutput = null)
    {
        foreach (var (path, bytes) in originalFiles.Where(item => item.Key != changedOutput))
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static JsonElement ReadReport(string standardError)
        => JsonSerializer.Deserialize<JsonElement>(Assert.Single(
            standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("{\"type\":\"sourceAnalysis\"", StringComparison.Ordinal)));

    private static JsonElement[] ReadCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "fixtures", "project-semantic-diagnostics", "cases.json")));
        Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
        return document.RootElement.GetProperty("cases").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static VbaProjectSemanticInputs ReadInputs(JsonElement corpus)
    {
        var catalogs = VbaProjectReferenceCatalogSet.Empty;
        var names = new List<string>();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (corpus.TryGetProperty("referenceCatalogs", out var references))
        {
            foreach (var entry in references.EnumerateArray())
            {
                var catalog = entry.Deserialize<VbaProjectReferenceCatalog>(options)!;
                catalogs = catalogs.WithCatalog(catalog);
                names.Add(catalog.ReferenceName);
            }
        }
        var host = corpus.TryGetProperty("hostEvents", out var hostData)
            ? hostData.Deserialize<VbaIntrinsicHostEventCatalog>(options) : null;
        return VbaProjectSemanticInputs.Capture(
            names.Count == 0 ? null : VbaReferenceSelection.Capture(names, null), catalogs, host);
    }

    private sealed class LiteralSemanticInputProvider(VbaProjectSemanticInputs inputs) : IProjectSemanticInputProvider
    {
        public Task<VbaProjectSemanticInputs> AcquireAsync(ResolvedProjectContext context,
            CapturedWorkbookTemplate template, IReadOnlyList<VbaSyntaxTree> sources, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(inputs);
        }
    }
}
