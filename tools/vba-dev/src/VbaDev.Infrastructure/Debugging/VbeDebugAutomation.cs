using VbaDev.App.Debugging;
using VbaDev.Infrastructure.Workbooks;
using VbaLanguageServer.Syntax;
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
    private readonly IExcelDebugWorkbookOpener workbookOpener;
    private readonly IDebugModalPromptMonitor promptMonitor;

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
        IStaComDispatcherFactory dispatcherFactory,
        IExcelDebugWorkbookOpener? workbookOpener = null,
        IDebugModalPromptMonitor? promptMonitor = null)
    {
        this.applicationFactory = applicationFactory;
        this.processApi = processApi;
        this.windowActivator = windowActivator;
        this.dispatcherFactory = dispatcherFactory;
        this.workbookOpener = workbookOpener ?? new ExcelComDebugWorkbookOpener();
        this.promptMonitor = promptMonitor ?? new DebugModalPromptMonitor();
    }

    /// <inheritdoc />
    public async Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existingExcelProcesses = processApi.CaptureRunningExcelProcesses();
        var dispatcher = dispatcherFactory.Create();
        object? excelObject = null;
        DebugExcelProcessOwner? processOwner = null;
        CancellationTokenRegistration ownershipCancellation = default;
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
                    ownershipCancellation = cancellationToken.UnsafeRegister(
                        static state =>
                            _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                        processOwner);
                    cancellationToken.ThrowIfCancellationRequested();
                    excel.Visible = true;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new VbeDebugSession(
                excelObject!,
                processOwner!,
                dispatcher,
                windowActivator,
                workbookOpener,
                promptMonitor);
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
        finally
        {
            ownershipCancellation.Dispose();
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
        private readonly IExcelDebugWorkbookOpener workbookOpener;
        private readonly IDebugModalPromptMonitor promptMonitor;
        private object? workbookObject;
        private Task targetPromptObservation = Task.CompletedTask;
        private int? foregroundPermissionHResult;
        private int workbookOpened;
        private int disposed;

        public VbeDebugSession(
            object excelObject,
            DebugExcelProcessOwner processOwner,
            IStaComDispatcher dispatcher,
            IDebugWindowActivator windowActivator,
            IExcelDebugWorkbookOpener workbookOpener,
            IDebugModalPromptMonitor promptMonitor)
        {
            this.excelObject = excelObject;
            this.processOwner = processOwner;
            this.dispatcher = dispatcher;
            this.windowActivator = windowActivator;
            this.workbookOpener = workbookOpener;
            this.promptMonitor = promptMonitor;
        }

        public int ProcessId => processOwner.ProcessId;

        public Task<DebugProcessExit> Completion => processOwner.Completion;

        public async Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await InvokeSetupAsync(
                ReadCompilationHostFacts,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task OpenGeneratedWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
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

            var inputWait = new DebugInputWait(
                DebugInputWaitKind.ExcelOrVbe,
                DebugInputWaitPhase.WorkbookOpen,
                ProcessId);
            var observation = inputWaitSink is null
                ? null
                : promptMonitor.Capture(inputWait);
            var operation = InvokeSetupAsync(
                () => workbookOpener.OpenVerified(excelObject, expectedWorkbookPath),
                cancellationToken);
            workbookObject = inputWaitSink is null
                ? await operation.ConfigureAwait(false)
                : await promptMonitor.ObserveAsync(
                    observation!,
                    operation,
                    processOwner.Completion,
                    inputWaitSink,
                    cancellationToken).ConfigureAwait(false);
        }

        public async Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await InvokeSetupAsync(
                () =>
                {
                    SetNativeBreakpoints(breakpoints);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inputWait = new DebugInputWait(
                DebugInputWaitKind.ExcelOrVbe,
                DebugInputWaitPhase.TargetStart,
                ProcessId);
            var observation = inputWaitSink is null
                ? null
                : promptMonitor.Capture(inputWait);
            var operation = InvokeSetupAsync(
                () =>
                {
                    RunTarget(target);
                    return true;
                },
                cancellationToken);
            _ = inputWaitSink is null
                ? await operation.ConfigureAwait(false)
                : await promptMonitor.ObserveAsync(
                    observation!,
                    operation,
                    processOwner.Completion,
                    inputWaitSink,
                    cancellationToken).ConfigureAwait(false);
            if (inputWaitSink is not null)
            {
                targetPromptObservation = promptMonitor.ObserveUntilProcessExitAsync(
                    observation!,
                    processOwner.Completion,
                    inputWaitSink,
                    CancellationToken.None);
            }
        }

        public ValueTask TerminateAsync() => processOwner.TerminateAsync();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Exception? cleanupError = null;
            try
            {
                await processOwner.TerminateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
            }

            try
            {
                await processOwner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }

            try
            {
                await targetPromptObservation.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }

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
            catch (Exception ex)
            {
                cleanupError ??= ex;
            }
            finally
            {
                try
                {
                    await dispatcher.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupError ??= ex;
                }
            }

            if (cleanupError is not null)
            {
                throw new DebugSetupException(
                    "Native Excel/VBE session cleanup did not complete normally after process termination.",
                    cleanupError);
            }
        }

        private async Task<T> InvokeSetupAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            using var cancellationRegistration = cancellationToken.UnsafeRegister(
                static state => ((VbeDebugSession)state!).RequestCancellationTermination(),
                this);
            try
            {
                var result = await dispatcher.InvokeAsync(
                    () =>
                    {
                        foregroundPermissionHResult = null;
                        return operation();
                    },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                await processOwner.TerminateAsync().ConfigureAwait(false);
                throw new OperationCanceledException(
                    "VBE debug setup was cancelled and its owned Excel process was terminated.",
                    ex,
                    cancellationToken);
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

        private void RequestCancellationTermination()
            => _ = TerminateAfterCancellationAsync();

        private async Task TerminateAfterCancellationAsync()
        {
            try
            {
                await processOwner.TerminateAsync().ConfigureAwait(false);
            }
            catch
            {
                // The awaiting setup path retains responsibility for surfacing a
                // termination failure after cancellation has unblocked COM.
            }
        }

        private DebugCompilationHostFacts ReadCompilationHostFacts()
        {
            object? vbeObject = null;
            try
            {
                dynamic excel = excelObject;
                var excelVersion = (string)excel.Version;
                var operatingSystem = (string)excel.OperatingSystem;
                vbeObject = excel.VBE;
                dynamic vbe = vbeObject;
                var vbeVersion = (string)vbe.Version;
                return ResolveCompilationHostFacts(
                    excelVersion,
                    vbeVersion,
                    operatingSystem,
                    processOwner.ProcessArchitecture);
            }
            finally
            {
                ComObjectReleaser.Release(vbeObject);
            }
        }

        private static DebugCompilationHostFacts ResolveCompilationHostFacts(
            string excelVersion,
            string vbeVersion,
            string operatingSystem,
            DebugExcelProcessArchitecture processArchitecture)
        {
            var excelGeneration = ResolveExcelGeneration(excelVersion);
            var vbeGeneration = ResolveVbeGeneration(vbeVersion);
            var operatingSystemBitness = ResolveOperatingSystemBitness(operatingSystem);
            var processBitness = ResolveProcessBitness(processArchitecture);

            if (operatingSystemBitness != HostBitness.Unknown
                && processBitness != HostBitness.Unknown
                && operatingSystemBitness != processBitness)
            {
                return UnprovedHostFacts(
                    DebugCompilationHostFactsStatus.Mismatch,
                    "Excel Application.OperatingSystem bitness contradicts the exact owned Excel process architecture.");
            }

            if (excelGeneration != VbaGeneration.Unknown
                && vbeGeneration != VbaGeneration.Unknown
                && excelGeneration != vbeGeneration)
            {
                return UnprovedHostFacts(
                    DebugCompilationHostFactsStatus.Mismatch,
                    "Excel Application.Version and VBE.Version identify contradictory VBA generations.");
            }

            if (processBitness == HostBitness.Unknown)
            {
                return UnprovedHostFacts(
                    DebugCompilationHostFactsStatus.Unknown,
                    "The exact owned Excel process architecture is unknown.");
            }

            if (operatingSystemBitness == HostBitness.Unknown)
            {
                return UnprovedHostFacts(
                    DebugCompilationHostFactsStatus.Unknown,
                    "Excel Application.OperatingSystem does not identify a supported Windows bitness.");
            }

            if (excelGeneration == VbaGeneration.Unknown
                || vbeGeneration == VbaGeneration.Unknown)
            {
                return UnprovedHostFacts(
                    DebugCompilationHostFactsStatus.Unknown,
                    "Excel Application.Version or VBE.Version does not identify a supported VBA generation.");
            }

            return new DebugCompilationHostFacts(
                excelVersion,
                vbeVersion,
                operatingSystem,
                processArchitecture,
                DebugCompilationHostFactsStatus.Verified,
                new DebugCompilerBuiltInConstants(
                    Vba6: true,
                    Vba7: vbeGeneration == VbaGeneration.Vba7,
                    Win16: false,
                    Win32: true,
                    Win64: processBitness == HostBitness.Bit64,
                    Mac: false),
                UnavailableReason: null);

            DebugCompilationHostFacts UnprovedHostFacts(
                DebugCompilationHostFactsStatus status,
                string reason)
                => new(
                    excelVersion,
                    vbeVersion,
                    operatingSystem,
                    processArchitecture,
                    status,
                    BuiltInConstants: null,
                    reason);
        }

        private static VbaGeneration ResolveExcelGeneration(string version)
        {
            if (!Version.TryParse(version, out var parsedVersion))
            {
                return VbaGeneration.Unknown;
            }

            return parsedVersion.Major switch
            {
                >= 9 and <= 12 => VbaGeneration.Vba6,
                >= 14 => VbaGeneration.Vba7,
                _ => VbaGeneration.Unknown
            };
        }

        private static VbaGeneration ResolveVbeGeneration(string version)
        {
            if (!Version.TryParse(version, out var parsedVersion))
            {
                return VbaGeneration.Unknown;
            }

            return parsedVersion.Major switch
            {
                6 => VbaGeneration.Vba6,
                7 => VbaGeneration.Vba7,
                _ => VbaGeneration.Unknown
            };
        }

        private static HostBitness ResolveOperatingSystemBitness(string operatingSystem)
        {
            if (operatingSystem.StartsWith(
                    "Windows (32-bit)",
                    StringComparison.OrdinalIgnoreCase))
            {
                return HostBitness.Bit32;
            }

            return operatingSystem.StartsWith(
                "Windows (64-bit)",
                StringComparison.OrdinalIgnoreCase)
                ? HostBitness.Bit64
                : HostBitness.Unknown;
        }

        private static HostBitness ResolveProcessBitness(
            DebugExcelProcessArchitecture architecture)
            => architecture switch
            {
                DebugExcelProcessArchitecture.X86 => HostBitness.Bit32,
                DebugExcelProcessArchitecture.X64 or
                    DebugExcelProcessArchitecture.Arm64 => HostBitness.Bit64,
                _ => HostBitness.Unknown
            };

        private enum VbaGeneration
        {
            Unknown,
            Vba6,
            Vba7
        }

        private enum HostBitness
        {
            Unknown,
            Bit32,
            Bit64
        }

        private void SetNativeBreakpoints(IReadOnlyList<VbeBreakpoint> breakpoints)
        {
            if (breakpoints.Count == 0)
            {
                return;
            }

            object? projectObject = null;
            object? componentsObject = null;
            var verifiedModules = new Dictionary<string, VerifiedCodeModule>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var sourceMaps = CollectDistinctSourceMaps(breakpoints);
                dynamic workbook = GetOpenedWorkbook();
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                EnsureDesignMode(project);

                componentsObject = project.VBComponents;
                dynamic components = componentsObject;
                foreach (var sourceMap in sourceMaps.Values)
                {
                    verifiedModules.Add(
                        sourceMap.ModuleName,
                        VerifyCodeModule(components, sourceMap));
                }

                foreach (var breakpoint in breakpoints)
                {
                    var verifiedModule = verifiedModules[breakpoint.ModuleName];
                    ExecuteNativeCommand(
                        verifiedModule.Component,
                        verifiedModule.CodeModule,
                        breakpoint.VbideLine,
                        51,
                        "Toggle Breakpoint",
                        "breakpoint",
                        CreateDisabledBreakpointMessage(breakpoint));
                }
            }
            finally
            {
                foreach (var verifiedModule in verifiedModules.Values)
                {
                    ComObjectReleaser.Release(verifiedModule.CodeModule);
                    ComObjectReleaser.Release(verifiedModule.Component);
                }

                ComObjectReleaser.Release(componentsObject);
                ComObjectReleaser.Release(projectObject);
            }
        }

        private static Dictionary<string, VbeCodeModuleSourceMap> CollectDistinctSourceMaps(
            IReadOnlyList<VbeBreakpoint> breakpoints)
        {
            var sourceMaps = new Dictionary<string, VbeCodeModuleSourceMap>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var breakpoint in breakpoints)
            {
                var sourceMap = breakpoint.SourceMap;
                if (string.IsNullOrWhiteSpace(sourceMap.ModuleName))
                {
                    throw new DebugSetupException(
                        "A native VBE breakpoint source map has no module identity.");
                }

                if (sourceMap.CodeLines.IsDefault)
                {
                    throw new DebugSetupException(
                        $"The breakpoint source map for '{sourceMap.ModuleName}' has no code-module contents.");
                }

                if (breakpoint.VbideLine <= 0 || breakpoint.VbideLine > sourceMap.CodeLines.Length)
                {
                    throw new DebugSetupException(
                        $"The native VBE breakpoint line for '{sourceMap.ModuleName}' is outside the exact code-module source map.");
                }

                if (sourceMaps.TryGetValue(sourceMap.ModuleName, out var existingSourceMap))
                {
                    if (existingSourceMap.ModuleKind != sourceMap.ModuleKind
                        || !existingSourceMap.CodeLines.SequenceEqual(
                            sourceMap.CodeLines,
                            StringComparer.Ordinal))
                    {
                        throw new DebugSetupException(
                            $"Conflicting whole code module source maps were supplied for '{sourceMap.ModuleName}'.");
                    }

                    continue;
                }

                sourceMaps.Add(sourceMap.ModuleName, sourceMap);
            }

            return sourceMaps;
        }

        private static VerifiedCodeModule VerifyCodeModule(
            dynamic components,
            VbeCodeModuleSourceMap sourceMap)
        {
            object? componentObject = null;
            object? codeModuleObject = null;
            var succeeded = false;
            try
            {
                componentObject = components.Item(sourceMap.ModuleName);
                dynamic component = componentObject;
                var actualModuleName = (string)component.Name;
                if (!string.Equals(
                        sourceMap.ModuleName,
                        actualModuleName,
                        StringComparison.Ordinal))
                {
                    throw new DebugSetupException(
                        $"The generated workbook component identity '{actualModuleName}' does not exactly match breakpoint module '{sourceMap.ModuleName}'.");
                }

                var expectedComponentType = GetComponentType(sourceMap.ModuleKind);
                if ((int)component.Type != expectedComponentType)
                {
                    throw new DebugSetupException(
                        $"The generated workbook component kind for breakpoint module '{sourceMap.ModuleName}' does not match the saved source map.");
                }

                codeModuleObject = component.CodeModule;
                dynamic codeModule = codeModuleObject;
                var actualLineCount = (int)codeModule.CountOfLines;
                if (actualLineCount != sourceMap.CodeLines.Length)
                {
                    throw new DebugSetupException(
                        $"The generated workbook whole code module '{sourceMap.ModuleName}' does not exactly match the saved breakpoint source map (line count {actualLineCount}, expected {sourceMap.CodeLines.Length}).");
                }

                for (var line = 1; line <= actualLineCount; line++)
                {
                    var actualCodeLine = (string)codeModule.Lines(line, 1);
                    if (!string.Equals(
                            sourceMap.CodeLines[line - 1],
                            actualCodeLine,
                            StringComparison.Ordinal))
                    {
                        throw new DebugSetupException(
                            $"The generated workbook whole code module '{sourceMap.ModuleName}' does not exactly match the saved breakpoint source map at line {line}.");
                    }
                }

                succeeded = true;
                return new VerifiedCodeModule(componentObject, codeModuleObject);
            }
            finally
            {
                if (!succeeded)
                {
                    ComObjectReleaser.Release(codeModuleObject);
                    ComObjectReleaser.Release(componentObject);
                }
            }
        }

        private static int GetComponentType(VbaModuleKind moduleKind)
            => moduleKind switch
            {
                VbaModuleKind.StandardModule => 1,
                VbaModuleKind.ClassModule => 2,
                VbaModuleKind.FormModule => 3,
                _ => throw new DebugSetupException(
                    $"The breakpoint source map has unsupported module kind '{moduleKind}'.")
            };

        private static string CreateDisabledBreakpointMessage(VbeBreakpoint breakpoint)
            => $"Invalid breakpoint at '{breakpoint.Source.SourcePath}:{breakpoint.Source.EditorLine + 1}' " +
                $"mapped to '{breakpoint.ModuleName}:{breakpoint.VbideLine}': the native VBE Toggle Breakpoint " +
                "command is disabled in the actual generated workbook compilation context, so the mapped " +
                "line is inactive or non-executable. The breakpoint was not relocated.";

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
                    "target code",
                    beforeExecute: () =>
                    {
                        dynamic excel = excelObject;
                        excel.EnableEvents = true;
                    });
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
            string contextName,
            string? disabledMessage = null,
            Action? beforeExecute = null)
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
                        disabledMessage
                            ?? $"The native VBE {commandName} command (ID {commandId}) is disabled in the {contextName} context.");
                }

                beforeExecute?.Invoke();
                try
                {
                    commandControl.Execute();
                }
                catch (Exception ex) when (
                    ex is COMException or RuntimeBinderException or InvalidCastException or
                        ArgumentException or TargetParameterCountException)
                {
                    throw new DebugSetupException(
                        $"The native VBE {commandName} command (ID {commandId}) was found and " +
                        "enabled, but its invocation did not complete. Resolve any visible VBE " +
                        "dialog and retry.",
                        ex);
                }
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

        private sealed record VerifiedCodeModule(object Component, object CodeModule);

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

    public IDebugProcessJob CreateKillOnCloseJob()
    {
        try
        {
            return WindowsDebugProcessJob.Create();
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            throw new DebugSetupException(
                "A kill-on-close Windows Job Object could not be created for owned Excel.",
                ex);
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
