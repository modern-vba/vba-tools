using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaDev.App.Testing;

/// <summary>
/// Formats normalized test results as human-readable text or NDJSON events.
/// </summary>
public sealed class TestResultOutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Formats a test run result set.
    /// </summary>
    /// <param name="format">The requested output format.</param>
    /// <param name="project">The project name to include in machine-readable events.</param>
    /// <param name="document">The document name to include in machine-readable events.</param>
    /// <param name="results">The normalized test results.</param>
    /// <returns>The formatted test output.</returns>
    public string Format(string format, string project, string document, IReadOnlyList<TestResultRecord> results)
        => Format(format, TestRun.FromResults(project, document, results));

    /// <summary>
    /// Formats a normalized test run.
    /// </summary>
    /// <param name="format">The requested output format.</param>
    /// <param name="testRun">The normalized test run.</param>
    /// <returns>The formatted test output.</returns>
    public string Format(string format, TestRun testRun)
        => format.ToLowerInvariant() switch
        {
            "ndjson" => FormatNdjson(testRun),
            "text" => FormatText(testRun.Results),
            _ => throw new InvalidOperationException($"Unsupported test output format: {format}")
        };

    private static string FormatNdjson(TestRun testRun)
    {
        var builder = new StringBuilder();
        foreach (var testRunEvent in testRun.CreateEvents())
        {
            builder.Append(JsonSerializer.Serialize(ToJsonRecord(testRunEvent), JsonOptions));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string FormatText(IReadOnlyList<TestResultRecord> results)
    {
        var summary = TestResultSummary.FromResults(results);
        var document = summary.Document.Length == 0 ? "(unknown document)" : summary.Document;
        var builder = new StringBuilder();
        builder.Append($"{document}: {summary.Passed} passed, {summary.Failed} failed, {summary.Errors} errors, {summary.Total} total\n");
        foreach (var result in results)
        {
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? string.Empty
                : $" - {result.Message}";
            builder.Append($"[{result.Outcome}] {result.Category}.{result.TestName}{message}\n");
        }

        return builder.ToString();
    }

    private static object ToJsonRecord(TestRunEvent testRunEvent)
        => testRunEvent switch
        {
            RunStartedTestRunEvent item => new RunStartedJsonRecord(item.Type, item.Project, item.Document),
            TestStartedTestRunEvent item => new TestStartedJsonRecord(
                item.Type,
                item.Project,
                item.Document,
                item.Module,
                item.Procedure),
            TestFinishedTestRunEvent item => new TestFinishedJsonRecord(
                item.Type,
                item.Project,
                item.Document,
                item.Module,
                item.Procedure,
                item.Outcome,
                item.Message,
                item.DurationMilliseconds,
                item.Location),
            RunFinishedTestRunEvent item => new RunFinishedJsonRecord(
                item.Type,
                item.Project,
                item.Document,
                item.Outcome,
                item.Total,
                item.Passed,
                item.Failed,
                item.Errors),
            _ => throw new InvalidOperationException($"Unsupported test event type: {testRunEvent.Type}")
        };

    private sealed record RunStartedJsonRecord(
        string Type,
        string Project,
        string Document);

    private sealed record TestStartedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Module,
        string Procedure);

    private sealed record TestFinishedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Module,
        string Procedure,
        string Outcome,
        string Message,
        double? DurationMilliseconds,
        TestProcedureSourceLocation? Location);

    private sealed record RunFinishedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Outcome,
        int Total,
        int Passed,
        int Failed,
        int Errors);

}
