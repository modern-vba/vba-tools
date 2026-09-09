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
/// Provides workbook COM objects and cooperative cleanup to the exact automation runtime.
/// </summary>
internal sealed class ExcelComWorkbookSession
{
    private const int MsoAutomationSecurityLow = 1;

    private readonly OwnedExcelTerminationController? terminationController;
    private bool disposed;

    private ExcelComWorkbookSession(
        object excelObject,
        object workbookObject,
        OwnedExcelTerminationController? terminationController)
    {
        ExcelObject = excelObject;
        WorkbookObject = workbookObject;
        this.terminationController = terminationController;
    }

    /// <summary>
    /// Gets the Excel.Application COM object.
    /// </summary>
    public object ExcelObject { get; }

    /// <summary>
    /// Gets the open workbook COM object.
    /// </summary>
    public object WorkbookObject { get; }

    internal IReadOnlyList<string> CaptureLoadedModulePaths()
        => terminationController?.CaptureLoadedModulePaths()
            ?? throw new InvalidOperationException("The workbook has no owned process for library-path inspection.");

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
            host.TerminationController);
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
            host.TerminationController);
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
            ComObjectReleaser.CollectReleasedComObjects();
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    internal static bool IsPreOwnershipBootstrapFailureAlreadyClassified(
        Exception startException)
        => startException is IOwnedExcelSessionStartFailure or
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException;

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
                ComObjectReleaser.CollectReleasedComObjects();
            }
        }

        if (cleanupError is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

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
                strongExcelProcess,
                terminationController);
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
        DebugExcelProcessOwner? StrongExcelProcess,
        OwnedExcelTerminationController? TerminationController);
}
