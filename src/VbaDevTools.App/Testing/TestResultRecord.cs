namespace VbaDevTools.App.Testing;

public sealed record TestResultRecord(
    string Document,
    string Category,
    string TestName,
    string Outcome,
    string Message,
    TimeSpan? Duration = null)
{
    public static TestResultRecord FromWorkbookRow(string document, WorkbookTestResultRow row)
        => new(
            document,
            row.Category,
            row.TestName,
            TestOutcome.FromUnitTestSheetValue(row.Result),
            row.Message,
            row.Duration);
}
