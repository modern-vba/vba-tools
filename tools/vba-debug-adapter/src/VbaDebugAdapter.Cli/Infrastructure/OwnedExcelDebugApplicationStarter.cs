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
    ExcelDebugNativeApplication BindApplication(int processId, Func<bool> hasProcessExited);
}

internal sealed record ExcelDebugNativeApplication(object Application, DebugFailureOutcome CleanupOutcome);

internal sealed record OwnedExcelDebugApplication(
    object Application,
    DebugExcelProcessOwner ProcessOwner)
{
    internal DebugFailureOutcome? StartupCleanupOutcome { get; init; }
}

internal sealed record ExcelDebugOwnedProcessLaunch(
    DebugExcelProcessOwner ProcessOwner,
    IDebugSuspendedPrimaryThread PrimaryThread,
    string BootstrapWorkbookPath)
{
    internal DebugFailureOutcome? LaunchCleanupOutcome { get; init; }
}

internal sealed class OwnedExcelDebugApplicationStarter(
    IExcelDebugOwnedProcessLauncher processLauncher,
    IExcelDebugNativeObjectModelBinder nativeObjectModelBinder,
    Func<object?, bool>? releaseComObject = null)
    : IExcelDebugOwnedApplicationStarter
{
    private readonly Func<object?, bool> release = releaseComObject ?? ComObjectReleaser.Release;

    public OwnedExcelDebugApplication Start(
        IDebugExcelProcessApi processApi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processApi);
        cancellationToken.ThrowIfCancellationRequested();
        ExcelDebugOwnedProcessLaunch? launch = null;
        object? application = null;
        var bootstrapDeleted = false;
        var threadReleaseAttempted = false;
        DebugFailureOutcome? bootstrapComOutcome = null;
        DebugFailureOutcome? bindingComOutcome = null;
        try
        {
            launch = processLauncher.Start(processApi, cancellationToken);
            using var cancellationRegistration = cancellationToken.UnsafeRegister(
                static state =>
                    _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                launch.ProcessOwner);
            cancellationToken.ThrowIfCancellationRequested();
            launch.PrimaryThread.ResumeExactlyOnce();
            var binding = nativeObjectModelBinder.BindApplication(
                launch.ProcessOwner.ProcessId,
                () => launch.ProcessOwner.HasExited);
            application = binding.Application;
            bindingComOutcome = binding.CleanupOutcome;
            cancellationToken.ThrowIfCancellationRequested();
            bootstrapComOutcome = CloseBootstrapWorkbook(application, launch.BootstrapWorkbookPath,
                launch.ProcessOwner.ProcessId);
            ExcelDebugBootstrapWorkbookFile.Delete(launch.BootstrapWorkbookPath);
            bootstrapDeleted = true;
            threadReleaseAttempted = true;
            launch.PrimaryThread.Dispose();
            if (!launch.PrimaryThread.HandleReleaseVerified)
            {
                throw new InvalidOperationException("The primary thread owner did not prove native handle release.");
            }
            var startupCompletion = new DebugFailureCompletion();
            if (launch.LaunchCleanupOutcome is { } launchOutcome) { startupCompletion.Merge(launchOutcome); }
            startupCompletion.Merge(bindingComOutcome);
            startupCompletion.Merge(bootstrapComOutcome);
            startupCompletion.AddEvidence(new("primary-thread-release", "Excel primary thread handle", DebugResourceKind.Handle,
                launch.PrimaryThread.HandleReleaseVerified, "Native release evidence reported by the primary thread owner.",
                launch.ProcessOwner.ProcessId));
            startupCompletion.AddEvidence(new("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                true, "The bootstrap owner completed file deletion.", launch.ProcessOwner.ProcessId));
            return new OwnedExcelDebugApplication(application, launch.ProcessOwner)
            {
                StartupCleanupOutcome = startupCompletion.Complete()
            };
        }
        catch (Exception startException)
        {
            var completion = new DebugFailureCompletion(startException);
            if (launch?.LaunchCleanupOutcome is { } launchOutcome) { completion.Merge(launchOutcome); }
            if (bootstrapComOutcome is not null) { completion.Merge(bootstrapComOutcome); }
            if (bindingComOutcome is not null) { completion.Merge(bindingComOutcome); }
            var applicationReleased = false;
            try { applicationReleased = release(application); }
            catch (Exception exception)
            {
                completion.AddFailure("startup-com-release", "Excel application", DebugResourceKind.Com,
                    exception, launch?.ProcessOwner.ProcessId);
            }
            completion.AddEvidence(new("startup-com-release", "Excel application", DebugResourceKind.Com,
                applicationReleased, "The COM owner reports final release or no acquired application wrapper.",
                launch?.ProcessOwner.ProcessId));
            if (launch is not null)
            {
                try
                {
                    launch.ProcessOwner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    completion.AddFailure("startup-process-cleanup", "Excel process", DebugResourceKind.Process,
                        exception, launch.ProcessOwner.ProcessId);
                }
                if (launch.ProcessOwner.CleanupOutcome is { } ownerOutcome) { completion.Merge(ownerOutcome); }
                if (!bootstrapDeleted)
                {
                    try
                    {
                        ExcelDebugBootstrapWorkbookFile.Delete(
                            launch.BootstrapWorkbookPath);
                        bootstrapDeleted = true;
                    }
                    catch (Exception exception)
                    {
                        completion.AddFailure("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                            exception, launch.ProcessOwner.ProcessId, launch.BootstrapWorkbookPath);
                    }
                }
                completion.AddEvidence(new("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                    bootstrapDeleted, "The bootstrap owner reports its file deletion result.",
                    launch.ProcessOwner.ProcessId, bootstrapDeleted ? null : launch.BootstrapWorkbookPath));
                if (!threadReleaseAttempted)
                {
                    try { launch.PrimaryThread.Dispose(); }
                    catch (Exception exception)
                    {
                        completion.AddFailure("primary-thread-release", "Excel primary thread handle", DebugResourceKind.Handle,
                            exception, launch.ProcessOwner.ProcessId);
                    }
                }
                completion.AddEvidence(new("primary-thread-release", "Excel primary thread handle", DebugResourceKind.Handle,
                    launch.PrimaryThread.HandleReleaseVerified, "Native release evidence reported by the primary thread owner.",
                    launch.ProcessOwner.ProcessId));
            }
            else if (startException is not IDebugFailureEvidence)
            {
                completion.AddEvidence(new("startup-process-cleanup", "Excel process", DebugResourceKind.Process,
                    false, "The process launcher did not return owner release evidence."));
            }
            completion.Complete().ThrowWithEvidence();
            throw new InvalidOperationException("Failed owned startup must retain its failure outcome.");
        }
    }

    private DebugFailureOutcome CloseBootstrapWorkbook(
        object application,
        string bootstrapWorkbookPath,
        int processId)
    {
        object? workbooksObject = null;
        object? workbookObject = null;
        Exception? primaryFailure = null;
        var precedingReleases = new List<DebugFailureOutcome>();
        try
        {
            dynamic excel = application;
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            var count = Convert.ToInt32(workbooks.Count);
            var expectedPath = Path.GetFullPath(bootstrapWorkbookPath);
            var found = false;
            for (var index = 1; index <= count; index++)
            {
                workbookObject = workbooks.Item(index);
                dynamic workbook = workbookObject;
                var actualPath = Path.GetFullPath(Convert.ToString(workbook.FullName)!);
                if (actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    workbook.Close(false);
                    found = true;
                    break;
                }
                var precedingWorkbook = workbookObject;
                workbookObject = null;
                precedingReleases.Add(ComObjectReleaser.ReleaseScope(null, processId,
                    bootstrapWorkbookPath, release, ($"bootstrap candidate workbook {index}", precedingWorkbook)));
            }

            if (!found)
            {
                throw new InvalidOperationException(
                    "The atomically launched Excel instance did not expose its bootstrap workbook.");
            }
        }
        catch (Exception exception) { primaryFailure = exception; }

        var completion = new DebugFailureCompletion(primaryFailure);
        foreach (var preceding in precedingReleases) { completion.Merge(preceding); }
        try
        {
            completion.Merge(ComObjectReleaser.ReleaseScope(primaryFailure, processId, bootstrapWorkbookPath,
                release, ("bootstrap workbook", workbookObject), ("bootstrap workbook collection", workbooksObject)));
        }
        catch (Exception exception)
        {
            completion.AddFailure("bootstrap-com-release", "Excel bootstrap COM objects", DebugResourceKind.Com,
                exception, processId, bootstrapWorkbookPath);
        }
        var outcome = completion.Complete();
        if (outcome.PrimaryFailure is not null || outcome.HasCleanupFailure) { throw new DebugFailureException(outcome); }
        return outcome;
    }
}

internal sealed class WindowsExcelDebugOwnedProcessLauncher : IExcelDebugOwnedProcessLauncher
{
    private readonly Func<string> resolveExcelExecutablePath;
    private readonly Func<ExcelDebugBootstrapWorkbook> createBootstrapWorkbook;
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
        Func<ExcelDebugBootstrapWorkbook> createBootstrapWorkbook,
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
        DebugFailureOutcome? bootstrapCleanupOutcome = null;
        var jobCreationAttempted = false;
        var processLaunchAttempted = false;
        try
        {
            var applicationPath = resolveExcelExecutablePath();
            var bootstrap = createBootstrapWorkbook();
            bootstrapWorkbookPath = bootstrap.Path;
            bootstrapCleanupOutcome = bootstrap.CleanupOutcome;
            cancellationToken.ThrowIfCancellationRequested();
            jobCreationAttempted = true;
            job = processApi.CreateKillOnCloseJob();
            processLaunchAttempted = true;
            suspendedLaunch = startSuspended(
                job,
                applicationPath,
                ["/x", bootstrapWorkbookPath]);
            var ownershipJob = job;
            job = null;
            owner = DebugExcelProcessOwner.AdoptPreassignedProcess(
                suspendedLaunch.Process, ownershipJob);
            cancellationToken.ThrowIfCancellationRequested();
            var launchCompletion = new DebugFailureCompletion();
            launchCompletion.Merge(bootstrapCleanupOutcome);
            if (suspendedLaunch.LaunchCleanupOutcome is { } completedLaunch) { launchCompletion.Merge(completedLaunch); }
            return new ExcelDebugOwnedProcessLaunch(
                owner,
                suspendedLaunch.PrimaryThread,
                bootstrapWorkbookPath)
            {
                LaunchCleanupOutcome = launchCompletion.Complete()
            };
        }
        catch (Exception startException)
        {
            var completion = new DebugFailureCompletion(startException);
            if (bootstrapCleanupOutcome is not null) { completion.Merge(bootstrapCleanupOutcome); }
            if (suspendedLaunch?.LaunchCleanupOutcome is { } localLaunchOutcome) { completion.Merge(localLaunchOutcome); }
            var processId = owner?.ProcessId ?? suspendedLaunch?.Process.Id;
            if (suspendedLaunch is not null)
            {
                try { suspendedLaunch.PrimaryThread.Dispose(); }
                catch (Exception exception)
                {
                    completion.AddFailure("launcher-thread-release", "Excel primary thread handle",
                        DebugResourceKind.Handle, exception, processId);
                }
                completion.AddEvidence(new("launcher-thread-release", "Excel primary thread handle", DebugResourceKind.Handle,
                    suspendedLaunch.PrimaryThread.HandleReleaseVerified,
                    "Native release evidence reported by the primary thread owner.", processId));
            }
            if (owner is not null)
            {
                try
                {
                    owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    completion.AddFailure("launcher-process-cleanup", "Excel process", DebugResourceKind.Process,
                        exception, processId);
                }
                if (owner.CleanupOutcome is { } ownerOutcome) { completion.Merge(ownerOutcome); }
            }
            else if (job is not null)
            {
                try { job.Dispose(); }
                catch (Exception exception)
                {
                    completion.AddFailure("launcher-job-release", "Excel Job handle", DebugResourceKind.Handle,
                        exception, processId);
                }
                completion.AddEvidence(new("launcher-job-release", "Excel Job handle", DebugResourceKind.Handle,
                    job.HandleReleaseVerified, "Native release evidence reported by the Job owner.", processId));
            }

            if (owner is null && !(startException is IDebugFailureEvidence lowerProcess
                && lowerProcess.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Process)))
            {
                completion.AddEvidence(new("launcher-process-cleanup", "Excel process", DebugResourceKind.Process,
                    !processLaunchAttempted, processLaunchAttempted
                        ? "Suspended startup did not return positive process cleanup evidence."
                        : "The suspended process launch operation was never invoked.", processId));
            }
            if (job is null && suspendedLaunch is null && !(startException is IDebugFailureEvidence lowerHandle
                && lowerHandle.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Handle)))
            {
                completion.AddEvidence(new("launcher-job-release", "Excel Job handle", DebugResourceKind.Handle,
                    !jobCreationAttempted, jobCreationAttempted
                        ? "Job creation did not return native handle release evidence."
                        : "The Job creation operation was never invoked."));
            }

            if (bootstrapWorkbookPath is not null)
            {
                var deleted = false;
                try
                {
                    deleteBootstrapWorkbook(bootstrapWorkbookPath);
                    deleted = true;
                }
                catch (Exception exception)
                {
                    completion.AddFailure("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                        exception, processId, bootstrapWorkbookPath);
                }
                completion.AddEvidence(new("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                    deleted, "The bootstrap file owner reports its deletion result.", processId,
                    deleted ? null : bootstrapWorkbookPath));
            }
            completion.Complete().ThrowWithEvidence();
            throw new InvalidOperationException("Failed owned launch must retain its failure outcome.");
        }
    }
}

