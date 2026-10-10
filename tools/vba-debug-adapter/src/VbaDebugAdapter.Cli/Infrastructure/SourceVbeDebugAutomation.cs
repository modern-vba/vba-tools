using VbaDebugAdapter.Debugging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VbaTools.Syntax;

namespace VbaDebugAdapter.Infrastructure;

internal interface ISourceVbeDesktopConnector
{
    SourceVbeDesktopBinding AttachOrOpen(string exactSourceWorkbookPath);
}

internal sealed class SourceVbeDesktopBinding(
    object application,
    object workbook,
    string workbookPath,
    int processId,
    long processStartUtcTicks,
    bool wasAlreadyOpen,
    Func<bool> isExactWorkbook,
    Action release,
    DebugExcelProcessArchitecture processArchitecture = DebugExcelProcessArchitecture.Unknown,
    Func<SourceVbeDebugSessionCompletion?>? completionProbe = null)
{
    private int released;
    internal object Application { get; } = application;
    internal object Workbook { get; } = workbook;
    internal string WorkbookPath { get; } = workbookPath;
    internal int ProcessId { get; } = processId;
    internal long ProcessStartUtcTicks { get; } = processStartUtcTicks;
    internal bool WasAlreadyOpen { get; } = wasAlreadyOpen;
    internal DebugExcelProcessArchitecture ProcessArchitecture { get; } = processArchitecture;
    internal bool HasCompletionProbe => completionProbe is not null;
    internal bool ReleaseVerified { get; private set; }
    internal bool IsExactWorkbook() => isExactWorkbook();
    internal SourceVbeDebugSessionCompletion? ProbeCompletion() => completionProbe?.Invoke();
    internal void Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0) return;
        release();
        ReleaseVerified = true;
    }
}

public sealed class SourceVbeDebugAutomation : ISourceVbeDebugSessionFactory
{
    private readonly ISourceVbeDesktopConnector connector;
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IDebugWindowActivator windowActivator;
    private readonly IDebugModalPromptMonitor? promptMonitor;
    private readonly Func<object?, bool> releaseReference;

    public SourceVbeDebugAutomation()
        : this(new WindowsSourceVbeDesktopConnector(),
            new StaComDispatcherFactory(), new WindowsDebugWindowActivator())
    {
    }

    internal SourceVbeDebugAutomation(
        ISourceVbeDesktopConnector connector,
        IStaComDispatcherFactory dispatcherFactory,
        IDebugWindowActivator windowActivator,
        IDebugModalPromptMonitor? promptMonitor = null,
        Func<object?, bool>? releaseReference = null)
    {
        this.connector = connector;
        this.dispatcherFactory = dispatcherFactory;
        this.windowActivator = windowActivator;
        this.promptMonitor = promptMonitor;
        this.releaseReference = releaseReference ?? SourceVbeComReferences.Release;
    }

