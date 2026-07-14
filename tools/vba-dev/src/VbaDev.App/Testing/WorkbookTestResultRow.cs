namespace VbaDev.App.Testing;

/// <summary>
/// Represents one raw test result row read from the workbook test runner output.
/// </summary>
/// <param name="Category">The workbook-reported test module or category.</param>
/// <param name="TestName">The workbook-reported test procedure name.</param>
/// <param name="Result">The workbook result code.</param>
/// <param name="Message">The workbook result message.</param>
/// <param name="Duration">The optional test duration reported by the workbook.</param>
public sealed record WorkbookTestResultRow(
    string Category,
    string TestName,
    string Result,
    string Message,
    TimeSpan? Duration = null);
