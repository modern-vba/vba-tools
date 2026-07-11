namespace VbaDev.App.Export;

/// <summary>
/// Exports VBA project modules from a workbook into a source directory.
/// </summary>
public interface IWorkbookModuleExporter
{
    /// <summary>
    /// Exports all modules from a workbook to a destination directory.
    /// </summary>
    /// <param name="workbookPath">The workbook to export from.</param>
    /// <param name="destinationDirectory">The directory that receives exported source files and sidecars.</param>
    void ExportModules(string workbookPath, string destinationDirectory);
}
