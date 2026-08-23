using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

/// <summary>
/// Exports VBA project modules from a workbook into a source directory.
/// </summary>
public interface IWorkbookModuleExporter
{
    /// <summary>
    /// Exports all modules through the original synchronous adapter contract.
    /// </summary>
    /// <param name="workbookPath">The workbook to export from.</param>
    /// <param name="destinationDirectory">The directory that receives exported source files and sidecars.</param>
    void ExportModules(string workbookPath, string destinationDirectory);

    /// <summary>
    /// Exports all modules from a workbook to a destination directory.
    /// </summary>
    /// <param name="workbookPath">The workbook to export from.</param>
    /// <param name="destinationDirectory">The directory that receives exported source files and sidecars.</param>
    /// <param name="cancellationToken">Cancels workbook automation before destination mutation.</param>
    Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExportModules(workbookPath, destinationDirectory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Exports all modules using caller-resolved workbook automation timeouts.
    /// </summary>
    /// <param name="workbookPath">The workbook to export from.</param>
    /// <param name="destinationDirectory">The directory that receives exported source files and sidecars.</param>
    /// <param name="automationTimeouts">The bounded workbook automation timeouts.</param>
    /// <param name="cancellationToken">Cancels workbook automation before destination mutation.</param>
    Task ExportModulesAsync(
        string workbookPath,
        string destinationDirectory,
        WorkbookAutomationTimeouts automationTimeouts,
        CancellationToken cancellationToken)
        => ExportModulesAsync(workbookPath, destinationDirectory, cancellationToken);
}
