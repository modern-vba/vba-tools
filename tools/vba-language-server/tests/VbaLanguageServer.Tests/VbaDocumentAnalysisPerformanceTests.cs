using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using VbaLanguageServer.ProjectModel;
using VbaLanguageServer.SourceModel;
using VbaLanguageServer.Syntax;
using VbaLanguageServer.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace VbaLanguageServer.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VbaDocumentAnalysisPerformanceTestCollection
{
    public const string Name = "Document analysis performance";
}

[Collection(VbaDocumentAnalysisPerformanceTestCollection.Name)]
public sealed class VbaDocumentAnalysisPerformanceTests
{
    private const int ReferenceLineCount = 8_000;
    private readonly ITestOutputHelper output;

    public VbaDocumentAnalysisPerformanceTests(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Release_ordinary_member_edit_analysis_p95_is_at_most_30_milliseconds()
    {
        var warmupCount = IsReleaseBuild ? 20 : 5;
        var measuredCount = IsReleaseBuild ? 200 : 20;
        const string uri = "file:///C:/work/AnalysisPerformance.bas";
        var firstText = CreateAnalysisFixture(editedValue: 1);
        var secondText = CreateAnalysisFixture(editedValue: 2);
        var workspace = new VbaLanguageWorkspace(
            new VbaProjectReferenceCatalogCache(VbaProjectReferenceCatalogSet.CreateBundled()));
        workspace.OpenDocument(uri, version: 1, firstText);
        var version = 1;

        for (var index = 0; index < warmupCount; index++)
        {
            var updateKind = workspace.ChangeDocument(
                uri,
                ++version,
                index % 2 == 0 ? secondText : firstText);
            Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, updateKind);
        }

        var measurements = new TimeSpan[measuredCount];
        for (var index = 0; index < measurements.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var updateKind = workspace.ChangeDocument(
                uri,
                ++version,
                index % 2 == 0 ? secondText : firstText);
            measurements[index] = Stopwatch.GetElapsedTime(started);
            Assert.Equal(VbaSyntaxTreeParseUpdateKind.ModuleMember, updateKind);
        }

        var p95 = Percentile(measurements, 0.95);
        output.WriteLine(
            "document analysis ({0}) p95={1:F3} ms, samples={2}",
            BuildConfiguration,
            p95.TotalMilliseconds,
            measurements.Length);
        if (IsReleaseBuild)
        {
            Assert.True(
                p95 <= TimeSpan.FromMilliseconds(30),
                $"Document analysis p95 was {p95.TotalMilliseconds:F3} ms.");
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Release_block_skeleton_processing_p95_and_p99_stay_within_budget()
    {
        var warmupCount = IsReleaseBuild ? 20 : 2;
        var measuredCount = IsReleaseBuild ? 200 : 5;
        var timingRoot = Directory.CreateTempSubdirectory(
            "vba-ls-analysis-performance-").FullName;
        try
        {
            await using var server = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = timingRoot
                });
            await server.InitializeAsync();
            const string uri = "file:///C:/work/BlockSkeletonPerformance.bas";
            const int version = 1;
            const int headerLine = ReferenceLineCount - 2;
            const string header = "Public Function Pending() As String";
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(
                    uri,
                    version,
                    CreateBlockSkeletonFixture(editedValue: 1)));
            await server.WaitForDiagnosticsAsync(uri, TimeSpan.FromSeconds(30));

            var requestId = 10;
            for (var index = 0; index < warmupCount; index++)
            {
                await SendBlockSkeletonRequestAsync(
                    server,
                    requestId++,
                    uri,
                    version,
                    headerLine,
                    header.Length);
            }

            var measurements = new TimeSpan[measuredCount];
            for (var index = 0; index < measurements.Length; index++)
            {
                var measuredRequestId = requestId++;
                await SendBlockSkeletonRequestAsync(
                    server,
                    measuredRequestId,
                    uri,
                    version,
                    headerLine,
                    header.Length);
                measurements[index] = await ReadExecutionTimeAsync(
                    timingRoot,
                    "vba/blockSkeletonInsertion",
                    measuredRequestId,
                    TimeSpan.FromSeconds(10));
            }

            var p95 = Percentile(measurements, 0.95);
            var p99 = Percentile(measurements, 0.99);
            output.WriteLine(
                "block skeleton ({0}) p95={1:F3} ms, p99={2:F3} ms, samples={3}",
                BuildConfiguration,
                p95.TotalMilliseconds,
                p99.TotalMilliseconds,
                measurements.Length);
            if (IsReleaseBuild)
            {
                Assert.True(
                    p95 <= TimeSpan.FromMilliseconds(50),
                    $"Block-skeleton processing p95 was {p95.TotalMilliseconds:F3} ms.");
                Assert.True(
                    p99 <= TimeSpan.FromMilliseconds(75),
                    $"Block-skeleton processing p99 was {p99.TotalMilliseconds:F3} ms.");
            }

            await server.ShutdownAsync(requestId);
        }
        finally
        {
            Directory.Delete(timingRoot, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Release_warm_completion_hover_and_signature_help_stay_within_budget()
    {
        var warmupCount = IsReleaseBuild ? 20 : 2;
        var measuredCount = IsReleaseBuild ? 200 : 5;
        var timingRoot = Directory.CreateTempSubdirectory(
            "vba-ls-warm-query-performance-").FullName;
        try
        {
            await using var server = await LanguageServerProcessHarness.StartAsync(
                environment: new Dictionary<string, string>
                {
                    ["VBA_TOOLS_INTERACTIVE_ADMISSION_DIRECTORY"] = timingRoot
                });
            await server.InitializeAsync();
            const string uri = "file:///C:/work/WarmQueryPerformance.bas";
            const string invocation = "    result = BuildValue(";
            var text = string.Join('\n',
            [
                "Attribute VB_Name = \"WarmQueryPerformance\"",
                "Public Sub Caller()",
                "    Dim result As String",
                invocation,
                "End Sub",
                "Public Function BuildValue(ByVal value As Long) As String",
                "End Function"
            ]);
            await server.SendNotificationAsync(
                "textDocument/didOpen",
                CreateOpenDocument(uri, version: 1, text));
            await server.WaitForDiagnosticsAsync(uri);
            var cases = new[]
            {
                new WarmQueryCase(
                    "textDocument/completion",
                    Line: 3,
                    Character: "    result = Build".Length),
                new WarmQueryCase(
                    "textDocument/hover",
                    Line: 3,
                    Character: "    result = Build".Length),
                new WarmQueryCase(
                    "textDocument/signatureHelp",
                    Line: 3,
                    Character: invocation.Length)
            };
            var requestId = 10;
            foreach (var queryCase in cases)
            {
                for (var index = 0; index < warmupCount; index++)
                {
                    await SendPositionRequestAsync(
                        server,
                        requestId++,
                        queryCase,
                        uri);
                }

                var measurements = new TimeSpan[measuredCount];
                for (var index = 0; index < measurements.Length; index++)
                {
                    var measuredRequestId = requestId++;
                    await SendPositionRequestAsync(
                        server,
                        measuredRequestId,
                        queryCase,
                        uri);
                    measurements[index] = await ReadExecutionTimeAsync(
                        timingRoot,
                        queryCase.Method,
                        measuredRequestId,
                        TimeSpan.FromSeconds(10));
                }

                var p95 = Percentile(measurements, 0.95);
                var p99 = Percentile(measurements, 0.99);
                output.WriteLine(
                    "{0} ({1}) p95={2:F3} ms, p99={3:F3} ms, samples={4}",
                    queryCase.Method,
                    BuildConfiguration,
                    p95.TotalMilliseconds,
                    p99.TotalMilliseconds,
                    measurements.Length);
                if (IsReleaseBuild)
                {
                    Assert.True(
                        p95 <= TimeSpan.FromMilliseconds(25),
                        $"{queryCase.Method} p95 was {p95.TotalMilliseconds:F3} ms.");
                    Assert.True(
                        p99 <= TimeSpan.FromMilliseconds(50),
                        $"{queryCase.Method} p99 was {p99.TotalMilliseconds:F3} ms.");
                }
            }

            await server.ShutdownAsync(requestId);
        }
        finally
        {
            Directory.Delete(timingRoot, recursive: true);
        }
    }

    private static string BuildConfiguration
        => typeof(VbaDocumentAnalysisPerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
            ?? "Debug";

    private static bool IsReleaseBuild
        => BuildConfiguration.Equals("Release", StringComparison.OrdinalIgnoreCase);

    private static string CreateAnalysisFixture(int editedValue)
    {
        var lines = CreateReferenceMemberLines(editedValue);
        lines.Add("Public Sub Tail()");
        lines.Add("    Dim value As Long");
        lines.Add("    value = 1");
        lines.Add("    If value > 0 Then");
        lines.Add("        value = value - 1");
        lines.Add("    End If");
        lines.Add("End Sub");
        Assert.Equal(ReferenceLineCount, lines.Count);
        return string.Join('\n', lines);
    }

    private static string CreateBlockSkeletonFixture(int editedValue)
    {
        var lines = CreateReferenceMemberLines(editedValue);
        lines.Add("' padding 1");
        lines.Add("' padding 2");
        lines.Add("' padding 3");
        lines.Add("' padding 4");
        lines.Add("' padding 5");
        lines.Add("Public Function Pending() As String");
        lines.Add("    ");
        Assert.Equal(ReferenceLineCount, lines.Count);
        return string.Join('\n', lines);
    }

    private static List<string> CreateReferenceMemberLines(int editedValue)
    {
        var lines = new List<string>(ReferenceLineCount)
        {
            "Attribute VB_Name = \"AnalysisPerformance\""
        };
        for (var memberIndex = 0; memberIndex < 999; memberIndex++)
        {
            var value = memberIndex == 499 ? editedValue : 1;
            lines.Add($"Public Sub Routine{memberIndex:D3}()");
            lines.Add("    Dim value As Long");
            lines.Add($"    value = {memberIndex}");
            lines.Add($"    value = value + {value}");
            lines.Add("    If value > 0 Then");
            lines.Add("        value = value - 1");
            lines.Add("    End If");
            lines.Add("End Sub");
        }

        return lines;
    }

    private static object CreateOpenDocument(string uri, int version, string text)
        => new
        {
            textDocument = new
            {
                uri,
                languageId = "vba",
                version,
                text
            }
        };

    private static async Task SendBlockSkeletonRequestAsync(
        LanguageServerProcessHarness server,
        int requestId,
        string uri,
        int version,
        int line,
        int character)
    {
        var response = await server.SendRequestAsync(
            requestId,
            "vba/blockSkeletonInsertion",
            new
            {
                documentUri = uri,
                documentVersion = version,
                position = new { line, character },
                options = new
                {
                    insertSpaces = true,
                    indentSize = 4,
                    tabSize = 4
                }
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.Equal(
            version,
            response.GetProperty("result").GetProperty("documentVersion").GetInt32());
    }

    private static async Task<TimeSpan> ReadExecutionTimeAsync(
        string timingRoot,
        string method,
        int requestId,
        TimeSpan timeout)
    {
        var sanitizedMethod = method.Replace('/', '_');
        var suffix =
            $"-request-{sanitizedMethod}-number-{requestId}.completed";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            var path = Directory.EnumerateFiles(timingRoot, "*.completed")
                .FirstOrDefault(
                    candidate => candidate.EndsWith(
                        suffix,
                        StringComparison.Ordinal));
            if (path is not null)
            {
                try
                {
                    var line = File.ReadLines(path).FirstOrDefault(
                        candidate => candidate.StartsWith(
                            "executionMilliseconds=",
                            StringComparison.Ordinal));
                    if (line is not null
                        && double.TryParse(
                            line["executionMilliseconds=".Length..],
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out var milliseconds))
                    {
                        return TimeSpan.FromMilliseconds(milliseconds);
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"No completion timing was recorded for request {requestId}.");
    }

    private static async Task SendPositionRequestAsync(
        LanguageServerProcessHarness server,
        int requestId,
        WarmQueryCase queryCase,
        string uri)
    {
        var response = await server.SendRequestAsync(
            requestId,
            queryCase.Method,
            new
            {
                textDocument = new { uri },
                position = new
                {
                    line = queryCase.Line,
                    character = queryCase.Character
                }
            },
            timeout: TimeSpan.FromSeconds(30));
        Assert.True(response.TryGetProperty("result", out _));
    }

    private static TimeSpan Percentile(
        IEnumerable<TimeSpan> values,
        double percentile)
    {
        var ordered = values.Order().ToArray();
        var index = (int)Math.Ceiling(ordered.Length * percentile) - 1;
        return ordered[Math.Max(0, index)];
    }

    private sealed record WarmQueryCase(
        string Method,
        int Line,
        int Character);
}
