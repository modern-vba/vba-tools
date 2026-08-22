using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IExcelComWorkbookGenerationLifecycle
{
    object Start(
        OwnedExcelTerminationController terminationController,
        CancellationToken cancellationToken);

    IWorkbookBuildSession Open(object host, string workbookPath);

    void DisposeHost(object host, TimeSpan cleanupGrace);

    void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace);
}

public sealed partial class ExcelComWorkbookBuildAutomation : IWorkbookGenerationAutomation
{
    private static readonly TimeSpan ForcedTerminationObservationAllowance =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DispatcherAbandonmentObservation =
        TimeSpan.FromMilliseconds(100);
    private readonly IStaComDispatcherFactory generationDispatcherFactory;
    private readonly IExcelComWorkbookGenerationLifecycle generationLifecycle;

    /// <summary>
    /// Creates the production Excel COM workbook automation adapter.
    /// </summary>
    public ExcelComWorkbookBuildAutomation()
        : this(
            new StaComDispatcherFactory(),
            new ExcelComWorkbookGenerationLifecycle())
    {
    }

    internal ExcelComWorkbookBuildAutomation(
        IStaComDispatcherFactory generationDispatcherFactory,
        IExcelComWorkbookGenerationLifecycle generationLifecycle)
    {
        this.generationDispatcherFactory = generationDispatcherFactory;
        this.generationLifecycle = generationLifecycle;
    }

    /// <inheritdoc />
    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<IWorkbookGenerationSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(operation);

