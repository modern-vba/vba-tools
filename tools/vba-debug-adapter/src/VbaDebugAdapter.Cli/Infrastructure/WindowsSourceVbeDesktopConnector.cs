using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VbaDebugAdapter.Debugging;

namespace VbaDebugAdapter.Infrastructure;

internal readonly record struct SourceVbeProcessIdentity(int ProcessId, long StartUtcTicks);

internal readonly record struct SourceVbePhysicalFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex);

internal interface ISourceVbeDesktopApi
{
    IReadOnlyList<SourceVbeProcessIdentity> CaptureExcelProcesses();
    object? TryBindApplication(SourceVbeProcessIdentity process);
    object CreateApplication();
    SourceVbeProcessIdentity ReadProcessIdentity(object application);

    DebugExcelProcessArchitecture ReadProcessArchitecture(SourceVbeProcessIdentity process)
        => DebugExcelProcessArchitecture.Unknown;

    SourceVbeDebugSessionCompletion? ReadProcessCompletion(SourceVbeProcessIdentity process)
        => null;
}

internal interface ISourceVbePhysicalIdentityReader
{
    SourceVbePhysicalFileIdentity Read(string path);
}

internal sealed class WindowsSourceVbeDesktopConnector : ISourceVbeDesktopConnector
{
    private const int MsoAutomationSecurityLow = 1;
    private static readonly object retentionGate = new();
    private static readonly List<object> retainedApplicationReferences = [];
    private readonly ISourceVbeDesktopApi api;
    private readonly ISourceVbePhysicalIdentityReader fileIdentityReader;
    private readonly Func<object?, bool> releaseReference;

    public WindowsSourceVbeDesktopConnector()
        : this(new WindowsSourceVbeDesktopApi(), new WindowsSourceVbePhysicalIdentityReader())
    {
    }

    internal WindowsSourceVbeDesktopConnector(
        ISourceVbeDesktopApi api,
        ISourceVbePhysicalIdentityReader fileIdentityReader,
        Func<object?, bool>? releaseReference = null)
    {
        this.api = api;
        this.fileIdentityReader = fileIdentityReader;
        this.releaseReference = releaseReference ?? SourceVbeComReferences.Release;
    }

