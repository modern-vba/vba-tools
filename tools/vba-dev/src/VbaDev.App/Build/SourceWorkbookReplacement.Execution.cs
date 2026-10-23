using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

internal sealed partial class SourceWorkbookReplacement
{
    /// <summary>Owns preparation resources through actual automation retirement and finalization.</summary>
    internal static async Task<SourceWorkbookExecutionResult<TResult>> RunAsync<TResult>(
        Func<CancellationToken, Task<PreparedSourceWorkbookBuild>> prepare,
        string workbookPath,
        ISourceWorkbookAutomation automation,
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        Func<PreparedSourceWorkbookBuild, SourceWorkbookReplacement, ISourceWorkbookSession,
            CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken,
        TimeSpan? recoveryBudget = null)
    {
        var prepared = await prepare(cancellationToken).ConfigureAwait(false);
        WorkbookExportStaging? recovery = null;
        SourceWorkbookReplacement? replacement = null;
        TResult? completed = default;
        var returned = false;
        Exception? failure = null;
        WorkbookAutomationTerminalFacts? facts = null;
        var warnings = new List<string>();
        try
        {
            recovery = WorkbookExportStaging.Create(ownershipFactory);
            replacement = new(prepared.SourceSet, prepared.SourcePreflight, recovery,
                ownershipFactory, recoveryBudget);
            await automation.RunAsync(workbookPath, prepared.Timeouts, async (session, token) =>
            {
                var result = await operation(prepared, replacement, session, token).ConfigureAwait(false);
                completed = result;
                returned = true;
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            facts = WorkbookAutomationTerminalFacts.Analyze(error, cancellationToken.IsCancellationRequested);
            if (!(returned && facts.Disposition == WorkbookAutomationDisposition.Cancelled
                && Released(facts)))
                failure = error;
        }

        var retain = facts is not null && !Released(facts);
        var sourcePaths = prepared.SourceSet.SourceFiles.SelectMany(file =>
            file.BinaryPath is null ? new[] { file.SourcePath } : new[] { file.SourcePath, file.BinaryPath });
        var retainedPaths = sourcePaths.Concat(new[] { recovery?.Path, replacement?.RecoveryVerification?.Path }
            .OfType<string>()).ToArray();
        if (retain)
        {
            var unproved = new List<string>();
            if (!facts!.ProcessReleaseProven) unproved.Add("Excel process release");
            if (!facts.DispatcherRetired) unproved.Add("STA retirement");
            if (!facts.ComReferenceReleaseProven) unproved.Add("COM reference release");
            failure = new BuildCommandException(
                $"{failure?.Message} Source mirror and recovery files were retained because {string.Join(" and ", unproved)} could not be proved: {string.Join(", ", retainedPaths)}.",
                failure!);
        }

        FinalizeExport(replacement?.RecoveryVerification, "Recovery verification staging cleanup",
            retain);
        FinalizeExport(recovery, "Recovery-file cleanup", retain || replacement?.RetainRecovery == true);
        try
        {
            if (retain) prepared.SourceSet.RetainWithoutCleanup();
            else RecordCleanup(prepared.SourceSet.CleanupAfterRelease(), "VBE import staging cleanup");
        }
        catch (Exception error) { RecordFailure(error, "VBE import staging release"); }
        return new(prepared, returned, completed, failure, facts, warnings.ToArray());

        void FinalizeExport(WorkbookExportStaging? staging, string stage, bool keep)
        {
            if (staging is null) return;
            try
            {
                if (keep) staging.Dispose();
                else RecordCleanup(staging.Cleanup(), stage);
            }
            catch (Exception error) { RecordFailure(error, stage + " release"); }
        }
        void RecordCleanup(InvocationScratchCleanupEvidence evidence, string stage)
        {
            if (evidence.Status == InvocationScratchCleanupStatus.Removed) return;
            var detail = $"{stage} failed ({evidence.Status}). Retained exact paths: {string.Join(", ", evidence.RetainedPaths)}";
            if (failure is null) warnings.Add("Warning: " + detail);
            else RecordFailure(new IOException(detail), stage);
        }
        void RecordFailure(Exception error, string stage)
        {
            failure = failure is null
                ? new BuildCommandException($"{stage} failed: {error.Message}", error)
                : new BuildCommandException($"{failure.Message} {stage} failed as well: {error.Message}",
                    new AggregateException(failure, error));
        }
    }

    private static bool Released(WorkbookAutomationTerminalFacts facts)
        => facts.ProcessReleaseProven && facts.DispatcherRetired && facts.ComReferenceReleaseProven;
}

internal sealed record SourceWorkbookExecutionResult<TResult>(
    PreparedSourceWorkbookBuild Prepared,
    bool CallbackReturned,
    TResult? Completed,
    Exception? Failure,
    WorkbookAutomationTerminalFacts? TerminalFacts,
    IReadOnlyList<string> CleanupWarnings);
