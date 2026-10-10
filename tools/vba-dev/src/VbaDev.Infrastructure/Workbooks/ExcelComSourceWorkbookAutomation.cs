using System.Runtime.ExceptionServices;
using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface ISourceWorkbookOpenLocator
{
    SourceWorkbookBorrowedBinding? TryAttach(string workbookPath);
}

/// <summary>One COM binding to a workbook already open in a user-owned Excel process.</summary>
internal sealed class SourceWorkbookBorrowedBinding(
    IWorkbookBuildSession workbook,
    Func<bool> isSaved,
    Action release,
    Func<bool>? isStillSelected = null,
    int? processId = null,
    long? processStartUtcTicks = null) : IDisposable
{
    private int disposed;

    internal IWorkbookBuildSession Workbook { get; } = workbook;

    internal int? ProcessId { get; } = processId;

    internal long? ProcessStartUtcTicks { get; } = processStartUtcTicks;

    internal bool IsSaved() => isSaved();

    internal bool IsStillSelected() => isStillSelected?.Invoke() ?? true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) release();
    }
}

/// <summary>
/// Chooses an exact already-open source workbook or opens the selected file in an
/// invocation-owned hidden Excel process. Borrowed Excel is never closed or killed.
/// </summary>
public sealed class ExcelComSourceWorkbookAutomation : ISourceWorkbookAutomation
{
    private readonly IStaComDispatcherFactory dispatcherFactory;
    private readonly ISourceWorkbookOpenLocator openLocator;
    private readonly ISourceWorkbookAutomation closedAutomation;
    private readonly bool validateSelectedWorkbookBeforeOperations;

    public ExcelComSourceWorkbookAutomation()
        : this(new StaComDispatcherFactory(), new WindowsSourceWorkbookOpenLocator(),
            new ExcelComClosedSourceWorkbookAutomation())
    {
    }

    internal ExcelComSourceWorkbookAutomation(
        IStaComDispatcherFactory dispatcherFactory,
        ISourceWorkbookOpenLocator openLocator,
        ISourceWorkbookAutomation closedAutomation,
        bool validateSelectedWorkbookBeforeOperations = false)
    {
        this.dispatcherFactory = dispatcherFactory;
        this.openLocator = openLocator;
        this.closedAutomation = closedAutomation;
        this.validateSelectedWorkbookBeforeOperations = validateSelectedWorkbookBeforeOperations;
    }

    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = dispatcherFactory.Create();
        using var lookupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<SourceWorkbookBorrowedBinding?>? lookup = null;
        SourceWorkbookBorrowedBinding? binding = null;
        TResult? result = default;
        Exception? operationError = null;
        try
        {
            lookup = dispatcher.InvokeAsync(() => openLocator.TryAttach(workbookPath), lookupCancellation.Token);
            binding = await WaitForStageAsync(
                    lookup,
                    new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookOpen,
                        Path.GetFileName(workbookPath)),
                    timeouts.WorkbookOpen,
                    cancellationToken)
                .ConfigureAwait(false);
            if (binding is not null)
            {
                result = await operation(
                        new BorrowedSourceWorkbookSession(dispatcher, binding, timeouts,
                            validateSelectedWorkbookBeforeOperations),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            lookupCancellation.Cancel();
            operationError = error;
        }

        Exception? cleanupError = null;
        try
        {
            // Queue COM release on the original STA even when a timed-out COM call
            // is still running. The user-owned Excel process is never terminated.
            var release = dispatcher.InvokeAsync(() =>
            {
                if (lookup?.IsCompletedSuccessfully == true)
                {
                    lookup.Result?.Dispose();
                }

                return true;
            }, CancellationToken.None);
            var retirement = dispatcher.DisposeAsync().AsTask();
            await Task.WhenAll(release, retirement)
                .WaitAsync(timeouts.ProcessCleanup)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var cleanupFailure = new WorkbookAutomationCleanupException(
                "Borrowed source-workbook COM release or STA retirement could not be proved; " +
                "the user's Excel process was left running.", error);
            ((IWorkbookAutomationLifecycleFailure)cleanupFailure).LifecycleEvidence =
                new WorkbookAutomationLifecycleEvidence(
                    Stage: new WorkbookAutomationStage(WorkbookAutomationStageKind.ProcessCleanup),
                    ProcessReleaseProven: false,
                    DispatcherRetired: false,
                    CancellationObserved: cancellationToken.IsCancellationRequested);
            cleanupError = cleanupFailure;
        }

        if (cleanupError is not null)
        {
            throw operationError is null
                ? cleanupError
                : new InvalidOperationException(
                    $"{operationError.Message} {cleanupError.Message}",
                    new AggregateException(operationError, cleanupError));
        }

        if (operationError is not null)
        {
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }

        return binding is null
            ? await closedAutomation.RunAsync(workbookPath, timeouts, operation, cancellationToken)
                .ConfigureAwait(false)
            : result!;
    }

