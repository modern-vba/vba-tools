using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelOwnedProcessLauncher
{
    ExcelOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        string qualifiedDesktopName,
        CancellationToken cancellationToken);
}

internal interface IExcelNativeObjectModelBinder
{
    object BindApplicationOnDesktop(
        int processId,
        nint desktopHandle,
        Func<bool> hasProcessExited);
}

internal sealed record OwnedExcelApplication(
    object Application,
    DebugExcelProcessOwner ProcessOwner,
    string BootstrapWorkbookPath);

internal sealed record ExcelOwnedProcessLaunch(
    DebugExcelProcessOwner ProcessOwner,
    IDebugSuspendedPrimaryThread PrimaryThread,
    string BootstrapWorkbookPath);

/// <summary>
/// Launches Excel explicitly, establishes exact process ownership, and only then binds COM.
/// </summary>
internal sealed class OwnedExcelApplicationBootstrapper
{
    private readonly IExcelOwnedProcessLauncher processLauncher;
    private readonly IDebugExcelProcessApi processApi;
    private readonly IExcelNativeObjectModelBinder nativeObjectModelBinder;
    private readonly IExcelAutomationDesktopIsolationFactory desktopIsolationFactory;
    private readonly Action<string> deleteBootstrapWorkbook;

    public OwnedExcelApplicationBootstrapper(
        IExcelOwnedProcessLauncher processLauncher,
        IDebugExcelProcessApi processApi,
        IExcelNativeObjectModelBinder nativeObjectModelBinder)
        : this(
            processLauncher,
            processApi,
            nativeObjectModelBinder,
            WindowsExcelAutomationDesktopIsolationFactory.Instance,
            ExcelBootstrapWorkbookFile.Delete)
    {
    }

    internal OwnedExcelApplicationBootstrapper(
        IExcelOwnedProcessLauncher processLauncher,
        IDebugExcelProcessApi processApi,
        IExcelNativeObjectModelBinder nativeObjectModelBinder,
        IExcelAutomationDesktopIsolationFactory desktopIsolationFactory)
        : this(
            processLauncher,
            processApi,
            nativeObjectModelBinder,
            desktopIsolationFactory,
            ExcelBootstrapWorkbookFile.Delete)
    {
    }

    internal OwnedExcelApplicationBootstrapper(
        IExcelOwnedProcessLauncher processLauncher,
        IDebugExcelProcessApi processApi,
        IExcelNativeObjectModelBinder nativeObjectModelBinder,
        IExcelAutomationDesktopIsolationFactory desktopIsolationFactory,
        Action<string> deleteBootstrapWorkbook)
    {
        this.processLauncher = processLauncher;
        this.processApi = processApi;
        this.nativeObjectModelBinder = nativeObjectModelBinder;
        this.desktopIsolationFactory = desktopIsolationFactory;
        this.deleteBootstrapWorkbook = deleteBootstrapWorkbook;
    }

