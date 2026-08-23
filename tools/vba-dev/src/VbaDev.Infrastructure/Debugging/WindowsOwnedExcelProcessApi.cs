using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VbaDev.Infrastructure.Workbooks;

namespace VbaDev.Infrastructure.Debugging;

internal sealed class WindowsDebugExcelProcessApi : IDebugExcelProcessApi
{
    public IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses()
        => ExcelComApplicationProcess.CaptureRunningExcelProcesses();

    public int GetProcessId(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId <= int.MaxValue ? (int)processId : 0;
    }

    public IDebugOwnedProcess OpenProcess(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DebugSetupException("Excel process ownership requires Windows.");
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (!process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    "The captured application window does not belong to Microsoft Excel.");
            }

            var ownedProcess = new SystemDebugOwnedProcess(process);
            process = null;
            return ownedProcess;
        }
        catch (DebugSetupException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new DebugSetupException(
                "The exact Excel process identity could not be captured for owned automation.",
                exception);
        }
        finally
        {
            process?.Dispose();
        }
    }

    public IDebugProcessJob CreateKillOnCloseJob()
    {
        try
        {
            return WindowsDebugProcessJob.Create();
        }
        catch (Exception exception)
            when (exception is Win32Exception or PlatformNotSupportedException)
        {
            throw new DebugSetupException(
                "A kill-on-close Windows Job Object could not be created for owned Excel.",
                exception);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

internal sealed class SystemDebugOwnedProcess : IDebugOwnedProcess
{
    private readonly Process process;

    public SystemDebugOwnedProcess(Process process)
    {
        this.process = process;
        Id = process.Id;
        StartTime = process.StartTime;
        Architecture = WindowsExcelProcessArchitecture.Read(process.Handle);
    }

    public int Id { get; }

    internal nint Handle => process.Handle;

    public DebugExcelProcessArchitecture Architecture { get; }

    public DateTime StartTime { get; }

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => process.WaitForExitAsync(cancellationToken);

    public void Kill()
    {
        if (process.Id != Id || process.StartTime != StartTime)
        {
            throw new InvalidOperationException(
                "The owned Excel process identity changed before termination.");
        }

        process.Kill(entireProcessTree: false);
    }

    public void Dispose() => process.Dispose();
}

internal static class WindowsExcelProcessArchitecture
{
    public static DebugExcelProcessArchitecture Read(nint processHandle)
    {
        if (!OperatingSystem.IsWindows() || processHandle == nint.Zero)
        {
            return DebugExcelProcessArchitecture.Unknown;
        }

        try
        {
            if (!IsWow64Process2(processHandle, out var processMachine, out var nativeMachine))
            {
                return DebugExcelProcessArchitecture.Unknown;
            }

            return ToArchitecture(processMachine == ImageFileMachineUnknown
                ? nativeMachine
                : processMachine);
        }
        catch (EntryPointNotFoundException)
        {
            return DebugExcelProcessArchitecture.Unknown;
        }
    }

    private static DebugExcelProcessArchitecture ToArchitecture(ushort machine)
        => machine switch
        {
            ImageFileMachineI386 => DebugExcelProcessArchitecture.X86,
            ImageFileMachineAmd64 => DebugExcelProcessArchitecture.X64,
            ImageFileMachineArm64 => DebugExcelProcessArchitecture.Arm64,
            _ => DebugExcelProcessArchitecture.Unknown
        };

    private const ushort ImageFileMachineUnknown = 0x0000;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xaa64;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(
        nint processHandle,
        out ushort processMachine,
        out ushort nativeMachine);
}
