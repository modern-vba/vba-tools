using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VbaDebugAdapter.Infrastructure;

internal static class WindowsJobProcessLauncher
{
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const ushort ShowWindowHidden = 0;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint FailedLaunchExitCode = 1;
    private const int FailedLaunchWaitMilliseconds = 5000;

    public static DebugSuspendedProcessLaunch StartSuspended(
        SafeFileHandle jobHandle,
        string applicationPath,
        IReadOnlyList<string> arguments,
        Action terminateJob,
        bool redirectOutput = false)
    {
        ArgumentNullException.ThrowIfNull(jobHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(terminateJob);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Atomic Job Object process launch requires Windows.");
        }

        nint attributeList = nint.Zero;
        nint jobHandleValue = nint.Zero;
        var attributeListInitialized = false;
        var jobHandleReferenceAdded = false;
        SafeFileHandle? createdProcessHandle = null;
        SafeFileHandle? createdThreadHandle = null;
        SafeFileHandle? standardInputRead = null;
        SafeFileHandle? standardInputWrite = null;
        SafeFileHandle? standardOutputRead = null;
        SafeFileHandle? standardOutputWrite = null;
        SafeFileHandle? standardErrorRead = null;
        SafeFileHandle? standardErrorWrite = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;
        Process? process = null;
        SafeProcessHandle? managedProcessHandle = null;
        var processCreated = false;
        int? processId = null;
        var completion = new DebugFailureCompletion();
        try
        {
            ObjectDisposedException.ThrowIf(jobHandle.IsClosed, jobHandle);
            if (jobHandle.IsInvalid)
            {
                throw new ArgumentException("The Job Object handle is invalid.", nameof(jobHandle));
            }
            if (redirectOutput)
            {
                CreateRedirectedHandles(out standardInputRead, out standardInputWrite,
                    out standardOutputRead, out standardOutputWrite, out standardErrorRead, out standardErrorWrite);
            }
            jobHandle.DangerousAddRef(ref jobHandleReferenceAdded);
            var startupInfo = CreateStartupInfo(jobHandle, redirectOutput,
                standardInputRead?.DangerousGetHandle() ?? nint.Zero,
                standardOutputWrite?.DangerousGetHandle() ?? nint.Zero,
                standardErrorWrite?.DangerousGetHandle() ?? nint.Zero,
                out attributeList, out jobHandleValue, out attributeListInitialized);
            var commandLine = new StringBuilder(BuildCommandLine(applicationPath, arguments));
            if (!CreateProcessW(applicationPath, commandLine, nint.Zero, nint.Zero,
                inheritHandles: redirectOutput, CreateSuspended | ExtendedStartupInfoPresent,
                nint.Zero, Path.GetDirectoryName(applicationPath), ref startupInfo, out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            processCreated = true;
            processId = checked((int)processInformation.ProcessId);
            // Acquire both handles before any fallible local release or managed wrapper.
            createdProcessHandle = new SafeFileHandle(processInformation.ProcessHandle, ownsHandle: true);
            createdThreadHandle = new SafeFileHandle(processInformation.ThreadHandle, ownsHandle: true);
            ReleaseHandle(ref standardInputRead, "stdin read");
            ReleaseHandle(ref standardInputWrite, "stdin write");
            ReleaseHandle(ref standardOutputWrite, "stdout write");
            ReleaseHandle(ref standardErrorWrite, "stderr write");
            if (!IsProcessInJob(createdProcessHandle, jobHandle, out var belongsToJob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!belongsToJob)
            {
                throw new InvalidOperationException(
                    "The suspended Excel process was not atomically assigned to its kill-on-close Job Object.");
            }
            process = Process.GetProcessById(processId.Value);
            managedProcessHandle = process.SafeHandle;
            var ownedProcess = new SystemDebugOwnedProcess(process);
            var primaryThread = new WindowsSuspendedPrimaryThread(createdThreadHandle);
            if (redirectOutput)
            {
                standardOutput = CreateReader(standardOutputRead!);
                standardOutputRead = null;
                standardError = CreateReader(standardErrorRead!);
                standardErrorRead = null;
            }
            ReleaseStartupResources();
            ReleaseHandle(ref createdProcessHandle, "creation process handle");
            var localOutcome = completion.Complete();
            if (localOutcome.HasCleanupFailure) { throw new DebugFailureException(localOutcome); }
            var result = new DebugSuspendedProcessLaunch(ownedProcess, primaryThread, standardOutput, standardError)
            {
                LaunchCleanupOutcome = localOutcome
            };
            // Transfer only after local cleanup and the complete launch result exist.
            process = null;
            createdThreadHandle = null;
            standardOutput = null;
            standardError = null;
            return result;
        }
        catch (Exception primary)
        {
            var prior = completion.Complete();
            completion = new DebugFailureCompletion(primary);
            completion.Merge(prior);
            var exited = !processCreated;
            if (processCreated)
            {
                try { terminateJob(); }
                catch (Exception failure)
                {
                    completion.AddFailure("launcher-job-termination", applicationPath,
                        DebugResourceKind.Process, failure, processId);
                    if (createdProcessHandle is not null && !createdProcessHandle.IsInvalid
                        && !TerminateProcess(createdProcessHandle, FailedLaunchExitCode))
                    {
                        completion.AddFailure("launcher-process-termination", applicationPath,
                            DebugResourceKind.Process, new Win32Exception(Marshal.GetLastWin32Error()), processId);
                    }
                }
                // The fixed failed-launch budget remains separate from visible Excel ownership.
                var waitHandle = (SafeHandle?)createdProcessHandle ?? managedProcessHandle;
                if (waitHandle is not null && !waitHandle.IsInvalid)
                {
                    var waitResult = WaitForSingleObject(waitHandle, FailedLaunchWaitMilliseconds);
                    exited = waitResult == WaitObject0;
                    if (!exited)
                    {
                        Exception failure = waitResult == WaitTimeout
                            ? new TimeoutException("Timed out while verifying cleanup of the suspended Excel process.")
                            : new Win32Exception(Marshal.GetLastWin32Error());
                        completion.AddFailure("launcher-process-cleanup", applicationPath,
                            DebugResourceKind.Process, failure, processId);
                    }
                }
            }
            completion.AddEvidence(new("launcher-process-cleanup", applicationPath, DebugResourceKind.Process,
                exited, processCreated ? "The launcher retained the native terminal wait result."
                    : "CreateProcess did not acquire a process.", processId));
            if (process is not null)
            {
                if (managedProcessHandle is not null)
                {
                    ReleaseNativeHandle(managedProcessHandle, "managed process handle");
                }
                try { process.Dispose(); }
                catch (Exception failure) { AddHandleFailure("managed process state", failure); }
            }
            ReleaseReader(standardOutput, "stdout read");
            ReleaseReader(standardError, "stderr read");
            ReleaseHandle(ref createdThreadHandle, "primary thread handle");
            ReleaseHandle(ref createdProcessHandle, "creation process handle");
            ReleaseHandle(ref standardInputRead, "stdin read");
            ReleaseHandle(ref standardInputWrite, "stdin write");
            ReleaseHandle(ref standardOutputRead, "stdout read");
            ReleaseHandle(ref standardOutputWrite, "stdout write");
            ReleaseHandle(ref standardErrorRead, "stderr read");
            ReleaseHandle(ref standardErrorWrite, "stderr write");
            ReleaseStartupResources();
            completion.Complete().ThrowWithEvidence();
            throw;
        }

        void AddHandleFailure(string resource, Exception failure)
            => completion.AddFailure("launcher-local-handles", resource, DebugResourceKind.Handle, failure, processId);

        void ReleaseNativeHandle(SafeHandle handle, string resource)
        {
            var release = new DebugNativeHandleRelease(handle);
            try { release.Release(); }
            catch (Exception failure) { AddHandleFailure(resource, failure); }
            completion.AddEvidence(new("launcher-local-handles", resource, DebugResourceKind.Handle,
                release.IsVerified, "The local owner retained the native CloseHandle result.", processId));
        }

        void ReleaseHandle(ref SafeFileHandle? handle, string resource)
        {
            if (handle is null) { return; }
            var owned = handle;
            handle = null;
            if (owned.IsInvalid && !owned.IsClosed)
            {
                // A failed native acquisition returned no valid resource.
                owned.Dispose();
                completion.AddEvidence(new("launcher-local-handles", resource, DebugResourceKind.Handle,
                    true, "The native acquisition returned an invalid handle without acquiring a resource.", processId));
                return;
            }
            ReleaseNativeHandle(owned, resource);
        }

        void ReleaseReader(StreamReader? reader, string resource)
        {
            if (reader is null) { return; }
            // These readers have not escaped launch and no asynchronous read has started.
            if (reader.BaseStream is FileStream file) { ReleaseNativeHandle(file.SafeFileHandle, resource); }
            try { reader.Dispose(); }
            catch (Exception failure) { AddHandleFailure(resource, failure); }
        }

        void ReleaseStartupResources()
        {
            if (attributeListInitialized)
            {
                attributeListInitialized = false;
                try { DeleteProcThreadAttributeList(attributeList); }
                catch (Exception failure) { AddHandleFailure("startup attribute list", failure); }
            }
            if (attributeList != nint.Zero)
            {
                var owned = attributeList;
                attributeList = nint.Zero;
                try { Marshal.FreeHGlobal(owned); }
                catch (Exception failure) { AddHandleFailure("startup attribute memory", failure); }
            }
            if (jobHandleValue != nint.Zero)
            {
                var owned = jobHandleValue;
                jobHandleValue = nint.Zero;
                try { Marshal.FreeHGlobal(owned); }
                catch (Exception failure) { AddHandleFailure("startup Job list memory", failure); }
            }
            if (jobHandleReferenceAdded)
            {
                jobHandleReferenceAdded = false;
                try { jobHandle.DangerousRelease(); }
                catch (Exception failure) { AddHandleFailure("startup Job handle reference", failure); }
            }
        }
    }

    private static StartupInfoEx CreateStartupInfo(
        SafeFileHandle jobHandle,
        bool redirectOutput,
        nint standardInput,
        nint standardOutput,
        nint standardError,
        out nint attributeList,
        out nint jobHandleValue,
        out bool attributeListInitialized)
    {
        attributeList = nint.Zero;
        jobHandleValue = nint.Zero;
        attributeListInitialized = false;
        nuint attributeListSize = 0;
        _ = InitializeProcThreadAttributeList(
            nint.Zero,
            attributeCount: 1,
            flags: 0,
            ref attributeListSize);
        if (attributeListSize == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
        if (!InitializeProcThreadAttributeList(
            attributeList,
            attributeCount: 1,
            flags: 0,
            ref attributeListSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        attributeListInitialized = true;

        jobHandleValue = Marshal.AllocHGlobal(nint.Size);
        Marshal.WriteIntPtr(jobHandleValue, jobHandle.DangerousGetHandle());
        if (!UpdateProcThreadAttribute(
            attributeList,
            flags: 0,
            ProcThreadAttributeJobList,
            jobHandleValue,
            (nuint)nint.Size,
            nint.Zero,
            nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseShowWindow |
                    (redirectOutput ? StartfUseStdHandles : 0u),
                ShowWindow = ShowWindowHidden,
                StandardInput = standardInput,
                StandardOutput = standardOutput,
                StandardError = standardError
            },
            AttributeList = attributeList
        };
    }

    private static void CreateRedirectedHandles(
        out SafeFileHandle standardInputRead,
        out SafeFileHandle standardInputWrite,
        out SafeFileHandle standardOutputRead,
        out SafeFileHandle standardOutputWrite,
        out SafeFileHandle standardErrorRead,
        out SafeFileHandle standardErrorWrite)
    {
        var securityAttributes = new SecurityAttributes
        {
            Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        CreatePipePair(
            ref securityAttributes,
            parentReads: false,
            out standardInputRead,
            out standardInputWrite);
        CreatePipePair(ref securityAttributes, parentReads: true,
            out standardOutputRead, out standardOutputWrite);
        CreatePipePair(ref securityAttributes, parentReads: true,
            out standardErrorRead, out standardErrorWrite);
    }

    private static void CreatePipePair(
        ref SecurityAttributes securityAttributes,
        bool parentReads,
        out SafeFileHandle readHandle,
        out SafeFileHandle writeHandle)
    {
        if (!CreatePipe(
                out readHandle,
                out writeHandle,
                ref securityAttributes,
                size: 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var parentHandle = parentReads ? readHandle : writeHandle;
        if (!SetHandleInformation(parentHandle, HandleFlagInherit, flags: 0))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            throw error;
        }
    }

    private static StreamReader CreateReader(SafeFileHandle readHandle)
        => new(
            new FileStream(readHandle, FileAccess.Read),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

    private static string BuildCommandLine(
        string applicationPath,
        IReadOnlyList<string> arguments)
    {
        var commandLine = new StringBuilder(QuoteArgument(applicationPath));
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            commandLine.Append(QuoteArgument(argument));
        }

        return commandLine.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 &&
            !argument.Any(static character =>
                char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private sealed class WindowsSuspendedPrimaryThread(
        SafeFileHandle handle) : IDebugSuspendedPrimaryThread
    {
        private readonly DebugNativeHandleRelease handleRelease = new(handle);
        private int resumed;

        public bool HandleReleaseVerified => handleRelease.IsVerified;

        public void ResumeExactlyOnce()
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            if (Interlocked.Exchange(ref resumed, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The owned Excel primary thread was already resumed.");
            }

            var previousSuspendCount = ResumeThread(handle);
            if (previousSuspendCount == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (previousSuspendCount != 1)
            {
                throw new InvalidOperationException(
                    $"The owned Excel primary thread had unexpected suspend count {previousSuspendCount}.");
            }
        }

        public void Dispose() => handleRelease.Release();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        nint attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint valueSize,
        nint previousValue,
        nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint attributeList);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        SafeFileHandle process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeFileHandle process,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        SafeHandle handle,
        int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);
}
