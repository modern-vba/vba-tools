namespace VbaDev.App.Testing;

using VbaDev.App.Workbooks;

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
    /// Runs tests through the original synchronous extension contract.
    /// </summary>
    IReadOnlyList<WorkbookTestResultRow> RunTests(
        string workbookPath,
        WorkbookTestSelector selector)
        => RunTestsAsync(
                workbookPath,
                selector,
                TimeSpan.FromSeconds(600),
                WorkbookAutomationTimeouts.Default,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Runs tests through bounded, strongly owned workbook automation.
    /// </summary>
    Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        string workbookPath,
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
        => Task.FromResult(RunTests(workbookPath, selector));
}