        using var terminationController = new OwnedExcelTerminationController();
        var dispatcher = generationDispatcherFactory.Create();
        var stageExecutor = new WorkbookAutomationStageExecutor(
            () => terminationController.HasAttachedProcessExited,
            terminationController.RequestForcedTermination,
            getOwnedProcessCompletion: () =>
                terminationController.AttachedProcessCompletion);
        object? host = null;
        IWorkbookBuildSession? buildSession = null;
        BoundedWorkbookGenerationSession? generationSession = null;
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
                        host = generationLifecycle.Start(
                            terminationController,
                            stageCancellation);
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            var openStage = new WorkbookAutomationStage(
                WorkbookAutomationStageKind.WorkbookOpen,
                Path.GetFileName(workbookPath));
            lifecycleStage = openStage;
            await stageExecutor.ExecuteAsync(
                openStage,
                timeouts.WorkbookOpen,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    () =>
                    {
                        buildSession = generationLifecycle.Open(host!, workbookPath);
                        host = null;
                        return true;
                    },
                    stageCancellation)).ConfigureAwait(false);

            generationSession = new BoundedWorkbookGenerationSession(
                dispatcher,
                stageExecutor,
                buildSession!,
                Path.GetFileName(workbookPath),
                timeouts);
            result = await operation(generationSession, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            operationError = NormalizeOperationError(
                ex,
                cancellationToken,
                generationSession?.LastStage ?? lifecycleStage);
        }

        if (operationError is null && terminationController.HasAttachedProcessExited)
        {
            operationError = new WorkbookAutomationProcessLostException(
                generationSession?.LastStage ??
                lifecycleStage ??
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup));
        }

        var cleanupError = await CleanupAsync(
            dispatcher,
            terminationController,
            host,
            buildSession,
            timeouts.ProcessCleanup,
            stageExecutor,
            operationError is WorkbookAutomationProcessLostException).ConfigureAwait(false);
        var dispatcherError = await DisposeDispatcherAsync(
            dispatcher,
            stageExecutor.HasAbandonedOperation).ConfigureAwait(false);
        if (dispatcherError is not null)
        {
            cleanupError = cleanupError is null
                ? dispatcherError
                : new WorkbookAutomationCleanupException(
                    "Workbook automation cleanup and STA dispatcher disposal both failed.",
                    new AggregateException(cleanupError, dispatcherError));
        }

        if (cleanupError is null &&
            operationError is null &&
            cancellationToken.IsCancellationRequested)
        {
            operationError = new WorkbookAutomationCanceledException(
                new WorkbookAutomationStage(
                    WorkbookAutomationStageKind.ProcessCleanup),
                cancellationToken);
        }

        if (cleanupError is not null)
        {
            if (operationError is null)
            {
                ExceptionDispatchInfo.Capture(cleanupError).Throw();
            }

            throw new WorkbookAutomationCleanupException(
                $"{operationError!.Message} The owned Excel process could not be verified as released during process cleanup.",
                new AggregateException(operationError, cleanupError));
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        return result!;
    }

    private async Task<Exception?> CleanupAsync(
        IStaComDispatcher dispatcher,
        OwnedExcelTerminationController terminationController,
        object? host,
        IWorkbookBuildSession? session,
        TimeSpan cleanupGrace,
        WorkbookAutomationStageExecutor stageExecutor,
        bool preserveVerifiedProcessLoss)
    {
        if (stageExecutor.HasAbandonedOperation)
        {
            return await CleanupOwnedProcessOnlyAsync(
                terminationController,
                cleanupGrace).ConfigureAwait(false);
        }

        if (session is null && host is null)
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
                    if (session is not null)
                    {
                        generationLifecycle.DisposeSession(session, cleanupGrace);
                    }
                    else
                    {
                        generationLifecycle.DisposeHost(host!, cleanupGrace);
                    }

                    return true;
                },
                CancellationToken.None);
            var completed = await Task.WhenAny(
                cleanupTask,
                Task.Delay(cleanupGrace + ForcedTerminationObservationAllowance))
                .ConfigureAwait(false);
            if (completed != cleanupTask)
            {
                stageExecutor.MarkOperationAbandoned();
                WorkbookAutomationStageExecutor.ObserveFault(cleanupTask);
                cooperativeCleanupError = new WorkbookAutomationTimeoutException(
                    new WorkbookAutomationStage(
                        WorkbookAutomationStageKind.ProcessCleanup),
                    cleanupGrace);
            }
            else
            {
                await cleanupTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            cooperativeCleanupError = ex is WorkbookAutomationCleanupException
                ? ex
                : new WorkbookAutomationCleanupException(
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

        if (ownershipCleanupError is null && preserveVerifiedProcessLoss)
        {
            // An exited COM server commonly rejects Close/Quit. Exact process-exit proof is
            // sufficient cleanup evidence, so preserve the stage-specific process-loss result.
            return null;
        }

        return ownershipCleanupError is null
            ? cooperativeCleanupError
            : new WorkbookAutomationCleanupException(
                "Cooperative cleanup and exact owned-process cleanup both failed.",
                new AggregateException(cooperativeCleanupError, ownershipCleanupError));
    }

    private static async Task<Exception?> CleanupOwnedProcessOnlyAsync(
        OwnedExcelTerminationController terminationController,
        TimeSpan cleanupGrace)
    {
        try
        {
            terminationController.RequestForcedTermination(cleanupGrace);
            await terminationController.ObserveCleanupWithinAsync(
                ForcedTerminationObservationAllowance).ConfigureAwait(false);

            return null;
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
        bool allowAbandonment)
    {
        try
        {
            var disposalTask = dispatcher.DisposeAsync().AsTask();
            if (!allowAbandonment)
            {
                await disposalTask.ConfigureAwait(false);
                return null;
            }

            var completed = await Task.WhenAny(
                disposalTask,
                Task.Delay(DispatcherAbandonmentObservation)).ConfigureAwait(false);
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
            }

            return null;
        }
        catch (Exception ex)
        {
            return new WorkbookAutomationCleanupException(
                "The Excel STA dispatcher could not be disposed cleanly.",
                ex);
        }
    }

    private static Exception NormalizeOperationError(
        Exception error,
        CancellationToken cancellationToken,
        WorkbookAutomationStage? lastStage)
    {
        var stage = lastStage ?? new WorkbookAutomationStage(
            WorkbookAutomationStageKind.ExcelStartup);
        var startFailure = FindOwnedSessionStartFailure(error);
        if (startFailure is not null && !startFailure.CleanupVerified)
        {
            var cleanupEvidence = startFailure.CleanupException ??
                new InvalidOperationException(
                    "The owned Excel process cleanup could not be verified.");
            return new WorkbookAutomationCleanupException(
                $"Workbook automation failed during {stage.Description}, and owned Excel process cleanup could not be verified.",
                new AggregateException(startFailure.StartException, cleanupEvidence));
        }

        if (error is WorkbookAutomationTimeoutException or
            WorkbookAutomationCanceledException or
            WorkbookAutomationProcessLostException or
            WorkbookAutomationCleanupException)
        {
            return error;
        }

        if (error is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return new WorkbookAutomationCanceledException(stage, cancellationToken, error);
        }

        return new InvalidOperationException(
            $"Workbook automation failed during {stage.Description}: {error.Message}",
            error);
    }

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

    private sealed class BoundedWorkbookGenerationSession(
        IStaComDispatcher dispatcher,
        WorkbookAutomationStageExecutor stageExecutor,
        IWorkbookBuildSession session,
        string workbookName,
        WorkbookAutomationTimeouts timeouts) : IWorkbookGenerationSession
    {
        public WorkbookAutomationStage? LastStage { get; private set; }

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(
            CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleRemoval),
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

        public Task VerifyAsync(CancellationToken cancellationToken)
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

        private Task<T> ExecuteAsync<T>(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Func<T> operation)
        {
            LastStage = stage;
            return stageExecutor.ExecuteAsync(
                stage,
                timeout,
                timeouts.ProcessCleanup,
                cancellationToken,
                stageCancellation => dispatcher.InvokeAsync(
                    operation,
                    stageCancellation));
        }

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
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController,
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
