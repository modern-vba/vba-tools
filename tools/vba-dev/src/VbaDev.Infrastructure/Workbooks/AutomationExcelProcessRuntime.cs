using VbaDev.App.Workbooks;
using VbaDev.App.Testing;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelComWorkbookGenerationLifecycle
{
    object Start(
        OwnedExcelTerminationController terminationController,
        bool enableAutomationSecurityLow,
        CancellationToken cancellationToken);

    IWorkbookBuildSession Open(object host, string workbookPath);

    void DisposeHost(object host, TimeSpan cleanupGrace);

    void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace);
}

/// <summary>
/// Owns the native process, private desktop, STA, deadlines, and cleanup for
/// bounded workbook and reference scenarios without deciding workflow results.
/// </summary>
internal sealed class AutomationExcelProcessRuntime
{
    private static readonly TimeSpan DispatcherRetirementObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory generationDispatcherFactory;
    private readonly IExcelComWorkbookGenerationLifecycle generationLifecycle;

    /// <summary>
    /// Creates the production runtime over the native Windows ownership boundary.
    /// </summary>
    internal AutomationExcelProcessRuntime()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComWorkbookGenerationLifecycle())
    {
    }

    internal AutomationExcelProcessRuntime(
        IStaComDispatcherFactory dispatcherFactory)
        : this(dispatcherFactory, new ExcelComWorkbookGenerationLifecycle())
    {
    }

    internal AutomationExcelProcessRuntime(
        IStaComDispatcherFactory generationDispatcherFactory,
        IExcelComWorkbookGenerationLifecycle generationLifecycle)
    {
        this.generationDispatcherFactory = generationDispatcherFactory;
        this.generationLifecycle = generationLifecycle;
    }

    internal Task<AutomationExcelProcessOutcome<TResult>> RunReferenceProbeAsync<TResult>(
        IExcelComVbaProjectReferenceProbeLifecycle lifecycle,
        WorkbookAutomationTimeouts timeouts,
        Func<AutomationExcelProcessSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
        => RunProcessAsync(
            timeouts, lifecycle.Start, lifecycle.DisposeHost, operation, cancellationToken,
            ProcessScenario.ReferenceProbe);

    /// <summary>
    /// Executes one workbook scenario and returns its terminal release evidence.
    /// </summary>
    internal Task<AutomationExcelProcessOutcome<TResult>> RunWorkbookAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
        => RunCoreAsync(
            workbookPath,
            timeouts,
            operation,
            enableAutomationSecurityLow: false,
            cancellationToken);

    private async Task<AutomationExcelProcessOutcome<TResult>> RunCoreAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        bool enableAutomationSecurityLow,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(operation);

        IWorkbookBuildSession? buildSession = null;
        return await RunProcessAsync(
            timeouts,
            (controller, token) => generationLifecycle.Start(
                controller, enableAutomationSecurityLow, token),
            (host, grace) =>
            {
                if (buildSession is null)
                {
                    generationLifecycle.DisposeHost(host, grace);
                }
                else
                {
                    generationLifecycle.DisposeSession(buildSession, grace);
                }
            },
            async (execution, token) =>
            {
                await execution.ExecuteAsync(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.WorkbookOpen,
                        Path.GetFileName(workbookPath)),
                    timeouts.WorkbookOpen,
                    token,
                    host =>
                    {
                        buildSession = generationLifecycle.Open(host, workbookPath);
                        return true;
                    }).ConfigureAwait(false);
                var session = new BoundedWorkbookGenerationSession(
                    execution, buildSession!, Path.GetFileName(workbookPath), timeouts);
                return await operation(session, token).ConfigureAwait(false);
            },
            cancellationToken,
            ProcessScenario.Workbook).ConfigureAwait(false);
    }

    private async Task<AutomationExcelProcessOutcome<TResult>> RunProcessAsync<TResult>(
        WorkbookAutomationTimeouts timeouts,
        Func<OwnedExcelTerminationController, CancellationToken, object> start,
        Action<object, TimeSpan> disposeHost,
        Func<AutomationExcelProcessSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken,
        ProcessScenario scenario)
    {
        using var terminationController = new OwnedExcelTerminationController();
        IStaComDispatcher dispatcher;
        try
        {
            dispatcher = generationDispatcherFactory.Create();
        }
        catch (Exception exception)
        {
            var stage = new WorkbookAutomationStage(WorkbookAutomationStageKind.ExcelStartup);
            return new AutomationExcelProcessOutcome<TResult>(
                default,
                new AutomationExcelProcessEvidence(
                    stage,
                    NormalizeOperationError(exception, cancellationToken, stage, null, scenario),
                    CleanupFailure: null,
                    DispatcherFailure: null,
                    CancellationRequestedDuringCleanup: false,
                    ProcessReleaseVerified: true,
                    DispatcherRetired: true,
                    IsolationDiagnostics: null,
                    DispatcherCreated: false));
        }

        var stageExecutor = new WorkbookAutomationStageExecutor(
            () => terminationController.HasAttachedProcessExited,
            terminationController.RequestForcedTermination,
            getOwnedProcessCompletion: () =>
                terminationController.AttachedProcessCompletion,
            captureAutomationStage: terminationController.CaptureAutomationStage,
            describeIsolationEvidence: terminationController.DescribeIsolationEvidence);
        object? host = null;
        AutomationExcelProcessSession? processSession = null;
        WorkbookAutomationStage? lifecycleStage = null;
        TResult? result = default;
        Exception? operationError = null;

        try
        {
            var startupStage = new WorkbookAutomationStage(
                WorkbookAutomationStageKind.ExcelStartup);
            lifecycleStage = startupStage;
            await stageExecutor.ExecuteAsync(
                startupStage,
                timeouts.ExcelStartup,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        host = start(terminationController, stageCancellation);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            processSession = new AutomationExcelProcessSession(
                dispatcher, stageExecutor, host!, timeouts.ProcessCleanup,
                () => terminationController.HasAttachedProcessExited);
            result = await operation(processSession, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operationError = NormalizeOperationError(
                ex,
                cancellationToken,
                processSession?.LastStage ?? lifecycleStage,
                terminationController.DescribeIsolationEvidence(),
                scenario);
        }

        if (operationError is null
            && terminationController.HasAttachedProcessExited
            && !(scenario == ProcessScenario.ReferenceProbe
                && processSession?.HasReportedTerminalFailure == true))
        {
            operationError = new WorkbookAutomationProcessLostException(
                processSession?.LastStage ??
                lifecycleStage ??
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                isolationDiagnostics:
                    terminationController.DescribeIsolationEvidence());
        }

        processSession?.Retire();
        var cancellationBeforeCleanup = cancellationToken.IsCancellationRequested;
        var cleanupError = await CleanupAsync(
            dispatcher,
            terminationController,
            host,
            disposeHost,
            timeouts.ProcessCleanup,
            stageExecutor,
            operationError,
            scenario).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            timeouts.ProcessCleanup).ConfigureAwait(false);
        return new AutomationExcelProcessOutcome<TResult>(
            result,
            new AutomationExcelProcessEvidence(
                processSession?.LastStage ?? lifecycleStage,
                operationError,
                cleanupError,
                dispatcherError,
                !cancellationBeforeCleanup && cancellationToken.IsCancellationRequested,
                (operationError is null || !ContainsReleaseProofFailure(operationError))
                    && (cleanupError is null || !ContainsReleaseProofFailure(cleanupError)),
                DispatcherRetired: dispatcherError is null,
                terminationController.DescribeIsolationEvidence()));
    }

    internal Task<AutomationExcelProcessOutcome<IReadOnlyList<WorkbookTestResultRow>>> RunWorkbookTestsAsync(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        TimeSpan executionTimeout,
        WorkbookTestSelector selector,
        CancellationToken cancellationToken)
        => RunCoreAsync(
            workbookPath,
            timeouts,
            (session, operationCancellationToken) =>
                ((BoundedWorkbookGenerationSession)session).RunTestsAsync(
                    selector,
                    executionTimeout,
                    operationCancellationToken),
            enableAutomationSecurityLow: true,
            cancellationToken);

    private static async Task<Exception?> CleanupAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        object? host,
        Action<object, TimeSpan> disposeHost,
        TimeSpan cleanupGrace,
        WorkbookAutomationStageExecutor stageExecutor,
        Exception? terminalFailureToPreserve,
        ProcessScenario scenario)
    {
        var cleanupStage = new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ProcessCleanup);
        terminationController.CaptureAutomationStage(cleanupStage);
        if (stageExecutor.HasAbandonedOperation
            || (scenario == ProcessScenario.ReferenceProbe
                && terminationController.HasAttachedProcessExited))
        {
            return await CleanupOwnedProcessOnlyAsync(
                terminationController,
                cleanupGrace).ConfigureAwait(false);
        }

        if (host is null)
        {
            return await CleanupOwnedProcessOnlyAsync(
                terminationController,
                cleanupGrace).ConfigureAwait(false);
        }

        terminationController.RequestForcedTermination(cleanupGrace);
        Exception? cooperativeCleanupError = null;
        try
        {
            var cleanupTask = dispatcher.InvokeAsync(
                () =>
                {
                    disposeHost(host, cleanupGrace);
                    return true;
                },
                CancellationToken.None);
            var completed = await Task.WhenAny(
                cleanupTask,
                Task.Delay(
                    cleanupGrace +
                    PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance))
                .ConfigureAwait(false);
            if (completed != cleanupTask)
            {
                stageExecutor.MarkOperationAbandoned();
                WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
                cooperativeCleanupError = new WorkbookAutomationTimeoutException(
                    cleanupStage,
                    cleanupGrace,
                    isolationDiagnostics:
                        terminationController.DescribeIsolationEvidence());
            }
            else
            {
                await cleanupTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            cooperativeCleanupError = scenario == ProcessScenario.ReferenceProbe
                ? ex
                : new WorkbookAutomationReleasedProcessCleanupException(
                    "Cooperative workbook automation cleanup failed.",
                    ex);
        }

        var ownershipCleanupError = await CleanupOwnedProcessOnlyAsync(
            terminationController,
            cleanupGrace).ConfigureAwait(false);
        if (cooperativeCleanupError is null)
        {
            return ownershipCleanupError;
        }

        if (ownershipCleanupError is null &&
            CanPreserveVerifiedTerminalFailure(
                terminalFailureToPreserve,
                cooperativeCleanupError))
        {
            // A COM server terminated by timeout, cancellation, or unexpected process loss
            // commonly rejects Close/Quit. Exact process-tree release is sufficient cleanup
            // evidence, so preserve the stage-specific terminal result.
            return null;
        }

        return ownershipCleanupError is null
            ? cooperativeCleanupError
            : ContainsReleaseProofFailure(ownershipCleanupError)
                ? new WorkbookAutomationCleanupException(
                    "Cooperative cleanup and exact owned-process cleanup both failed.",
                    new AggregateException(cooperativeCleanupError, ownershipCleanupError))
                : new WorkbookAutomationReleasedProcessCleanupException(
                    "Cooperative cleanup and exact owned-process cleanup both failed after exact owned-process release was verified.",
                    new AggregateException(cooperativeCleanupError, ownershipCleanupError));
    }

    private static bool CanPreserveVerifiedTerminalFailure(
        Exception? terminalFailure,
        Exception cleanupError)
        => ReleasedAutomationCleanupPolicy.CanPreservePrimaryFailure(
            terminalFailure,
            cleanupError);

    private static async Task<Exception?> CleanupOwnedProcessOnlyAsync(
        OwnedExcelTerminationController terminationController,
        TimeSpan cleanupGrace)
    {
        try
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            await terminationController.ObserveCleanupWithinAsync(
                PrivateDesktopOwnedExcelProcessControl.ForcedCleanupObservationAllowance)
                .ConfigureAwait(false);

            return null;
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            return new WorkbookAutomationCleanupException(
                "The owned Excel process could not be verified as released during process cleanup.",
                ex);
        }
    }

    private static async Task<Exception?> DisposeDispatcherAsync(
        IStaComDispatcher dispatcher,
        TimeSpan cleanupGrace)
    {
        try
        {
            var disposalTask = dispatcher.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(
                disposalTask,
                Task.Delay(cleanupGrace + DispatcherRetirementObservation)).ConfigureAwait(false);
            if (completed == disposalTask)
            {
                await disposalTask.ConfigureAwait(false);
            }
            else
            {
                _ = disposalTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return new WorkbookAutomationReleasedProcessCleanupException(
                    "The Excel STA dispatcher did not retire within its bounded cleanup observation period.");
            }

            return null;
        }
        catch (Exception ex)
        {
            return new WorkbookAutomationReleasedProcessCleanupException(
                "The Excel STA dispatcher could not be disposed cleanly.",
                ex);
        }
    }

    private static Exception NormalizeOperationError(
        Exception error,
        CancellationToken cancellationToken,
        WorkbookAutomationStage? lastStage,
        string? isolationDiagnostics,
        ProcessScenario scenario)
    {
        var stage = lastStage ?? new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ExcelStartup);
        var startFailure = FindOwnedSessionStartFailure(error);
        if (startFailure is not null && !startFailure.CleanupVerified)
        {
            var cleanupEvidence = startFailure.CleanupException ??
                new InvalidOperationException(
                    "The owned Excel process cleanup could not be verified.");
            if (cleanupEvidence is WorkbookAutomationReleasedProcessCleanupException)
            {
                return new WorkbookAutomationReleasedProcessCleanupException(
                    $"Workbook automation failed during {stage.Description}, and automation cleanup or isolation also failed after owned Excel process release was verified.",
                    new AggregateException(
                        startFailure.StartException,
                        cleanupEvidence));
            }

            return new WorkbookAutomationCleanupException(
                $"Workbook automation failed during {stage.Description}, and owned Excel process cleanup could not be verified.",
                new AggregateException(startFailure.StartException, cleanupEvidence));
        }

        if (error is WorkbookAutomationTimeoutException or
            WorkbookAutomationCanceledException or
            WorkbookAutomationProcessLostException or
            WorkbookAutomationCleanupException or
            WorkbookAutomationReleasedProcessCleanupException)
        {
            return error;
        }

        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return new WorkbookAutomationCanceledException(
                stage,
                cancellationToken,
                error,
                isolationDiagnostics);
        }

        return scenario == ProcessScenario.ReferenceProbe ? error : new InvalidOperationException(
            $"Workbook automation failed during {stage.Description}: {error.Message}",
            error);
    }

    private static bool ContainsReleaseProofFailure(Exception error)
        => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error);

    private static IOwnedExcelSessionStartFailure? FindOwnedSessionStartFailure(
        Exception error)
    {
        if (error is IOwnedExcelSessionStartFailure startFailure)
        {
            return startFailure;
        }

        if (error is AggregateException aggregate)
        {
            foreach (var innerError in aggregate.InnerExceptions)
            {
                var nestedFailure = FindOwnedSessionStartFailure(innerError);
                if (nestedFailure is not null)
                {
                    return nestedFailure;
                }
            }
        }

        return error.InnerException is null
            ? null
            : FindOwnedSessionStartFailure(error.InnerException);
    }

    private enum ProcessScenario
    {
        Workbook,
        ReferenceProbe
    }

    internal sealed class AutomationExcelProcessSession(
        IStaComDispatcher dispatcher,
        WorkbookAutomationStageExecutor stageExecutor,
        object host,
        TimeSpan cleanupGrace,
        Func<bool> hasOwnedProcessExited)
    {
        private int retired;

        internal WorkbookAutomationStage? LastStage { get; private set; }

        internal bool HasAbandonedOperation => stageExecutor.HasAbandonedOperation;

        internal bool HasOwnedProcessExited => hasOwnedProcessExited();

        internal bool HasReportedTerminalFailure { get; private set; }

        internal void Retire() => Interlocked.Exchange(ref retired, 1);

        internal void ThrowIfRetired()
            => ObjectDisposedException.ThrowIf(Volatile.Read(ref retired) != 0, this);

        internal async Task<T> ExecuteAsync<T>(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<object, T> operation)
        {
            ThrowIfRetired();
            LastStage = stage;
            try
            {
                return await stageExecutor.ExecuteAsync(
                    stage,
                    timeout,
                    cleanupGrace,
                    cancellationToken,
                    stageCancellation => dispatcher.InvokeAsync(
                        () =>
                        {
                            ThrowIfRetired();
                            return operation(host);
                        },
                        stageCancellation)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WorkbookAutomationTimeoutException
                or WorkbookAutomationCanceledException or WorkbookAutomationProcessLostException)
            {
                HasReportedTerminalFailure = true;
                throw;
            }
        }
    }

    private sealed class BoundedWorkbookGenerationSession(
        AutomationExcelProcessSession execution,
        IWorkbookBuildSession session,
        string workbookName,
        WorkbookAutomationTimeouts timeouts) :
        IWorkbookGenerationSession
    {
        public WorkbookAutomationStage? LastStage => execution.LastStage;

        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleInspection),
                timeouts.ModuleImport,
                cancellationToken,
                session.GetProjectName);

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleInspection),
                timeouts.ModuleImport,
                cancellationToken,
                session.GetModules);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt),
                timeouts.ReferenceAttempt,
                cancellationToken,
                session.GetReferences);

        public Task<bool> RemoveReferenceAsync(
            string referenceName,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ReferenceAttempt,
                    referenceName),
                timeouts.ReferenceAttempt,
                cancellationToken,
                () => session.RemoveReference(referenceName));

        public Task AddReferenceAsync(
            ResolvedVbaProjectReference reference,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ReferenceAttempt,
                    reference.Name),
                timeouts.ReferenceAttempt,
                cancellationToken,
                () => session.AddReference(reference));

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName,
            ResolvedVbaProjectReference candidate,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ReferenceAttempt,
                    referenceName),
                timeouts.ReferenceAttempt,
                cancellationToken,
                () => session.TryResolveReference(referenceName, candidate));

        public Task RemoveModuleAsync(
            string moduleName,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ModuleRemoval,
                    moduleName),
                timeouts.ModuleImport,
                cancellationToken,
                () => session.RemoveModule(moduleName));

        public Task ImportModuleAsync(
            VbeImportSourceFile sourceFile,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ModuleImport,
                    sourceFile.FileName),
                timeouts.ModuleImport,
                cancellationToken,
                () => session.ImportModule(sourceFile));

        public Task ExportModuleAsync(
            string moduleName,
            string destinationPath,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ModuleExport,
                    moduleName),
                timeouts.ModuleImport,
                cancellationToken,
                () => session.ExportModule(moduleName, destinationPath));

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.Verification),
                timeouts.ModuleImport,
                cancellationToken,
                session.VerifyImportedModules);

        public Task SaveAsync(CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.WorkbookSave,
                    workbookName),
                timeouts.WorkbookSave,
                cancellationToken,
                session.Save);

        public Task<IReadOnlyList<WorkbookTestResultRow>> RunTestsAsync(
            WorkbookTestSelector selector,
            TimeSpan executionTimeout,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.TestExecution),
                executionTimeout,
                cancellationToken,
                () => ((IExcelComWorkbookTestSession)session).RunTests(selector));

        private Task<T> ExecuteAsync<T>(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<T> operation)
            => execution.ExecuteAsync(
                stage,
                timeout,
                cancellationToken,
                _ => operation());

        private Task ExecuteAsync(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action operation)
            => ExecuteAsync(
                stage,
                timeout,
                cancellationToken,
                () =>
                {
                    operation();
                    return true;
                });
    }

    private sealed class ExcelComWorkbookGenerationLifecycle
        : IExcelComWorkbookGenerationLifecycle
    {
        public object Start(
            OwnedExcelTerminationController terminationController,
            bool enableAutomationSecurityLow,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
                enableAutomationSecurityLow,
                cancellationToken);

        public IWorkbookBuildSession Open(object host, string workbookPath)
            => new ExcelComWorkbookBuildSession(
                ExcelComWorkbookSession.OpenOwnedForGeneration(
                    (ExcelComWorkbookSession.ExcelComHostObjects)host,
                    workbookPath));

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host,
                cleanupGrace);

        public void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace)
            => ((ExcelComWorkbookBuildSession)session).DisposeOwnedGeneration(cleanupGrace);
    }
}