    public OwnedExcelApplication Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminationController);
        OwnedExcelTerminationController.OwnedExcelLaunchLease? launchLease = null;
        IExcelAutomationDesktopIsolation? desktopIsolation = null;
        ExcelOwnedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        PrivateDesktopOwnedExcelProcessControl? processControl = null;
        object? application = null;
        var ownershipTransferred = false;
        var launchSettled = false;
        try
        {
            launchLease = terminationController.BeginLaunch(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            desktopIsolation = desktopIsolationFactory.Create();
            launch = processLauncher.Start(
                processApi,
                desktopIsolation.QualifiedDesktopName,
                cancellationToken);
            owner = launch.ProcessOwner;
            cancellationToken.ThrowIfCancellationRequested();
            desktopIsolation.StartObservingBeforeResumeAsync(
                    owner.ProcessId,
                    cancellationToken)
                .GetAwaiter()
                .GetResult();
            processControl = new PrivateDesktopOwnedExcelProcessControl(
                owner,
                desktopIsolation);
            var continueStartup = terminationController.Attach(
                processControl);
            ownershipTransferred = true;
            launchLease.Dispose();
            launchSettled = true;
            if (!continueStartup)
            {
                throw new OperationCanceledException(
                    "Excel startup was sealed before COM binding could begin.",
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            processControl.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            launch.PrimaryThread.ResumeExactlyOnce();
            application = nativeObjectModelBinder.BindApplicationOnDesktop(
                owner.ProcessId,
                desktopIsolation.DesktopHandle,
                () => owner.HasExited);
            processControl.Capture(DesktopWindowLifecyclePhase.BootstrapBinding);
            cancellationToken.ThrowIfCancellationRequested();
            return new OwnedExcelApplication(
                application,
                owner,
                launch.BootstrapWorkbookPath);
        }
        catch (Exception startException)
        {
            if (startException is IOwnedExcelSessionStartFailure launchFailure &&
                launch is null)
            {
                var launchReleaseProofException = launchFailure.CleanupVerified
                    ? null
                    : launchFailure.CleanupException ??
                        new WorkbookAutomationCleanupException(
                            "The atomic Excel launch did not verify exact process cleanup.");
                var launchSecondaryCleanupException = launchFailure.CleanupVerified
                    ? launchFailure.CleanupException
                    : null;
                if (desktopIsolation is not null)
                {
                    try
                    {
                        desktopIsolation.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        var desktopReleaseFailure =
                            ex is WorkbookAutomationCleanupException
                                ? ex
                                : new WorkbookAutomationCleanupException(
                                    "The private Excel automation desktop could not be released after launch failure.",
                                    ex);
                        launchReleaseProofException = launchReleaseProofException is null
                            ? desktopReleaseFailure
                            : new AggregateException(
                                launchReleaseProofException,
                                desktopReleaseFailure);
                    }
                }

                if (launchReleaseProofException is not null)
                {
                    launchLease!.CompleteWithCleanupFailure(launchReleaseProofException);
                    launchSettled = true;
                    var combinedCleanupException = launchSecondaryCleanupException is null
                        ? launchReleaseProofException
                        : new AggregateException(
                            launchReleaseProofException,
                            launchSecondaryCleanupException);
                    throw CreateStartFailure(
                        launchFailure.StartException,
                        combinedCleanupException,
                        cleanupVerified: false);
                }

                if (launchSecondaryCleanupException is not null)
                {
                    throw new WorkbookAutomationReleasedProcessCleanupException(
                        "The atomic Excel process was released, but startup cleanup failed.",
                        new AggregateException(
                            launchFailure.StartException,
                            launchSecondaryCleanupException));
                }

                throw;
            }

            var reportedStartException = startException;
            Exception? releaseProofException = null;
            Exception? secondaryCleanupException = null;
            if (startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                reportedStartException = ownershipCleanup.OwnershipException;
                releaseProofException = ownershipCleanup.CleanupException;
            }

            if (releaseProofException is null)
            {
                reportedStartException = NormalizeUnclassifiedCancellation(
                    reportedStartException,
                    cancellationToken);
            }

            if (ownershipTransferred)
            {
                try
                {
                    terminationController.RequestCleanupAsync(TimeSpan.Zero)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (WorkbookAutomationReleasedProcessCleanupException ex)
                {
                    secondaryCleanupException = ex;
                }
                catch (Exception ex)
                {
                    releaseProofException = ex;
                }
            }
            else if (processControl is not null)
            {
                try
                {
                    processControl.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (WorkbookAutomationReleasedProcessCleanupException ex)
                {
                    secondaryCleanupException = ex;
                }
                catch (Exception ex)
                {
                    releaseProofException = ex;
                }
            }
            else
            {
                if (owner is not null)
                {
                    try
                    {
                        owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        releaseProofException = ex;
                    }
                }

                if (desktopIsolation is not null)
                {
                    try
                    {
                        desktopIsolation.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        releaseProofException = releaseProofException is null
                            ? ex
                            : new AggregateException(releaseProofException, ex);
                    }
                }
            }

            if (application is not null)
            {
                try
                {
                    ComObjectReleaser.Release(application);
                    ComObjectReleaser.CollectReleasedComObjects();
                }
                catch (Exception ex)
                {
                    secondaryCleanupException = secondaryCleanupException is null
                        ? ex
                        : new AggregateException(secondaryCleanupException, ex);
                }
            }

            if (launch is not null)
            {
                try
                {
                    deleteBootstrapWorkbook(launch.BootstrapWorkbookPath);
                }
                catch (Exception ex)
                {
                    secondaryCleanupException = secondaryCleanupException is null
                        ? ex
                        : new AggregateException(secondaryCleanupException, ex);
                }
            }

            if (!ownershipTransferred && releaseProofException is not null)
            {
                launchLease!.CompleteWithCleanupFailure(releaseProofException);
                launchSettled = true;
            }

            if (releaseProofException is null && secondaryCleanupException is not null)
            {
                throw new WorkbookAutomationReleasedProcessCleanupException(
                    "The owned Excel process and private desktop were released, but startup cleanup failed.",
                    new AggregateException(
                        reportedStartException,
                        secondaryCleanupException));
            }

            if (releaseProofException is null)
            {
                if (ReferenceEquals(reportedStartException, startException) &&
                    ExcelComWorkbookSession.IsPreOwnershipBootstrapFailureAlreadyClassified(
                        reportedStartException))
                {
                    throw;
                }

                throw CreateStartFailure(
                    reportedStartException,
                    cleanupException: null,
                    cleanupVerified: true);
            }

            var cleanupException = secondaryCleanupException is null
                ? releaseProofException
                : new AggregateException(
                    releaseProofException,
                    secondaryCleanupException);
            throw CreateStartFailure(
                reportedStartException,
                cleanupException,
                cleanupVerified: false);
        }
        finally
        {
            try
            {
                launch?.PrimaryThread.Dispose();
            }
            finally
            {
                if (!launchSettled)
                {
                    launchLease?.Dispose();
                }
            }
        }
    }

    internal static Exception CreateStartFailure(
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
            ExcelComWorkbookSession.IsPreOwnershipBootstrapFailureAlreadyClassified(
                startException))
        {
            return startException;
        }

        return new OperationCanceledException(
            "Excel startup was canceled before native object-model binding completed.",
            startException,
            cancellationToken);
    }
}

internal sealed class WindowsExcelOwnedProcessLauncher : IExcelOwnedProcessLauncher
{
    private readonly Func<string> resolveExcelExecutablePath;
    private readonly Func<string> createBootstrapWorkbook;
    private readonly Action<string> deleteBootstrapWorkbook;
    private readonly Func<
        IDebugProcessJob,
        string,
        IReadOnlyList<string>,
        string,
        DebugSuspendedProcessLaunch> startSuspended;

    public WindowsExcelOwnedProcessLauncher()
        : this(
            ExcelExecutablePathResolver.Resolve,
            ExcelBootstrapWorkbookFile.Create,
            ExcelBootstrapWorkbookFile.Delete,
            static (job, applicationPath, arguments, desktopName) =>
            {
                if (job is not WindowsDebugProcessJob windowsJob)
                {
                    throw new DebugSetupException(
                        "Atomic Excel startup requires the Windows Job Object process adapter.");
                }

                return windowsJob.StartSuspended(
                    applicationPath,
                    arguments,
                    desktopName);
            })
    {
    }

    internal WindowsExcelOwnedProcessLauncher(
        Func<string> resolveExcelExecutablePath,
        Func<string> createBootstrapWorkbook,
        Action<string> deleteBootstrapWorkbook,
        Func<
            IDebugProcessJob,
            string,
            IReadOnlyList<string>,
            string,
            DebugSuspendedProcessLaunch> startSuspended)
    {
        this.resolveExcelExecutablePath = resolveExcelExecutablePath;
        this.createBootstrapWorkbook = createBootstrapWorkbook;
        this.deleteBootstrapWorkbook = deleteBootstrapWorkbook;
        this.startSuspended = startSuspended;
    }

    public ExcelOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        string qualifiedDesktopName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processApi);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedDesktopName);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel process launch is supported only on Windows.");
        }

        IDebugProcessJob? job = null;
        DebugSuspendedProcessLaunch? suspendedLaunch = null;
        DebugExcelProcessOwner? owner = null;
        string? bootstrapWorkbookPath = null;
        try
        {
            var excelExecutablePath = resolveExcelExecutablePath();
            bootstrapWorkbookPath = createBootstrapWorkbook();
            cancellationToken.ThrowIfCancellationRequested();
            job = processApi.CreateKillOnCloseJob();
            suspendedLaunch = startSuspended(
                job,
                excelExecutablePath,
                ["/x", bootstrapWorkbookPath],
                qualifiedDesktopName);
            owner = DebugExcelProcessOwner.AdoptPreassignedProcess(
                suspendedLaunch.Process,
                job);
            job = null;
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelOwnedProcessLaunch(
                owner,
                suspendedLaunch.PrimaryThread,
                bootstrapWorkbookPath);
        }
        catch (Exception startException)
        {
            if (startException is IOwnedExcelSessionStartFailure &&
                owner is null &&
                suspendedLaunch is null &&
                job is null &&
                bootstrapWorkbookPath is null)
            {
                throw;
            }

            var reportedStartException = startException;
            Exception? releaseProofException = null;
            Exception? secondaryCleanupException = null;
            if (startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                reportedStartException = ownershipCleanup.OwnershipException;
                releaseProofException = ownershipCleanup.CleanupException;
            }

            suspendedLaunch?.PrimaryThread.Dispose();
            if (owner is not null)
            {
                try
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    releaseProofException = releaseProofException is null
                        ? ex
                        : new AggregateException(releaseProofException, ex);
                }
            }
            else if (job is not null)
            {
                try
                {
                    job.Dispose();
                }
                catch (Exception ex)
                {
                    releaseProofException = releaseProofException is null
                        ? ex
                        : new AggregateException(releaseProofException, ex);
                }
            }

            if (bootstrapWorkbookPath is not null)
            {
                try
                {
                    deleteBootstrapWorkbook(bootstrapWorkbookPath);
                }
                catch (Exception ex)
                {
                    secondaryCleanupException = secondaryCleanupException is null
                        ? ex
                        : new AggregateException(secondaryCleanupException, ex);
                }
            }

            var cleanupException = releaseProofException switch
            {
                null => secondaryCleanupException,
                _ when secondaryCleanupException is null => releaseProofException,
                _ => new AggregateException(
                    releaseProofException,
                    secondaryCleanupException)
            };
            throw OwnedExcelApplicationBootstrapper.CreateStartFailure(
                reportedStartException,
                cleanupException,
                cleanupVerified: releaseProofException is null);
        }
    }
}

