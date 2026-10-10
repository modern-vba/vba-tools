using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.DebugPreparation;

/// <summary>Prepares the bound source workbook for debugging without saving or owning Excel.</summary>
public sealed class DebugWorkbookPreparationCommand
{
    private readonly WorkbookMaterializer materializer;
    private readonly Func<int, long, ISourceWorkbookAutomation> automationFactory;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly TimeSpan? recoveryBudgetOverride;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();

    internal DebugWorkbookPreparationCommand(
        WorkbookMaterializer materializer,
        Func<int, long, ISourceWorkbookAutomation> automationFactory,
        WorkbookReferenceNormalizer referenceNormalizer,
        IExactFileSystemObjectOwnershipFactory ownershipFactory,
        TimeSpan? recoveryBudgetOverride = null)
    {
        this.materializer = materializer;
        this.automationFactory = automationFactory;
        this.referenceNormalizer = referenceNormalizer;
        this.ownershipFactory = ownershipFactory;
        this.recoveryBudgetOverride = recoveryBudgetOverride;
    }

    public async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        DebugWorkbookPreparationRequest request,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        Func<DebugWorkbookPreparationReady, CancellationToken, Task<bool>> continueAfterCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(continueAfterCapture);
        VbeImportSourceSet? sourceSet = null;
        WorkbookExportStaging? recovery = null;
        SourceWorkbookReplacement? replacement = null;
        SourceWorkbookReplacementResult? completed = null;
        Exception? failure = null;
        var retainScratch = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Debug preparation supports only Excel documents: {context.DocumentName}");
            ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceSnapshotDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.GenerationId);
            if (request.GenerationId.Length != 32 || !request.GenerationId.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException(
                    "Debug preparation requires a generation token of 32 lowercase hexadecimal characters.");
            if (request.ExcelProcessId <= 0 || request.ExcelProcessStartUtcTicks <= 0
                || request.ExcelProcessStartUtcTicks > DateTime.MaxValue.Ticks)
                throw new InvalidOperationException("Debug preparation requires an exact live Excel process identity.");
            var prepared = await materializer.PrepareSourceWorkbookDebugAsync(
                context, request.SourceSnapshotDirectory, request.WorkingDirectory, cancellationToken)
                .ConfigureAwait(false);
            sourceSet = prepared.SourceSet;
            recovery = WorkbookExportStaging.Create(ownershipFactory);
            replacement = new SourceWorkbookReplacement(sourceSet, prepared.SourcePreflight,
                recovery, ownershipFactory, recoveryBudgetOverride,
                new("Debug preparation", "completion", "source workbook"));
            var ready = new DebugWorkbookPreparationReady(request.GenerationId,
                Path.GetFullPath(context.TemplateDocumentPath),
                request.ExcelProcessId, request.ExcelProcessStartUtcTicks);
            var automation = automationFactory(request.ExcelProcessId, request.ExcelProcessStartUtcTicks)
                ?? throw new InvalidOperationException("Debug preparation workbook automation is unavailable.");
            await automation.RunAsync(context.TemplateDocumentPath, prepared.Timeouts,
                async (session, token) =>
                {
                    if (!session.WasAlreadyOpen)
                        throw new InvalidOperationException(
                            "Debug preparation requires the exact source workbook to remain open in the bound Excel process.");
                    var projectName = await session.GetProjectNameAsync(token).ConfigureAwait(false);
                    var modules = await session.GetModulesAsync(token).ConfigureAwait(false);
                    var references = await session.GetReferencesAsync(token).ConfigureAwait(false);
                    var retainedModules = modules.Where(module => !module.Kind.IsImportable()).ToArray();
                    var desiredNames = context.Document.References
                        .Select(reference => reference.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var remainingReferences = references.Where(reference =>
                        !reference.IsRemovable || desiredNames.Contains(reference.Name)).ToArray();
                    namePreflight.ThrowIfFailed(prepared.SourcePreflight,
                        namePreflight.InspectLivePhase(sourceSet.SourceFiles,
                            retainedModules, projectName, remainingReferences));
                    // Launch/Restart already requests Debug replacement, even for dirty workbooks.
                    // Retain the callback parameter for callers, but never request consent or Save.
                    var narrowedSession = new SourceWorkbookReplacementSessionView(session);
                    await replacement.CaptureAsync(narrowedSession, modules, references, token)
                        .ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        using var continuation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        continuation.CancelAfter(prepared.Timeouts.WorkbookOpen);
                        bool proceed;
                        try
                        {
                            proceed = await continueAfterCapture(ready, continuation.Token)
                                .WaitAsync(continuation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested
                            && continuation.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"Debug preparation readiness continuation timed out; no VBA code was replaced: {ready.WorkbookPath}");
                        }
                        token.ThrowIfCancellationRequested();
                        if (!proceed)
                            throw new InvalidOperationException(
                                $"Debug preparation readiness was declined; no VBA code was replaced: {ready.WorkbookPath}");
                        var applied = await replacement.ReplaceAndVerifyAsync(narrowedSession,
                            replacementToken => referenceNormalizer.NormalizeAsync(session,
                                context.DocumentName, context.Document.References, replacementToken),
                            currentProjectName =>
                            {
                                if (!projectName.Equals(currentProjectName, StringComparison.Ordinal))
                                    throw new InvalidOperationException(
                                        "The source workbook project identity changed during debug preparation.");
                            },
                            _ => { },
                            token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        completed = applied;
                        return true;
                    }
                    catch (Exception operationFailure)
                    {
                        if (replacement.ReplacementStarted
                            && session.SaveState == SourceWorkbookSaveState.NotStarted)
                            await replacement.RestoreAsync(narrowedSession, operationFailure)
                                .ConfigureAwait(false);
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var facts = WorkbookAutomationTerminalFacts.Analyze(error, cancellationToken.IsCancellationRequested);
            if (!(completed is not null && facts.Disposition == WorkbookAutomationDisposition.Cancelled
                && facts.ProcessReleaseProven && facts.DispatcherRetired))
                failure = error;
            retainScratch = !facts.ProcessReleaseProven || !facts.DispatcherRetired;
            if (retainScratch)
            {
                var retainedPaths = MirrorPaths(sourceSet).Concat(
                    new[] { recovery?.Path, replacement?.RecoveryVerification?.Path }
                        .Where(path => path is not null));
                failure = new InvalidOperationException(
                    $"{error.Message} Debug preparation mirror and recovery files were retained because workbook automation release or STA retirement could not be proved: {string.Join(", ", retainedPaths)}. Do not remove these paths until pending Excel automation is no longer using them.", error);
            }
        }

        foreach (var cleanupFailure in CleanupScratch(sourceSet, recovery, replacement, retainScratch))
        {
            failure = failure is null
                ? cleanupFailure
                : new InvalidOperationException(
                    $"{failure.Message} Cleanup failed as well: {cleanupFailure.Message}",
                    new AggregateException(failure, cleanupFailure));
        }
        if (failure is not null)
        {
            var facts = WorkbookAutomationTerminalFacts.Analyze(failure, cancellationToken.IsCancellationRequested);
            var detail = completed is null ? failure.Message
                : $"Source workbook VBA code was prepared without Save, but a later preparation step failed: {context.TemplateDocumentPath}. Failure: {failure.Message}";
            var result = facts.Disposition == WorkbookAutomationDisposition.Cancelled
                ? CommandResult.Cancelled(detail) : CommandResult.UsageError(detail);
            return retainScratch ? result.MarkOwnedProcessReleaseUnproven() : result;
        }
        if (completed is null)
            return CommandResult.UsageError("Debug preparation produced no verified completion.");
        return new CommandResult(0, JsonSerializer.Serialize(new
        {
            type = "debugWorkbookPrepared",
            schemaVersion = "1.0",
            generationId = request.GenerationId,
            workbookPath = Path.GetFullPath(context.TemplateDocumentPath),
            excelProcessId = request.ExcelProcessId,
            excelProcessStartUtcTicks = request.ExcelProcessStartUtcTicks,
            importedSourceFileCount = sourceSet!.SourceFiles.Count,
            warnings = completed.Warnings
        }) + Environment.NewLine, VbeImportWarningRenderer.Render(completed.Verification));
    }

    private static IEnumerable<string> MirrorPaths(VbeImportSourceSet? sourceSet)
        => sourceSet?.SourceFiles.SelectMany(file => file.BinaryPath is null
            ? new[] { file.SourcePath } : new[] { file.SourcePath, file.BinaryPath! }) ?? [];

    private static IReadOnlyList<Exception> CleanupScratch(
        VbeImportSourceSet? sourceSet,
        WorkbookExportStaging? recovery,
        SourceWorkbookReplacement? replacement,
        bool retainScratch)
    {
        var failures = new List<Exception>();
        void ReleaseExport(WorkbookExportStaging? staging, bool retain)
        {
            if (staging is null) return;
            try
            {
                if (retain) staging.Dispose();
                else
                {
                    var cleanup = staging.Cleanup();
                    if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
                        throw new IOException(
                            $"Debug preparation export cleanup was {cleanup.Status}. Retained paths: {string.Join(", ", cleanup.RetainedPaths)}");
                }
            }
            catch (Exception error)
            {
                failures.Add(new IOException(
                    $"Debug preparation export staging release failed: {staging.Path}. Failure: {error.Message}", error));
            }
        }
        ReleaseExport(replacement?.RecoveryVerification, retainScratch);
        ReleaseExport(recovery, retainScratch || replacement?.RetainRecovery == true);
        if (sourceSet is not null)
        {
            try
            {
                if (retainScratch) sourceSet.RetainWithoutCleanup();
                else sourceSet.Dispose();
            }
            catch (Exception error)
            {
                failures.Add(new IOException(
                    $"VBE import staging cleanup failed. Retained exact paths: {string.Join(", ", sourceSet.CleanupEvidence?.RetainedPaths ?? [.. MirrorPaths(sourceSet)])}. Failure: {error.Message}", error));
            }
        }
        return failures;
    }
}
