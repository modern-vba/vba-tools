using VbaDev.Domain;

namespace VbaDev.App.Testing;

/// <summary>Executes tests on the workbook already bound to this automation session.</summary>
public interface IWorkbookTestExecutionSession
{
    /// <summary>Runs the selected tests without reopening, saving, or replacing the bound workbook.</summary>
    Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
        WorkbookTestSelector selector,
        TimeSpan executionTimeout,
        CancellationToken cancellationToken);
}