internal static class ExcelBootstrapWorkbookFile
{
    public static string Create()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vba-dev-excel-bootstrap-{Guid.NewGuid():N}.xlsx");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            WriteEntry(
                archive,
                "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            WriteEntry(
                archive,
                "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            WriteEntry(
                archive,
                "xl/workbook.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            WriteEntry(
                archive,
                "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            WriteEntry(
                archive,
                "xl/worksheets/sheet1.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>
                """);
            return path;
        }
        catch (Exception createException)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception cleanupException)
            {
                throw new OwnedExcelSessionStartException(
                    createException,
                    cleanupException,
                    cleanupVerified: true);
            }

            throw;
        }
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}

internal static class ExcelExecutablePathResolver
{
    private const string AppPathsSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe";

    public static string Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Microsoft Excel is available only on Windows.");
        }

        var views = Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
            : new[] { RegistryView.Registry32 };
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in views)
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var appPathKey = baseKey.OpenSubKey(AppPathsSubKey);
                var configuredPath = appPathKey?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(configuredPath))
                {
                    continue;
                }

                var path = configuredPath.Trim().Trim('"');
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }

        throw new InvalidOperationException(
            "Microsoft Excel executable was not found in the registered App Paths entries.");
    }
}

internal sealed class WindowsExcelNativeObjectModelBinder : IExcelNativeObjectModelBinder
{
    private const uint ObjectIdNativeObjectModel = 0xfffffff0;
    private static readonly Guid IDispatchId =
        new("00020400-0000-0000-C000-000000000046");

    internal object BindApplicationOnCallerDesktopForUnisolatedControl(
        int processId,
        Func<bool> hasProcessExited)
        => BindApplication(
            processId,
            hasProcessExited,
            FindTopLevelWindows);

    public object BindApplicationOnDesktop(
        int processId,
        nint desktopHandle,
        Func<bool> hasProcessExited)
    {
        if (desktopHandle == nint.Zero)
        {
            throw new ArgumentException(
                "An explicit Windows desktop handle is required.",
                nameof(desktopHandle));
        }

        return BindApplication(
            processId,
            hasProcessExited,
            candidateProcessId => FindTopLevelWindows(
                desktopHandle,
                candidateProcessId));
    }

    private static object BindApplication(
        int processId,
        Func<bool> hasProcessExited,
        Func<int, IReadOnlyList<nint>> findTopLevelWindows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(hasProcessExited);
        ArgumentNullException.ThrowIfNull(findTopLevelWindows);
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel native object-model binding requires Windows.");
        }

        while (!hasProcessExited())
        {
            foreach (var topLevelWindow in findTopLevelWindows(processId))
            {
                var nativeObjectWindow = FindDescendantWindow(topLevelWindow, "EXCEL7");
                if (nativeObjectWindow == nint.Zero)
                {
                    continue;
                }

                var application = TryBindApplication(nativeObjectWindow, processId);
                if (application is not null)
                {
                    return application;
                }
            }

            Thread.Sleep(50);
        }

        throw new InvalidOperationException(
            "The explicitly launched Excel process exited before COM automation was available.");
    }

    private static object? TryBindApplication(nint nativeObjectWindow, int expectedProcessId)
    {
        object? nativeObject = null;
        object? application = null;
        try
        {
            var dispatchId = IDispatchId;
            Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(
                nativeObjectWindow,
                ObjectIdNativeObjectModel,
                ref dispatchId,
                out nativeObject));
            dynamic excelWindow = nativeObject;
            application = excelWindow.Application;
            dynamic excel = application;
            var applicationWindow = new nint(Convert.ToInt64(excel.Hwnd));
            _ = GetWindowThreadProcessId(applicationWindow, out var applicationProcessId);
            if (applicationProcessId != expectedProcessId)
            {
                ComObjectReleaser.Release(application);
                application = null;
                return null;
            }

            return application;
        }
        catch (Exception ex) when (
            ex is COMException or RuntimeBinderException or InvalidCastException)
        {
            ComObjectReleaser.Release(application);
            application = null;
            return null;
        }
        finally
        {
            ComObjectReleaser.Release(nativeObject);
        }
    }

    private static IReadOnlyList<nint> FindTopLevelWindows(int processId)
    {
        var windows = new List<nint>();
        _ = EnumWindows(
            (windowHandle, parameter) =>
            {
                _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId == processId)
                {
                    windows.Add(windowHandle);
                }

                return true;
            },
            nint.Zero);
        return windows;
    }

    private static IReadOnlyList<nint> FindTopLevelWindows(
        nint desktopHandle,
        int processId)
    {
        var windows = new List<nint>();
        Marshal.SetLastPInvokeError(0);
        if (!EnumDesktopWindows(
                desktopHandle,
                (windowHandle, parameter) =>
                {
                    _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                    if (windowProcessId == processId)
                    {
                        windows.Add(windowHandle);
                    }

                    return true;
                },
                nint.Zero))
        {
            var nativeError = Marshal.GetLastPInvokeError();
            if (nativeError == 0)
            {
                return windows;
            }

            throw new Win32Exception(
                nativeError,
                "The explicit Windows desktop could not be enumerated for Excel native binding.");
        }

        return windows;
    }

    private static nint FindDescendantWindow(nint parentWindow, string className)
    {
        nint result = nint.Zero;
        _ = EnumChildWindows(
            parentWindow,
            (windowHandle, parameter) =>
            {
                var buffer = new StringBuilder(256);
                _ = GetClassName(windowHandle, buffer, buffer.Capacity);
                if (!buffer.ToString().Equals(className, StringComparison.Ordinal))
                {
                    return true;
                }

                result = windowHandle;
                return false;
            },
            nint.Zero);
        return result;
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint windowHandle,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object accessibleObject);

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(
        nint desktop,
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(
        nint parentWindow,
        EnumWindowsCallback callback,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        nint windowHandle,
        StringBuilder className,
        int maximumCharacterCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out int processId);
}
