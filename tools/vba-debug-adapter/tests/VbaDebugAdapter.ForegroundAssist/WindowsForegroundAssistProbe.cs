using System.ComponentModel;
using System.Diagnostics;
using Microsoft.CSharp.RuntimeBinder;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace VbaDebugAdapter.ForegroundAssist;

[SupportedOSPlatform("windows")]
internal sealed class WindowsForegroundAssistProbe(
    string workspaceRoot, string workbookFileName, string ownerToken)
    : IForegroundAssistProbe
{
    private const uint ObjectIdNativeObjectModel = 0xfffffff0;
    private const int ShowWindowRestore = 9;
    private static readonly Guid IDispatchId = new("00020400-0000-0000-C000-000000000046");

    public IReadOnlyList<int> CaptureExcelProcessIds()
    {
        var result = new List<int>();
        foreach (var process in Process.GetProcessesByName("EXCEL"))
        {
            using (process)
            {
                result.Add(process.Id);
            }
        }

        return result;
    }

    public bool IsDebugWorkbookProcess(int processId) =>
        RunAgainstExcelWindow(processId, window =>
            HasExpectedDebugWorkbook(window, workspaceRoot, workbookFileName, ownerToken));

    public bool TryBringVbeToForeground(int processId) =>
        RunAgainstExcelWindow(processId, window =>
            HasExpectedDebugWorkbook(window, workspaceRoot, workbookFileName, ownerToken) &&
            EstablishForeground(window, processId));

    private static bool RunAgainstExcelWindow(int processId, Func<nint, bool> action)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Refresh();
            if (!process.ProcessName.Equals("EXCEL", StringComparison.OrdinalIgnoreCase) ||
                process.MainWindowHandle == nint.Zero ||
                !IsWindowVisible(process.MainWindowHandle))
            {
                return false;
            }

            var excelWindow = process.MainWindowHandle;
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    completion.TrySetResult(action(excelWindow));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Extension Host test VBE foreground assist"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return completion.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or
            InvalidComObjectException or RuntimeBinderException or UnauthorizedAccessException or
            InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool HasExpectedDebugWorkbook(
        nint excelWindowHandle, string workspaceRoot, string workbookFileName, string ownerToken)
    {
        var nativeObjectWindow = FindDescendantWindow(excelWindowHandle, "EXCEL7");
        if (nativeObjectWindow == nint.Zero)
        {
            return false;
        }

        object? nativeObject = null;
        object? application = null;
        object? workbooksObject = null;
        object? workbookObject = null;
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
            workbooksObject = excel.Workbooks;
            dynamic workbooks = workbooksObject;
            var count = Convert.ToInt32(workbooks.Count);
            for (var index = 1; index <= count; index++)
            {
                workbookObject = workbooks.Item(index);
                dynamic workbook = workbookObject;
                var fullName = Convert.ToString(workbook.FullName);
                if (fullName is not null && DebugWorkbookIdentity.Matches(
                        workspaceRoot, workbookFileName, fullName, ownerToken))
                {
                    return true;
                }

                ReleaseComObject(workbookObject);
                workbookObject = null;
            }

            return false;
        }
        finally
        {
            ReleaseComObject(workbookObject);
            ReleaseComObject(workbooksObject);
            ReleaseComObject(application);
            ReleaseComObject(nativeObject);
        }
    }

    private static bool EstablishForeground(nint excelWindowHandle, int processId)
    {
        var nativeObjectWindow = FindDescendantWindow(excelWindowHandle, "EXCEL7");
        if (nativeObjectWindow == nint.Zero)
        {
            return false;
        }

        object? nativeObject = null;
        object? application = null;
        object? vbeObject = null;
        object? mainWindowObject = null;
        object? codePaneObject = null;
        object? codeWindowObject = null;
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
            AllowComServerForeground(application);
            dynamic excel = application;
            vbeObject = excel.VBE;
            dynamic vbe = vbeObject;
            mainWindowObject = vbe.MainWindow;
            dynamic mainWindow = mainWindowObject;
            mainWindow.Visible = true;
            mainWindow.SetFocus();
            codePaneObject = vbe.ActiveCodePane;
            if (codePaneObject is not null)
            {
                dynamic codePane = codePaneObject;
                codePane.Show();
                codeWindowObject = codePane.Window;
                dynamic codeWindow = codeWindowObject;
                codeWindow.SetFocus();
            }

            var vbeWindowHandle = new nint(Convert.ToInt64(mainWindow.HWnd));
            _ = GetWindowThreadProcessId(vbeWindowHandle, out var ownerProcessId);
            if (ownerProcessId != processId)
            {
                return false;
            }

            return SetForegroundFromAttachedInputQueues(vbeWindowHandle);
        }
        finally
        {
            ReleaseComObject(codeWindowObject);
            ReleaseComObject(codePaneObject);
            ReleaseComObject(mainWindowObject);
            ReleaseComObject(vbeObject);
            ReleaseComObject(application);
            ReleaseComObject(nativeObject);
        }
    }

    private static nint FindDescendantWindow(nint parentWindow, string className)
    {
        nint result = nint.Zero;
        _ = EnumChildWindows(parentWindow, (window, _) =>
        {
            var buffer = new StringBuilder(256);
            _ = GetClassName(window, buffer, buffer.Capacity);
            if (!buffer.ToString().Equals(className, StringComparison.Ordinal))
            {
                return true;
            }

            result = window;
            return false;
        }, nint.Zero);
        return result;
    }

    private static bool SetForegroundFromAttachedInputQueues(nint targetWindow)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == targetWindow)
        {
            return true;
        }

        var currentThreadId = GetCurrentThreadId();
        var foregroundThreadId = foregroundWindow == nint.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
        var attachedForeground = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
        var attachedTarget = targetThreadId != 0 &&
            targetThreadId != currentThreadId &&
            targetThreadId != foregroundThreadId &&
            AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            _ = ShowWindow(targetWindow, ShowWindowRestore);
            _ = BringWindowToTop(targetWindow);
            _ = SetForegroundWindow(targetWindow);
            _ = SetFocus(targetWindow);
            return GetForegroundWindow() == targetWindow;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedForeground)
            {
                _ = AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private static void AllowComServerForeground(object application)
    {
        if (!Marshal.IsComObject(application))
        {
            return;
        }

        var unknown = Marshal.GetIUnknownForObject(application);
        try
        {
            _ = CoAllowSetForegroundWindow(unknown, nint.Zero);
        }
        finally
        {
            _ = Marshal.Release(unknown);
        }
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint windowHandle,
        uint objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object accessibleObject);

    [DllImport("ole32.dll")]
    private static extern int CoAllowSetForegroundWindow(nint unknown, nint reserved);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parentWindow, EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximumLength);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
