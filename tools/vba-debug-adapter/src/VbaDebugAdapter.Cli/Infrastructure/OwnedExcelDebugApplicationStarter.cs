using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace VbaDebugAdapter.Infrastructure;

internal interface IExcelDebugOwnedApplicationStarter
{
    OwnedExcelDebugApplication Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken);
}

internal interface IExcelDebugOwnedProcessLauncher
{
    ExcelDebugOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken);
}

internal interface IExcelDebugNativeObjectModelBinder
{
    object BindApplication(int processId, Func<bool> hasProcessExited);
}

internal sealed record OwnedExcelDebugApplication(
    object Application,
    DebugExcelProcessOwner ProcessOwner);

internal sealed record ExcelDebugOwnedProcessLaunch(
    DebugExcelProcessOwner ProcessOwner,
    IDebugSuspendedPrimaryThread PrimaryThread,
    string BootstrapWorkbookPath);

internal sealed class OwnedExcelDebugApplicationStarter(
    IExcelDebugOwnedProcessLauncher processLauncher,
    IExcelDebugNativeObjectModelBinder nativeObjectModelBinder)
    : IExcelDebugOwnedApplicationStarter
{
    public OwnedExcelDebugApplication Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processApi);
        cancellationToken.ThrowIfCancellationRequested();
        ExcelDebugOwnedProcessLaunch? launch = null;
        object? application = null;
        var bootstrapDeleted = false;
        try
        {
            launch = processLauncher.Start(processApi, cancellationToken);
            using var cancellationRegistration = cancellationToken.UnsafeRegister(
                static state =>
                    _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                launch.ProcessOwner);
            cancellationToken.ThrowIfCancellationRequested();
            launch.PrimaryThread.ResumeExactlyOnce();
            application = nativeObjectModelBinder.BindApplication(
                launch.ProcessOwner.ProcessId,
                () => launch.ProcessOwner.HasExited);
            cancellationToken.ThrowIfCancellationRequested();
            CloseBootstrapWorkbook(application, launch.BootstrapWorkbookPath);
            ExcelDebugBootstrapWorkbookFile.Delete(launch.BootstrapWorkbookPath);
            bootstrapDeleted = true;
            return new OwnedExcelDebugApplication(application, launch.ProcessOwner);
        }
        catch (Exception startException)
        {
            Exception? cleanupException = null;
            ComObjectReleaser.Release(application);
            if (launch is not null)
            {
                try
                {
                    launch.ProcessOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    cleanupException = exception;
                }
                if (!bootstrapDeleted)
                {
                    try
                    {
                        ExcelDebugBootstrapWorkbookFile.Delete(
                            launch.BootstrapWorkbookPath);
                    }
                    catch (Exception exception)
                    {
                        cleanupException = cleanupException is null
                            ? exception
                            : new AggregateException(cleanupException, exception);
                    }
                }
            }

            if (cleanupException is not null)
            {
                throw new DebugProcessOwnershipCleanupException(
                    startException,
                    cleanupException);
            }
            throw;
        }
        finally
        {
            launch?.PrimaryThread.Dispose();
        }
    }

    private static void CloseBootstrapWorkbook(
        object application,
        string bootstrapWorkbookPath)
    {
        object? workbooksObject = null;
        object? workbookObject = null;
        try
        {
            dynamic excel = application;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            var count = Convert.ToInt32(workbooks.Count);
            var expectedPath = Path.GetFullPath(bootstrapWorkbookPath);
            for (var index = 1; index <= count; index++)
            {
                workbookObject = workbooks.Item(index);
                dynamic workbook = workbookObject;
                var actualPath = Path.GetFullPath(Convert.ToString(workbook.FullName)!);
                if (actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    workbook.Close(false);
                    return;
                }
                ComObjectReleaser.Release(workbookObject);
                workbookObject = null;
            }

            throw new InvalidOperationException(
                "The atomically launched Excel instance did not expose its bootstrap workbook.");
        }
        finally
        {
            ComObjectReleaser.Release(workbookObject);
            ComObjectReleaser.Release(workbooksObject);
        }
    }
}

