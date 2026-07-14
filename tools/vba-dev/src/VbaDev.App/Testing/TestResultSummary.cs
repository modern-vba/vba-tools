namespace VbaDev.App.Testing;

/// <summary>
/// Summarizes normalized test results for one document.
/// </summary>
/// <param name="Document">The document name shared by the summarized results.</param>
/// <param name="Total">The total number of test results.</param>
/// <param name="Passed">The number of passing tests.</param>
/// <param name="Failed">The number of failing assertion results.</param>
/// <param name="Errors">The number of execution error results.</param>
public sealed record TestResultSummary(
    string Document,
    int Total,
    int Passed,
    int Failed,
    int Errors)
{
    /// <summary>
    /// Creates a summary from normalized test result records.
    /// </summary>
    /// <param name="results">The result records to summarize.</param>
    /// <returns>The aggregate result summary.</returns>
    public static TestResultSummary FromResults(IReadOnlyList<TestResultRecord> results)
    {
        var document = results.Count == 0 ? string.Empty : results[0].Document;
        return new TestResultSummary(
            document,
            results.Count,
            results.Count(result => result.Outcome.Equals(TestOutcome.Passed, StringComparison.OrdinalIgnoreCase)),
            results.Count(result => result.Outcome.Equals(TestOutcome.Failed, StringComparison.OrdinalIgnoreCase)),
            results.Count(result => result.Outcome.Equals(TestOutcome.Error, StringComparison.OrdinalIgnoreCase)));
    }
}
