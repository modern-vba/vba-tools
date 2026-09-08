using VbaDebugAdapter.Debugging;
using VbaDebugAdapter.Infrastructure;
using VbaTools.Syntax;
using Microsoft.CSharp.RuntimeBinder;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VbaDebugAdapter.Infrastructure;

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

    bool ReleaseVerified => false;
}

internal interface IStaComDispatcherFactory
{
    IStaComDispatcher Create();
}

internal interface IVbeDebugSessionStartFailure
{
    Exception StartException { get; }

    Exception? CleanupException { get; }

    bool CleanupVerified { get; }
}

internal sealed class VbeDebugSessionStartException : DebugSetupException,
    IVbeDebugSessionStartFailure, IDebugFailureEvidence
{
    public VbeDebugSessionStartException(DebugFailureOutcome outcome)
        : base(outcome.Describe(), outcome.PrimaryFailure ?? new AggregateException(outcome.CleanupFailures.Select(item => item.Exception)))
    {
        FailureOutcome = outcome;
    }

    public VbeDebugSessionStartException(Exception startException, Exception? cleanupException, bool cleanupVerified)
        : this(CreateLegacyOutcome(startException, cleanupException, cleanupVerified)) { }

    public DebugFailureOutcome FailureOutcome { get; }
    public Exception StartException => FailureOutcome.PrimaryFailure!;
    public Exception? CleanupException => FailureOutcome.CleanupFailures.Count switch
    {
        0 => null,
        1 => FailureOutcome.CleanupFailures[0].Exception,
        _ => new AggregateException(FailureOutcome.CleanupFailures.Select(item => item.Exception))
    };
    public bool CleanupVerified => !FailureOutcome.HasCleanupFailure;

    private static DebugFailureOutcome CreateLegacyOutcome(Exception primary, Exception? cleanup, bool verified)
    {
        var completion = new DebugFailureCompletion(primary);
        if (cleanup is not null) { completion.AddFailure("startup", "Excel owner", DebugResourceKind.Process, cleanup); }
        completion.AddEvidence(new("startup", "Excel owner", DebugResourceKind.Process,
            verified, "Explicit startup owner observation."));
        return completion.Complete();
    }
}

internal sealed class VbeDebugSessionStartCanceledException(
    OperationCanceledException startException,
    DebugFailureOutcome outcome) :
    DebugFailureCanceledException(outcome), IVbeDebugSessionStartFailure
{
    public Exception StartException { get; } = startException;

    public Exception? CleanupException => null;

    public bool CleanupVerified => !FailureOutcome.HasCleanupFailure;

}

/// <summary>
/// Starts a dedicated visible Excel instance and performs VBE-native target execution.
/// </summary>
public sealed class VbeDebugAutomation : IVbeDebugSessionFactory
{
    private readonly IExcelDebugApplicationFactory? applicationFactory;
    private readonly IExcelDebugOwnedApplicationStarter? ownedApplicationStarter;
    private readonly IDebugExcelProcessApi processApi;
    private readonly IDebugWindowActivator windowActivator;
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly IExcelDebugWorkbookOpener workbookOpener;
    private readonly IDebugModalPromptMonitor promptMonitor;
    private readonly IDebugWorkbookLifetimeMonitor workbookLifetimeMonitor;
    private readonly Func<object?, bool> releaseComObject;

    /// <summary>
    /// Creates the production Excel/VBIDE automation adapter.
    /// </summary>
    public VbeDebugAutomation()
        : this(
            applicationFactory: null,
            new OwnedExcelDebugApplicationStarter(
                new WindowsExcelDebugOwnedProcessLauncher(),
                new WindowsExcelDebugNativeObjectModelBinder()),
            new WindowsDebugExcelProcessApi(),
            new WindowsDebugWindowActivator(),
            new StaComDispatcherFactory(),
            workbookOpener: null,
            promptMonitor: null,
            workbookLifetimeMonitor: null)
    {
    }

    internal VbeDebugAutomation(
        IExcelDebugApplicationFactory applicationFactory,
        IDebugExcelProcessApi processApi,
        IDebugWindowActivator windowActivator,
        IStaComDispatcherFactory dispatcherFactory,
        IExcelDebugWorkbookOpener? workbookOpener = null,
        IDebugModalPromptMonitor? promptMonitor = null,
        IDebugWorkbookLifetimeMonitor? workbookLifetimeMonitor = null,
        Func<object?, bool>? releaseComObject = null)
        : this(
            applicationFactory,
            ownedApplicationStarter: null,
            processApi,
            windowActivator,
            dispatcherFactory,
            workbookOpener,
            promptMonitor,
            workbookLifetimeMonitor,
            releaseComObject)
    {
    }

    internal VbeDebugAutomation(
        IExcelDebugOwnedApplicationStarter ownedApplicationStarter,
        IDebugExcelProcessApi processApi,
        IDebugWindowActivator windowActivator,
        IStaComDispatcherFactory dispatcherFactory,
        IExcelDebugWorkbookOpener? workbookOpener = null,
        IDebugModalPromptMonitor? promptMonitor = null,
        IDebugWorkbookLifetimeMonitor? workbookLifetimeMonitor = null,
        Func<object?, bool>? releaseComObject = null)
        : this(
            applicationFactory: null,
            ownedApplicationStarter,
            processApi,
            windowActivator,
            dispatcherFactory,
            workbookOpener,
            promptMonitor,
            workbookLifetimeMonitor,
            releaseComObject)
    {
    }

