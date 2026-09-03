using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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

    public bool CleanupVerified { get; } = cleanupVerified;
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

    public bool CleanupVerified { get; } = cleanupVerified;
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
            CancellationToken.None);

    /// <summary>
    /// Opens a workbook in a strictly identified, kill-on-close Excel process for a build.
    /// </summary>
    internal static ExcelComWorkbookSession OpenOwnedForBuild(
        string workbookPath,
        CancellationToken cancellationToken)
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow: false,
            cancellationToken);

    internal static ExcelComWorkbookSession OpenOwnedForBuild(
        string workbookPath,
        CancellationToken cancellationToken,
        Func<
            bool,
            CancellationToken,
            OwnedExcelTerminationController,
            ExcelComHostObjects> startAutomationExcel)
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow: false,
            cancellationToken,
            startAutomationExcel,
            CompleteStrongOwnerCleanup);

    internal static ExcelComWorkbookSession OpenOwnedForBuild(
        string workbookPath,
        CancellationToken cancellationToken,
        Func<
            bool,
            CancellationToken,
            OwnedExcelTerminationController,
            ExcelComHostObjects> startAutomationExcel,
        Action<
            DebugExcelProcessOwner?,
            OwnedExcelTerminationController?,
            TimeSpan> completeStrongOwnerCleanup)
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow: false,
            cancellationToken,
            startAutomationExcel,
            completeStrongOwnerCleanup);

    /// <summary>
    /// Starts a hidden Excel application and establishes exact process ownership before workbook open.
    /// </summary>
    internal static ExcelComHostObjects StartOwnedForGeneration(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
        => StartOwnedForGeneration(
            terminationController,
            enableAutomationSecurityLow: false,
            cancellationToken);

    internal static ExcelComHostObjects StartOwnedForGeneration(
        OwnedExcelTerminationController terminationController,
        bool enableAutomationSecurityLow,
        CancellationToken cancellationToken)
        => StartAutomationExcel(
            enableAutomationSecurityLow,
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
    /// Creates a workbook from an explicit Excel template in an already owned hidden application.
    /// </summary>
    internal static ExcelComWorkbookSession CreateOwnedForGeneration(
        ExcelComHostObjects host,
        int workbookTemplate)
    {
        dynamic workbooks = host.WorkbooksObject;
        var workbookObject = workbooks.Add(workbookTemplate);
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
        CancellationToken cancellationToken)
        => OpenCore(
            workbookPath,
            enableAutomationSecurityLow,
            cancellationToken,
            StartAutomationExcel,
            CompleteStrongOwnerCleanup);

    private static ExcelComWorkbookSession OpenCore(
        string workbookPath,
        bool enableAutomationSecurityLow,
        CancellationToken cancellationToken,
        Func<
            bool,
            CancellationToken,
            OwnedExcelTerminationController,
            ExcelComHostObjects> startAutomationExcel,
        Action<
            DebugExcelProcessOwner?,
            OwnedExcelTerminationController?,
            TimeSpan> completeStrongOwnerCleanup)
    {
        ArgumentNullException.ThrowIfNull(startAutomationExcel);
        ArgumentNullException.ThrowIfNull(completeStrongOwnerCleanup);
        cancellationToken.ThrowIfCancellationRequested();
        var ownedTerminationController = new OwnedExcelTerminationController();
        var callerCancellationRegistration = RegisterCallerCancellation(
            ownedTerminationController,
            cancellationToken);
        ExcelComHostObjects host;
        try
        {
            host = startAutomationExcel(
                enableAutomationSecurityLow,
                cancellationToken,
                ownedTerminationController) with
            {
                CancellationRegistration = callerCancellationRegistration
            };
        }
        catch
        {
            callerCancellationRegistration.Dispose();
            ownedTerminationController.Dispose();
            throw;
        }
        object? workbookObject = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception openException)
        {
            var reportedOpenException = NormalizeUnclassifiedCancellation(
                openException,
                cancellationToken);
            host.CancellationRegistration.Dispose();
            ComObjectReleaser.Release(workbookObject);
            var ownershipCleanupVerified = false;
            Exception? ownershipCleanupException = null;
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
                try
                {
                    if (host.StrongExcelProcess is not null &&
                        host.TerminationController is not null)
                    {
                        completeStrongOwnerCleanup(
                            host.StrongExcelProcess,
                            host.TerminationController,
                            TimeSpan.Zero);
                        ownershipCleanupVerified = true;
                    }
                    else
                    {
                        DisposeStrongOwner(host.StrongExcelProcess);
                        ownershipCleanupVerified = host.StrongExcelProcess is not null;
                        ownedTerminationController.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    ownershipCleanupException = ex;
                }

                ComObjectReleaser.CollectReleasedComObjects();
            }

            if (ownershipCleanupException is not null)
            {
                throw CombineCooperativeAndOwnershipCleanupErrors(
                    reportedOpenException,
                    ownershipCleanupException);
            }

            if (ownershipCleanupVerified &&
                !ReferenceEquals(reportedOpenException, openException))
            {
                throw CreateOwnedSessionStartFailure(
                    reportedOpenException,
                    cleanupException: null,
                    cleanupVerified: true);
            }

            throw;
        }
        finally
        {
            ComObjectReleaser.Release(host.WorkbooksObject);
        }
    }

    internal static CancellationTokenRegistration RegisterCallerCancellation(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminationController);
        return cancellationToken.UnsafeRegister(
            static state =>
                ((OwnedExcelTerminationController)state!)
                    .RequestForcedTermination(TimeSpan.Zero),
            terminationController);
    }

    internal static bool IsPreOwnershipBootstrapFailureAlreadyClassified(
        Exception startException)
        => startException is IOwnedExcelSessionStartFailure or
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException;

    /// <summary>
    /// Creates a new workbook in a dedicated hidden Excel session.
    /// </summary>
    /// <returns>The Excel workbook session.</returns>
    public static ExcelComWorkbookSession Create()
    {
        var terminationController = new OwnedExcelTerminationController();
        ExcelComHostObjects host;
        try
        {
            host = StartAutomationExcel(
                enableAutomationSecurityLow: false,
                CancellationToken.None,
                terminationController);
        }
        catch
        {
            terminationController.Dispose();
            throw;
        }
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
                if (host.StrongExcelProcess is not null &&
                    host.TerminationController is not null)
                {
                    CompleteStrongOwnerCleanup(
                        host.StrongExcelProcess,
                        host.TerminationController,
                        TimeSpan.Zero);
                }
                else
                {
                    DisposeStrongOwner(host.StrongExcelProcess);
                    host.TerminationController?.Dispose();
                }

                ComObjectReleaser.CollectReleasedComObjects();
            }

            throw;
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
                        if (terminationController is null)
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
                                    cleanupGrace ?? TimeSpan.Zero);
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
                ? ClassifyOwnershipCleanupError(ownershipCleanupError)
                : CombineCooperativeAndOwnershipCleanupErrors(
                    cleanupError,
                    ownershipCleanupError);
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

    internal static Exception CombineCooperativeAndOwnershipCleanupErrors(
        Exception cooperativeCleanupError,
        Exception ownershipCleanupError)
        => IsExplicitlyReleasedProcessCleanup(ownershipCleanupError)
            ? new WorkbookAutomationReleasedProcessCleanupException(
                "Cooperative workbook cleanup failed after exact owned-process release was verified.",
                new AggregateException(
                    cooperativeCleanupError,
                    ownershipCleanupError))
            : new WorkbookAutomationCleanupException(
                "The owned Excel process could not be verified as released after cooperative cleanup failed.",
                new AggregateException(
                    cooperativeCleanupError,
                    ownershipCleanupError));

    internal static Exception ClassifyOwnershipCleanupError(
        Exception ownershipCleanupError)
    {
        if (ownershipCleanupError is WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException)
        {
            return ownershipCleanupError;
        }

        return IsExplicitlyReleasedProcessCleanup(ownershipCleanupError)
            ? new WorkbookAutomationReleasedProcessCleanupException(
                "Exact owned-process release was verified, but process cleanup failed.",
                ownershipCleanupError)
            : new WorkbookAutomationCleanupException(
                "The owned Excel process could not be verified as released.",
                ownershipCleanupError);
    }

    private static bool IsExplicitlyReleasedProcessCleanup(Exception cleanupError)
        => cleanupError switch
        {
            WorkbookAutomationReleasedProcessCleanupException => true,
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0 =>
                aggregate.InnerExceptions.All(IsExplicitlyReleasedProcessCleanup),
            _ => false
        };

    private static ExcelComHostObjects StartAutomationExcel(
        bool enableAutomationSecurityLow,
        CancellationToken cancellationToken,
        OwnedExcelTerminationController terminationController)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel COM automation is supported only on Windows.");
        }

        return StartExplicitlyOwnedHiddenExcel(
            enableAutomationSecurityLow,
            terminationController,
            cancellationToken);
    }

    private static ExcelComHostObjects StartExplicitlyOwnedHiddenExcel(
        bool enableAutomationSecurityLow,
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
        => StartExplicitlyOwnedHiddenExcel(
            enableAutomationSecurityLow,
            terminationController,
            cancellationToken,
            static (controller, token) =>
            {
                var processApi = new WindowsDebugExcelProcessApi();
                return new OwnedExcelApplicationBootstrapper(
                    new WindowsExcelOwnedProcessLauncher(),
                    processApi,
                    new WindowsExcelNativeObjectModelBinder()).Start(
                        controller,
                        token);
            },
            ExcelBootstrapWorkbookFile.Delete);

    internal static ExcelComHostObjects StartExplicitlyOwnedHiddenExcel(
        bool enableAutomationSecurityLow,
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken,
        Func<
            OwnedExcelTerminationController,
            CancellationToken,
            OwnedExcelApplication> startOwnedApplication,
        Action<string> deleteBootstrapWorkbook)
    {
        ArgumentNullException.ThrowIfNull(startOwnedApplication);
        ArgumentNullException.ThrowIfNull(deleteBootstrapWorkbook);
        object? excelObject = null;
        object? workbooksObject = null;
        DebugExcelProcessOwner? strongExcelProcess = null;
        string? bootstrapWorkbookPath = null;
        try
        {
            var startedApplication = startOwnedApplication(
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
            deleteBootstrapWorkbook(bootstrapWorkbookPath);
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
            if (strongExcelProcess is null &&
                IsPreOwnershipBootstrapFailureAlreadyClassified(startException))
            {
                throw;
            }

            var reportedStartException = NormalizeUnclassifiedCancellation(
                startException,
                cancellationToken);

            Exception? cleanupException = null;
            var ownedProcessReleaseVerified = false;
            if (strongExcelProcess is not null)
            {
                try
                {
                    terminationController.RequestCleanupAsync(TimeSpan.Zero)
                        .GetAwaiter()
                        .GetResult();
                    ownedProcessReleaseVerified = true;
                }
                catch (WorkbookAutomationReleasedProcessCleanupException ex)
                {
                    ownedProcessReleaseVerified = true;
                    cleanupException = ex;
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
                cleanupException = cleanupException is null
                    ? ex
                    : new AggregateException(cleanupException, ex);
            }

            if (bootstrapWorkbookPath is not null)
            {
                try
                {
                    deleteBootstrapWorkbook(bootstrapWorkbookPath);
                }
                catch (Exception ex)
                {
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
                }
            }

            if (ownedProcessReleaseVerified && cleanupException is not null)
            {
                throw new WorkbookAutomationReleasedProcessCleanupException(
                    "The owned Excel process was released, but startup cleanup or automation isolation failed.",
                    new AggregateException(reportedStartException, cleanupException));
            }

            throw CreateOwnedSessionStartFailure(
                reportedStartException,
                cleanupException,
                cleanupVerified: ownedProcessReleaseVerified);
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

    private static Exception NormalizeUnclassifiedCancellation(
        Exception startException,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested ||
            startException is OperationCanceledException ||
            IsPreOwnershipBootstrapFailureAlreadyClassified(startException))
        {
            return startException;
        }

        return new OperationCanceledException(
            "Excel startup was canceled before the requested workbook session was ready.",
            startException,
            cancellationToken);
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

    internal sealed record ExcelComHostObjects(
        object ExcelObject,
        object WorkbooksObject,
        ExcelComApplicationProcess? ExcelProcess,
        DebugExcelProcessOwner? StrongExcelProcess,
        OwnedExcelTerminationController? TerminationController,
        CancellationTokenRegistration CancellationRegistration);

}
