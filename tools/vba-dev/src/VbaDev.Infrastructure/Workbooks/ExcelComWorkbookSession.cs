using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Workbooks;

/// <summary>
/// Owns one hidden Excel COM application and workbook lifecycle.
/// </summary>
internal sealed class ExcelComWorkbookSession : IDisposable
{
    private const int MsoAutomationSecurityLow = 1;

    private readonly ExcelComApplicationProcess? excelProcess;
    private bool disposed;

    private ExcelComWorkbookSession(
        object excelObject,
        object workbookObject,
        ExcelComApplicationProcess? excelProcess)
    {
        ExcelObject = excelObject;
        WorkbookObject = workbookObject;
        this.excelProcess = excelProcess;
    }

    /// <summary>
    /// Gets the Excel.Application COM object.
    /// </summary>
    public object ExcelObject { get; }

    /// <summary>
    /// Gets the open workbook COM object.
    /// </summary>
    public object WorkbookObject { get; }

    /// <summary>
    /// Opens an existing workbook in a dedicated hidden Excel session.
    /// </summary>
    /// <param name="workbookPath">The workbook path to open.</param>
    /// <param name="enableAutomationSecurityLow">Whether macros should be allowed to run in the session.</param>
    /// <returns>The Excel workbook session.</returns>
    public static ExcelComWorkbookSession Open(string workbookPath, bool enableAutomationSecurityLow = false)
    {
        var host = StartHiddenExcel(enableAutomationSecurityLow);
        object? workbookObject = null;
        try
        {
            dynamic workbooks = host.WorkbooksObject;
            workbookObject = workbooks.Open(workbookPath, 0, false);
            return new ExcelComWorkbookSession(host.ExcelObject, workbookObject, host.ExcelProcess);
        }
        catch
        {
            ComObjectReleaser.Release(workbookObject);
            QuitExcel(host.ExcelObject);
            ComObjectReleaser.CollectReleasedComObjects();
            throw;
        }
        finally
        {
            ComObjectReleaser.Release(host.WorkbooksObject);
        }
    }

    /// <summary>
    /// Creates a new workbook in a dedicated hidden Excel session.
    /// </summary>
    /// <returns>The Excel workbook session.</returns>
    public static ExcelComWorkbookSession Create()
    {
        var host = StartHiddenExcel(enableAutomationSecurityLow: false);
        object? workbookObject = null;
        try
        {
            dynamic workbooks = host.WorkbooksObject;
            workbookObject = workbooks.Add();
            return new ExcelComWorkbookSession(host.ExcelObject, workbookObject, host.ExcelProcess);
        }
        catch
        {
            ComObjectReleaser.Release(workbookObject);
            QuitExcel(host.ExcelObject);
            ComObjectReleaser.CollectReleasedComObjects();
            throw;
        }
        finally
        {
            ComObjectReleaser.Release(host.WorkbooksObject);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var safeToTerminateOrphanedExcel = false;
        try
        {
            CloseWorkbook(WorkbookObject);
        }
        finally
        {
            try
            {
                safeToTerminateOrphanedExcel = HasNoOpenWorkbooks(ExcelObject);
                QuitExcel(ExcelObject);
            }
            finally
            {
                ComObjectReleaser.CollectReleasedComObjects();
                if (safeToTerminateOrphanedExcel)
                {
                    excelProcess?.TerminateIfStillRunning();
                }
            }
        }
    }

    private static ExcelComHostObjects StartHiddenExcel(bool enableAutomationSecurityLow)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM automation is supported only on Windows.");
        }

        var existingExcelProcesses = ExcelComApplicationProcess.CaptureRunningExcelProcesses();
        var excelType = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new InvalidOperationException("Excel COM automation is not available.");
        object? excelObject = null;
        object? workbooksObject = null;
        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            if (enableAutomationSecurityLow)
            {
                excel.AutomationSecurity = MsoAutomationSecurityLow;
            }

            var excelProcess = ExcelComApplicationProcess.TryCaptureOwned(excelObject, existingExcelProcesses);
            workbooksObject = excel.Workbooks;
            return new ExcelComHostObjects(excelObject, workbooksObject, excelProcess);
        }
        catch
        {
            ComObjectReleaser.Release(workbooksObject);
            QuitExcel(excelObject);
            ComObjectReleaser.CollectReleasedComObjects();
            throw;
        }
    }

    private static bool HasNoOpenWorkbooks(object excelObject)
    {
        object? workbooksObject = null;
        try
        {
            dynamic excel = excelObject;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            return (int)workbooks.Count == 0;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            ComObjectReleaser.Release(workbooksObject);
        }
    }

    private static void CloseWorkbook(object? workbookObject)
    {
        if (workbookObject is null)
        {
            return;
        }

        try
        {
            dynamic workbook = workbookObject;
            workbook.Close(false);
        }
        finally
        {
            ComObjectReleaser.Release(workbookObject);
        }
    }

    private static void QuitExcel(object? excelObject)
    {
        if (excelObject is null)
        {
            return;
        }

        try
        {
            dynamic excel = excelObject;
            excel.Quit();
        }
        finally
        {
            ComObjectReleaser.Release(excelObject);
        }
    }

    private sealed record ExcelComHostObjects(
        object ExcelObject,
        object WorkbooksObject,
        ExcelComApplicationProcess? ExcelProcess);
}
