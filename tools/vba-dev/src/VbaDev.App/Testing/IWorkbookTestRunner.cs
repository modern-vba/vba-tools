namespace VbaDev.App.Testing;

/// <summary>
/// Selects the VBA tests to run inside a workbook.
/// </summary>
/// <param name="ModuleName">The optional test module name.</param>
/// <param name="ProcedureName">The optional test procedure name within the selected module.</param>
public sealed record WorkbookTestSelector(string? ModuleName = null, string? ProcedureName = null);

/// <summary>
/// Runs VBA unit tests inside a workbook and returns raw workbook result rows.
/// </summary>
public interface IWorkbookTestRunner
{
    /// <summary>
    /// Runs tests in a workbook using an optional module/procedure selector.
    /// </summary>
    /// <param name="workbookPath">The workbook containing the test runner and test modules.</param>
    /// <param name="selector">The optional test selection.</param>
    /// <returns>The raw workbook result rows.</returns>
    IReadOnlyList<WorkbookTestResultRow> RunTests(string workbookPath, WorkbookTestSelector selector);
}
