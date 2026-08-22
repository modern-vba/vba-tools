using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using VbaDev.App.Debugging;
using VbaDev.App.Workbooks;
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
    private readonly OwnedExcelTerminationController? terminationController;
    private readonly CancellationTokenRegistration cancellationRegistration;
    private bool disposed;

    private ExcelComWorkbookSession(
        object excelObject,
        object workbookObject,
        ExcelComApplicationProcess? excelProcess,
        DebugExcelProcessOwner? strongExcelProcess,
        OwnedExcelTerminationController? terminationController,
        CancellationTokenRegistration cancellationRegistration)
    {
        ExcelObject = excelObject;
        WorkbookObject = workbookObject;
        this.excelProcess = excelProcess;
        this.strongExcelProcess = strongExcelProcess;
        this.terminationController = terminationController;
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

    /// <summary>
    /// Starts a hidden Excel application and establishes exact process ownership before workbook open.
    /// </summary>
    internal static ExcelComHostObjects StartOwnedForGeneration(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
        => StartHiddenExcel(
            enableAutomationSecurityLow: false,
            requireStrongOwnership: true,
            cancellationToken,
            terminationController);

    /// <summary>
    /// Opens a staged workbook in an already owned hidden Excel application.
    /// </summary>
    internal static ExcelComWorkbookSession OpenOwnedForGeneration(
        ExcelComHostObjects host,
        string workbookPath)
    {
        dynamic workbooks = host.WorkbooksObject;
        var workbookObject = workbooks.Open(workbookPath, 0, false);
        var session = new ExcelComWorkbookSession(
            host.ExcelObject,
            workbookObject,
            host.ExcelProcess,
            host.StrongExcelProcess,
            host.TerminationController,
            host.CancellationRegistration);
        ComObjectReleaser.Release(host.WorkbooksObject);
        return session;
    }

    /// <summary>
    /// Releases an owned Excel application when workbook open did not complete.
    /// </summary>
    internal static void DisposeOwnedGenerationHost(
        ExcelComHostObjects host,
        TimeSpan cleanupGrace)
    {
        host.TerminationController?.RequestForcedTermination(cleanupGrace);
        Exception? cleanupError = null;
        try
        {
            ComObjectReleaser.Release(host.WorkbooksObject);
            QuitExcel(host.ExcelObject);
        }
        catch (Exception ex)
        {
            cleanupError = ex;
        }
        finally
        {
            host.CancellationRegistration.Dispose();
            ComObjectReleaser.CollectReleasedComObjects();
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

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
                host.TerminationController,
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
                host.TerminationController,
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
                host.TerminationController,
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
    public void Dispose() => DisposeCore(cleanupGrace: null);

    /// <summary>
    /// Cooperatively closes workbook and Excel, then force-terminates only the owned process after the grace period.
    /// </summary>
    internal void DisposeOwnedGeneration(TimeSpan cleanupGrace)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        terminationController?.RequestForcedTermination(cleanupGrace);
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
                QuitExcel(ExcelObject);
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }
            finally
            {
                cancellationRegistration.Dispose();
                ComObjectReleaser.CollectReleasedComObjects();
            }
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private void DisposeCore(TimeSpan? cleanupGrace)
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var safeToTerminateOrphanedExcel = false;
        Exception? cleanupError = null;
        Exception? ownershipCleanupError = null;
        var ownershipCleanupVerified = false;
        if (cleanupGrace is not null)
        {
            terminationController?.RequestForcedTermination(cleanupGrace.Value);
        }

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
                        if (cleanupGrace is null || terminationController is null)
                        {
                            DisposeStrongOwner(strongExcelProcess);
                        }
                        else
                        {
                            try
                            {
                                CompleteStrongOwnerCleanup(
                                    strongExcelProcess,
                                    terminationController,
                                    cleanupGrace.Value);
                                ownershipCleanupVerified = true;
                            }
                            catch (Exception ex)
                            {
                                ownershipCleanupError = ex;
                            }
                        }
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

        if (ownershipCleanupError is not null)
        {
            throw cleanupError is null
                ? ownershipCleanupError
                : new WorkbookAutomationCleanupException(
                    "The owned Excel process could not be verified as released after cooperative cleanup failed.",
                    new AggregateException(cleanupError, ownershipCleanupError));
        }

        if (cleanupGrace is not null && strongExcelProcess is not null && ownershipCleanupVerified)
        {
            return;
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private static ExcelComHostObjects StartHiddenExcel(
        bool enableAutomationSecurityLow,
        bool requireStrongOwnership,
        CancellationToken cancellationToken,
        OwnedExcelTerminationController? terminationController = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM automation is supported only on Windows.");
        }

        if (requireStrongOwnership && terminationController is not null)
        {
            return StartExplicitlyOwnedHiddenExcel(
                enableAutomationSecurityLow,
                terminationController,
                cancellationToken);
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
                if (terminationController is not null)
                {
                    terminationController.Attach(
                        new DebugOwnedExcelProcessControl(strongExcelProcess));
                    cancellationRegistration = cancellationToken.UnsafeRegister(
                        static state =>
                            ((OwnedExcelTerminationController)state!).RequestForcedTermination(TimeSpan.Zero),
                        terminationController);
                }
                else
                {
                    cancellationRegistration = cancellationToken.UnsafeRegister(
                        static state =>
                            _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                        strongExcelProcess);
                }
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
                terminationController,
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

    private static ExcelComHostObjects StartExplicitlyOwnedHiddenExcel(
        bool enableAutomationSecurityLow,
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
    {
        object? excelObject = null;
        object? workbooksObject = null;
        DebugExcelProcessOwner? strongExcelProcess = null;
        string? bootstrapWorkbookPath = null;
        try
        {
            var processApi = new WindowsDebugExcelProcessApi();
            var startedApplication = new OwnedExcelApplicationBootstrapper(
                new WindowsExcelOwnedProcessLauncher(),
                processApi,
                new WindowsExcelNativeObjectModelBinder()).Start(
                    terminationController,
                    cancellationToken);
            excelObject = startedApplication.Application;
            strongExcelProcess = startedApplication.ProcessOwner;
            bootstrapWorkbookPath = startedApplication.BootstrapWorkbookPath;

            dynamic excel = excelObject;
            excel.Visible = false;
            excel.DisplayAlerts = false;
            if (enableAutomationSecurityLow)
            {
                excel.AutomationSecurity = MsoAutomationSecurityLow;
            }

            workbooksObject = excel.Workbooks;
            CloseBootstrapWorkbook(workbooksObject, bootstrapWorkbookPath);
            ExcelBootstrapWorkbookFile.Delete(bootstrapWorkbookPath);
            bootstrapWorkbookPath = null;
            return new ExcelComHostObjects(
                excelObject,
                workbooksObject,
                ExcelProcess: null,
                strongExcelProcess,
                terminationController,
                CancellationRegistration: default);
        }
        catch (Exception startException)
        {
            if (startException is IOwnedExcelSessionStartFailure &&
                strongExcelProcess is null)
            {
                throw;
            }

            Exception? cleanupException = null;
            if (strongExcelProcess is not null)
            {
                try
                {
                    terminationController.RequestCleanupAsync(TimeSpan.Zero)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
            }

            try
            {
                ComObjectReleaser.Release(workbooksObject);
                ComObjectReleaser.Release(excelObject);
                ComObjectReleaser.CollectReleasedComObjects();
            }
            catch (Exception ex)
            {
                cleanupException ??= ex;
            }

            if (bootstrapWorkbookPath is not null)
            {
                try
                {
                    ExcelBootstrapWorkbookFile.Delete(bootstrapWorkbookPath);
                }
                catch (Exception ex)
                {
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
                }
            }

            throw CreateOwnedSessionStartFailure(
                startException,
                cleanupException,
                cleanupVerified: strongExcelProcess is not null && cleanupException is null);
        }
    }

    private static void CloseBootstrapWorkbook(
        object workbooksObject,
        string bootstrapWorkbookPath)
    {
        dynamic workbooks = workbooksObject;
        var workbookCount = (int)workbooks.Count;
        for (var index = 1; index <= workbookCount; index++)
        {
            object? workbookObject = null;
            try
            {
                workbookObject = workbooks.Item(index);
                dynamic workbook = workbookObject;
                var workbookPath = Convert.ToString(workbook.FullName);
                if (!string.Equals(
                        Path.GetFullPath(workbookPath ?? string.Empty),
                        Path.GetFullPath(bootstrapWorkbookPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                workbook.Close(false);
                return;
            }
            finally
            {
                ComObjectReleaser.Release(workbookObject);
            }
        }

        throw new InvalidOperationException(
            "The bootstrap workbook was not present in the exactly owned Excel process.");
    }

    private static void DisposeStrongOwner(DebugExcelProcessOwner? owner)
        => owner?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static void CompleteStrongOwnerCleanup(
        DebugExcelProcessOwner? owner,
        OwnedExcelTerminationController? controller,
        TimeSpan cleanupGrace)
    {
        if (owner is null)
        {
            controller?.Dispose();
            return;
        }

        if (controller is null)
        {
            DisposeStrongOwner(owner);
            return;
        }

        controller.RequestForcedTermination(cleanupGrace);
        try
        {
            var processExited = controller
                .WaitForExitOrTerminationAttemptAsync()
                .GetAwaiter()
                .GetResult();
            controller.CancelForcedTermination();
            controller.ObserveTerminationAsync().GetAwaiter().GetResult();
            if (controller.TerminationFailure is not null)
            {
                throw new WorkbookAutomationCleanupException(
                    "The owned Excel process could not be force-terminated during process cleanup.",
                    controller.TerminationFailure);
            }

            if (!processExited)
            {
                throw new WorkbookAutomationCleanupException(
                    "The owned Excel process remained live after forced process cleanup completed.");
            }

            DisposeStrongOwner(owner);
        }
        finally
        {
            controller.Dispose();
        }
    }

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

    internal sealed record ExcelComHostObjects(
        object ExcelObject,
        object WorkbooksObject,
        ExcelComApplicationProcess? ExcelProcess,
        DebugExcelProcessOwner? StrongExcelProcess,
        OwnedExcelTerminationController? TerminationController,
        CancellationTokenRegistration CancellationRegistration);

}
