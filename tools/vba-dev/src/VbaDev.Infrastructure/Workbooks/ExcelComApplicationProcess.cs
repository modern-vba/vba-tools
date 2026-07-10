using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Workbooks;

internal sealed class ExcelComApplicationProcess
{
    private readonly int processId;
    private readonly DateTime startTime;

    private ExcelComApplicationProcess(int processId, DateTime startTime)
    {
        this.processId = processId;
        this.startTime = startTime;
    }

    public static IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new Dictionary<int, DateTime>();
        }

        var processes = new Dictionary<int, DateTime>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                var startTime = TryGetProcessStartTime(process);
                if (startTime is not null)
                {
                    processes[process.Id] = startTime.Value;
                }
            }
        }

        return processes;
    }

    public static ExcelComApplicationProcess? TryCaptureOwned(
        object excelObject,
        IReadOnlyDictionary<int, DateTime> existingExcelProcesses)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var hwndProcess = TryCaptureFromApplicationHwnd(excelObject);
        if (hwndProcess is not null && !existingExcelProcesses.ContainsKey(hwndProcess.processId))
        {
            return hwndProcess;
        }

        return TryCaptureSingleNewExcelProcess(existingExcelProcesses);
    }

    private static ExcelComApplicationProcess? TryCaptureFromApplicationHwnd(object excelObject)
    {
        try
        {
            dynamic excel = excelObject;
            var hwnd = new IntPtr((int)excel.Hwnd);
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            var startTime = TryGetProcessStartTime(process);
            return startTime is null
                ? null
                : new ExcelComApplicationProcess((int)processId, startTime.Value);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }

    private static ExcelComApplicationProcess? TryCaptureSingleNewExcelProcess(
        IReadOnlyDictionary<int, DateTime> existingExcelProcesses)
    {
        var candidates = new List<ExcelComApplicationProcess>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                if (existingExcelProcesses.ContainsKey(process.Id))
                {
                    continue;
                }

                var startTime = TryGetProcessStartTime(process);
                if (startTime is not null)
                {
                    candidates.Add(new ExcelComApplicationProcess(process.Id, startTime.Value));
                }
            }
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public void TerminateIfStillRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            if (!process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (process.StartTime != startTime)
            {
                return;
            }

            if (process.WaitForExit(2000))
            {
                return;
            }

            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static DateTime? TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
