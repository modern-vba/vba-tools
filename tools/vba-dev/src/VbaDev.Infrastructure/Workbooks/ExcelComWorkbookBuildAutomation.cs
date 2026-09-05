using VbaDev.App.Workbooks;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Implements workbook build automation through Excel COM and VBIDE.
/// </summary>
public sealed partial class ExcelComWorkbookBuildAutomation : IWorkbookBuildAutomation
{
    /// <summary>
    /// Opens an Excel workbook for VBA project build operations.
    /// </summary>
    /// <param name="workbookPath">The workbook path to open.</param>
    /// <returns>An Excel COM-backed workbook build session.</returns>
    public IWorkbookBuildSession OpenWorkbook(string workbookPath)
        => OpenWorkbook(workbookPath, CancellationToken.None);

    /// <summary>
    /// Opens an Excel workbook in a strongly owned, cancellable build process.
    /// </summary>
    public IWorkbookBuildSession OpenWorkbook(
        string workbookPath,
        CancellationToken cancellationToken)
        => new ExcelComWorkbookBuildSession(ExcelComWorkbookSession.OpenOwnedForBuild(
            workbookPath,
            cancellationToken));
}