    internal static async Task<T> WaitForStageAsync<T>(
        Task<T> operation,
        WorkbookAutomationStage stage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException error)
        {
            WorkbookAutomationStageExecutor.ObserveFault(operation);
            throw new WorkbookAutomationTimeoutException(stage, timeout, error);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            WorkbookAutomationStageExecutor.ObserveFault(operation);
            throw new WorkbookAutomationCanceledException(stage, cancellationToken, error);
        }
    }

    private sealed class BorrowedSourceWorkbookSession(
        IStaComDispatcher dispatcher,
        SourceWorkbookBorrowedBinding binding,
        WorkbookAutomationTimeouts timeouts,
        bool validateSelectedWorkbookBeforeOperations) : ISourceWorkbookSession
    {
        private int saveState;

        public bool WasAlreadyOpen => true;

        public SourceWorkbookSaveState SaveState =>
            (SourceWorkbookSaveState)Volatile.Read(ref saveState);

        public Task<bool> IsSavedAsync(CancellationToken cancellationToken)
            => ExecuteAsync(
                new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleInspection),
                timeouts.ModuleImport,
                binding.IsSaved,
                cancellationToken);

        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleInspection),
                timeouts.ModuleImport, binding.Workbook.GetProjectName, cancellationToken);

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleInspection),
                timeouts.ModuleImport, binding.Workbook.GetModules, cancellationToken);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt),
                timeouts.ReferenceAttempt, binding.Workbook.GetReferences, cancellationToken);

        public Task<IReadOnlyList<WorkbookReference>> GetReferenceIdentitiesAsync(
            IReadOnlyList<string> referenceNames, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt),
                timeouts.ReferenceAttempt,
                () => binding.Workbook.GetReferenceIdentities(referenceNames), cancellationToken);

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt, referenceName),
                timeouts.ReferenceAttempt, () => binding.Workbook.RemoveReference(referenceName), cancellationToken);

        public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt, reference.Name),
                timeouts.ReferenceAttempt, () => binding.Workbook.AddReference(reference), cancellationToken);

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName, ResolvedVbaProjectReference candidate, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ReferenceAttempt, referenceName),
                timeouts.ReferenceAttempt,
                () => binding.Workbook.TryResolveReference(referenceName, candidate), cancellationToken);

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleRemoval, moduleName),
                timeouts.ModuleImport, () => binding.Workbook.RemoveModule(moduleName), cancellationToken);

        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleImport, sourceFile.FileName),
                timeouts.ModuleImport, () => binding.Workbook.ImportModule(sourceFile), cancellationToken);

        public Task ExportModuleAsync(
            string moduleName, string destinationPath, CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.ModuleExport, moduleName),
                timeouts.ModuleImport,
                () => binding.Workbook.ExportModule(moduleName, destinationPath), cancellationToken);

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.Verification),
                timeouts.ModuleImport, binding.Workbook.VerifyImportedModules, cancellationToken);

        public Task SaveAsync(CancellationToken cancellationToken)
            => ExecuteAsync(new WorkbookAutomationStage(WorkbookAutomationStageKind.WorkbookSave),
                timeouts.WorkbookSave,
                () =>
                {
                    Volatile.Write(ref saveState, (int)SourceWorkbookSaveState.Unknown);
                    binding.Workbook.Save();
                    if (!binding.IsSaved())
                    {
                        throw new InvalidOperationException(
                            "Excel returned from source workbook Save, but the selected workbook remains unsaved.");
                    }
                    if (!binding.IsStillSelected())
                    {
                        throw new InvalidOperationException(
                            "Excel returned from Save, but the selected source workbook path changed.");
                    }
                    Volatile.Write(ref saveState, (int)SourceWorkbookSaveState.Saved);
                },
                cancellationToken);

        private async Task<T> ExecuteAsync<T>(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            Func<T> operation,
            CancellationToken cancellationToken)
        {
            using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pending = dispatcher.InvokeAsync(() =>
            {
                if (validateSelectedWorkbookBeforeOperations && !binding.IsStillSelected())
                    throw new InvalidOperationException(
                        "The selected source workbook is no longer bound to its original Excel workbook.");
                return operation();
            }, stageCancellation.Token);
            try
            {
                return await WaitForStageAsync(pending, stage, timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                stageCancellation.Cancel();
                throw;
            }
        }

        private Task ExecuteAsync(
            WorkbookAutomationStage stage,
            TimeSpan timeout,
            Action operation,
            CancellationToken cancellationToken)
            => ExecuteAsync(stage, timeout, () => { operation(); return true; }, cancellationToken);
    }
}
