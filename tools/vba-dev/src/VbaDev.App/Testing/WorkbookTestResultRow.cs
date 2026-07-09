namespace VbaDev.App.Testing;

public sealed record WorkbookTestResultRow(
    string Category,
    string TestName,
    string Result,
    string Message,
    TimeSpan? Duration = null);