    public async Task<ISourceVbeDebugSession> AttachOrOpenAsync(
        string exactSourceWorkbookPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactSourceWorkbookPath);
        cancellationToken.ThrowIfCancellationRequested();
        IStaComDispatcher? dispatcher = null;
        SourceVbeDesktopBinding? acquiredBinding = null;
        var acquisitionEntered = 0;
        Task<SourceVbeDesktopBinding>? acquiring = null;
        try
        {
            dispatcher = dispatcherFactory.Create();
            acquiring = dispatcher.InvokeAsync(() =>
            {
                Volatile.Write(ref acquisitionEntered, 1);
                var acquired = connector.AttachOrOpen(exactSourceWorkbookPath);
                Volatile.Write(ref acquiredBinding, acquired);
                return acquired;
            }, cancellationToken);
            ObserveLateFailure(acquiring);
            var binding = await acquiring.WaitAsync(cancellationToken).ConfigureAwait(false);
            var session = new SourceVbeDebugSession(binding, dispatcher, windowActivator, promptMonitor, releaseReference);
            session.StartMonitoring();
            return session;
        }
        catch (Exception failure)
        {
            var result = new DebugFailureCompletion(failure);
            var bindingReleased = false;
            if (dispatcher is not null)
            {
                try
                {
                    // Queued behind an in-flight acquisition: a late successful
                    // binding is captured inside the STA operation and detached
                    // without ever taking Excel/workbook lifetime authority.
                    var releasing = dispatcher.InvokeAsync(() =>
                    {
                        var binding = Volatile.Read(ref acquiredBinding);
                        if (binding is null) return false;
                        binding.Release();
                        return binding.ReleaseVerified;
                    }, CancellationToken.None);
                    ObserveLateFailure(releasing);
                    bindingReleased = await releasing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception releaseFailure)
                {
                    result.AddFailure("source acquire COM release", "source workbook binding",
                        DebugResourceKind.Com, releaseFailure, acquiredBinding?.ProcessId, exactSourceWorkbookPath);
                }
                try
                {
                    var retiring = dispatcher.DisposeAsync().AsTask();
                    ObserveLateFailure(retiring);
                    await retiring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception retirementFailure)
                {
                    result.AddFailure("source acquire STA retirement", "source COM dispatcher",
                        DebugResourceKind.Handle, retirementFailure, acquiredBinding?.ProcessId);
                }
            }
            // A connector with retained evidence can prove its own partial
            // acquisition cleanup. A plain exception cannot prove that no COM
            // references were acquired before it threw.
            var connectorEvidence = failure is IDebugFailureEvidence retained
                ? retained.FailureOutcome : null;
            if (acquiring is { IsCompleted: true, IsFaulted: true })
            {
                var lateFailure = acquiring.Exception!.InnerExceptions.Count == 1
                    ? acquiring.Exception.InnerExceptions[0] : acquiring.Exception;
                if (lateFailure is IDebugFailureEvidence lateRetained)
                {
                    connectorEvidence = lateRetained.FailureOutcome;
                    foreach (var evidence in connectorEvidence.Evidence) result.AddEvidence(evidence);
                    foreach (var cleanup in connectorEvidence.CleanupFailures)
                        result.AddFailure(cleanup.Stage, cleanup.Resource, cleanup.Kind,
                            cleanup.Exception, cleanup.ProcessId, cleanup.RetainedPath);
                    if (connectorEvidence.PrimaryFailure is { } latePrimary)
                    {
                        result.AddFailure("source acquire late observation", "source connector operation",
                            DebugResourceKind.Observation, latePrimary);
                        result.AddEvidence(new("source acquire late observation", "source connector operation",
                            DebugResourceKind.Observation, true,
                            "The late connector failure was observed; it is not evidence of retained lifetime ownership."));
                    }
                }
            }
            var noAcquisition = Volatile.Read(ref acquisitionEntered) == 0 && acquiring is not { IsCompleted: false };
            var connectorReleaseProved = connectorEvidence is { HasUnprovedRelease: false }
                && connectorEvidence.Evidence.Any(item => item.Kind == DebugResourceKind.Com && item.Released);
            result.AddEvidence(new("source lifetime authority", "Excel process", DebugResourceKind.Process, true,
                "Source acquisition acquired no lifetime ownership; Excel was not saved, closed, quit or terminated.",
                acquiredBinding?.ProcessId));
            var comReleased = bindingReleased || noAcquisition || connectorReleaseProved;
            result.AddEvidence(new("source acquire COM release", "source workbook binding", DebugResourceKind.Com,
                comReleased, comReleased ? "Acquired binding references were released or no COM acquisition was entered."
                    : "Partial source COM acquisition release is unproved; Excel was left open. Resolve visible prompts and use Reset if needed.",
                acquiredBinding?.ProcessId, exactSourceWorkbookPath));
            result.AddEvidence(new("source acquire STA retirement", "source COM dispatcher", DebugResourceKind.Handle,
                dispatcher?.ReleaseVerified == true, dispatcher?.ReleaseVerified == true
                    ? "The source COM dispatcher retired."
                    : "Source COM dispatcher retirement is unproved; no Excel lifetime action was taken.", acquiredBinding?.ProcessId));
            result.Complete().ThrowWithEvidence();
            throw;
        }
    }

    private static void ObserveLateFailure(Task task)
        => _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed class SourceVbeDebugSession(
        SourceVbeDesktopBinding binding,
        IStaComDispatcher dispatcher,
        IDebugWindowActivator windowActivator,
        IDebugModalPromptMonitor? promptMonitor,
        Func<object?, bool> releaseReference) : ISourceVbeDebugSession, IDebugResourceOwnerEvidence
    {
        private readonly TaskCompletionSource<SourceVbeDebugSessionCompletion> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource monitorStopping = new();
        private Task lifetimeMonitor = Task.CompletedTask;
        private readonly object disposalGate = new();
        private readonly DebugFailureCompletion releaseObservations = new();
        private readonly object releaseObservationGate = new();
        private bool releaseObservationsSealed;
        private IDebugModalPromptPhase? targetPromptPhase;
        private Task targetPromptObservation = Task.CompletedTask;
        private Task? targetPromptRetirement;
        private Task? disposal;
        public DebugFailureOutcome? CleanupOutcome { get; private set; }

        internal void StartMonitoring()
        {
            if (binding.HasCompletionProbe) lifetimeMonitor = MonitorCompletionAsync();
        }

        private async Task MonitorCompletionAsync()
        {
            var token = monitorStopping.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    try
                    {
                        var probing = dispatcher.InvokeAsync(binding.ProbeCompletion, token);
                        ObserveLateFailure(probing);
                        var observed = await probing.WaitAsync(token).ConfigureAwait(false);
                        if (observed is not null)
                        {
                            completion.TrySetResult(observed);
                            return;
                        }
                    }
                    catch (COMException error) when (unchecked((uint)error.HResult)
                        is 0x80010001 or 0x8001010A or 0x800AC472)
                    {
                        // Busy Excel and canceled close prompts are not closure evidence.
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception failure)
                    {
                        completion.TrySetException(ExplainFailure(
                            "The live source workbook could no longer be observed; closure and execution state are unproved. " +
                            "Use Reset in the selected source workbook's VBE if needed. Excel was not terminated.", failure));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public int ProcessId => binding.ProcessId;
        public long ProcessStartUtcTicks => binding.ProcessStartUtcTicks;
        public bool WasAlreadyOpen => binding.WasAlreadyOpen;
        public Task<SourceVbeDebugSessionCompletion> Completion => completion.Task;

        public Task<SourceVbeDebugSessionInspection> InspectAsync(CancellationToken cancellationToken)
            => dispatcher.InvokeAsync(() =>
            {
                if (!binding.IsExactWorkbook())
                {
                    throw new DebugSetupException(
                        "The selected source workbook is no longer bound to the exact Excel process and file.");
                }

                object? projectObject = null;
                Exception? failure = null;
                try
                {
                    dynamic workbook = binding.Workbook;
                    var actualPath = Path.GetFullPath((string)workbook.FullName);
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    return new SourceVbeDebugSessionInspection(
                        actualPath,
                        binding.ProcessId,
                        binding.ProcessStartUtcTicks,
                        (bool)workbook.Saved,
                        (int)project.Mode);
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    ReleaseScope(failure, ("source inspection VBA project", projectObject));
                }
            }, cancellationToken);

        public Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
            => dispatcher.InvokeAsync(() =>
            {
                if (!binding.IsExactWorkbook())
                    throw new DebugSetupException("The exact source workbook binding changed before live host inspection.");
                object? vbeObject = null;
                Exception? failure = null;
                try
                {
                    dynamic excel = binding.Application;
                    var excelVersion = (string)excel.Version;
                    var operatingSystem = (string)excel.OperatingSystem;
                    vbeObject = excel.VBE;
                    dynamic vbe = vbeObject;
                    var vbeVersion = (string)vbe.Version;
                    var architecture = binding.ProcessArchitecture;
                    var processBits = architecture switch
                    {
                        DebugExcelProcessArchitecture.X86 => 32,
                        DebugExcelProcessArchitecture.X64 or DebugExcelProcessArchitecture.Arm64 => 64,
                        _ => 0
                    };
                    var osBits = operatingSystem.StartsWith("Windows (64-bit)", StringComparison.OrdinalIgnoreCase)
                        ? 64 : operatingSystem.StartsWith("Windows (32-bit)", StringComparison.OrdinalIgnoreCase) ? 32 : 0;
                    var excelGeneration = Version.TryParse(excelVersion, out var excelParsed)
                        ? excelParsed.Major switch { >= 9 and <= 12 => 6, >= 14 => 7, _ => 0 } : 0;
                    var vbeGeneration = Version.TryParse(vbeVersion, out var vbeParsed)
                        ? vbeParsed.Major switch { 6 => 6, 7 => 7, _ => 0 } : 0;
                    if (processBits == 0 || osBits == 0 || excelGeneration == 0 || vbeGeneration == 0)
                        return new DebugCompilationHostFacts(excelVersion, vbeVersion, operatingSystem, architecture,
                            DebugCompilationHostFactsStatus.Unknown, null,
                            "The exact live Excel architecture, Windows bitness or VBA generation could not be verified.");
                    if ((processBits == 64 && osBits == 32) || excelGeneration != vbeGeneration)
                        return new DebugCompilationHostFacts(excelVersion, vbeVersion, operatingSystem, architecture,
                            DebugCompilationHostFactsStatus.Mismatch, null,
                            "Live Excel/VBE version or bitness contradicts the exact bound process.");
                    return new DebugCompilationHostFacts(excelVersion, vbeVersion, operatingSystem, architecture,
                        DebugCompilationHostFactsStatus.Verified,
                        new DebugCompilerBuiltInConstants(true, vbeGeneration == 7, false, true, processBits == 64, false), null);
                    // Project-level custom constants intentionally have no authority
                    // here. Dirty saved package bytes are not the live project.
                }
                catch (Exception error) { failure = error; throw; }
                finally { ReleaseScope(failure, ("live host VBE", vbeObject)); }
            }, cancellationToken).WaitAsync(cancellationToken);

        public Task SetNativeBreakpointsAsync(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            CancellationToken cancellationToken)
            => dispatcher.InvokeAsync(() =>
            {
                if (!binding.IsExactWorkbook())
                    throw new DebugSetupException("The exact source workbook binding is no longer available.");
                var references = new List<(string Resource, object? Value)>();
                var modules = new Dictionary<string, (object Component, object Code, VbeCodeModuleSourceMap Map)>(
                    StringComparer.OrdinalIgnoreCase);
                Exception? failure = null;
                try
                {
                    dynamic workbook = binding.Workbook;
                    object projectObject = workbook.VBProject;
                    references.Add(("source VBA project", projectObject));
                    dynamic project = projectObject;
                    if ((int)project.Mode != 2)
                        throw new DebugSetupException("The source project is not in design mode before breakpoint transfer.");
                    object componentsObject = project.VBComponents;
                    references.Add(("source VBA components", componentsObject));
                    dynamic components = componentsObject;
                    // Validate the entire participating set before the first toggle.
                    foreach (var breakpoint in breakpoints)
                    {
                        var map = breakpoint.SourceMap;
                        if (!VbaIdentifier.IsIdentifier(map.ModuleName) || map.CodeLines.IsDefault
                            || breakpoint.VbideLine < 1 || breakpoint.VbideLine > map.CodeLines.Length)
                            throw new DebugSetupException("A native source breakpoint has no exact module/line source map.");
                        if (modules.TryGetValue(map.ModuleName, out var existing))
                        {
                            if (existing.Map.ModuleKind != map.ModuleKind
                                || !existing.Map.CodeLines.SequenceEqual(map.CodeLines, StringComparer.Ordinal))
                                throw new DebugSetupException("Conflicting whole-module breakpoint source maps were supplied.");
                            continue;
                        }
                        object componentObject = components.Item(map.ModuleName);
                        references.Insert(0, ($"breakpoint component {map.ModuleName}", componentObject));
                        dynamic component = componentObject;
                        var expectedKind = map.ModuleKind switch
                        {
                            VbaModuleKind.StandardModule => 1,
                            VbaModuleKind.ClassModule => 2,
                            VbaModuleKind.FormModule => 3,
                            _ => throw new DebugSetupException("The source breakpoint module kind is unsupported.")
                        };
                        if ((string)component.Name != map.ModuleName || (int)component.Type != expectedKind)
                            throw new DebugSetupException("The live source breakpoint component identity/kind differs from its snapshot.");
                        object codeObject = component.CodeModule;
                        references.Insert(0, ($"breakpoint code {map.ModuleName}", codeObject));
                        dynamic code = codeObject;
                        if ((int)code.CountOfLines != map.CodeLines.Length)
                            throw new DebugSetupException("The live source whole-module line count differs from its breakpoint snapshot.");
                        for (var line = 1; line <= map.CodeLines.Length; line++)
                            if ((string)code.Lines(line, 1) != map.CodeLines[line - 1])
                                throw new DebugSetupException($"The live source whole-module code differs at '{map.ModuleName}:{line}'.");
                        modules.Add(map.ModuleName, (componentObject, codeObject, map));
                    }
                    foreach (var breakpoint in breakpoints)
                    {
                        var module = modules[breakpoint.ModuleName];
                        ExecuteNativeCommand(module.Component, module.Code, breakpoint.VbideLine, 51, "Toggle Breakpoint");
                    }
                    return true;
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    ReleaseScope(failure, references.ToArray());
                }
            }, cancellationToken).WaitAsync(cancellationToken);

        public async Task RunTargetAsync(
            DebugTargetProcedure target,
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (disposalGate)
            {
                ObjectDisposedException.ThrowIf(disposal is not null, this);
                if (inputWaitSink is not null && targetPromptPhase is not null)
                    throw new DebugSetupException("Source native Run already has an active modal observation phase.");
            }
            void BeginPromptObservation()
            {
                if (inputWaitSink is null) return;
                lock (disposalGate)
                {
                    ObjectDisposedException.ThrowIf(disposal is not null, this);
                    if (targetPromptPhase is not null)
                        throw new DebugSetupException("Source native Run already has an active modal observation phase.");
                    var monitor = promptMonitor ?? new DebugModalPromptMonitor(
                        new ExactSourceModalWindowApi(new(binding.ProcessId, binding.ProcessStartUtcTicks)));
                    // This legacy monitor input is intentionally unresolved. It
                    // is NOT process-exit evidence; typed source completion is
                    // raced separately and retires the phase independently.
                    var noExitEvidence = new TaskCompletionSource<DebugProcessExit>(
                        TaskCreationOptions.RunContinuationsAsynchronously).Task;
                    // The exact native context is already visible and verified.
                    // Its ordinary VBE windows belong in this baseline; only
                    // windows appearing after Execute can require new input.
                    var phase = monitor.BeginPhase(new(DebugInputWaitKind.ExcelOrVbe,
                        DebugInputWaitPhase.TargetStart, ProcessId), noExitEvidence,
                        new SourceLifecycleInputWaitSink(inputWaitSink));
                    targetPromptPhase = phase;
                    targetPromptObservation = ObserveTargetPromptLifetimeAsync(phase);
                    ObserveLateFailure(targetPromptObservation);
                }
            }
            Task<bool> RunAsync() => dispatcher.InvokeAsync(() =>
            {
                if (!binding.IsExactWorkbook())
                    throw new DebugSetupException("The exact source workbook binding is no longer available.");
                object? projectObject = null;
                object? componentsObject = null;
                object? componentObject = null;
                object? codeObject = null;
                Exception? failure = null;
                try
                {
                    dynamic workbook = binding.Workbook;
                    projectObject = workbook.VBProject;
                    dynamic project = projectObject;
                    if ((int)project.Mode != 2)
                        throw new DebugSetupException("The exact source project is not in design mode before native Run.");
                    componentsObject = project.VBComponents;
                    dynamic components = componentsObject;
                    componentObject = components.Item(target.ModuleName);
                    dynamic component = componentObject;
                    if ((int)component.Type != 1 || (string)component.Name != target.ModuleName)
                        throw new DebugSetupException("The source debug target is not the exact standard module.");
                    codeObject = component.CodeModule;
                    dynamic code = codeObject;
                    var line = (int)code.ProcBodyLine(target.ProcedureName, 0);
                    if (line <= 0 || line > (int)code.CountOfLines)
                        throw new DebugSetupException("The native source debug target procedure could not be resolved.");
                    ExecuteNativeCommand(componentObject, codeObject, line, 186, "Run Sub/UserForm",
                        BeginPromptObservation);
                    return true;
                }
                catch (Exception error) { failure = error; throw; }
                finally
                {
                    ReleaseScope(failure,
                        ("target code module", codeObject), ("target component", componentObject),
                        ("source VBA components", componentsObject), ("source VBA project", projectObject));
                }
            }, cancellationToken).WaitAsync(cancellationToken);
            // A late-created modal phase is supervised independently and faults
            // typed source Completion. Do not begin an earlier phase merely to
            // wrap this dispatch, or infer an exit from the monitor sentinel.
            var running = RunAsync();
            ObserveLateFailure(running);
            _ = await Task.WhenAny(running, Completion).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!running.IsCompleted && Completion.IsCompleted)
            {
                var ended = await Completion.ConfigureAwait(false);
                throw new DebugSetupException($"The source session ended ({ended.Reason}) before native Run completed. " +
                    "No Excel process exit code was inferred. Use Reset in the selected source workbook's VBE if needed.");
            }
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ObserveTargetPromptLifetimeAsync(IDebugModalPromptPhase phase)
        {
            try
            {
                _ = await Task.WhenAny(phase.Completion, Completion).ConfigureAwait(false);
                if (Completion.IsCompleted) await RetireTargetPromptAsync().ConfigureAwait(false);
                else await phase.Completion.ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                lock (disposalGate)
                {
                    if (disposal is null && ReferenceEquals(targetPromptPhase, phase) && targetPromptRetirement is null)
                        completion.TrySetException(ExplainFailure(
                            "Source Excel/VBE modal observation failed; execution state is unconfirmed. " +
                            "Use Reset in the selected source workbook's VBE if needed. Excel was not terminated.", failure));
                }
            }
        }

        private Task RetireTargetPromptAsync()
        {
            lock (disposalGate)
            {
                if (targetPromptRetirement is not null) return targetPromptRetirement;
                try { targetPromptRetirement = targetPromptPhase?.DisposeAsync().AsTask() ?? Task.CompletedTask; }
                catch (Exception failure) { targetPromptRetirement = Task.FromException(failure); }
                ObserveLateFailure(targetPromptRetirement);
                return targetPromptRetirement;
            }
        }

        private void ExecuteNativeCommand(object componentObject, object codeObject, int line,
            int commandId, string commandName, Action? beforeExecute = null)
        {
            object? paneObject = null;
            object? activePaneObject = null;
            object? vbeObject = null;
            object? windowObject = null;
            object? codeWindowObject = null;
            object? barsObject = null;
            object? commandObject = null;
            Exception? failure = null;
            try
            {
                _ = windowActivator.AllowComServerForeground(binding.Application);
                dynamic component = componentObject;
                component.Activate();
                dynamic code = codeObject;
                paneObject = code.CodePane;
                dynamic pane = paneObject;
                dynamic excel = binding.Application;
                vbeObject = excel.VBE;
                dynamic vbe = vbeObject;
                windowObject = vbe.MainWindow;
                dynamic window = windowObject;
                window.Visible = true;
                pane.Show();
                vbe.ActiveCodePane = paneObject;
                pane.SetSelection(line, 1, line, 1);
                window.SetFocus();
                codeWindowObject = pane.Window;
                dynamic codeWindow = codeWindowObject;
                codeWindow.SetFocus();
                windowActivator.BringOwnedWindowToForeground(
                    new nint(Convert.ToInt64(window.HWnd)), ProcessId);
                activePaneObject = vbe.ActiveCodePane;
                if (!ReferenceEquals(activePaneObject, paneObject))
                    throw new DebugSetupException("The exact source VBE code pane is not active.");
                var startLine = 0;
                var startColumn = 0;
                var endLine = 0;
                var endColumn = 0;
                pane.GetSelection(ref startLine, ref startColumn, ref endLine, ref endColumn);
                if (startLine != line || endLine != line || startColumn != 1 || endColumn != 1)
                    throw new DebugSetupException("The exact source VBE line selection was not retained.");
                barsObject = vbe.CommandBars;
                dynamic bars = barsObject;
                commandObject = bars.FindControl(1, commandId, Type.Missing, false);
                if (commandObject is null)
                    throw new DebugSetupException($"The native VBE {commandName} command (ID {commandId}) is unavailable.");
                dynamic command = commandObject;
                if ((int)command.Id != commandId || !(bool)command.BuiltIn || !(bool)command.Enabled)
                    throw new DebugSetupException($"The built-in VBE {commandName} command is disabled in the exact source context. " +
                        "The target or breakpoint was not relocated. Resolve any native compile dialog in the VBE.");
                if (!binding.IsExactWorkbook())
                    throw new DebugSetupException("The source workbook binding changed before native execution.");
                beforeExecute?.Invoke();
                command.Execute();
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                ReleaseScope(failure,
                    ("native VBE command", commandObject), ("VBE command bars", barsObject),
                    ("VBE code window", codeWindowObject), ("VBE main window", windowObject),
                    ("active VBE code pane", activePaneObject), ("VBE", vbeObject),
                    ("source code pane", paneObject));
            }
        }

        public async Task ResetExecutionAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopping.CancelAfter(timeout);
            try
            {
                var state = await InspectAsync(stopping.Token).WaitAsync(stopping.Token).ConfigureAwait(false);
                if (state.ProjectMode == 2)
                {
                    await RetirePromptGenerationAsync(stopping.Token).ConfigureAwait(false);
                    return;
                }
                await dispatcher.InvokeAsync(() =>
                {
                    if (!binding.IsExactWorkbook())
                        throw new DebugSetupException("The exact source workbook binding is no longer available.");
                    object? projectObject = null;
                    object? vbeObject = null;
                    object? activeProjectObject = null;
                    object? confirmedProjectObject = null;
                    object? windowObject = null;
                    object? barsObject = null;
                    object? controlObject = null;
                    Exception? failure = null;
                    try
                    {
                        dynamic workbook = binding.Workbook;
                        projectObject = workbook.VBProject;
                        dynamic project = projectObject;
                        if ((int)project.Mode == 2) return true;
                        dynamic excel = binding.Application;
                        vbeObject = excel.VBE;
                        dynamic vbe = vbeObject;
                        activeProjectObject = vbe.ActiveVBProject;
                        if (!ReferenceEquals(activeProjectObject, projectObject))
                            throw new DebugSetupException("Another VBA project is active; execution ownership is ambiguous. " +
                                "The native global Reset command was not requested.");
                        _ = windowActivator.AllowComServerForeground(binding.Application);
                        windowObject = vbe.MainWindow;
                        dynamic window = windowObject;
                        window.Visible = true;
                        window.SetFocus();
                        windowActivator.BringOwnedWindowToForeground(
                            new nint(Convert.ToInt64(window.HWnd)), binding.ProcessId);
                        confirmedProjectObject = vbe.ActiveVBProject;
                        if (!ReferenceEquals(confirmedProjectObject, projectObject))
                            throw new DebugSetupException("The selected source VBA project is not the active Reset context.");
                        barsObject = vbe.CommandBars;
                        dynamic bars = barsObject;
                        controlObject = bars.FindControl(1, 228, Type.Missing, false);
                        if (controlObject is null)
                            throw new DebugSetupException("The native VBE Reset command (ID 228) is unavailable.");
                        dynamic control = controlObject;
                        if ((int)control.Id != 228 || !(bool)control.BuiltIn || !(bool)control.Enabled)
                            throw new DebugSetupException("The built-in VBE Reset command is not enabled in the exact source project.");
                        stopping.Token.ThrowIfCancellationRequested();
                        control.Execute();
                        return true;
                    }
                    catch (Exception error) { failure = error; throw; }
                    finally
                    {
                        ReleaseScope(failure, ("Reset command", controlObject),
                            ("VBE command bars", barsObject), ("VBE main window", windowObject),
                            ("active VBA project", activeProjectObject), ("VBE", vbeObject),
                            ("confirmed VBA project", confirmedProjectObject),
                            ("source VBA project", projectObject));
                    }
                }, stopping.Token).WaitAsync(stopping.Token).ConfigureAwait(false);
                while (true)
                {
                    state = await InspectAsync(stopping.Token).WaitAsync(stopping.Token).ConfigureAwait(false);
                    if (state.ProjectMode == 2)
                    {
                        await RetirePromptGenerationAsync(stopping.Token).ConfigureAwait(false);
                        return;
                    }
                    await Task.Delay(50, stopping.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                throw ExplainFailure(
                    "VBA execution could not be confirmed stopped in the selected source workbook. " +
                    "Use Reset in that workbook's VBE; Excel and the workbook were left open.", failure);
            }
        }

        private async Task RetirePromptGenerationAsync(CancellationToken token)
        {
            await RetireTargetPromptAsync().WaitAsync(token).ConfigureAwait(false);
            await targetPromptObservation.WaitAsync(token).ConfigureAwait(false);
            lock (disposalGate)
            {
                targetPromptPhase = null;
                targetPromptObservation = Task.CompletedTask;
                targetPromptRetirement = null;
            }
        }

        private void ReleaseScope(Exception? primary, params (string Resource, object? Value)[] resources)
        {
            try
            {
                var outcome = SourceVbeComReferences.ReleaseScope(primary, ProcessId, binding.WorkbookPath, releaseReference, resources);
                RecordReleaseOutcome(outcome);
            }
            catch (Exception failure) when (failure is IDebugFailureEvidence)
            {
                var outcome = ((IDebugFailureEvidence)failure).FailureOutcome;
                RecordReleaseOutcome(outcome);
                throw;
            }
        }

        private DebugSetupException ExplainFailure(string message, Exception failure)
        {
            if (failure is not IDebugFailureEvidence retained)
                return new DebugSetupException(message, failure);
            RecordReleaseOutcome(retained.FailureOutcome);
            return new SourceVbeEvidenceSetupException(message, failure, retained.FailureOutcome);
        }

        private void RecordReleaseOutcome(DebugFailureOutcome outcome)
        {
            lock (releaseObservationGate)
            {
                // Cleanup still executes after a bounded detach. Its actual
                // operation failure remains observable on that operation task,
                // but a published uncertain owner outcome cannot be mutated.
                if (releaseObservationsSealed) return;
                foreach (var observation in outcome.Evidence) releaseObservations.AddEvidence(observation);
                foreach (var cleanup in outcome.CleanupFailures)
                    releaseObservations.AddFailure(cleanup.Stage, cleanup.Resource, cleanup.Kind,
                        cleanup.Exception, cleanup.ProcessId, cleanup.RetainedPath);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (disposalGate) return new ValueTask(disposal ??= DisposeOnceAsync());
        }

        private async Task DisposeOnceAsync()
        {
            var result = new DebugFailureCompletion();
            var monitorReleased = false;
            var bindingReleased = false;
            var promptReleased = targetPromptPhase is null;
            try { monitorStopping.Cancel(); }
            catch (Exception failure) { result.AddFailure("source monitor cancellation", "source observer", DebugResourceKind.Handle, failure, ProcessId); }
            try
            {
                await lifetimeMonitor.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                monitorReleased = lifetimeMonitor.IsCompleted;
            }
            catch (Exception failure) { result.AddFailure("source monitor release", "source observer", DebugResourceKind.Handle, failure, ProcessId); }
            try
            {
                await RetireTargetPromptAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await targetPromptObservation.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                promptReleased = true;
            }
            catch (Exception failure)
            {
                result.AddFailure("source modal observer release", "source modal observer", DebugResourceKind.Handle,
                    failure, ProcessId);
            }
            try
            {
                var releasing = dispatcher.InvokeAsync(() => { binding.Release(); return binding.ReleaseVerified; }, CancellationToken.None);
                ObserveLateFailure(releasing);
                bindingReleased = await releasing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception failure) { result.AddFailure("source COM release", "source workbook binding", DebugResourceKind.Com, failure, ProcessId, binding.WorkbookPath); }
            try
            {
                var retiring = dispatcher.DisposeAsync().AsTask();
                ObserveLateFailure(retiring);
                await retiring.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception failure) { result.AddFailure("source STA retirement", "source COM dispatcher", DebugResourceKind.Handle, failure, ProcessId); }
            try { monitorStopping.Dispose(); }
            catch (Exception failure) { result.AddFailure("source monitor token", "source observer", DebugResourceKind.Handle, failure, ProcessId); }
            completion.TrySetResult(new SourceVbeDebugSessionCompletion(
                SourceVbeDebugSessionEndReason.Detached, null));
            lock (releaseObservationGate)
            {
                releaseObservationsSealed = true;
                result.Merge(releaseObservations.Complete());
            }
            result.AddEvidence(new("source lifetime authority", "Excel process", DebugResourceKind.Process, true,
                "Source automation acquired no lifetime ownership; Excel was not saved, closed, quit or terminated.", ProcessId));
            result.AddEvidence(new("source COM release", "source workbook binding", DebugResourceKind.Com, bindingReleased,
                bindingReleased ? "The acquired source binding references were released." : "Source binding reference release is unproved; leave Excel open and resolve its visible prompts.", ProcessId, binding.WorkbookPath));
            result.AddEvidence(new("source monitor release", "source observer", DebugResourceKind.Handle, monitorReleased,
                monitorReleased ? "The observer stopped and its cancellation state was disposed." : "The observer did not retire within the release bound.", ProcessId));
            result.AddEvidence(new("source modal observer release", "source modal observer", DebugResourceKind.Handle, promptReleased,
                promptReleased ? "The source modal phase retired without taking process lifetime authority."
                    : "Source modal phase release is unproved; use Reset in the source workbook's VBE if needed.", ProcessId));
            result.AddEvidence(new("source STA retirement", "source COM dispatcher", DebugResourceKind.Handle, dispatcher.ReleaseVerified,
                dispatcher.ReleaseVerified ? "The source COM STA retired." : "Source COM STA retirement is unproved; no Excel lifetime action was taken.", ProcessId));
            CleanupOutcome = result.Complete();
            CleanupOutcome.ThrowWithEvidence();
        }

        private static void ObserveLateFailure(Task task)
            => _ = task.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private sealed class SourceLifecycleInputWaitSink(IDebugInputWaitSink sink) : IDebugInputWaitSink
    {
        public ValueTask InputRequiredAsync(DebugInputWait wait, CancellationToken token)
            => sink is IDebugLifecycleSink lifecycle
                ? lifecycle.WriteAsync(wait.ToSourceLifecycleMessage(), token)
                : sink.InputRequiredAsync(wait, token);
    }

    private sealed class ExactSourceModalWindowApi(SourceVbeProcessIdentity identity) : IDebugModalWindowApi
    {
        private readonly WindowsDebugModalWindowApi inner = new();
        public IReadOnlySet<nint> CaptureVisibleModalWindows(int processId)
        {
            if (processId != identity.ProcessId) throw new DebugSetupException("The source modal observation PID changed.");
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return new HashSet<nint>();
                if (process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks)
                    throw new DebugSetupException("The source modal observation process identity changed.");
                return inner.CaptureVisibleModalWindows(processId);
            }
            catch (ArgumentException)
            {
                // Absence is not translated into a fabricated DebugProcessExit.
                // The source connector owns actual typed completion observation.
                return new HashSet<nint>();
            }
        }
        public Task WaitForNextObservationAsync(CancellationToken token) => inner.WaitForNextObservationAsync(token);
    }
}

internal sealed class SourceVbeEvidenceSetupException(
    string message, Exception failure, DebugFailureOutcome outcome)
    : DebugSetupException(message, failure), IDebugFailureEvidence
{
    public DebugFailureOutcome FailureOutcome { get; } = outcome;
}

/// <summary>Releases only references acquired by source automation, not shared RCWs.</summary>
internal static class SourceVbeComReferences
{
    internal static bool Release(object? value)
        => value is null || !OperatingSystem.IsWindows() || !Marshal.IsComObject(value)
            || Marshal.ReleaseComObject(value) >= 0;

    internal static DebugFailureOutcome ReleaseScope(Exception? primary, int processId, string path,
        params (string Resource, object? Value)[] resources)
        => ReleaseScope(primary, processId, path, Release, resources);

    internal static DebugFailureOutcome ReleaseScope(Exception? primary, int processId, string path,
        Func<object?, bool> releaseReference, params (string Resource, object? Value)[] resources)
    {
        var completion = new DebugFailureCompletion(primary);
        foreach (var (resource, value) in resources)
        {
            if (value is null) continue;
            var released = false;
            try { released = releaseReference(value); }
            catch (Exception failure)
            {
                completion.AddFailure("source COM reference", resource, DebugResourceKind.Com,
                    failure, processId, path);
            }
            completion.AddEvidence(new("source COM reference", resource, DebugResourceKind.Com, released,
                released ? "The acquired COM reference was released; shared reference lifetime is unchanged."
                    : "Release of the acquired COM reference is unproved.", processId, path));
        }
        var outcome = completion.Complete();
        if (outcome.HasCleanupFailure) outcome.Throw();
        return outcome;
    }
}