    public SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath)
    {
        var path = Path.GetFullPath(exactSourceWorkbookPath);
        SourceVbePhysicalFileIdentity expectedFile = default;
        var applications = new List<(SourceVbeProcessIdentity Process, object Application)>();
        var releaseOutcomes = new List<DebugFailureOutcome>();
        object? selectedWorkbook = null;
        object? selectedApplication = null;
        var selectedApplicationIndex = -1;
        var selectedApplicationHandoffVerified = true;
        SourceVbeProcessIdentity selectedProcess = default;
        var wasAlreadyOpen = false;
        var transferred = false;
        Exception? primaryFailure = null;

        void ReleaseAcquired(Exception? primary, int processId,
            params (string Resource, object? Value)[] resources)
        {
            try
            {
                releaseOutcomes.Add(SourceVbeComReferences.ReleaseScope(
                    primary, processId, path, releaseReference, resources));
            }
            catch (Exception failure) when (failure is IDebugFailureEvidence)
            {
                releaseOutcomes.Add(((IDebugFailureEvidence)failure).FailureOutcome);
                throw;
            }
        }

        try
        {
            expectedFile = fileIdentityReader.Read(path);
            foreach (var process in api.CaptureExcelProcesses())
            {
                var application = api.TryBindApplication(process)
                    ?? throw new DebugSetupException(
                        "An Excel instance on the caller desktop could not be inspected. " +
                        "Resolve its visible prompts and retry; no duplicate source workbook was opened.");
                applications.Add((process, application));
                if (api.ReadProcessIdentity(application) != process)
                    throw new DebugSetupException("The caller-desktop Excel process identity changed during attachment.");
                object? booksObject = null;
                Exception? enumerationFailure = null;
                try
                {
                    dynamic excel = application;
                    booksObject = excel.Workbooks;
                    dynamic books = booksObject;
                    var count = (int)books.Count;
                    for (var index = 1; index <= count; index++)
                    {
                        object? candidate = null;
                        Exception? candidateFailure = null;
                        try
                        {
                            candidate = books.Item(index);
                            dynamic book = candidate;
                            var candidatePath = (string)book.FullName;
                            if (!Path.IsPathFullyQualified(candidatePath))
                                continue; // Unsaved blank workbooks have no physical source identity.
                            if (fileIdentityReader.Read(candidatePath) != expectedFile) continue;
                            if (selectedWorkbook is not null)
                                throw new DebugSetupException("The exact source workbook is open more than once. " +
                                    "Close the duplicate explicitly and retry; no workbook was opened or closed.");
                            selectedWorkbook = candidate;
                            candidate = null;
                            selectedApplication = application;
                            selectedApplicationIndex = applications.Count - 1;
                            selectedProcess = process;
                            wasAlreadyOpen = true;
                        }
                        catch (Exception failure) { candidateFailure = failure; throw; }
                        finally
                        {
                            ReleaseAcquired(candidateFailure, process.ProcessId,
                                ($"candidate workbook {index}", candidate));
                        }
                    }
                }
                catch (Exception failure) { enumerationFailure = failure; throw; }
                finally
                {
                    ReleaseAcquired(enumerationFailure, process.ProcessId,
                        ("enumerated workbooks", booksObject));
                }
            }

            if (selectedWorkbook is null)
            {
                selectedApplicationIndex = applications.Count;
                selectedApplication = api.CreateApplication();
                // Record the acquired reference before any identity probe can fail.
                applications.Add((default, selectedApplication));
                selectedApplicationHandoffVerified = false;
                selectedProcess = api.ReadProcessIdentity(selectedApplication);
                applications[selectedApplicationIndex] = (selectedProcess, selectedApplication);
                if (applications.Take(selectedApplicationIndex).Any(item => item.Process == selectedProcess))
                {
                    // This reference belongs to a known existing application;
                    // no new-session setting was changed and it can be released.
                    selectedApplicationHandoffVerified = true;
                    throw new DebugSetupException("Excel activation reused an existing caller-desktop process. " +
                        "No source workbook was opened and no existing application settings were changed.");
                }
                dynamic excel = selectedApplication;
                excel.Visible = true;
                excel.UserControl = true;
                if (!(bool)excel.Visible || !(bool)excel.UserControl)
                    throw new DebugSetupException("The new Excel session could not be handed to visible user control. " +
                        "No source workbook was opened.");
                selectedApplicationHandoffVerified = true;
                object? booksObject = null;
                Exception? openingFailure = null;
                try
                {
                    booksObject = excel.Workbooks;
                    dynamic books = booksObject;
                    OpenSourceWithScopedSettings(selectedApplication,
                        () => selectedWorkbook = books.Open(path), path, selectedProcess, releaseOutcomes);
                }
                catch (Exception failure) { openingFailure = failure; throw; }
                finally
                {
                    ReleaseAcquired(openingFailure, selectedProcess.ProcessId,
                        ("source-open workbooks", booksObject));
                }
            }

            if (selectedApplication is null || selectedWorkbook is null)
                throw new DebugSetupException("Excel did not provide the selected source workbook binding.");
            var boundApplication = selectedApplication;
            var boundWorkbook = selectedWorkbook;
            var boundProcess = selectedProcess;
            bool IsExact()
            {
                if (api.ReadProcessIdentity(boundApplication) != boundProcess) return false;
                dynamic workbook = boundWorkbook;
                var actualPath = (string)workbook.FullName;
                return Path.IsPathFullyQualified(actualPath)
                    && fileIdentityReader.Read(actualPath) == expectedFile
                    && fileIdentityReader.Read(path) == expectedFile;
            }
            if (!IsExact())
                throw new DebugSetupException("The opened source workbook did not retain its exact file and process binding.");
            var binding = new SourceVbeDesktopBinding(boundApplication, boundWorkbook, path,
                boundProcess.ProcessId, boundProcess.StartUtcTicks, wasAlreadyOpen, IsExact,
                () => SourceVbeComReferences.ReleaseScope(null, boundProcess.ProcessId, path,
                    releaseReference, ("source workbook", boundWorkbook),
                    ("borrowed Excel application", boundApplication)),
                api.ReadProcessArchitecture(boundProcess),
                () => ProbeCompletion(boundApplication, boundWorkbook, boundProcess));
            transferred = true;
            return binding;
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            var completion = new DebugFailureCompletion(primaryFailure);
            foreach (var outcome in releaseOutcomes) completion.Merge(outcome);
            var cleanupFailed = false;
            void ReleaseTerminal(string resource, object? value, int processId)
            {
                try
                {
                    completion.Merge(SourceVbeComReferences.ReleaseScope(
                        null, processId, path, releaseReference, (resource, value)));
                }
                catch (Exception failure)
                {
                    cleanupFailed = true;
                    completion.AddFailure("source attachment release", resource, DebugResourceKind.Com,
                        failure, processId > 0 ? processId : null, path);
                }
            }

            for (var index = 0; index < applications.Count; index++)
            {
                if (!transferred || index != selectedApplicationIndex)
                {
                    if (index == selectedApplicationIndex && !selectedApplicationHandoffVerified)
                    {
                        // Dropping this last RCW could implicitly quit a
                        // programmatically created, non-user-controlled Excel.
                        lock (retentionGate) retainedApplicationReferences.Add(applications[index].Application);
                        cleanupFailed = true;
                        completion.AddEvidence(new("source attachment release",
                            $"borrowed Excel application acquisition {index}", DebugResourceKind.Com, false,
                            "Visible user control could not be proved. The exact acquired reference is retained; " +
                            "inspect the new Excel window manually. No source workbook was opened.",
                            applications[index].Process.ProcessId > 0 ? applications[index].Process.ProcessId : null, path));
                        continue;
                    }
                    ReleaseTerminal($"borrowed Excel application acquisition {index}",
                        applications[index].Application, applications[index].Process.ProcessId);
                }
            }
            // If releasing a non-selected reference prevents return, do not leak
            // the references that were about to transfer to the binding.
            if (!transferred || cleanupFailed)
            {
                ReleaseTerminal("source workbook", selectedWorkbook, selectedProcess.ProcessId);
                if (transferred)
                    ReleaseTerminal($"borrowed Excel application acquisition {selectedApplicationIndex}",
                        selectedApplication, selectedProcess.ProcessId);
            }

            if (primaryFailure is not null || cleanupFailed)
            {
                completion.AddEvidence(new("source attachment", "source Excel lifetime", DebugResourceKind.Process,
                    true, "Source Excel lifetime ownership was never acquired; the workbook and process remain user-owned.",
                    selectedProcess.ProcessId > 0 ? selectedProcess.ProcessId : null, path));
                completion.AddEvidence(new("source attachment", "transient metadata handles", DebugResourceKind.Handle,
                    true, "The connector retains no process or file handles; metadata probes dispose their scoped handles.",
                    selectedProcess.ProcessId > 0 ? selectedProcess.ProcessId : null, path));
                completion.Complete().ThrowWithEvidence();
            }
        }
    }

    private static void OpenSourceWithScopedSettings(object application, Action open,
        string path, SourceVbeProcessIdentity process, List<DebugFailureOutcome> releaseOutcomes)
    {
        dynamic excel = application;
        var originalEvents = (bool)excel.EnableEvents;
        var originalSecurity = (int)excel.AutomationSecurity;
        var eventsNeedRestore = false;
        var securityNeedsRestore = false;
        Exception? openingFailure = null;
        try
        {
            eventsNeedRestore = true;
            excel.EnableEvents = false;
            securityNeedsRestore = true;
            excel.AutomationSecurity = MsoAutomationSecurityLow;
            if ((bool)excel.EnableEvents || (int)excel.AutomationSecurity != MsoAutomationSecurityLow)
                throw new DebugSetupException("The new Excel session did not confirm the scoped source-open settings.");
            // The caller records the acquired workbook before restoration can
            // fail, so that failure still releases its connection reference.
            open();
        }
        catch (Exception failure) { openingFailure = failure; throw; }
        finally
        {
            var restoration = new DebugFailureCompletion(openingFailure);
            void Restore(string resource, Action restore, Func<bool> isRestored)
            {
                var restored = false;
                try
                {
                    restore();
                    restored = isRestored();
                    if (!restored) throw new DebugSetupException($"The original Excel {resource} setting was not restored.");
                }
                catch (Exception failure)
                {
                    restoration.AddFailure("source-open settings restoration", resource,
                        DebugResourceKind.Observation, failure, process.ProcessId, path);
                }
                restoration.AddEvidence(new("source-open settings restoration", resource,
                    DebugResourceKind.Observation, restored,
                    restored ? "The exact captured application setting was restored."
                        : "The captured setting could not be restored; inspect the selected Excel session manually.",
                    process.ProcessId, path));
            }

            if (securityNeedsRestore)
                Restore("AutomationSecurity", () => excel.AutomationSecurity = originalSecurity,
                    () => (int)excel.AutomationSecurity == originalSecurity);
            if (eventsNeedRestore)
                Restore("EnableEvents", () => excel.EnableEvents = originalEvents,
                    () => (bool)excel.EnableEvents == originalEvents);
            var outcome = restoration.Complete();
            releaseOutcomes.Add(outcome);
            if (outcome.HasCleanupFailure) outcome.ThrowWithEvidence();
        }
    }

    private SourceVbeDebugSessionCompletion? ProbeCompletion(object application, object workbook,
        SourceVbeProcessIdentity process)
    {
        if (api.ReadProcessCompletion(process) is { } exited) return exited;
        try { return ProbeWorkbookCompletion(application, workbook, process); }
        catch (Exception failure)
        {
            // The exact process can exit after the OS observation but before a
            // COM/window read. COM disconnection alone never proves closure.
            // Confirm it independently, without hiding unproved COM release.
            if (failure is IDebugFailureEvidence retained && retained.FailureOutcome.HasCleanupFailure) throw;
            SourceVbeDebugSessionCompletion? confirmedExit;
            try { confirmedExit = api.ReadProcessCompletion(process); }
            catch (Exception observationFailure)
            {
                var outcome = new DebugFailureCompletion(failure);
                outcome.AddFailure("source process-exit confirmation", "exact source Excel process",
                    DebugResourceKind.Observation, observationFailure, process.ProcessId);
                outcome.Complete().ThrowWithEvidence();
                throw;
            }
            if (confirmedExit is { Reason: SourceVbeDebugSessionEndReason.ProcessExited }) return confirmedExit;
            throw;
        }
    }

    private SourceVbeDebugSessionCompletion? ProbeWorkbookCompletion(object application, object workbook,
        SourceVbeProcessIdentity process)
    {
        if (api.ReadProcessIdentity(application) != process)
            throw new DebugSetupException("The source Excel process identity changed during binding observation. " +
                "No workbook closure or process exit was proved; inspect the selected VBE manually.");
        object? booksObject = null;
        Exception? inspectionFailure = null;
        try
        {
            dynamic excel = application;
            booksObject = excel.Workbooks;
            dynamic books = booksObject;
            var count = (int)books.Count;
            for (var index = 1; index <= count; index++)
            {
                object? candidate = null;
                Exception? candidateFailure = null;
                try
                {
                    candidate = books.Item(index);
                    if (ReferenceEquals(candidate, workbook)) return null;
                }
                catch (Exception failure) { candidateFailure = failure; throw; }
                finally
                {
                    SourceVbeComReferences.ReleaseScope(candidateFailure, process.ProcessId, "caller desktop",
                        releaseReference, ($"observed workbook {index}", candidate));
                }
            }
            return new(SourceVbeDebugSessionEndReason.WorkbookClosed, null);
        }
        catch (Exception failure) { inspectionFailure = failure; throw; }
        finally
        {
            SourceVbeComReferences.ReleaseScope(inspectionFailure, process.ProcessId, "caller desktop",
                releaseReference, ("observed workbooks", booksObject));
        }
    }
}