    private VbeDebugAutomation(
        IExcelDebugApplicationFactory? applicationFactory,
        IExcelDebugOwnedApplicationStarter? ownedApplicationStarter,
        IDebugExcelProcessApi processApi,
        IDebugWindowActivator windowActivator,
        IStaComDispatcherFactory dispatcherFactory,
        IExcelDebugWorkbookOpener? workbookOpener,
        IDebugModalPromptMonitor? promptMonitor,
        IDebugWorkbookLifetimeMonitor? workbookLifetimeMonitor,
        Func<object?, bool>? releaseComObject = null)
    {
        this.releaseComObject = releaseComObject ?? ComObjectReleaser.Release;
        this.applicationFactory = applicationFactory;
        this.ownedApplicationStarter = ownedApplicationStarter;
        this.processApi = processApi;
        this.windowActivator = windowActivator;
        this.dispatcherFactory = dispatcherFactory;
        this.workbookOpener = workbookOpener ?? new ExcelComDebugWorkbookOpener();
        this.promptMonitor = promptMonitor ?? new DebugModalPromptMonitor();
        this.workbookLifetimeMonitor = workbookLifetimeMonitor
            ?? new DebugWorkbookLifetimeMonitor();
    }

    /// <inheritdoc />
    public async Task<IVbeDebugSession> StartVisibleAsync(CancellationToken cancellationToken)
    {
        IStaComDispatcher? dispatcher = null;
        object? excelObject = null;
        DebugExcelProcessOwner? processOwner = null;
        DebugFailureOutcome? startupCleanupOutcome = null;
        var ownedStartupAttempted = false;
        CancellationTokenRegistration ownershipCancellation = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingExcelProcesses = ownedApplicationStarter is null
                ? processApi.CaptureRunningExcelProcesses()
                : null;
            dispatcher = dispatcherFactory.Create();
            await dispatcher.InvokeAsync(
                () =>
                {
                    if (ownedApplicationStarter is not null)
                    {
                        ownedStartupAttempted = true;
                        var ownedApplication = ownedApplicationStarter.Start(
                            processApi,
                            cancellationToken);
                        excelObject = ownedApplication.Application;
                        processOwner = ownedApplication.ProcessOwner;
                        startupCleanupOutcome = ownedApplication.StartupCleanupOutcome;
                    }
                    else
                    {
                        excelObject = applicationFactory!.Create();
                        dynamic capturedExcel = excelObject;
                        var capturedWindowHandle = ToWindowHandle(capturedExcel.Hwnd);
                        processOwner = DebugExcelProcessOwner.Capture(
                            capturedWindowHandle,
                            existingExcelProcesses!,
                            processApi);
                    }
                    dynamic excel = excelObject;
                    ownershipCancellation = cancellationToken.UnsafeRegister(
                        static state =>
                            _ = ((DebugExcelProcessOwner)state!).TerminateAsync().AsTask(),
                        processOwner);
                    cancellationToken.ThrowIfCancellationRequested();
                    excel.DisplayAlerts = false;
                    excel.Visible = true;
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            ownershipCancellation.Dispose();
            cancellationToken.ThrowIfCancellationRequested();

            return new VbeDebugSession(
                excelObject!,
                processOwner!,
                dispatcher,
                windowActivator,
                workbookOpener,
                promptMonitor,
                workbookLifetimeMonitor,
                releaseComObject);
        }
        catch (Exception startException)
        {
            var reportedStartException = startException is not IDebugFailureEvidence
                && startException is DebugProcessOwnershipCleanupException ownershipFailure
                ? ownershipFailure.OwnershipException : startException;
            var completion = new DebugFailureCompletion(reportedStartException);
            if (startupCleanupOutcome is not null) { completion.Merge(startupCleanupOutcome); }
            if (startException is not IDebugFailureEvidence
                && startException is DebugProcessOwnershipCleanupException ownershipCleanup)
            {
                completion.AddFailure("startup ownership", "Excel process", DebugResourceKind.Process,
                    ownershipCleanup.CleanupException);
            }
            try { ownershipCancellation.Dispose(); }
            catch (Exception failure)
            {
                completion.AddFailure("cancellation registration", "Excel startup", DebugResourceKind.Handle, failure);
            }

            if (processOwner is not null)
            {
                try { await processOwner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure)
                {
                    completion.AddFailure("process cleanup", "Excel process", DebugResourceKind.Process,
                        failure, processOwner.ProcessId);
                }
                if ((object)processOwner is IDebugResourceOwnerEvidence { CleanupOutcome: { } processOutcome })
                {
                    completion.Merge(processOutcome);
                }
                else
                {
                    completion.AddEvidence(new("process cleanup", "Excel process", DebugResourceKind.Process,
                        false, "The process owner did not supply release evidence.", processOwner.ProcessId));
                }
            }
            else if ((excelObject is not null || ownedStartupAttempted)
                && (startException is not IDebugFailureEvidence retained
                    || !retained.FailureOutcome.Evidence.Any(item => item.Kind == DebugResourceKind.Process)))
            {
                completion.AddEvidence(new("startup ownership", "Excel process", DebugResourceKind.Process,
                    false, "Excel was created without a proved process owner."));
            }

            if (excelObject is not null && dispatcher is not null)
            {
                var released = false;
                try
                {
                    released = await dispatcher.InvokeAsync(
                        () => releaseComObject(excelObject), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    completion.AddFailure("COM release", "Excel application", DebugResourceKind.Com,
                        failure, processOwner?.ProcessId);
                }
                completion.AddEvidence(new("COM release", "Excel application", DebugResourceKind.Com, released,
                    released ? "The owned COM reference was released." : "The owned COM reference release was not proved.",
                    processOwner?.ProcessId));
            }
            if (dispatcher is not null)
            {
                try { await dispatcher.DisposeAsync().ConfigureAwait(false); }
                catch (Exception failure)
                {
                    completion.AddFailure("dispatcher release", "STA dispatcher", DebugResourceKind.Handle, failure);
                }
                completion.AddEvidence(new("dispatcher release", "STA dispatcher", DebugResourceKind.Handle,
                    dispatcher.ReleaseVerified, "The dispatcher owner reports its terminal worker state."));
            }
            var outcome = completion.Complete();
            if (outcome.PrimaryFailure is OperationCanceledException cancellation && !outcome.HasCleanupFailure)
            {
                throw new VbeDebugSessionStartCanceledException(cancellation, outcome);
            }
            throw new VbeDebugSessionStartException(outcome);
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

    private sealed class VbeDebugSession : IVbeDebugSession, IVbeDebugDoctorControl, IDebugResourceOwnerEvidence
    {
        private const int VbeBreakMode = 1;
        private const int VbeDesignMode = 2;

        private readonly object excelObject;
        private readonly DebugExcelProcessOwner processOwner;
        private readonly IStaComDispatcher dispatcher;
        private readonly IDebugWindowActivator windowActivator;
        private readonly IExcelDebugWorkbookOpener workbookOpener;
        private readonly IDebugModalPromptMonitor promptMonitor;
        private readonly IDebugWorkbookLifetimeMonitor workbookLifetimeMonitor;
        private readonly Func<object?, bool> releaseComObject;
        private readonly TaskCompletionSource<object> workbookOpenedSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object generationOwnershipGate = new();
        private readonly Task<DebugProcessExit> completion;
        private readonly int processId;
        private readonly TaskCompletionSource<SessionEnd> terminalEnd =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource observationStopping = new();
        private readonly List<Exception> lifetimeFailures = [];
        private Task processObservation = Task.CompletedTask;
        private Task workbookObservation = Task.CompletedTask;
        private object? workbookObject;
        private IVbaDebugGenerationWorkspace? generationWorkspace;
        private Task targetPromptObservation = Task.CompletedTask;
        private IDebugModalPromptPhase? targetPromptPhase;
        private Task workbookPromptObservation = Task.CompletedTask;
        private IDebugModalPromptPhase? workbookPromptPhase;
        private int? foregroundPermissionHResult;
        private int workbookOpened;
        private int disposed;

        public VbeDebugSession(
            object excelObject,
            DebugExcelProcessOwner processOwner,
            IStaComDispatcher dispatcher,
            IDebugWindowActivator windowActivator,
            IExcelDebugWorkbookOpener workbookOpener,
            IDebugModalPromptMonitor promptMonitor,
            IDebugWorkbookLifetimeMonitor workbookLifetimeMonitor,
            Func<object?, bool> releaseComObject)
        {
            this.releaseComObject = releaseComObject;
            this.excelObject = excelObject;
            this.processOwner = processOwner;
            this.dispatcher = dispatcher;
            this.windowActivator = windowActivator;
            this.workbookOpener = workbookOpener;
            this.promptMonitor = promptMonitor;
            this.workbookLifetimeMonitor = workbookLifetimeMonitor;
            processId = processOwner.ProcessId;
            processObservation = SuperviseLifetimeAsync(processOwner.Completion, "process exit", terminateOnSuccess: true);
            workbookObservation = SuperviseLifetimeAsync(ObserveWorkbookCloseAsync(), "workbook close", terminateOnSuccess: true);
            completion = CompleteLifetimeAsync();
        }

        public int ProcessId => processId;

        public DebugFailureOutcome? CleanupOutcome { get; private set; }

        public bool StrongProcessOwnershipEstablished =>
            processOwner.KillOnCloseJobAssigned;

        public Task<DebugProcessExit> Completion => completion;

        public async Task CreateFixtureWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
            var expectedWorkbookPath = Path.GetFullPath(workbookPath);
            if (!Path.GetExtension(expectedWorkbookPath).Equals(
                    ".xlsm",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DebugSetupException(
                    "The Doctor fixture workbook must use the .xlsm extension.");
            }

            _ = await InvokeSetupAsync(
                () =>
                {
                    object? workbooksObject = null;
                    object? fixtureWorkbookObject = null;
                    Exception? primaryFailure = null;
                    try
                    {
                        dynamic excel = excelObject;
                        workbooksObject = excel.Workbooks;
                        dynamic workbooks = workbooksObject;
                        fixtureWorkbookObject = workbooks.Add();
                        dynamic fixtureWorkbook = fixtureWorkbookObject;
                        fixtureWorkbook.SaveAs(expectedWorkbookPath, 52);
                        var actualPath = Path.GetFullPath(
                            Convert.ToString(fixtureWorkbook.FullName)!);
                        if (!actualPath.Equals(
                                expectedWorkbookPath,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DebugSetupException(
                                "Excel saved the Doctor fixture to an unexpected path.");
                        }
                        fixtureWorkbook.Close(false);
                        return true;
                    }
                    catch (Exception failure)
                    {
                        primaryFailure = failure;
                        throw;
                    }
                    finally
                    {
                        ReleaseComScope(primaryFailure,
                            ("Doctor fixture workbook", fixtureWorkbookObject),
                            ("Excel workbooks", workbooksObject));
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<DebugCompilationHostFacts> GetCompilationHostFactsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await InvokeSetupAsync(
                ReadCompilationHostFacts,
                cancellationToken).ConfigureAwait(false);
        }

        public void AdoptGenerationWorkspace(
            IVbaDebugGenerationWorkspace generationWorkspace)
        {
            ArgumentNullException.ThrowIfNull(generationWorkspace);
            lock (generationOwnershipGate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                ObjectDisposedException.ThrowIf(terminalEnd.Task.IsCompleted, this);
                if (this.generationWorkspace is not null)
                {
                    throw new InvalidOperationException(
                        "The VBE debug session already owns a generation workspace.");
                }
                this.generationWorkspace = generationWorkspace;
            }
        }

        public async Task OpenGeneratedWorkbookAsync(
            IDebugInputWaitSink? inputWaitSink,
            CancellationToken cancellationToken)
        {
            IVbaDebugGenerationWorkspace ownedGenerationWorkspace;
            lock (generationOwnershipGate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                ownedGenerationWorkspace = generationWorkspace
                    ?? throw new InvalidOperationException(
                        "The VBE debug session has not adopted a generation workspace.");
            }
            ownedGenerationWorkspace.VerifyGeneratedWorkbook();
            await OpenWorkbookAsync(
                ownedGenerationWorkspace.WorkbookPath,
                inputWaitSink,
                verifyVbideAccess: true,
                cancellationToken).ConfigureAwait(false);
            ownedGenerationWorkspace.VerifyGeneratedWorkbook();
        }

        private async Task OpenWorkbookAsync(
            string workbookPath,
            IDebugInputWaitSink? inputWaitSink,
            bool verifyVbideAccess,
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
            await using var phase = StartPromptPhase(inputWait, inputWaitSink);
            Task<object> OpenAsync() => InvokeSetupAsync(
                () => verifyVbideAccess
                    ? workbookOpener.OpenVerified(excelObject, expectedWorkbookPath)
                    : workbookOpener.OpenPathVerified(excelObject, expectedWorkbookPath),
                cancellationToken);
            workbookObject = await ObserveSetupOperationAsync(phase, OpenAsync, cancellationToken).ConfigureAwait(false);
            workbookOpenedSignal.TrySetResult(workbookObject);
        }

        public Task OpenFixtureWorkbookAsync(
            string workbookPath,
            CancellationToken cancellationToken)
            => OpenWorkbookAsync(
                workbookPath,
                inputWaitSink: null,
                verifyVbideAccess: false,
                cancellationToken);

        public async Task ImportFixtureModuleAsync(
            string sourcePath,
            VbeCodeModuleSourceMap sourceMap,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentNullException.ThrowIfNull(sourceMap);
            var expectedSourcePath = Path.GetFullPath(sourcePath);
            if (!File.Exists(expectedSourcePath))
            {
                throw new DebugSetupException(
                    $"The Doctor standard-module source does not exist: {expectedSourcePath}");
            }

            _ = await InvokeSetupAsync(
                () =>
                {
                    object? projectObject = null;
                    object? componentsObject = null;
                    object? importedComponentObject = null;
                    VerifiedCodeModule? verifiedModule = null;
                    Exception? primaryFailure = null;
                    try
                    {
                        dynamic workbook = GetOpenedWorkbook();
                        projectObject = workbook.VBProject;
                        dynamic project = projectObject;
                        EnsureDesignMode(project);
                        componentsObject = project.VBComponents;
                        dynamic components = componentsObject;
                        importedComponentObject = components.Import(expectedSourcePath);
                        verifiedModule = VerifyCodeModule(components, sourceMap);
                        return true;
                    }
                    catch (Exception failure)
                    {
                        primaryFailure = failure;
                        throw;
                    }
                    finally
                    {
                        ReleaseComScope(primaryFailure,
                            ($"code module {sourceMap.ModuleName}", verifiedModule?.CodeModule),
                            ($"component {sourceMap.ModuleName}", verifiedModule?.Component),
                            ("imported Doctor component", importedComponentObject),
                            ("VBA components", componentsObject),
                            ("VBA project", projectObject));
                    }
                },
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

        public async Task VerifyCommandContextAsync(
            VbeBreakpoint breakpoint,
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(breakpoint);
            ArgumentNullException.ThrowIfNull(target);
            _ = await InvokeSetupAsync(
                () =>
                {
                    SetNativeBreakpoints([breakpoint], execute: false);
                    ExecuteTargetCommand(
                        target,
                        VbeDesignMode,
                        "target code",
                        beforeExecute: null,
                        execute: false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public Task ClearNativeBreakpointAsync(
            VbeBreakpoint breakpoint,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(breakpoint);
            return SetNativeBreakpointsAsync(
                [breakpoint],
                cancellationToken);
        }

        public async Task CloseOwnedProcessCooperativelyAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                _ = await InvokeSetupAsync(
                    () =>
                    {
                        if (workbookObject is not null)
                        {
                            dynamic workbook = workbookObject;
                            workbook.Saved = true;
                            workbook.Close(false);
                        }
                        dynamic excel = excelObject;
                        excel.Quit();
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
                _ = await processOwner.Completion.WaitAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                RequestCancellationTermination(cancellationToken);
                throw;
            }
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
            var phase = StartPromptPhase(inputWait, inputWaitSink);
            Task<bool> RunAsync() => InvokeSetupAsync(
                () =>
                {
                    RunTarget(target);
                    return true;
                },
                cancellationToken);
            _ = await ObserveSetupOperationAsync(phase, RunAsync, cancellationToken).ConfigureAwait(false);
        }

        public Task WaitForBreakModeAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => WaitForProbeStateWithTimeoutAsync(
                state => state.ProjectMode == VbeBreakMode,
                "The VBA project did not enter native break mode",
                timeout,
                cancellationToken);

        public Task WaitForBreakModeAsync(CancellationToken cancellationToken)
            => WaitForProbeStateUntilCanceledAsync(
                state => state.ProjectMode == VbeBreakMode,
                cancellationToken);

        public async Task ContinueTargetAsync(
            DebugTargetProcedure target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await InvokeSetupAsync(
                () =>
                {
                    ExecuteTargetCommand(
                        target,
                        VbeBreakMode,
                        "break mode",
                        beforeExecute: null);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }

        public Task WaitForCompletionAsync(
            string expectedMarker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => WaitForProbeStateWithTimeoutAsync(
                state =>
                    state.ProjectMode == VbeDesignMode &&
                    string.Equals(
                        expectedMarker,
                        state.CompletionMarker,
                        StringComparison.Ordinal),
                "The VBA project did not return to design mode with the expected completion marker",
                timeout,
                cancellationToken);

        public Task WaitForCompletionAsync(
            string expectedMarker,
            CancellationToken cancellationToken)
            => WaitForProbeStateUntilCanceledAsync(
                state =>
                    state.ProjectMode == VbeDesignMode &&
                    string.Equals(
                        expectedMarker,
                        state.CompletionMarker,
                        StringComparison.Ordinal),
                cancellationToken);

        public ValueTask TerminateAsync()
        {
            EstablishEnd("Stop", null);
            return new ValueTask(completion);
        }

        public ValueTask DisposeAsync() => TerminateAsync();

        private IDebugModalPromptPhase? StartPromptPhase(DebugInputWait inputWait, IDebugInputWaitSink? sink)
        {
            lock (generationOwnershipGate)
            {
                ObjectDisposedException.ThrowIf(disposed != 0 || terminalEnd.Task.IsCompleted, this);
                if (sink is null) { return null; }
                try
                {
                    var phase = promptMonitor.BeginPhase(inputWait, processOwner.Completion, sink);
                    var observation = SuperviseLifetimeAsync(phase.Completion, "modal observation", terminateOnSuccess: false);
                    if (inputWait.Phase == DebugInputWaitPhase.WorkbookOpen)
                    {
                        workbookPromptPhase = phase;
                        workbookPromptObservation = observation;
                    }
                    else
                    {
                        targetPromptPhase = phase;
                        targetPromptObservation = observation;
                    }
                    return phase;
                }
                catch (Exception failure)
                {
                    EstablishEnd("modal observation", failure);
                    throw;
                }
            }
        }

        private void EstablishEnd(string cause, Exception? failure)
        {
            lock (generationOwnershipGate)
            {
                if (failure is not null) { lifetimeFailures.Add(failure); }
                terminalEnd.TrySetResult(new SessionEnd(cause, failure));
            }
        }

        private async Task SuperviseLifetimeAsync(Task observation, string cause, bool terminateOnSuccess)
        {
            _ = observation.ContinueWith(static task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            try
            {
                await observation.WaitAsync(observationStopping.Token).ConfigureAwait(false);
                if (terminateOnSuccess) { EstablishEnd(cause, null); }
            }
            catch (OperationCanceledException) when (observationStopping.IsCancellationRequested)
            {
            }
            catch (Exception failure)
            {
                EstablishEnd(cause, failure);
            }
        }

        private async Task ObserveWorkbookCloseAsync()
        {
            var rawProcessCompletion = processOwner.Completion;
            var first = await Task.WhenAny(rawProcessCompletion, workbookOpenedSignal.Task).ConfigureAwait(false);
            if (ReferenceEquals(first, rawProcessCompletion))
            {
                await rawProcessCompletion.ConfigureAwait(false);
                return;
            }
            var workbook = await workbookOpenedSignal.Task.ConfigureAwait(false);
            await workbookLifetimeMonitor.WaitForCloseAsync(workbook, dispatcher, rawProcessCompletion).ConfigureAwait(false);
        }

        private async Task<DebugProcessExit> CompleteLifetimeAsync()
        {
            var established = await terminalEnd.Task.ConfigureAwait(false);
            IVbaDebugGenerationWorkspace? ownedGenerationWorkspace;
            lock (generationOwnershipGate)
            {
                disposed = 1;
                ownedGenerationWorkspace = generationWorkspace;
                generationWorkspace = null;
            }

            var failureCompletion = new DebugFailureCompletion(established.Failure);
            async Task AttemptAsync(string stage, string resource, DebugResourceKind kind, Func<ValueTask> cleanup)
            {
                try { await cleanup().ConfigureAwait(false); }
                catch (Exception failure) { failureCompletion.AddFailure(stage, resource, kind, failure, processId); }
            }

            // Advance process and Job cleanup before waiting for either observation loop.
            await AttemptAsync("process termination", "Excel process", DebugResourceKind.Process,
                processOwner.TerminateAsync).ConfigureAwait(false);
            await AttemptAsync("process cleanup", "Excel process", DebugResourceKind.Process,
                processOwner.DisposeAsync).ConfigureAwait(false);
            if ((object)processOwner is IDebugResourceOwnerEvidence { CleanupOutcome: { } processOutcome })
            {
                failureCompletion.Merge(processOutcome);
            }
            else
            {
                failureCompletion.AddEvidence(new("process cleanup", "Excel process", DebugResourceKind.Process,
                    false, "The process owner did not supply release evidence.", processId));
            }
            try { observationStopping.Cancel(); }
            catch (Exception failure)
            {
                failureCompletion.AddFailure("observation shutdown", "session observers", DebugResourceKind.Observation, failure, processId);
            }
            if (targetPromptPhase is not null)
            {
                await AttemptAsync("target observation", "modal phase", DebugResourceKind.Observation,
                    targetPromptPhase.DisposeAsync).ConfigureAwait(false);
            }
            if (workbookPromptPhase is not null)
            {
                await AttemptAsync("workbook observation", "modal phase", DebugResourceKind.Observation,
                    workbookPromptPhase.DisposeAsync).ConfigureAwait(false);
            }
            var observations = new[] { processObservation, workbookObservation, targetPromptObservation, workbookPromptObservation };
            try { await Task.WhenAll(observations).ConfigureAwait(false); }
            catch (Exception failure)
            {
                failureCompletion.AddFailure("observation shutdown", "session observers", DebugResourceKind.Observation, failure, processId);
            }
            failureCompletion.AddEvidence(new("observation shutdown", "session observers", DebugResourceKind.Observation,
                observations.All(task => task.IsCompleted), "Every owned observation task settled.", processId));

            async Task ReleaseComAsync(object? value, string resource)
            {
                var released = false;
                try
                {
                    released = await dispatcher.InvokeAsync(() => releaseComObject(value),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    failureCompletion.AddFailure("COM release", resource, DebugResourceKind.Com, failure, processId);
                }
                failureCompletion.AddEvidence(new("COM release", resource, DebugResourceKind.Com, released,
                    released ? "The owned COM reference was released." : "The owned COM reference release was not proved.", processId));
            }
            await ReleaseComAsync(workbookObject, "Excel workbook").ConfigureAwait(false);
            await ReleaseComAsync(excelObject, "Excel application").ConfigureAwait(false);
            await AttemptAsync("dispatcher release", "STA dispatcher", DebugResourceKind.Handle,
                dispatcher.DisposeAsync).ConfigureAwait(false);
            failureCompletion.AddEvidence(new("dispatcher release", "STA dispatcher", DebugResourceKind.Handle,
                dispatcher.ReleaseVerified, "The dispatcher owner reports its terminal worker state.", processId));
            if (ownedGenerationWorkspace is not null)
            {
                await AttemptAsync("generation cleanup", "debug generation", DebugResourceKind.Handle,
                    ownedGenerationWorkspace.DisposeAsync).ConfigureAwait(false);
                if (ownedGenerationWorkspace is IDebugResourceOwnerEvidence { CleanupOutcome: { } generationOutcome })
                {
                    failureCompletion.Merge(generationOutcome);
                }
                else
                {
                    failureCompletion.AddEvidence(new("generation cleanup", "debug generation", DebugResourceKind.Handle,
                        false, "The generation owner did not supply release evidence.",
                        RetainedPath: ownedGenerationWorkspace.GenerationWorkspacePath));
                }
            }
            try { observationStopping.Dispose(); }
            catch (Exception failure)
            {
                failureCompletion.AddFailure("observation cancellation", "session observers", DebugResourceKind.Handle, failure, processId);
            }
            lock (generationOwnershipGate)
            {
                foreach (var failure in lifetimeFailures)
                {
                    failureCompletion.AddFailure("session observation", "session observers", DebugResourceKind.Observation, failure, processId);
                }
                CleanupOutcome = failureCompletion.Complete();
            }
            if (CleanupOutcome.HasCleanupFailure)
            {
                throw new VbeDebugSessionLifetimeException(established.Cause, CleanupOutcome);
            }
            CleanupOutcome.Throw();
            return await processOwner.Completion.ConfigureAwait(false);
        }

        private sealed record SessionEnd(string Cause, Exception? Failure);

        private async Task<T> ObserveSetupOperationAsync<T>(
            IDebugModalPromptPhase? phase,
            Func<Task<T>> operation,
            CancellationToken cancellationToken)
        {
            try
            {
                return phase is null
                    ? await operation().ConfigureAwait(false)
                    : await phase.ObserveOperationAsync(operation, cancellationToken).ConfigureAwait(false);
            }
            catch when (terminalEnd.Task.IsCompletedSuccessfully && terminalEnd.Task.Result.Failure is not null)
            {
                // The raw process exit can win the phase race while the session is
                // completing the setup failure that caused that same exit.
                await completion.ConfigureAwait(false);
                throw;
            }
        }

        private async Task<T> InvokeSetupAsync<T>(
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            using var cancellationRegistration = cancellationToken.UnsafeRegister(
                static state =>
                {
                    var (session, token) = ((VbeDebugSession, CancellationToken))state!;
                    session.RequestCancellationTermination(token);
                },
                (this, cancellationToken));
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
                Exception cancellationFailure = ex is OperationCanceledException
                    or IDebugFailureEvidence { FailureOutcome.PrimaryFailure: OperationCanceledException } ? ex
                    : new OperationCanceledException("VBE debug setup was cancelled.", ex, cancellationToken);
                if (terminalEnd.Task.IsCompletedSuccessfully
                    && terminalEnd.Task.Result.Failure is OperationCanceledException retainedCancellation
                    && retainedCancellation.CancellationToken == cancellationToken)
                {
                    cancellationFailure = retainedCancellation;
                }
                if (!ReferenceEquals(cancellationFailure, ex) && ex is IDebugFailureEvidence retained)
                {
                    var cancelled = new DebugFailureCompletion(cancellationFailure);
                    cancelled.Merge(retained.FailureOutcome);
                    cancellationFailure = new DebugFailureException(cancelled.Complete());
                }
                await ThrowSetupFailureAsync(cancellationFailure, "setup cancellation").ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (ex is IDebugFailureEvidence)
            {
                if (((IDebugFailureEvidence)ex).FailureOutcome.PrimaryFailure is DebugSetupException setupError)
                {
                    RecordForegroundPermission(setupError);
                }
                await ThrowSetupFailureAsync(ex, "debug setup").ConfigureAwait(false);
                throw;
            }
            catch (DebugSetupException ex)
            {
                RecordForegroundPermission(ex);
                await ThrowSetupFailureAsync(ex, "debug setup").ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (
                ex is COMException or RuntimeBinderException or InvalidCastException or
                    ArgumentException or TargetParameterCountException)
            {
                var setupError = new DebugSetupException(
                    "Excel or the VBE rejected the generated workbook debug setup.",
                    ex);
                RecordForegroundPermission(setupError);
                await ThrowSetupFailureAsync(setupError, "debug setup").ConfigureAwait(false);
                throw setupError;
            }
        }

        private async Task ThrowSetupFailureAsync(Exception failure, string cause)
        {
            EstablishEnd(cause, failure);
            await completion.ConfigureAwait(false);
        }

        private void RequestCancellationTermination(CancellationToken cancellationToken)
        {
            lock (generationOwnershipGate)
            {
                if (!terminalEnd.Task.IsCompleted)
                {
                    var failure = new OperationCanceledException("VBE debug setup was cancelled.", cancellationToken);
                    lifetimeFailures.Add(failure);
                    terminalEnd.TrySetResult(new SessionEnd("setup cancellation", failure));
                }
            }
            _ = TerminateAfterCancellationAsync();
        }

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
            Exception? primaryFailure = null;
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
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                ReleaseComScope(primaryFailure, ("VBE", vbeObject));
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

        private void SetNativeBreakpoints(
            IReadOnlyList<VbeBreakpoint> breakpoints,
            bool execute = true)
        {
            if (breakpoints.Count == 0)
            {
                return;
            }

            object? projectObject = null;
            object? componentsObject = null;
            var verifiedModules = new Dictionary<string, VerifiedCodeModule>(
                StringComparer.OrdinalIgnoreCase);
            Exception? primaryFailure = null;
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
                        VbeNativeCommandContract.ToggleBreakpointCommandId,
                        "Toggle Breakpoint",
                        "breakpoint",
                        CreateDisabledBreakpointMessage(breakpoint),
                        execute: execute);
                }
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                ReleaseComScope(primaryFailure,
                    verifiedModules.SelectMany(pair => new (string Resource, object? Value)[]
                    {
                        ($"code module {pair.Key}", pair.Value.CodeModule),
                        ($"component {pair.Key}", pair.Value.Component)
                    }).Concat(new (string Resource, object? Value)[]
                    {
                        ("VBA components", componentsObject),
                        ("VBA project", projectObject)
                    }).ToArray());
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
                if (string.IsNullOrEmpty(sourceMap.ModuleName)
                    || !VbaIdentifier.IsIdentifier(sourceMap.ModuleName)
                    || sourceMap.ModuleName.EnumerateRunes().Take(32).Count() > 31)
                {
                    throw new DebugSetupException(
                        "A native VBE breakpoint source map has no valid module identity.");
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

        private VerifiedCodeModule VerifyCodeModule(
            dynamic components,
            VbeCodeModuleSourceMap sourceMap)
        {
            object? componentObject = null;
            object? codeModuleObject = null;
            var succeeded = false;
            Exception? primaryFailure = null;
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
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                if (!succeeded)
                {
                    ReleaseComScope(primaryFailure,
                        ($"code module {sourceMap.ModuleName}", codeModuleObject),
                        ($"component {sourceMap.ModuleName}", componentObject));
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
            => $"Invalid breakpoint at '{breakpoint.Source.SourceUri}:{breakpoint.Source.EditorLine + 1}' " +
                $"mapped to '{breakpoint.ModuleName}:{breakpoint.VbideLine}': the native VBE Toggle Breakpoint " +
                "command is disabled in the actual generated workbook compilation context, so the mapped " +
                "line is inactive or non-executable. The breakpoint was not relocated.";

        private void RunTarget(DebugTargetProcedure target)
            => ExecuteTargetCommand(
                target,
                VbeDesignMode,
                "target code",
                beforeExecute: () =>
                {
                    dynamic excel = excelObject;
                    excel.EnableEvents = true;
                });

        private void ExecuteTargetCommand(
            DebugTargetProcedure target,
            int expectedProjectMode,
            string contextName,
            Action? beforeExecute,
            bool execute = true)
        {
            object? projectObject = null;
            object? componentsObject = null;
            object? componentObject = null;
            object? codeModuleObject = null;
            Exception? primaryFailure = null;
            try
            {
                dynamic workbook = GetOpenedWorkbook();
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                EnsureProjectMode(project, expectedProjectMode);

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
                    VbeNativeCommandContract.RunOrContinueCommandId,
                    "Run Sub/UserForm",
                    contextName,
                    beforeExecute: beforeExecute,
                    execute: execute);
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                ReleaseComScope(primaryFailure,
                    ($"code module {target.ModuleName}", codeModuleObject),
                    ($"component {target.ModuleName}", componentObject),
                    ("VBA components", componentsObject), ("VBA project", projectObject));
            }
        }

        private async Task WaitForProbeStateWithTimeoutAsync(
            Func<DebugProbeState, bool> predicate,
            string timeoutMessage,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await WaitForProbeStateUntilCanceledAsync(
                    predicate,
                    timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested &&
                timeoutSource.IsCancellationRequested)
            {
                throw new DebugSetupException(
                    $"{timeoutMessage} within {timeout.TotalSeconds:0.###} seconds.",
                    ex);
            }
        }

        private async Task WaitForProbeStateUntilCanceledAsync(
            Func<DebugProbeState, bool> predicate,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var state = await InvokeSetupAsync(
                        ReadDebugProbeState,
                        cancellationToken).ConfigureAwait(false);
                    if (predicate(state))
                    {
                        return;
                    }

                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                RequestCancellationTermination(cancellationToken);
                throw;
            }
        }

        private DebugProbeState ReadDebugProbeState()
        {
            object? projectObject = null;
            object? worksheetsObject = null;
            object? worksheetObject = null;
            object? rangeObject = null;
            Exception? primaryFailure = null;
            try
            {
                dynamic workbook = GetOpenedWorkbook();
                projectObject = workbook.VBProject;
                dynamic project = projectObject;
                worksheetsObject = workbook.Worksheets;
                dynamic worksheets = worksheetsObject;
                worksheetObject = worksheets.Item(1);
                dynamic worksheet = worksheetObject;
                rangeObject = worksheet.Range("A1");
                dynamic range = rangeObject;
                return new DebugProbeState(
                    (int)project.Mode,
                    Convert.ToString(range.Value2));
            }
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                ReleaseComScope(primaryFailure,
                    ("Doctor probe range", rangeObject),
                    ("Doctor probe worksheet", worksheetObject),
                    ("Doctor probe worksheets", worksheetsObject),
                    ("VBA project", projectObject));
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
            Action? beforeExecute = null,
            bool execute = true)
        {
            object? codePaneObject = null;
            object? activeCodePaneObject = null;
            object? vbeObject = null;
            object? mainWindowObject = null;
            object? codeWindowObject = null;
            object? commandBarsObject = null;
            object? commandControlObject = null;
            Exception? primaryFailure = null;
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

                if (!execute)
                {
                    return;
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
            catch (Exception failure)
            {
                primaryFailure = failure;
                throw;
            }
            finally
            {
                ReleaseComScope(primaryFailure,
                    ($"native {commandName} command", commandControlObject),
                    ("VBE command bars", commandBarsObject),
                    ("VBE code window", codeWindowObject),
                    ("VBE main window", mainWindowObject),
                    ("VBE", vbeObject),
                    ("active VBE code pane", activeCodePaneObject),
                    ("VBE code pane", codePaneObject));
            }
        }

        private void ReleaseComScope(
            Exception? primaryFailure,
            params (string Resource, object? Value)[] resources)
        {
            if (primaryFailure is OperationCanceledException
                or IDebugFailureEvidence { FailureOutcome.PrimaryFailure: OperationCanceledException })
            {
                EstablishEnd("setup cancellation", primaryFailure);
            }
            try
            {
                ComObjectReleaser.ReleaseScope(primaryFailure, processId, null, releaseComObject, resources);
            }
            catch (Exception failure)
            {
                EstablishEnd("debug setup", failure);
                throw;
            }
        }
        private object GetOpenedWorkbook()
            => workbookObject ?? throw new DebugSetupException(
                "The generated debug workbook has not been opened.");

        private static void EnsureDesignMode(dynamic project)
            => EnsureProjectMode(project, VbeDesignMode);

        private static void EnsureProjectMode(dynamic project, int expectedMode)
        {
            if ((int)project.Mode == expectedMode)
            {
                return;
            }

            throw new DebugSetupException(expectedMode == VbeDesignMode
                ? "The generated workbook VBA project is not in design mode."
                : "The generated workbook VBA project is not in break mode.");
        }

        private sealed record VerifiedCodeModule(object Component, object CodeModule);

        private sealed record DebugProbeState(int ProjectMode, string? CompletionMarker);

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
    private readonly DebugNativeHandleRelease handleRelease;
    private readonly object waitGate = new();
    private readonly List<Task> exitWaits = [];
    private bool releasing;

    public SystemDebugOwnedProcess(Process process)
    {
        this.process = process;
        // Keep metadata queries on the process handle that this owner must release.
        handleRelease = new DebugNativeHandleRelease(process.SafeHandle);
        Id = process.Id;
        StartTime = process.StartTime;
        Architecture = WindowsExcelProcessArchitecture.Read(process.Handle);
    }

    public int Id { get; }

    internal nint Handle => process.Handle;

    internal StreamReader StandardOutput => process.StandardOutput;

    internal StreamReader StandardError => process.StandardError;

    public DebugExcelProcessArchitecture Architecture { get; }

    public DateTime StartTime { get; }

    public bool HasExited => process.HasExited;

    public bool HandleReleaseVerified => handleRelease.IsVerified;

    public int ExitCode => process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        lock (waitGate)
        {
            ObjectDisposedException.ThrowIf(releasing, this);
            exitWaits.RemoveAll(wait => wait.IsCompleted);
            var wait = process.WaitForExitAsync(cancellationToken);
            exitWaits.Add(wait);
            return wait;
        }
    }

    public void Kill()
    {
        if (process.Id != Id || process.StartTime != StartTime)
        {
            throw new InvalidOperationException(
                "The owned Excel process identity changed before termination.");
        }

        process.Kill(entireProcessTree: false);
    }

    public void Dispose()
    {
        var completion = new DebugFailureCompletion();
        bool waitsSettled;
        lock (waitGate)
        {
            releasing = true;
            waitsSettled = exitWaits.All(wait => wait.IsCompleted);
        }
        // Never invalidate a numeric handle while an asynchronous OS wait may use it.
        // Normal SafeHandle disposal can defer its release; that is not positive proof.
        try { if (waitsSettled) { handleRelease.Release(); } }
        catch (Exception failure)
        {
            completion.AddFailure("process handle", "Excel process", DebugResourceKind.Handle, failure, Id);
        }
        try { process.Dispose(); }
        catch (Exception failure)
        {
            completion.AddFailure("process managed state", "Excel process", DebugResourceKind.Handle, failure, Id);
        }
        completion.AddEvidence(new("process handle", "Excel process", DebugResourceKind.Handle,
            handleRelease.IsVerified, waitsSettled ? "The native handle owner reports CloseHandle completion."
                : "An exit wait remains active; native handle release cannot be proved.", Id));
        completion.Complete().Throw();
    }
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
