using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Workbooks;
using Microsoft.CSharp.RuntimeBinder;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace VbaDev.Infrastructure.Debugging;

internal interface IExcelDebugApplicationFactory
{
    object Create();
}

internal interface IDebugWindowActivator
{
    int AllowComServerForeground(object comServerObject);

    void BringOwnedWindowToForeground(nint windowHandle, int processId);
}

internal interface IWindowsDebugWindowApi
{
    int AllowComServerForeground(object comServerObject);

    int GetProcessId(nint windowHandle);

    void Restore(nint windowHandle);

    bool SetForeground(nint windowHandle);

    nint GetForegroundWindow();

    void WaitForForegroundTransition();
}

internal interface IStaComDispatcher : IAsyncDisposable
{
    Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken);
}

internal interface IStaComDispatcherFactory
{
    IStaComDispatcher Create();
}

/// <summary>
/// Starts a dedicated visible Excel instance and performs VBE-native target execution.
/// </summary>
public sealed class VbeDebugAutomation : IVbeDebugSessionFactory
{
    private readonly IExcelDebugApplicationFactory applicationFactory;
    private readonly IDebugExcelProcessApi processApi;
    private readonly IDebugWindowActivator windowActivator;
    private readonly IStaComDispatcherFactory dispatcherFactory;

    /// <summary>
    /// Creates the production Excel/VBIDE automation adapter.
    /// </summary>
    public VbeDebugAutomation()
        : this(
            new ExcelDebugApplicationFactory(),
            new WindowsDebugExcelProcessApi(),
            new WindowsDebugWindowActivator(),
            new StaComDispatcherFactory())
    {
    }

    internal VbeDebugAutomation(
        IExcelDebugApplicationFactory applicationFactory,
        IDebugExcelProcessApi processApi,
        IDebugWindowActivator windowActivator,
        IStaComDispatcherFactory dispatcherFactory)
    {
        this.applicationFactory = applicationFactory;
        this.processApi = processApi;
        this.windowActivator = windowActivator;
        this.dispatcherFactory = dispatcherFactory;
    }