internal sealed class WindowsSourceVbeDesktopApi : ISourceVbeDesktopApi
{
    private readonly WindowsExcelDebugNativeObjectModelApi nativeApi = new();
    private readonly WindowsSourceVbeCallerDesktopWindows callerDesktopWindows = new();

    public IReadOnlyList<SourceVbeProcessIdentity> CaptureExcelProcesses()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Source-workbook debugging requires Windows desktop Excel.");
        // A failed scan is not proof that the caller desktop has no Excel.
        _ = callerDesktopWindows.Find(0);
        using var caller = Process.GetCurrentProcess();
        var result = new List<SourceVbeProcessIdentity>();
        var processes = Process.GetProcessesByName("EXCEL");
        try
        {
            foreach (var process in processes)
            {
                // EnumDesktopWindows(NULL) enumerates only the current desktop. A
                // process on a private/other desktop grants no attach authority.
                if (process.SessionId != caller.SessionId
                    || callerDesktopWindows.Find(process.Id).Count == 0) continue;
                if (process.HasExited)
                    throw new DebugSetupException("A caller-desktop Excel instance exited during discovery. Retry explicitly.");
                result.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks));
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return result.OrderBy(item => item.ProcessId).ToArray();
    }

    public object? TryBindApplication(SourceVbeProcessIdentity process)
    {
        foreach (var topLevel in callerDesktopWindows.Find(process.ProcessId))
        {
            var nativeWindow = nativeApi.FindNativeObjectWindow(topLevel);
            if (nativeWindow == nint.Zero) continue;
            object? nativeObject = null;
            object? application = null;
            var transferred = false;
            Exception? primaryFailure = null;
            try
            {
                nativeApi.BindNativeObject(nativeWindow, out nativeObject);
                dynamic window = nativeObject!;
                application = window.Application;
                if (ReadProcessIdentity(application!) != process)
                    throw new DebugSetupException("Excel PID/start identity changed during native desktop binding.");
                var acquiredNativeObject = nativeObject;
                nativeObject = null;
                SourceVbeComReferences.ReleaseScope(null, process.ProcessId, "caller desktop",
                    ("native Excel window", acquiredNativeObject));
                transferred = true;
                return application;
            }
            catch (Exception failure) { primaryFailure = failure; throw; }
            finally
            {
                SourceVbeComReferences.ReleaseScope(primaryFailure, process.ProcessId, "caller desktop",
                    ("native Excel window", nativeObject),
                    ("borrowed Excel application", transferred ? null : application));
            }
        }
        // Do not treat an inaccessible Excel instance as proof that the source
        // is closed, and do not use GetActiveObject as a discovery fallback.
        throw new DebugSetupException("An Excel instance on the caller desktop has no accessible native object model. " +
            "Open its workbook window and resolve its prompts before retrying.");
    }

    public object CreateApplication()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Source-workbook debugging requires Windows desktop Excel.");
        var type = Type.GetTypeFromProgID("Excel.Application")
            ?? throw new DebugSetupException("Excel.Application is unavailable.");
        return Activator.CreateInstance(type)
            ?? throw new DebugSetupException("Excel.Application did not return a visible source session.");
    }

    public SourceVbeProcessIdentity ReadProcessIdentity(object application)
    {
        var processId = nativeApi.GetApplicationProcessId(application);
        if (processId <= 0) throw new DebugSetupException("The exact Excel window has no live PID.");
        using var process = Process.GetProcessById(processId);
        using var caller = Process.GetCurrentProcess();
        if (process.HasExited || process.SessionId != caller.SessionId
            || !process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)
            || callerDesktopWindows.Find(processId).Count == 0)
            throw new DebugSetupException("The Excel process is not a live caller-session desktop binding.");
        return new(processId, process.StartTime.ToUniversalTime().Ticks);
    }

    public DebugExcelProcessArchitecture ReadProcessArchitecture(SourceVbeProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
            return DebugExcelProcessArchitecture.Unknown;
        return WindowsExcelProcessArchitecture.Read(process.Handle);
    }

    public SourceVbeDebugSessionCompletion? ReadProcessCompletion(SourceVbeProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
                return new(SourceVbeDebugSessionEndReason.ProcessExited, null);
            if (process.HasExited)
                return new(SourceVbeDebugSessionEndReason.ProcessExited, process.ExitCode);
            return null;
        }
        catch (ArgumentException)
        {
            // The old PID entry vanished: exit is known, its code is not.
            return new(SourceVbeDebugSessionEndReason.ProcessExited, null);
        }
    }
}

