using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Build;

/// <summary>Applies an analyzed ordinary Build to the selected source workbook in place.</summary>
internal sealed class SourceWorkbookBuildCommand(
    WorkbookMaterializer materializer,
    ISourceWorkbookAutomation automation,
    WorkbookReferenceNormalizer referenceNormalizer,
    IExactFileSystemObjectOwnershipFactory ownershipFactory,
    TimeSpan? recoveryBudgetOverride = null)
{
    private readonly TimeSpan recoveryBudget = recoveryBudgetOverride switch
    {
        null => TimeSpan.FromMinutes(10),
        { } value when value > TimeSpan.Zero
            && value <= TimeSpan.FromMilliseconds(uint.MaxValue - 1) => value,
        _ => throw new ArgumentOutOfRangeException(nameof(recoveryBudgetOverride),
            "The source workbook recovery budget must be finite and positive.")
    };
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();

    internal async Task<WorkbookMaterializationResult> MaterializeAsync(
        ResolvedProjectContext context,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        CancellationToken cancellationToken)
    {
        var prepared = await materializer.PrepareSourceWorkbookBuildAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var sourceSet = prepared.SourceSet;
        var sourceMirrorFiles = sourceSet.SourceFiles
            .SelectMany(file => file.BinaryPath is null
                ? new[] { file.SourcePath }
                : new[] { file.SourcePath, file.BinaryPath! })
            .ToArray();
        using var recovery = CreateRecoveryStagingOrReleaseSourceSet(sourceSet, sourceMirrorFiles);
        var replacement = new SourceWorkbookReplacement(
            sourceSet, prepared.SourcePreflight, recovery, ownershipFactory, recoveryBudget);
        ISourceWorkbookSession? observedSession = null;
        (IReadOnlyList<string> warnings, VbeImportVerificationReport verification)? completedOperation = null;
        (IReadOnlyList<string> warnings, VbeImportVerificationReport verification) sessionResult;
        Exception? terminalFailure = null;
        try
        {
            try
            {
                sessionResult = await automation.RunAsync(
                    context.TemplateDocumentPath,
                    prepared.Timeouts,
                    async (session, token) =>
                    {
                        observedSession = session;
                        var projectName = await session.GetProjectNameAsync(token).ConfigureAwait(false);
                        WorkbookMaterializer.VerifyAnalyzedProjectIdentity(prepared.SemanticInputs, projectName);
                        var modules = await session.GetModulesAsync(token).ConfigureAwait(false);
                        var references = await session.GetReferencesAsync(token).ConfigureAwait(false);
                        var retainedModules = modules.Where(module => !module.Kind.IsImportable()).ToArray();
                        var desiredReferenceNames = context.Document.References
                            .Select(reference => reference.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var referencesKnownToRemain = references.Where(reference =>
                            !reference.IsRemovable || desiredReferenceNames.Contains(reference.Name)).ToArray();
                        namePreflight.ThrowIfFailed(prepared.SourcePreflight,
                            namePreflight.InspectLivePhase(sourceSet.SourceFiles,
                                retainedModules, projectName, referencesKnownToRemain));

                        if (session.WasAlreadyOpen && !await session.IsSavedAsync(token).ConfigureAwait(false))
                        {
                            var warning = $"Build will replace VBA code in the already-open source workbook '{context.TemplateDocumentPath}' and save its other unsaved workbook edits. Continue?";
                            if (confirmUnsavedChanges is null)
                                throw new BuildCommandException(
                                    $"The source workbook has unsaved changes: {context.TemplateDocumentPath}. Explicit confirmation is required before Build can replace code and save other workbook edits.");
                            if (!await confirmUnsavedChanges(warning, token).ConfigureAwait(false))
                                throw new BuildCommandException(
                                    $"Build was declined; the source workbook was not changed: {context.TemplateDocumentPath}");
                        }

                        var replacementSession = new SourceWorkbookReplacementSessionView(session);
                        await replacement.CaptureAsync(replacementSession, modules, references, token)
                            .ConfigureAwait(false);
                        try
                        {
                            var applied = await replacement.ReplaceAndVerifyAsync(
                                replacementSession,
                                replacementToken => referenceNormalizer.NormalizeAsync(
                                    session, context.DocumentName, context.Document.References,
                                    replacementToken, prepared.SemanticInputs),
                                name => WorkbookMaterializer.VerifyAnalyzedProjectIdentity(
                                    prepared.SemanticInputs, name),
                                liveReferences => WorkbookMaterializer.VerifyAnalyzedReferences(
                                    prepared.SemanticInputs, liveReferences),
                                token).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            await session.SaveAsync(token).ConfigureAwait(false);
                            if (session.SaveState != SourceWorkbookSaveState.Saved)
                                throw new BuildCommandException(
                                    $"Source workbook Save outcome is unknown: {context.TemplateDocumentPath}");
                            completedOperation = (applied.Warnings, applied.Verification);
                            return completedOperation.Value;
                        }
                        catch (Exception operationFailure)
                        {
                            if (replacement.ReplacementStarted && session.WasAlreadyOpen
                                && session.SaveState == SourceWorkbookSaveState.NotStarted)
                            {
                                await replacement.RestoreAsync(replacementSession, operationFailure)
                                    .ConfigureAwait(false);
                            }
                            throw;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var terminalFacts = WorkbookAutomationTerminalFacts.Analyze(
                    error, cancellationToken.IsCancellationRequested);
                if (observedSession?.SaveState == SourceWorkbookSaveState.Saved
                    && completedOperation is { } completed
                    && terminalFacts.Disposition == WorkbookAutomationDisposition.Cancelled
                    && terminalFacts.ProcessReleaseProven && terminalFacts.DispatcherRetired)
                {
                    // Verification and native Save completed. A later cooperative
                    // cancellation cannot reverse the saved source workbook.
                    sessionResult = completed;
                }
                else
                {
                    Exception reportError = error;
                    if (terminalFacts.ProcessReleaseProven && terminalFacts.DispatcherRetired)
                    {
                        if (replacement.RecoveryVerification is not null)
                        {
                            try
                            {
                                var verificationCleanup = replacement.RecoveryVerification.Cleanup();
                                if (verificationCleanup.Status != InvocationScratchCleanupStatus.Removed)
                                    throw new IOException(
                                        $"Recovery verification staging cleanup was {verificationCleanup.Status}. Retained paths: {string.Join(", ", verificationCleanup.RetainedPaths)}");
                            }
                            catch (Exception cleanupError)
                            {
                                reportError = CombineCleanupFailure(reportError, cleanupError,
                                    "Recovery verification staging cleanup", replacement.RecoveryVerification.Path);
                            }
                        }
                        if (!replacement.RetainRecovery)
                        {
                            try
                            {
                                var recoveryCleanup = recovery.Cleanup();
                                if (recoveryCleanup.Status != InvocationScratchCleanupStatus.Removed)
                                    throw new IOException(
                                        $"Recovery-file cleanup was {recoveryCleanup.Status}. Retained paths: {string.Join(", ", recoveryCleanup.RetainedPaths)}");
                            }
                            catch (Exception cleanupError)
                            {
                                reportError = CombineCleanupFailure(reportError, cleanupError,
                                    "Recovery-file cleanup", recovery.Path);
                            }
                        }
                    }
                    if (observedSession?.SaveState == SourceWorkbookSaveState.Unknown)
                    {
                        terminalFailure = new BuildCommandException(
                            $"Source workbook Save outcome is unknown: {context.TemplateDocumentPath}. Inspect the workbook before rebuilding. Failure: {reportError.Message}",
                            reportError);
                        throw terminalFailure;
                    }
                    if (observedSession?.SaveState == SourceWorkbookSaveState.Saved)
                    {
                        terminalFailure = new BuildCommandException(
                            $"Source workbook Save completed, but a later Build step failed: {context.TemplateDocumentPath}. Failure: {reportError.Message}",
                            reportError);
                        throw terminalFailure;
                    }
                    terminalFailure = reportError;
                    if (ReferenceEquals(reportError, error)) throw;
                    throw reportError;
                }
            }
            try
            {
                var cleanup = recovery.Cleanup();
                if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
                    throw new IOException(
                        $"Cleanup was {cleanup.Status}. Retained paths: {string.Join(", ", cleanup.RetainedPaths)}");
            }
            catch (Exception cleanupError)
            {
                throw new BuildCommandException(
                    $"Source workbook Save completed, but recovery-file cleanup failed: {recovery.Path}. Failure: {cleanupError.Message}",
                    cleanupError);
            }

            return new WorkbookMaterializationResult(
                context.TemplateDocumentPath,
                sourceSet.SourceFiles.Count,
                sessionResult.warnings,
                sessionResult.verification,
                prepared.SourceAdmission);
        }
        catch (Exception error)
        {
            var terminalFacts = WorkbookAutomationTerminalFacts.Analyze(error);
            if (!terminalFacts.ProcessReleaseProven || !terminalFacts.DispatcherRetired)
            {
                sourceSet.RetainWithoutCleanup();
                terminalFailure = new BuildCommandException(
                    $"{error.Message} Excel-dependent Build source mirror and recovery files were retained because workbook automation release or STA retirement could not be proved: {string.Join(", ", sourceMirrorFiles)}, {recovery.Path}{(replacement.RecoveryVerification is null ? string.Empty : ", " + replacement.RecoveryVerification.Path)}. Do not remove these paths until the pending Excel automation is no longer using them.",
                    error);
                throw terminalFailure;
            }
            terminalFailure = error;
            throw;
        }
        finally
        {
            Exception? verificationReleaseFailure = null;
            try
            {
                replacement.RecoveryVerification?.Dispose();
            }
            catch (Exception error)
            {
                verificationReleaseFailure = error;
            }
            Exception? sourceReleaseFailure = null;
            try
            {
                sourceSet.Dispose();
            }
            catch (Exception error)
            {
                sourceReleaseFailure = error;
            }
            if (verificationReleaseFailure is not null || sourceReleaseFailure is not null)
            {
                var failures = new List<Exception>();
                if (terminalFailure is not null) failures.Add(terminalFailure);
                if (verificationReleaseFailure is not null) failures.Add(verificationReleaseFailure);
                if (sourceReleaseFailure is not null) failures.Add(sourceReleaseFailure);
                var retained = sourceSet.CleanupEvidence?.RetainedPaths;
                var retainedSourcePaths = retained is { Length: > 0 }
                    ? retained.Value.AsEnumerable()
                    : sourceMirrorFiles;
                var primaryMessage = terminalFailure?.Message
                    ?? (observedSession?.SaveState == SourceWorkbookSaveState.Saved
                        ? "Source workbook Save completed, but final cleanup failed."
                        : "Source workbook Build failed during final cleanup.");
                var verificationDetail = verificationReleaseFailure is null
                    ? string.Empty
                    : $" Recovery verification staging release failed: {replacement.RecoveryVerification?.Path}. Failure: {verificationReleaseFailure.Message}";
                var sourceDetail = sourceReleaseFailure is null
                    ? string.Empty
                    : $" VBE import staging cleanup failed. Retained exact paths: {string.Join(", ", retainedSourcePaths)}. Failure: {sourceReleaseFailure.Message}";
                throw new BuildCommandException(
                    primaryMessage + verificationDetail + sourceDetail,
                    new AggregateException(failures));
            }
        }
    }

    private WorkbookExportStaging CreateRecoveryStagingOrReleaseSourceSet(
        VbeImportSourceSet sourceSet,
        IReadOnlyList<string> sourceMirrorFiles)
    {
        try
        {
            return WorkbookExportStaging.Create(ownershipFactory);
        }
        catch (Exception creationFailure)
        {
            try
            {
                sourceSet.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                var retained = sourceSet.CleanupEvidence?.RetainedPaths;
                var paths = retained is { Length: > 0 }
                    ? retained.Value.AsEnumerable()
                    : sourceMirrorFiles;
                throw new BuildCommandException(
                    $"Recovery staging could not be created: {creationFailure.Message} VBE import source cleanup also failed. Retained exact paths: {string.Join(", ", paths)}. Cleanup failure: {cleanupFailure.Message}",
                    new AggregateException(creationFailure, cleanupFailure));
            }
            throw;
        }
    }

    private static BuildCommandException CombineCleanupFailure(
        Exception primary, Exception cleanup, string stage, string path)
        => new($"{primary.Message} {stage} failed as well: {path}. Failure: {cleanup.Message}",
            new AggregateException(primary, cleanup));

}
