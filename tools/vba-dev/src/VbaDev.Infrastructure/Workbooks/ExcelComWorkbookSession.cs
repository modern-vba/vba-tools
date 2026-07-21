using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IOwnedExcelSessionStartFailure
{
    Exception StartException { get; }

    Exception? CleanupException { get; }

    bool CleanupVerified { get; }
}

internal sealed class OwnedExcelSessionStartException(
    Exception startException,
    Exception? cleanupException,
    bool cleanupVerified) :
    DebugSetupException(startException.Message, startException),
    IOwnedExcelSessionStartFailure
{
    public Exception StartException { get; } = startException;

    public Exception? CleanupException { get; } = cleanupException;

    public bool CleanupVerified { get; } = cleanupVerified && cleanupException is null;
}

internal sealed class OwnedExcelSessionStartCanceledException(
    OperationCanceledException startException,
    Exception? cleanupException,
    bool cleanupVerified) :
    OperationCanceledException(
        startException.Message,
        startException,
        startException.CancellationToken),
    IOwnedExcelSessionStartFailure
{
    public Exception StartException { get; } = startException;

    public Exception? CleanupException { get; } = cleanupException;

    public bool CleanupVerified { get; } = cleanupVerified && cleanupException is null;
}

/// <summary>
/// Owns one hidden Excel COM application and workbook lifecycle.
/// </summary>
internal sealed class ExcelComWorkbookSession : IDisposable
{
    private const int MsoAutomationSecurityLow = 1;

    private readonly ExcelComApplicationProcess? excelProcess;
    private readonly DebugExcelProcessOwner? strongExcelProcess;
    private readonly CancellationTokenRegistration cancellationRegistration;
    private bool disposed;

    private ExcelComWorkbookSession(
        object excelObject,
        object workbookObject,
        ExcelComApplicationProcess? excelProcess,
        DebugExcelProcessOwner? strongExcelProcess,
        CancellationTokenRegistration cancellationRegistration)
    {
        ExcelObject = excelObject;
        WorkbookObject = workbookObject;
        this.excelProcess = excelProcess;
        this.strongExcelProcess = strongExcelProcess;
        this.cancellationRegistration = cancellationRegistration;
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
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow,
            requireStrongOwnership: false,
            CancellationToken.None);

