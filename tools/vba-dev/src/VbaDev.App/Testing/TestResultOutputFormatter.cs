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
        => format.ToLowerInvariant() switch
        {
            "ndjson" => FormatNdjson(project, document, results),
            "text" => FormatText(results),
            _ => throw new InvalidOperationException($"Unsupported test output format: {format}")
        };

    private static string FormatNdjson(string project, string document, IReadOnlyList<TestResultRecord> results)
    {
        var builder = new StringBuilder();
        builder.Append(JsonSerializer.Serialize(new RunStartedJsonRecord("runStarted", project, document), JsonOptions));
        builder.Append('\n');

        foreach (var result in results)
        {
            builder.Append(JsonSerializer.Serialize(TestStartedJsonRecord.FromResult(project, result), JsonOptions));
            builder.Append('\n');
            builder.Append(JsonSerializer.Serialize(TestFinishedJsonRecord.FromResult(project, result), JsonOptions));
            builder.Append('\n');
        }

        builder.Append(JsonSerializer.Serialize(RunFinishedJsonRecord.FromResults(project, document, results), JsonOptions));
        builder.Append('\n');
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

    private sealed record RunStartedJsonRecord(
        string Type,
        string Project,
        string Document);

    private sealed record TestStartedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Module,
        string Procedure)
    {
        /// <summary>
        /// Creates a JSON record that marks the start of a single workbook test.
        /// </summary>
        /// <param name="project">The project that produced the test result.</param>
        /// <param name="result">The test result to describe.</param>
        /// <returns>The JSON output record.</returns>
        public static TestStartedJsonRecord FromResult(string project, TestResultRecord result)
            => new(
                "testStarted",
                project,
                result.Document,
                result.Category,
                result.TestName);
    }

    private sealed record TestFinishedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Module,
        string Procedure,
        string Outcome,
        string Message,
        double? DurationMilliseconds)
    {
        /// <summary>
        /// Creates a JSON record that reports the completion of a single workbook test.
        /// </summary>
        /// <param name="project">The project that produced the test result.</param>
        /// <param name="result">The completed test result.</param>
        /// <returns>The JSON output record.</returns>
        public static TestFinishedJsonRecord FromResult(string project, TestResultRecord result)
            => new(
                "testFinished",
                project,
                result.Document,
                result.Category,
                result.TestName,
                result.Outcome,
                result.Message,
                result.Duration?.TotalMilliseconds);
    }

    private sealed record RunFinishedJsonRecord(
        string Type,
        string Project,
        string Document,
        string Outcome,
        int Total,
        int Passed,
        int Failed,
        int Errors)
    {
        /// <summary>
        /// Creates a JSON record that summarizes the completed workbook test run.
        /// </summary>
        /// <param name="project">The project that produced the test results.</param>
        /// <param name="document">The workbook document that was tested.</param>
        /// <param name="results">The completed test results.</param>
        /// <returns>The JSON output record.</returns>
        public static RunFinishedJsonRecord FromResults(string project, string document, IReadOnlyList<TestResultRecord> results)
        {
            var summary = TestResultSummary.FromResults(results);
            var outcome = summary.Failed == 0 && summary.Errors == 0
                ? TestOutcome.Passed
                : TestOutcome.Failed;
            return new("runFinished", project, document, outcome, summary.Total, summary.Passed, summary.Failed, summary.Errors);
        }
    }

}
