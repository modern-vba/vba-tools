using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VbaDev.Infrastructure.Debugging;

internal static class WindowsJobProcessLauncher
{
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseShowWindow = 0x00000001;
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
        string? desktopName = null)
    {
        ArgumentNullException.ThrowIfNull(jobHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(terminateJob);
        if (desktopName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Atomic Job Object process launch requires Windows.");
        }

        nint attributeList = nint.Zero;
        nint jobHandleValue = nint.Zero;
        nint desktopNameValue = nint.Zero;
        var attributeListInitialized = false;
        var jobHandleReferenceAdded = false;
        SafeFileHandle? createdProcessHandle = null;
        SafeFileHandle? createdThreadHandle = null;
        Process? process = null;
        var processCreated = false;
        try
        {
            ObjectDisposedException.ThrowIf(jobHandle.IsClosed, jobHandle);
            if (jobHandle.IsInvalid)
            {
                throw new ArgumentException("The Job Object handle is invalid.", nameof(jobHandle));
            }

            jobHandle.DangerousAddRef(ref jobHandleReferenceAdded);
            if (desktopName is not null)
            {
                desktopNameValue = Marshal.StringToHGlobalUni(desktopName);
            }

            var startupInfo = CreateStartupInfo(
                jobHandle,
                desktopNameValue,
                out attributeList,
                out jobHandleValue,
                out attributeListInitialized);
            var commandLine = new StringBuilder(BuildCommandLine(applicationPath, arguments));
            if (!CreateProcessW(
                applicationPath,
                commandLine,
                nint.Zero,
                nint.Zero,
                inheritHandles: false,
                CreateSuspended | ExtendedStartupInfoPresent,
                nint.Zero,
                Path.GetDirectoryName(applicationPath),
                ref startupInfo,
                out var processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processCreated = true;
            createdProcessHandle = new SafeFileHandle(
                processInformation.ProcessHandle,
                ownsHandle: true);
            createdThreadHandle = new SafeFileHandle(
                processInformation.ThreadHandle,
                ownsHandle: true);
            if (!IsProcessInJob(createdProcessHandle, jobHandle, out var belongsToJob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!belongsToJob)
            {
                throw new InvalidOperationException(
                    "The suspended Excel process was not atomically assigned to its kill-on-close Job Object.");
            }

            process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            var ownedProcess = new SystemDebugOwnedProcess(process);
            process = null;
            var primaryThread = new WindowsSuspendedPrimaryThread(createdThreadHandle);
            createdThreadHandle = null;
            createdProcessHandle.Dispose();
            createdProcessHandle = null;
            return new DebugSuspendedProcessLaunch(ownedProcess, primaryThread);
        }
        catch (Exception launchException)
        {
            Exception? cleanupException = null;
            if (processCreated)
            {
                try
                {
                    terminateJob();
                }
                catch (Exception ex)
                {
                    cleanupException = ex;
                    if (createdProcessHandle is not null &&
                        !createdProcessHandle.IsInvalid &&
                        !TerminateProcess(createdProcessHandle, FailedLaunchExitCode))
                    {
                        cleanupException = new AggregateException(
                            cleanupException,
                            new Win32Exception(Marshal.GetLastWin32Error()));
                    }
                }

                if (createdProcessHandle is not null && !createdProcessHandle.IsInvalid)
                {
                    var waitResult = WaitForSingleObject(
                        createdProcessHandle,
                        FailedLaunchWaitMilliseconds);
                    if (waitResult != WaitObject0)
                    {
                        Exception waitError = waitResult == WaitTimeout
                            ? new TimeoutException(
                                "Timed out while verifying cleanup of the suspended Excel process.")
                            : new Win32Exception(Marshal.GetLastWin32Error());
                        cleanupException = cleanupException is null
                            ? waitError
                            : new AggregateException(cleanupException, waitError);
                    }
                }
            }

            process?.Dispose();
            createdThreadHandle?.Dispose();
            createdProcessHandle?.Dispose();
            if (cleanupException is not null)
            {
                throw new DebugProcessOwnershipCleanupException(
                    launchException,
                    cleanupException);
            }

            throw;
        }
        finally
        {
            if (attributeListInitialized)
            {
                DeleteProcThreadAttributeList(attributeList);
            }

            if (attributeList != nint.Zero)
            {
                Marshal.FreeHGlobal(attributeList);
            }

            if (jobHandleValue != nint.Zero)
            {
                Marshal.FreeHGlobal(jobHandleValue);
            }

            if (desktopNameValue != nint.Zero)
            {
                Marshal.FreeHGlobal(desktopNameValue);
            }

            if (jobHandleReferenceAdded)
            {
                jobHandle.DangerousRelease();
            }
        }
    }

    private static StartupInfoEx CreateStartupInfo(
        SafeFileHandle jobHandle,
        nint desktopName,
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
                Desktop = desktopName,
                Flags = StartfUseShowWindow,
                ShowWindow = ShowWindowHidden
            },
            AttributeList = attributeList
        };
    }

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
        private int resumed;

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

        public void Dispose() => handle.Dispose();
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
        SafeFileHandle handle,
        int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
}