    /// <inheritdoc />
    public async Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existingExcelProcesses = processApi.CaptureRunningExcelProcesses();
        var dispatcher = dispatcherFactory.Create();
        object? excelObject = null;
        DebugExcelProcessOwner? processOwner = null;
        try
        {
            await dispatcher.InvokeAsync(
                () =>
                {
                    excelObject = applicationFactory.Create();
                    dynamic excel = excelObject;
                    var windowHandle = ToWindowHandle(excel.Hwnd);
                    processOwner = DebugExcelProcessOwner.Capture(
                        windowHandle,
                        existingExcelProcesses,
                        processApi);
                    excel.Visible = true;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return new VbeDebugSession(
                excelObject!,
                processOwner!,
                dispatcher,
                windowActivator);
        }
        catch
        {
            if (processOwner is not null)
            {
                await processOwner.DisposeAsync().ConfigureAwait(false);
            }

            if (excelObject is not null)
            {
                try
                {
                    await dispatcher.InvokeAsync(
                        () =>
                        {
                            ComObjectReleaser.Release(excelObject);
                            return true;
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            await dispatcher.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static nint ToWindowHandle(object value)
    {
        try
        {
            return new nint(Convert.ToInt64(value));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DebugSetupException("Excel returned an invalid application window handle.", ex);
        }
    }

    private sealed class VbeDebugSession : IVbeDebugSession
    {
        private readonly object excelObject;
        private readonly DebugExcelProcessOwner processOwner;
        private readonly IStaComDispatcher dispatcher;
        private readonly IDebugWindowActivator windowActivator;
        private object? workbookObject;
        private int? foregroundPermissionHResult;
        private int workbookOpened;
        private int disposed;

        public VbeDebugSession(
            object excelObject,
            DebugExcelProcessOwner processOwner,
            IStaComDispatcher dispatcher,
            IDebugWindowActivator windowActivator)
        {
            this.excelObject = excelObject;
            this.processOwner = processOwner;
            this.dispatcher = dispatcher;
            this.windowActivator = windowActivator;
        }

        public int ProcessId => processOwner.ProcessId;

        public Task<DebugProcessExit> Completion => processOwner.Completion;

        public async Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref workbookOpened, 1, 0) != 0)
            {
                throw new DebugSetupException("The generated debug workbook has already been opened.");
            }

            var expectedWorkbookPath = Path.GetFullPath(workbookPath);
            if (!File.Exists(expectedWorkbookPath))
            {
                throw new DebugSetupException(
                    $"The generated debug workbook does not exist: {expectedWorkbookPath}");
            }

            workbookObject = await InvokeSetupAsync(
                () => OpenWorkbook(expectedWorkbookPath),
                cancellationToken).ConfigureAwait(false);
        }

        public async Task SetNativeBreakpointAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await InvokeSetupAsync(
                () =>
                {
                    SetNativeBreakpoint(breakpoint);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task RunTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await InvokeSetupAsync(
                () =>
                {
                    RunTarget(target);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public ValueTask TerminateAsync() => processOwner.TerminateAsync();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            await processOwner.TerminateAsync().ConfigureAwait(false);
            try
            {
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        ComObjectReleaser.Release(workbookObject);
                        ComObjectReleaser.Release(excelObject);
                        return true;
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await processOwner.DisposeAsync().ConfigureAwait(false);
                await dispatcher.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<T> InvokeSetupAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            try
            {
                return await dispatcher.InvokeAsync(
                    () =>
                    {
                        foregroundPermissionHResult = null;
                        return operation();
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (DebugSetupException ex)
            {
                RecordForegroundPermission(ex);
                await processOwner.TerminateAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (
                ex is COMException or RuntimeBinderException or InvalidCastException or
                    ArgumentException or TargetParameterCountException)
            {
                await processOwner.TerminateAsync().ConfigureAwait(false);
                var setupError = new DebugSetupException(
                    "Excel or the VBE rejected the generated workbook debug setup.",
                    ex);
                RecordForegroundPermission(setupError);
                throw setupError;
            }
        }

        private object OpenWorkbook(string expectedWorkbookPath)
        {
            object? workbooksObject = null;
            object? workbook = null;
            object? projectObject = null;
            var succeeded = false;

            try
            {
                dynamic excel = excelObject;
                workbooksObject = excel.Workbooks;
                dynamic workbooks = workbooksObject;
                workbook = workbooks.Open(expectedWorkbookPath);
                dynamic openedWorkbook = workbook;

                var actualWorkbookPath = Path.GetFullPath((string)openedWorkbook.FullName);
                if (!string.Equals(
                        expectedWorkbookPath,
                        actualWorkbookPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new DebugSetupException(
                        "Excel opened a workbook other than the exact generated debug workbook.");
                }

                projectObject = openedWorkbook.VBProject;
                dynamic project = projectObject;
                EnsureDesignMode(project);
                succeeded = true;
                return workbook;
            }
            finally
            {
                ComObjectReleaser.Release(projectObject);
                if (!succeeded)
                {
                    ComObjectReleaser.Release(workbook);
                }

                ComObjectReleaser.Release(workbooksObject);
            }
        }

        private void SetNativeBreakpoint(VbeBreakpoint breakpoint)
        {
            if (breakpoint.VbideLine <= 0)
            {
                throw new DebugSetupException(
                    "The native VBE breakpoint line must be a positive one-based line number.");
            }

            object? projectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            object? codeModuleObject = null;
            try
            {
                dynamic workbook = GetOpenedWorkbook();
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                EnsureDesignMode(project);

                componentsObject = project.VBComponents;
                dynamic components = componentsObject;
                componentObject = components.Item(breakpoint.ModuleName);
                dynamic component = componentObject;
                if ((int)component.Type != 1)
                {
                    throw new DebugSetupException(
                        $"The breakpoint module '{breakpoint.ModuleName}' is not a standard module.");
                }

                codeModuleObject = component.CodeModule;
                dynamic codeModule = codeModuleObject;
                var actualCodeLine = (string)codeModule.Lines(breakpoint.VbideLine, 1);
                if (!string.Equals(
                        breakpoint.ExpectedCodeLine,
                        actualCodeLine,
                        StringComparison.Ordinal))
                {
                    throw new DebugSetupException(
                        $"The generated workbook code at '{breakpoint.ModuleName}:{breakpoint.VbideLine}' does not exactly match the saved breakpoint source line.");
                }

                ExecuteNativeCommand(
                    componentObject,
                    codeModuleObject,
                    breakpoint.VbideLine,
                    51,
                    "Toggle Breakpoint",
                    "breakpoint");
            }
            finally
            {
                ComObjectReleaser.Release(codeModuleObject);
                ComObjectReleaser.Release(componentObject);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(projectObject);
            }
        }

        private void RunTarget(DebugTargetProcedure target)
        {
            object? projectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            object? codeModuleObject = null;
            try
            {
                dynamic workbook = GetOpenedWorkbook();
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                EnsureDesignMode(project);

                componentsObject = project.VBComponents;
                dynamic components = componentsObject;
                componentObject = components.Item(target.ModuleName);
                dynamic component = componentObject;
                if ((int)component.Type != 1)
                {
                    throw new DebugSetupException(
                        $"The debug target module '{target.ModuleName}' is not a standard module.");
                }

                codeModuleObject = component.CodeModule;
                dynamic codeModule = codeModuleObject;
                var bodyLine = (int)codeModule.ProcBodyLine(target.ProcedureName, 0);
                if (bodyLine <= 0)
                {
                    throw new DebugSetupException(
                        $"The debug target procedure '{target.ModuleName}.{target.ProcedureName}' could not be resolved.");
                }

                ExecuteNativeCommand(
                    componentObject,
                    codeModuleObject,
                    bodyLine,
                    186,
                    "Run Sub/UserForm",
                    "target code");
            }
            finally
            {
                ComObjectReleaser.Release(codeModuleObject);
                ComObjectReleaser.Release(componentObject);
                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(projectObject);
            }
        }

        private void ExecuteNativeCommand(
            object componentObject,
            object codeModuleObject,
            int selectedLine,
            int commandId,
            string commandName,
            string contextName)
        {
            object? codePaneObject = null;
            object? activeCodePaneObject = null;
            object? vbeObject = null;
            object? mainWindowObject = null;
            object? codeWindowObject = null;
            object? commandBarsObject = null;
            object? commandControlObject = null;
            try
            {
                dynamic component = componentObject;
                dynamic codeModule = codeModuleObject;
                dynamic excel = excelObject;
                foregroundPermissionHResult =
                    windowActivator.AllowComServerForeground(excelObject);
                component.Activate();

                codePaneObject = codeModule.CodePane;
                dynamic codePane = codePaneObject;
                vbeObject = excel.VBE;
                dynamic vbe = vbeObject;
                mainWindowObject = vbe.MainWindow;
                dynamic mainWindow = mainWindowObject;
                mainWindow.Visible = true;
                codePane.Show();
                vbe.ActiveCodePane = codePaneObject;
                codePane.SetSelection(selectedLine, 1, selectedLine, 1);
                mainWindow.SetFocus();
                codeWindowObject = codePane.Window;
                dynamic codeWindow = codeWindowObject;
                codeWindow.SetFocus();
                windowActivator.BringOwnedWindowToForeground(
                    ToWindowHandle(mainWindow.HWnd),
                    ProcessId);

                activeCodePaneObject = vbe.ActiveCodePane;
                if (!ReferenceEquals(activeCodePaneObject, codePaneObject))
                {
                    throw new DebugSetupException(
                        $"The intended VBE code pane is not active in the {contextName} context.");
                }

                var actualStartLine = 0;
                var actualStartColumn = 0;
                var actualEndLine = 0;
                var actualEndColumn = 0;
                codePane.GetSelection(
                    ref actualStartLine,
                    ref actualStartColumn,
                    ref actualEndLine,
                    ref actualEndColumn);
                if (actualStartLine != selectedLine ||
                    actualStartColumn != 1 ||
                    actualEndLine != selectedLine ||
                    actualEndColumn != 1)
                {
                    throw new DebugSetupException(
                        $"The exact VBE line selection was not retained in the {contextName} context.");
                }

                commandBarsObject = vbe.CommandBars;
                dynamic commandBars = commandBarsObject;
                commandControlObject = commandBars.FindControl(
                    1,
                    commandId,
                    Type.Missing,
                    false);
                if (commandControlObject is null)
                {
                    throw new DebugSetupException(
                        $"The native VBE {commandName} command (ID {commandId}) was not found.");
                }

                dynamic commandControl = commandControlObject;
                if ((int)commandControl.Id != commandId || !(bool)commandControl.BuiltIn)
                {
                    throw new DebugSetupException(
                        $"The resolved VBE command is not the built-in {commandName} command (ID {commandId}).");
                }

                if (!(bool)commandControl.Enabled)
                {
                    throw new DebugSetupException(
                        $"The native VBE {commandName} command (ID {commandId}) is disabled in the {contextName} context.");
                }

                commandControl.Execute();
            }
            finally
            {
                ComObjectReleaser.Release(commandControlObject);
                ComObjectReleaser.Release(commandBarsObject);
                ComObjectReleaser.Release(codeWindowObject);
                ComObjectReleaser.Release(mainWindowObject);
                ComObjectReleaser.Release(vbeObject);
                if (!ReferenceEquals(activeCodePaneObject, codePaneObject))
                {
                    ComObjectReleaser.Release(activeCodePaneObject);
                }

                ComObjectReleaser.Release(codePaneObject);
            }
        }

        private object GetOpenedWorkbook()
            => workbookObject ?? throw new DebugSetupException(
                "The generated debug workbook has not been opened.");

        private static void EnsureDesignMode(dynamic project)
        {
            if ((int)project.Mode != 2)
            {
                throw new DebugSetupException(
                    "The generated workbook VBA project is not in design mode.");
            }
        }

        private void RecordForegroundPermission(DebugSetupException error)
        {
            if (foregroundPermissionHResult is int hResult)
            {
                error.Data["CoAllowSetForegroundWindow.HResult"] =
                    $"0x{unchecked((uint)hResult):X8}";
            }
        }
    }
}

internal sealed class ExcelDebugApplicationFactory : IExcelDebugApplicationFactory
{
    public object Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DebugSetupException(
                "VBE debugging requires Windows and a locally installed Microsoft Excel application.");
        }

        try
        {
            var excelType = Type.GetTypeFromProgID("Excel.Application");
            if (excelType is null)
            {
                throw new DebugSetupException(
                    "Microsoft Excel is not registered for COM automation.");
            }

            return Activator.CreateInstance(excelType)
                ?? throw new DebugSetupException(
                    "Microsoft Excel COM automation did not create an application instance.");
        }
        catch (DebugSetupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            throw new DebugSetupException(
                "Microsoft Excel could not be started for VBE debugging.",
                ex);
        }
    }
}

internal sealed class WindowsDebugExcelProcessApi : IDebugExcelProcessApi
{
    private readonly IWindowsDebugWindowApi windowApi = new WindowsDebugWindowApi();

    public IReadOnlyDictionary<int, DateTime> CaptureRunningExcelProcesses()
        => ExcelComApplicationProcess.CaptureRunningExcelProcesses();

    public int GetProcessId(nint windowHandle) => windowApi.GetProcessId(windowHandle);

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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            throw new DebugSetupException(
                "The exact Excel process identity could not be captured for VBE debugging.",
                ex);
        }
        finally
        {
            process?.Dispose();
        }
    }
}

internal sealed class SystemDebugOwnedProcess : IDebugOwnedProcess
{
    private readonly Process process;

    public SystemDebugOwnedProcess(Process process)
    {
        this.process = process;
        Id = process.Id;
        StartTime = process.StartTime;
    }

    public int Id { get; }

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

internal sealed class WindowsDebugWindowActivator : IDebugWindowActivator
{
    private readonly IWindowsDebugWindowApi windowApi;

    public WindowsDebugWindowActivator()
        : this(new WindowsDebugWindowApi())
    {
    }

    internal WindowsDebugWindowActivator(IWindowsDebugWindowApi windowApi)
    {
        this.windowApi = windowApi;
    }

    public int AllowComServerForeground(object comServerObject)
        => windowApi.AllowComServerForeground(comServerObject);

    public void BringOwnedWindowToForeground(nint windowHandle, int processId)
    {
        if (windowHandle == nint.Zero ||
            processId <= 0 ||
            windowApi.GetProcessId(windowHandle) != processId)
        {
            throw new DebugSetupException(
                "The VBE window does not belong to the owned Excel process.");
        }

        windowApi.Restore(windowHandle);
        var setForegroundResult = windowApi.SetForeground(windowHandle);

        var foregroundWindow = nint.Zero;
        var foregroundProcessId = 0;
        for (var check = 0; check <= 40; check++)
        {
            foregroundWindow = windowApi.GetForegroundWindow();
            foregroundProcessId = foregroundWindow == nint.Zero
                ? 0
                : windowApi.GetProcessId(foregroundWindow);
            if (foregroundWindow == windowHandle && foregroundProcessId == processId)
            {
                return;
            }

            if (check < 40)
            {
                windowApi.WaitForForegroundTransition();
            }
        }

        var error = new DebugSetupException(
            $"The requested VBE window did not become the exact foreground window; foreground HWND {foregroundWindow} with foreground PID {foregroundProcessId} did not match requested HWND {windowHandle} for owned Excel PID {processId} after the activation transition wait.");
        error.Data["SetForegroundWindow.Result"] = setForegroundResult;
        error.Data["ForegroundWindow.Handle"] = foregroundWindow;
        throw error;
    }
}

internal sealed class WindowsDebugWindowApi : IWindowsDebugWindowApi
{
    public int AllowComServerForeground(object comServerObject)
    {
        if (!OperatingSystem.IsWindows())
        {
            return unchecked((int)0x80004001);
        }

        if (!Marshal.IsComObject(comServerObject))
        {
            return unchecked((int)0x80004002);
        }

        nint unknown = nint.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(comServerObject);
            return CoAllowSetForegroundWindow(unknown, nint.Zero);
        }
        catch (Exception ex) when (
            ex is ArgumentException or COMException or InvalidComObjectException or PlatformNotSupportedException)
        {
            return ex.HResult;
        }
        finally
        {
            if (unknown != nint.Zero)
            {
                _ = Marshal.Release(unknown);
            }
        }
    }

    public int GetProcessId(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId <= int.MaxValue ? (int)processId : 0;
    }

    public void Restore(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new DebugSetupException("VBE window activation requires Windows.");
        }

        _ = ShowWindow(windowHandle, ShowWindowRestore);
    }

    public bool SetForeground(nint windowHandle)
        => OperatingSystem.IsWindows() && SetForegroundWindow(windowHandle);

    public nint GetForegroundWindow()
        => OperatingSystem.IsWindows() ? GetForegroundWindowNative() : nint.Zero;

    public void WaitForForegroundTransition()
        => Thread.Sleep(TimeSpan.FromMilliseconds(50));

    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowNative();

    [DllImport("ole32.dll")]
    private static extern int CoAllowSetForegroundWindow(
        nint unknown,
        nint reserved);

}

internal sealed class StaComDispatcherFactory : IStaComDispatcherFactory
{
    public IStaComDispatcher Create() => new StaComDispatcher();
}