internal sealed class WindowsExcelDebugOwnedProcessLauncher : IExcelDebugOwnedProcessLauncher
{
    private readonly Func<string> resolveExcelExecutablePath;
    private readonly Func<string> createBootstrapWorkbook;
    private readonly Action<string> deleteBootstrapWorkbook;
    private readonly Func<
        IDebugProcessJob,
        string,
        IReadOnlyList<string>,
        DebugSuspendedProcessLaunch> startSuspended;

    public WindowsExcelDebugOwnedProcessLauncher()
        : this(
            ExcelDebugExecutablePathResolver.Resolve,
            ExcelDebugBootstrapWorkbookFile.Create,
            ExcelDebugBootstrapWorkbookFile.Delete,
            static (job, applicationPath, arguments) =>
            {
                if (job is not WindowsDebugProcessJob windowsJob)
                {
                    throw new InvalidOperationException(
                        "Atomic Excel startup requires the Windows Job Object process adapter.");
                }
                return windowsJob.StartSuspended(applicationPath, arguments);
            })
    {
    }

    internal WindowsExcelDebugOwnedProcessLauncher(
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

    public ExcelDebugOwnedProcessLaunch Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processApi);
        cancellationToken.ThrowIfCancellationRequested();
        IDebugProcessJob? job = null;
        DebugSuspendedProcessLaunch? suspendedLaunch = null;
        DebugExcelProcessOwner? owner = null;
        string? bootstrapWorkbookPath = null;
        try
        {
            var applicationPath = resolveExcelExecutablePath();
            bootstrapWorkbookPath = createBootstrapWorkbook();
            cancellationToken.ThrowIfCancellationRequested();
            job = processApi.CreateKillOnCloseJob();
            suspendedLaunch = startSuspended(
                job,
                applicationPath,
                ["/x", bootstrapWorkbookPath]);
            owner = DebugExcelProcessOwner.AdoptPreassignedProcess(
                suspendedLaunch.Process,
                job);
            job = null;
            cancellationToken.ThrowIfCancellationRequested();
            return new ExcelDebugOwnedProcessLaunch(
                owner,
                suspendedLaunch.PrimaryThread,
                bootstrapWorkbookPath);
        }
        catch (Exception startException)
        {
            var reportedStartException = startException;
            Exception? cleanupException = null;
            if (startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                reportedStartException = ownershipCleanup.OwnershipException;
                cleanupException = ownershipCleanup.CleanupException;
            }

            suspendedLaunch?.PrimaryThread.Dispose();
            if (owner is not null)
            {
                try
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    cleanupException = cleanupException is null
                        ? exception
                        : new AggregateException(cleanupException, exception);
                }
            }
            else
            {
                job?.Dispose();
            }

            if (bootstrapWorkbookPath is not null)
            {
                try
                {
                    deleteBootstrapWorkbook(bootstrapWorkbookPath);
                }
                catch (Exception exception)
                {
                    cleanupException = cleanupException is null
                        ? exception
                        : new AggregateException(cleanupException, exception);
                }
            }

            if (cleanupException is not null)
            {
                throw new DebugProcessOwnershipCleanupException(
                    reportedStartException,
                    cleanupException);
            }
            throw;
        }
    }
}

internal static class ExcelDebugBootstrapWorkbookFile
{
    public static string Create()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vba-debug-adapter-excel-bootstrap-{Guid.NewGuid():N}.xlsx");
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
        catch
        {
            File.Delete(path);
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

internal static class ExcelDebugExecutablePathResolver
{
    private const string AppPathsSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe";

    public static string Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Microsoft Excel is available only on Windows.");
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

internal sealed class WindowsExcelDebugNativeObjectModelBinder
    : IExcelDebugNativeObjectModelBinder
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
            throw new PlatformNotSupportedException(
                "Excel native object-model binding requires Windows.");
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
            "The atomically owned Excel process exited before COM automation was available.");
    }

    private static object? TryBindApplication(
        nint nativeObjectWindow,
        int expectedProcessId)
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
            }
            return application;
        }
        catch (Exception exception) when (
            exception is COMException or RuntimeBinderException or InvalidCastException)
        {
            ComObjectReleaser.Release(application);
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
