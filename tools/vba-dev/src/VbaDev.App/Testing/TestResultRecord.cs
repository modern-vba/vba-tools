namespace VbaDev.App.Testing;

/// <summary>
/// Represents one normalized test procedure result emitted by the test command.
/// </summary>
/// <param name="Document">The document whose workbook produced the result.</param>
/// <param name="Category">The test module or category.</param>
/// <param name="TestName">The test procedure name.</param>
/// <param name="Outcome">The normalized test outcome.</param>
/// <param name="Message">The test result message.</param>
/// <param name="Duration">The optional test duration.</param>
public sealed record TestResultRecord(
    string Document,
    string Category,
    string TestName,
    string Outcome,
    string Message,
    TimeSpan? Duration = null)
{
    /// <summary>
    /// Converts a raw workbook test result row into a normalized result record.
    /// </summary>
    /// <param name="document">The document whose workbook produced the row.</param>
    /// <param name="row">The workbook result row.</param>
    /// <returns>The normalized test result record.</returns>
    public static TestResultRecord FromWorkbookRow(string document, WorkbookTestResultRow row)
        => new(
            document,
            row.Category,
            row.TestName,
            TestOutcome.FromUnitTestSheetValue(row.Result),
            row.Message,
            row.Duration);
}
