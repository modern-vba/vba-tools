namespace VbaDev.App.Workbooks;

/// <summary>
/// Opens workbooks for build, import, publish, and diagnostic automation.
/// </summary>
public interface IWorkbookBuildAutomation
{
    /// <summary>
    /// Opens a workbook and returns a build session over its VBA project.
    /// </summary>
    /// <param name="workbookPath">The workbook path to open.</param>
    /// <returns>An automation session that must be disposed after use.</returns>
    IWorkbookBuildSession OpenWorkbook(string workbookPath);

    /// <summary>
    /// Opens a workbook for a cancellable debug build.
    /// </summary>
    /// <param name="workbookPath">The workbook path to open.</param>
    /// <param name="cancellationToken">Cancels the owned build automation session.</param>
    /// <returns>An automation session that must be disposed after use.</returns>
    IWorkbookBuildSession OpenWorkbook(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return OpenWorkbook(workbookPath);
    }
}
