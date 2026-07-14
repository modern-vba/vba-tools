namespace VbaDev.App.Testing;

/// <summary>
/// Carries command-line inputs for a workbook-backed test run.
/// </summary>
/// <param name="Format">The output format, such as text or ndjson.</param>
/// <param name="BuildFirst">Whether the selected document should be built before tests run.</param>
/// <param name="Selector">The optional module or procedure selector.</param>
public sealed record TestCommandRequest(string Format, bool BuildFirst, WorkbookTestSelector Selector);