internal sealed class WindowsSourceVbeCallerDesktopWindows
{
    private readonly Func<Action<nint>, bool> enumerate;
    private readonly Func<nint, int> readProcessId;

    internal WindowsSourceVbeCallerDesktopWindows()
        : this(EnumerateCurrentDesktop, ReadWindowProcessId)
    {
    }

    internal WindowsSourceVbeCallerDesktopWindows(
        Func<Action<nint>, bool> enumerate, Func<nint, int> readProcessId)
    {
        this.enumerate = enumerate;
        this.readProcessId = readProcessId;
    }

    internal IReadOnlyList<nint> Find(int processId)
    {
        var windows = new List<nint>();
        var enumerated = enumerate(window =>
        {
            if (processId == 0 || readProcessId(window) == processId) windows.Add(window);
        });
        if (!enumerated)
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Caller desktop window enumeration failed. No absent-source or duplicate-open authority was established.");
        return windows;
    }

    private static bool EnumerateCurrentDesktop(Action<nint> visit)
        => EnumDesktopWindows(nint.Zero, (window, _) => { visit(window); return true; }, nint.Zero);

    private static int ReadWindowProcessId(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    private delegate bool EnumDesktopWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(nint desktop, EnumDesktopWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
}

internal sealed class WindowsSourceVbePhysicalIdentityReader : ISourceVbePhysicalIdentityReader
{
    public SourceVbePhysicalFileIdentity Read(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical workbook identity requires Windows.");
        using var handle = CreateFile(Path.GetFullPath(path), 0, 7, nint.Zero, 3, 0, nint.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The exact source workbook file identity could not be read.");
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess,
        uint shareMode, nint securityAttributes, uint creationDisposition, uint flags, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
