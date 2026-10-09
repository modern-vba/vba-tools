using VbaDev.App.Workbooks;
using VbaDev.Infrastructure.Debugging;

namespace VbaDev.Infrastructure.Workbooks;

internal interface IWorkbookGenerationSavedStateReader
{
    Task<bool> IsSavedAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the selected file in a hidden invocation-owned Excel process.</summary>
internal sealed class ExcelComClosedSourceWorkbookAutomation : ISourceWorkbookAutomation
{
    public async Task<TResult> RunAsync<TResult>(
        string workbookPath,
        WorkbookAutomationTimeouts timeouts,
        Func<ISourceWorkbookSession, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var saveTracker = new SourceWorkbookSaveTracker();
        var runtime = new AutomationExcelProcessRuntime(
            new StaComDispatcherFactory(), new SourceWorkbookGenerationLifecycle(saveTracker));
        var outcome = await runtime.RunWorkbookAsync(
                workbookPath,
                timeouts,
                (session, token) => operation(
                    new ClosedSourceWorkbookSession(session, saveTracker), token),
                cancellationToken)
            .ConfigureAwait(false);
        // A known native Save remains known even if cancellation arrives during
        // process cleanup. The workflow decides how to report that boundary.
        return outcome.GetReleasedResult();
    }

    internal sealed class SourceWorkbookSaveTracker
    {
        private int state;

        internal SourceWorkbookSaveState State => (SourceWorkbookSaveState)Volatile.Read(ref state);

        internal void MarkSaving() => Volatile.Write(ref state, (int)SourceWorkbookSaveState.Unknown);

        internal void MarkSaved() => Volatile.Write(ref state, (int)SourceWorkbookSaveState.Saved);
    }

    internal static ExcelComWorkbookBuildSession CreateTrackedBuildSession(
        ExcelComWorkbookSession session,
        SourceWorkbookSaveTracker saveTracker,
        Func<bool>? isStillSelected = null)
        => new(session, saveTracker.MarkSaving, () =>
        {
            dynamic workbook = session.WorkbookObject;
            if (!(bool)workbook.Saved)
            {
                throw new InvalidOperationException(
                    "Excel returned from source workbook Save, but the selected workbook remains unsaved.");
            }
            if (isStillSelected is not null && !isStillSelected())
            {
                throw new InvalidOperationException(
                    "Excel returned from Save, but the selected source workbook path changed.");
            }

            saveTracker.MarkSaved();
        });

    private sealed class SourceWorkbookGenerationLifecycle(SourceWorkbookSaveTracker saveTracker)
        : IExcelComWorkbookGenerationLifecycle
    {
        public object Start(
            OwnedExcelTerminationController terminationController,
            bool enableAutomationSecurityLow,
            CancellationToken cancellationToken)
            => ExcelComWorkbookSession.StartOwnedForGeneration(
                terminationController, enableAutomationSecurityLow, cancellationToken);

        public IWorkbookBuildSession Open(object host, string workbookPath)
        {
            var session = ExcelComWorkbookSession.OpenOwnedForGeneration(
                (ExcelComWorkbookSession.ExcelComHostObjects)host, workbookPath);
            return CreateTrackedBuildSession(session, saveTracker,
                () => SourceWorkbookPathVerification.IsSelectedWorkbookPath(
                    session.WorkbookObject, workbookPath));
        }

        public void DisposeHost(object host, TimeSpan cleanupGrace)
            => ExcelComWorkbookSession.DisposeOwnedGenerationHost(
                (ExcelComWorkbookSession.ExcelComHostObjects)host, cleanupGrace);

        public void DisposeSession(IWorkbookBuildSession session, TimeSpan cleanupGrace)
            => ((ExcelComWorkbookBuildSession)session).DisposeOwnedGeneration(cleanupGrace);
    }

    private sealed class ClosedSourceWorkbookSession(
        IWorkbookGenerationSession session,
        SourceWorkbookSaveTracker saveTracker) : ISourceWorkbookSession
    {
        public bool WasAlreadyOpen => false;

        public SourceWorkbookSaveState SaveState => saveTracker.State;

        public Task<bool> IsSavedAsync(CancellationToken cancellationToken)
            => ((IWorkbookGenerationSavedStateReader)session).IsSavedAsync(cancellationToken);

        public Task<string> GetProjectNameAsync(CancellationToken cancellationToken)
            => session.GetProjectNameAsync(cancellationToken);

        public Task<IReadOnlyList<WorkbookModule>> GetModulesAsync(CancellationToken cancellationToken)
            => session.GetModulesAsync(cancellationToken);

        public Task<IReadOnlyList<WorkbookReference>> GetReferencesAsync(CancellationToken cancellationToken)
            => session.GetReferencesAsync(cancellationToken);

        public Task<IReadOnlyList<WorkbookReference>> GetReferenceIdentitiesAsync(
            IReadOnlyList<string> referenceNames,
            CancellationToken cancellationToken)
            => session.GetReferenceIdentitiesAsync(referenceNames, cancellationToken);

        public Task<bool> RemoveReferenceAsync(string referenceName, CancellationToken cancellationToken)
            => session.RemoveReferenceAsync(referenceName, cancellationToken);

        public Task AddReferenceAsync(ResolvedVbaProjectReference reference, CancellationToken cancellationToken)
            => session.AddReferenceAsync(reference, cancellationToken);

        public Task<VbaProjectReferenceProbeAttemptResult> TryResolveAsync(
            string referenceName, ResolvedVbaProjectReference candidate, CancellationToken cancellationToken)
            => session.TryResolveAsync(referenceName, candidate, cancellationToken);

        public Task RemoveModuleAsync(string moduleName, CancellationToken cancellationToken)
            => session.RemoveModuleAsync(moduleName, cancellationToken);

        public Task ImportModuleAsync(VbeImportSourceFile sourceFile, CancellationToken cancellationToken)
            => session.ImportModuleAsync(sourceFile, cancellationToken);

        public Task ExportModuleAsync(
            string moduleName, string destinationPath, CancellationToken cancellationToken)
            => session.ExportModuleAsync(moduleName, destinationPath, cancellationToken);

        public Task<VbeImportVerificationReport> VerifyAsync(CancellationToken cancellationToken)
            => session.VerifyAsync(cancellationToken);

        public Task SaveAsync(CancellationToken cancellationToken)
            => session.SaveAsync(cancellationToken);
    }
}
