using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;
using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelOwnedProcessLauncher
{
    ExcelOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken);
}

internal interface IExcelNativeObjectModelBinder
{
    object BindApplication(int processId, Func<bool> hasProcessExited);
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
internal sealed class OwnedExcelApplicationBootstrapper(
    IExcelOwnedProcessLauncher processLauncher,
    IDebugExcelProcessApi processApi,
    IExcelNativeObjectModelBinder nativeObjectModelBinder)
{
    public OwnedExcelApplication Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminationController);
        using var launchLease = terminationController.BeginLaunch(cancellationToken);
        ExcelOwnedProcessLaunch? launch = null;
        DebugExcelProcessOwner? owner = null;
        var ownershipTransferred = false;
        var launchSettled = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            launch = processLauncher.Start(processApi, cancellationToken);
            owner = launch.ProcessOwner;
            cancellationToken.ThrowIfCancellationRequested();
            var continueStartup = terminationController.Attach(
                new DebugOwnedExcelProcessControl(owner));
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
            launch.PrimaryThread.ResumeExactlyOnce();
            var application = nativeObjectModelBinder.BindApplication(
                owner.ProcessId,
                () => owner.HasExited);
            return new OwnedExcelApplication(
                application,
                owner,
                launch.BootstrapWorkbookPath);
        }
        catch (Exception startException)
        {
            if (startException is IOwnedExcelSessionStartFailure && launch is null)
            {
                throw;
            }

            var reportedStartException = startException;
            Exception? cleanupException = null;
            if (startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                reportedStartException = ownershipCleanup.OwnershipException;
                cleanupException = ownershipCleanup.CleanupException;
            }

            if (ownershipTransferred)
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
            else if (owner is not null)
            {
                try
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                }
            }

            if (launch is not null)
            {
                try
                {
                    ExcelBootstrapWorkbookFile.Delete(launch.BootstrapWorkbookPath);
                }
                catch (Exception ex)
                {
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
                }
            }

            if (!ownershipTransferred && cleanupException is not null)
            {
                launchLease.CompleteWithCleanupFailure(cleanupException);
                launchSettled = true;
            }

            if (ownershipTransferred && cleanupException is null)
            {
                throw new OwnedExcelSessionStartException(
                    reportedStartException,
                    cleanupException: null,
                    cleanupVerified: true);
            }

            throw new OwnedExcelSessionStartException(
                reportedStartException,
                cleanupException,
                cleanupVerified: cleanupException is null);
        }
        finally
        {
            launch?.PrimaryThread.Dispose();
            if (!launchSettled)
            {
                launchLease.Dispose();
            }
        }
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
        DebugSuspendedProcessLaunch> startSuspended;

    public WindowsExcelOwnedProcessLauncher()
        : this(
            ExcelExecutablePathResolver.Resolve,
            ExcelBootstrapWorkbookFile.Create,
            ExcelBootstrapWorkbookFile.Delete,
            static (job, applicationPath, arguments) =>
            {
                if (job is not WindowsDebugProcessJob windowsJob)
                {
                    throw new DebugSetupException(
                        "Atomic Excel startup requires the Windows Job Object process adapter.");
                }

                return windowsJob.StartSuspended(applicationPath, arguments);
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
            DebugSuspendedProcessLaunch> startSuspended)
    {
        this.resolveExcelExecutablePath = resolveExcelExecutablePath;
        this.createBootstrapWorkbook = createBootstrapWorkbook;
        this.deleteBootstrapWorkbook = deleteBootstrapWorkbook;
        this.startSuspended = startSuspended;
    }

    public ExcelOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processApi);
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
                ["/x", bootstrapWorkbookPath]);
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
            Exception? cleanupException = null;
            var cleanupVerified = true;
            if (startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                reportedStartException = ownershipCleanup.OwnershipException;
                cleanupException = ownershipCleanup.CleanupException;
                cleanupVerified = false;
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
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
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
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
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
                    cleanupException = cleanupException is null
                        ? ex
                        : new AggregateException(cleanupException, ex);
                }
            }

            throw new OwnedExcelSessionStartException(
                reportedStartException,
                cleanupException,
                cleanupVerified: cleanupVerified && cleanupException is null);
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
                    cleanupVerified: false);
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

    public object BindApplication(int processId, Func<bool> hasProcessExited)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(hasProcessExited);
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Excel native object-model binding requires Windows.");
        }

        while (!hasProcessExited())
        {
            foreach (var topLevelWindow in FindTopLevelWindows(processId))
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