    /// <summary>
    /// Opens a workbook in a strictly identified, kill-on-close Excel process for a debug build.
    /// </summary>
    internal static ExcelComWorkbookSession OpenOwnedForDebugBuild(
        string workbookPath,
        CancellationToken cancellationToken)
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow: false,
            requireStrongOwnership: true,
            cancellationToken);

    private static ExcelComWorkbookSession OpenCore(
        string workbookPath,
        bool enableAutomationSecurityLow,
        bool requireStrongOwnership,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = StartHiddenExcel(
            enableAutomationSecurityLow,
            requireStrongOwnership,
            cancellationToken);
        object? workbookObject = null;
        try
        {
            dynamic workbooks = host.WorkbooksObject;
            workbookObject = workbooks.Open(workbookPath, 0, false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelComWorkbookSession(
                host.ExcelObject,
                workbookObject,
                host.ExcelProcess,
                host.StrongExcelProcess,
                host.CancellationRegistration);
        }
        catch
        {
            host.CancellationRegistration.Dispose();
            ComObjectReleaser.Release(workbookObject);
            try
            {
                QuitExcel(host.ExcelObject);
            }
            catch (COMException)
            {
                ComObjectReleaser.Release(host.ExcelObject);
            }
            finally
            {
                DisposeStrongOwner(host.StrongExcelProcess);
                ComObjectReleaser.CollectReleasedComObjects();
            }

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
        var host = StartHiddenExcel(
            enableAutomationSecurityLow: false,
            requireStrongOwnership: false,
            CancellationToken.None);
        object? workbookObject = null;
        try
        {
            dynamic workbooks = host.WorkbooksObject;
            workbookObject = workbooks.Add();
            return new ExcelComWorkbookSession(
                host.ExcelObject,
                workbookObject,
                host.ExcelProcess,
                host.StrongExcelProcess,
                host.CancellationRegistration);
        }
        catch
        {
            host.CancellationRegistration.Dispose();
            ComObjectReleaser.Release(workbookObject);
            QuitExcel(host.ExcelObject);
            DisposeStrongOwner(host.StrongExcelProcess);
            ComObjectReleaser.CollectReleasedComObjects();
            throw;
        }
        finally
        {
            ComObjectReleaser.Release(host.WorkbooksObject);
        }
    }

    /// <summary>
    /// Creates a new workbook in a strictly identified, kill-on-close Excel process for a debug probe.
    /// </summary>
    internal static ExcelComWorkbookSession CreateOwnedForDebugBuild(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var host = StartHiddenExcel(
            enableAutomationSecurityLow: false,
            requireStrongOwnership: true,
            cancellationToken);
        object? workbookObject = null;
        try
        {
            dynamic workbooks = host.WorkbooksObject;
            workbookObject = workbooks.Add();
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelComWorkbookSession(
                host.ExcelObject,
                workbookObject,
                host.ExcelProcess,
                host.StrongExcelProcess,
                host.CancellationRegistration);
        }
        catch (Exception startException)
        {
            Exception? cleanupException = null;
            try
            {
                host.CancellationRegistration.Dispose();
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }

            ComObjectReleaser.Release(workbookObject);
            try
            {
                QuitExcel(host.ExcelObject);
            }
            catch (Exception ex)
            {
                cleanupException ??= ex;
            }
            finally
            {
                try
                {
                    DisposeStrongOwner(host.StrongExcelProcess);
                }
                catch (Exception ex)
                {
                    cleanupException ??= ex;
                }
                finally
                {
                    ComObjectReleaser.CollectReleasedComObjects();
                }
            }

            throw CreateOwnedSessionStartFailure(
                startException,
                cleanupException,
                cleanupVerified: cleanupException is null && host.StrongExcelProcess is not null);
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
        Exception? cleanupError = null;
        try
        {
            CloseWorkbook(WorkbookObject);
        }
        catch (Exception ex)
        {
            cleanupError = ex;
        }
        finally
        {
            try
            {
                if (strongExcelProcess is null)
                {
                    safeToTerminateOrphanedExcel = HasNoOpenWorkbooks(ExcelObject);
                }

                QuitExcel(ExcelObject);
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }
            finally
            {
                cancellationRegistration.Dispose();
                try
                {
                    if (strongExcelProcess is not null)
                    {
                        DisposeStrongOwner(strongExcelProcess);
                    }
                    else if (safeToTerminateOrphanedExcel)
                    {
                        excelProcess?.TerminateIfStillRunning();
                    }
                }
                catch (Exception ex)
                {
                    cleanupError ??= ex;
                }
                finally
                {
                    ComObjectReleaser.CollectReleasedComObjects();
                }
            }
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private static ExcelComHostObjects StartHiddenExcel(
        bool enableAutomationSecurityLow,
        bool requireStrongOwnership,
        CancellationToken cancellationToken)
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
        DebugExcelProcessOwner? strongExcelProcess = null;
        CancellationTokenRegistration cancellationRegistration = default;
        try
        {
            excelObject = Activator.CreateInstance(excelType)
                ?? throw new InvalidOperationException("Excel COM automation could not be started.");
            dynamic excel = excelObject;
            ExcelComApplicationProcess? excelProcess = null;
            if (requireStrongOwnership)
            {
                var windowHandle = new nint(Convert.ToInt64(excel.Hwnd));
                strongExcelProcess = DebugExcelProcessOwner.Capture(
                    windowHandle,
                    existingExcelProcesses,
                    new WindowsDebugExcelProcessApi());
                cancellationRegistration = cancellationToken.UnsafeRegister(
                    static state =>
                        _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                    strongExcelProcess);
            }
            else
            {
                excelProcess = ExcelComApplicationProcess.TryCaptureOwned(
                    excelObject,
                    existingExcelProcesses);
            }

            cancellationToken.ThrowIfCancellationRequested();
            excel.Visible = false;
            excel.DisplayAlerts = false;
            if (enableAutomationSecurityLow)
            {
                excel.AutomationSecurity = MsoAutomationSecurityLow;
            }

            workbooksObject = excel.Workbooks;
            return new ExcelComHostObjects(
                excelObject,
                workbooksObject,
                excelProcess,
                strongExcelProcess,
                cancellationRegistration);
        }
        catch (Exception startException) when (requireStrongOwnership)
        {
            var ownershipEstablished = strongExcelProcess is not null;
            var noTemporaryProcessWasCreated = excelObject is null ||
                startException is ExistingExcelProcessOwnershipRejectedException;
            Exception? cleanupException = null;
            try
            {
                cancellationRegistration.Dispose();
            }
            catch (Exception ex)
            {
                cleanupException = ex;
            }

            try
            {
                ComObjectReleaser.Release(workbooksObject);
            }
            catch (Exception ex)
            {
                cleanupException ??= ex;
            }

            if (ownershipEstablished)
            {
                try
                {
                    QuitExcel(excelObject);
                }
                catch (Exception ex)
                {
                    cleanupException ??= ex;
                }
            }
            else
            {
                try
                {
                    ComObjectReleaser.Release(excelObject);
                }
                catch (Exception ex)
                {
                    cleanupException ??= ex;
                }
            }

            try
            {
                DisposeStrongOwner(strongExcelProcess);
            }
            catch (Exception ex)
            {
                cleanupException ??= ex;
            }
            finally
            {
                ComObjectReleaser.CollectReleasedComObjects();
            }

            var cleanupVerified = cleanupException is null &&
                (ownershipEstablished || noTemporaryProcessWasCreated);
            throw CreateOwnedSessionStartFailure(
                startException,
                cleanupException,
                cleanupVerified);
        }
        catch
        {
            cancellationRegistration.Dispose();
            ComObjectReleaser.Release(workbooksObject);
            try
            {
                QuitExcel(excelObject);
            }
            catch (COMException)
            {
                ComObjectReleaser.Release(excelObject);
            }
            finally
            {
                DisposeStrongOwner(strongExcelProcess);
                ComObjectReleaser.CollectReleasedComObjects();
            }

            throw;
        }
    }

    private static void DisposeStrongOwner(DebugExcelProcessOwner? owner)
        => owner?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static Exception CreateOwnedSessionStartFailure(
        Exception startException,
        Exception? cleanupException,
        bool cleanupVerified)
        => startException is OperationCanceledException cancellation
            ? new OwnedExcelSessionStartCanceledException(
                cancellation,
                cleanupException,
                cleanupVerified)
            : new OwnedExcelSessionStartException(
                startException,
                cleanupException,
                cleanupVerified);

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
        ExcelComApplicationProcess? ExcelProcess,
        DebugExcelProcessOwner? StrongExcelProcess,
        CancellationTokenRegistration CancellationRegistration);
}
