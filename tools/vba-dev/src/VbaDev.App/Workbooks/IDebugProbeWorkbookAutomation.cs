namespace VbaDev.App.Workbooks;

/// <summary>
/// Creates one macro-enabled workbook in a strongly owned hidden Excel process for a debug probe.
/// </summary>
public interface IDebugProbeWorkbookAutomation
{
    /// <summary>
    /// Creates the workbook and returns a build session that owns the hidden Excel process.
    /// </summary>
    IWorkbookBuildSession CreateMacroEnabledWorkbook(
        string workbookPath,
        CancellationToken cancellationToken);
}
