namespace VbaDev.App.Testing;

public sealed record TestResultSummary(
    string Document,
    int Total,
    int Passed,
    int Failed,
    int Errors)
{
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