internal sealed record ExcelDebugBootstrapWorkbook(string Path, DebugFailureOutcome CleanupOutcome);

internal static class ExcelDebugBootstrapWorkbookFile
{
    public static ExcelDebugBootstrapWorkbook Create()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"vba-debug-adapter-excel-bootstrap-{Guid.NewGuid():N}.xlsx");
        return Create(path, WriteEntries, Delete);
    }

    internal static ExcelDebugBootstrapWorkbook Create(
        string path, Action<ZipArchive> writeEntries, Action<string> delete)
    {
        FileStream? stream = null;
        ZipArchive? archive = null;
        DebugNativeHandleRelease? handleRelease = null;
        Exception? creationFailure = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1);
            handleRelease = new(stream.SafeFileHandle);
            archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            writeEntries(archive);
        }
        catch (Exception exception) { creationFailure = exception; }

        var completion = new DebugFailureCompletion(creationFailure);
        try { archive?.Dispose(); }
        catch (Exception exception)
        {
            completion.AddFailure("bootstrap-archive-release", "Excel bootstrap archive", DebugResourceKind.Handle,
                exception, retainedPath: path);
        }
        // The unbuffered stream has no pending writes after archive finalization.
        // Record the native close result before disposing the managed wrapper.
        try { handleRelease?.Release(); }
        catch (Exception exception)
        {
            completion.AddFailure("bootstrap-handle-release", "Excel bootstrap file handle", DebugResourceKind.Handle,
                exception, retainedPath: path);
        }
        try { stream?.Dispose(); }
        catch (Exception exception)
        {
            completion.AddFailure("bootstrap-stream-release", "Excel bootstrap stream", DebugResourceKind.Handle,
                exception, retainedPath: path);
        }
        completion.AddEvidence(new("bootstrap-handle-release", "Excel bootstrap file handle", DebugResourceKind.Handle,
            handleRelease?.IsVerified == true, "The bootstrap file owner reports the native handle close result.",
            RetainedPath: path));
        var released = completion.Complete();
        if (released.PrimaryFailure is null && !released.HasCleanupFailure)
        {
            return new(path, released);
        }

        var failureCompletion = new DebugFailureCompletion(new DebugFailureException(released));
        if (stream is not null)
        {
            var deleted = false;
            try { delete(path); deleted = true; }
            catch (Exception exception)
            {
                failureCompletion.AddFailure("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                    exception, retainedPath: path);
            }
            failureCompletion.AddEvidence(new("bootstrap-delete", "Excel bootstrap workbook", DebugResourceKind.FileSystem,
                deleted, "The bootstrap file owner reports its deletion result.", RetainedPath: deleted ? null : path));
        }
        failureCompletion.Complete().ThrowWithEvidence();
        throw new InvalidOperationException("Failed bootstrap creation must retain its failure outcome.");
    }

    public static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteEntries(ZipArchive archive)
    {
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

internal interface IExcelDebugNativeObjectModelApi
{
    IReadOnlyList<nint> FindTopLevelWindows(int processId);
    nint FindNativeObjectWindow(nint parentWindow);
    void BindNativeObject(nint windowHandle, out object? nativeObject);
    int GetApplicationProcessId(object application);
}

internal sealed class WindowsExcelDebugNativeObjectModelBinder(
    IExcelDebugNativeObjectModelApi? nativeObjectModelApi = null,
    Func<object?, bool>? releaseComObject = null)
    : IExcelDebugNativeObjectModelBinder
{
    private readonly IExcelDebugNativeObjectModelApi api = nativeObjectModelApi ?? new WindowsExcelDebugNativeObjectModelApi();
    private readonly Func<object?, bool> release = releaseComObject ?? ComObjectReleaser.Release;

    public ExcelDebugNativeApplication BindApplication(int processId, Func<bool> hasProcessExited)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentNullException.ThrowIfNull(hasProcessExited);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Excel native object-model binding requires Windows.");
        }

        var releases = new DebugFailureCompletion();
        try
        {
            while (!hasProcessExited())
            {
                foreach (var topLevelWindow in api.FindTopLevelWindows(processId))
                {
                    var nativeObjectWindow = api.FindNativeObjectWindow(topLevelWindow);
                    if (nativeObjectWindow == nint.Zero)
                    {
                        continue;
                    }
                    var (application, outcome) = TryBindApplication(nativeObjectWindow, processId);
                    foreach (var evidence in outcome.Evidence) { releases.AddEvidence(evidence); }
                    if (application is not null)
                    {
                        return new(application, releases.Complete());
                    }
                }
                Thread.Sleep(50);
            }

            throw new InvalidOperationException(
                "The atomically owned Excel process exited before COM automation was available.");
        }
        catch (Exception exception)
        {
            var completion = new DebugFailureCompletion(exception);
            completion.Merge(releases.Complete());
            throw new DebugFailureException(completion.Complete());
        }
    }

    private (object? Application, DebugFailureOutcome CleanupOutcome) TryBindApplication(
        nint nativeObjectWindow,
        int expectedProcessId)
    {
        object? nativeObject = null;
        object? application = null;
        Exception? bindingFailure = null;
        var admitted = false;
        try
        {
            api.BindNativeObject(nativeObjectWindow, out nativeObject);
            dynamic excelWindow = nativeObject!;
            application = excelWindow.Application;
            admitted = application is not null && api.GetApplicationProcessId(application) == expectedProcessId;
        }
        catch (Exception exception) { bindingFailure = exception; }

        if (!admitted)
        {
            var rejected = ComObjectReleaser.ReleaseScope(bindingFailure, expectedProcessId, null, release,
                ("native Excel application", application), ("native Excel window", nativeObject));
            if (bindingFailure is not null and not (COMException or RuntimeBinderException or InvalidCastException))
            {
                throw new DebugFailureException(rejected);
            }
            return (null, rejected);
        }

        try
        {
            var released = ComObjectReleaser.ReleaseScope(null, expectedProcessId, null, release,
                ("native Excel window", ReferenceEquals(nativeObject, application) ? null : nativeObject));
            return (application, released);
        }
        catch (Exception temporaryReleaseFailure)
        {
            var completion = new DebugFailureCompletion(temporaryReleaseFailure);
            try
            {
                completion.Merge(ComObjectReleaser.ReleaseScope(null, expectedProcessId, null, release,
                    ("native Excel application", application)));
            }
            catch (Exception exception)
            {
                completion.AddFailure("native-binding-release", "native Excel application", DebugResourceKind.Com,
                    exception, expectedProcessId);
            }
            throw new DebugFailureException(completion.Complete());
        }
    }
}

internal sealed class WindowsExcelDebugNativeObjectModelApi : IExcelDebugNativeObjectModelApi
{
    private const uint ObjectIdNativeObjectModel = 0xfffffff0;
    private static readonly Guid IDispatchId = new("00020400-0000-0000-C000-000000000046");

    public void BindNativeObject(nint windowHandle, out object? nativeObject)
    {
        var dispatchId = IDispatchId;
        Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(windowHandle, ObjectIdNativeObjectModel,
            ref dispatchId, out nativeObject));
    }

    public int GetApplicationProcessId(object application)
    {
        dynamic excel = application;
        var applicationWindow = new nint(Convert.ToInt64(excel.Hwnd));
        _ = GetWindowThreadProcessId(applicationWindow, out var processId);
        return processId;
    }

    public IReadOnlyList<nint> FindTopLevelWindows(int processId)
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

    public nint FindNativeObjectWindow(nint parentWindow)
    {
        nint result = nint.Zero;
        _ = EnumChildWindows(
            parentWindow,
            (windowHandle, parameter) =>
            {
                var buffer = new StringBuilder(256);
                _ = GetClassName(windowHandle, buffer, buffer.Capacity);
                if (!buffer.ToString().Equals("EXCEL7", StringComparison.Ordinal))
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
