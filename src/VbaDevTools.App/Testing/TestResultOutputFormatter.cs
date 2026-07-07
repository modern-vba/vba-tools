using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VbaDevTools.App.Testing;

public sealed class TestResultOutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string Format(string format, IReadOnlyList<TestResultRecord> results)
        => format.ToLowerInvariant() switch
        {
            "ndjson" => FormatNdjson(results),
            "json" => FormatJson(results),
            "text" => FormatText(results),
            _ => throw new InvalidOperationException($"Unsupported test output format: {format}")
        };

    private static string FormatNdjson(IReadOnlyList<TestResultRecord> results)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
        {
            builder.Append(JsonSerializer.Serialize(ResultJsonRecord.FromResult(result), JsonOptions));
            builder.Append('\n');
        }

        builder.Append(JsonSerializer.Serialize(SummaryJsonRecord.FromResults(results), JsonOptions));
        builder.Append('\n');
        return builder.ToString();
    }

    private static string FormatJson(IReadOnlyList<TestResultRecord> results)
    {
        var document = TestResultDocumentJsonRecord.FromResults(results);
        return JsonSerializer.Serialize(document, JsonOptions) + "\n";
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

    private sealed record ResultJsonRecord(
        string Type,
        string Document,
        string Category,
        string TestName,
        string Outcome,
        string Message,
        double? DurationMilliseconds)
    {
        public static ResultJsonRecord FromResult(TestResultRecord result)
            => new(
                "result",
                result.Document,
                result.Category,
                result.TestName,
                result.Outcome,
                result.Message,
                result.Duration?.TotalMilliseconds);
    }

    private sealed record SummaryJsonRecord(
        string Type,
        string Document,
        int Total,
        int Passed,
        int Failed,
        int Errors)
    {
        public static SummaryJsonRecord FromResults(IReadOnlyList<TestResultRecord> results)
        {
            var summary = TestResultSummary.FromResults(results);
            return new("summary", summary.Document, summary.Total, summary.Passed, summary.Failed, summary.Errors);
        }
    }

    private sealed record TestResultDocumentJsonRecord(
        string Document,
        TestResultSummaryJsonRecord Summary,
        IReadOnlyList<ResultJsonRecord> Results)
    {
        public static TestResultDocumentJsonRecord FromResults(IReadOnlyList<TestResultRecord> results)
        {
            var summary = TestResultSummary.FromResults(results);
            return new(
                summary.Document,
                new TestResultSummaryJsonRecord(summary.Total, summary.Passed, summary.Failed, summary.Errors),
                results.Select(ResultJsonRecord.FromResult).ToArray());
        }
    }

    private sealed record TestResultSummaryJsonRecord(int Total, int Passed, int Failed, int Errors);
}
