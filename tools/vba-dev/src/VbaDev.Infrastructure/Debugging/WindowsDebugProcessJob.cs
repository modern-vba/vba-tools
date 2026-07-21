using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using VbaDev.App.Debugging;

namespace VbaDev.Infrastructure.Debugging;

/// <summary>
/// Holds a kill-on-close Windows Job Object for one owned Excel process tree.
/// </summary>
internal sealed class WindowsDebugProcessJob : IDebugProcessJob
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint DebugTerminationExitCode = 1;

    private readonly SafeFileHandle handle;
    private int disposed;

    private WindowsDebugProcessJob(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    public static WindowsDebugProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Strong Excel process ownership requires Windows Job Objects.");
        }

        var jobHandle = CreateJobObjectW(nint.Zero, null);
        if (jobHandle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            jobHandle.Dispose();
            throw error;
        }

        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };
            if (!SetInformationJobObject(
                jobHandle,
                JobObjectExtendedLimitInformationClass,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new WindowsDebugProcessJob(jobHandle);
        }
        catch
        {
            jobHandle.Dispose();
            throw;
        }
    }

    public void Assign(IDebugOwnedProcess process)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (process is not SystemDebugOwnedProcess systemProcess)
        {
            throw new DebugSetupException(
                "The exact Excel process does not expose a Windows process handle for strong ownership.");
        }

        if (!AssignProcessToJobObject(handle, systemProcess.Handle))
        {
            throw new DebugSetupException(
                $"Owned Excel process {process.Id} could not be assigned to a kill-on-close Windows Job Object.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    public void Terminate()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!TerminateJobObject(handle, DebugTerminationExitCode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        SafeFileHandle job,
        uint exitCode);
}
