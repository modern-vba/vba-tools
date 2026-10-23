using System.Text.Json;
using VbaDev.App.Build;
using VbaDev.App.Cli;
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
        var importedSourceCount = 0;
        IReadOnlyList<string> cleanupWarnings = [];
        SourceWorkbookReplacementResult? completed = null;
        Exception? failure = null;
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
            var ready = new DebugWorkbookPreparationReady(request.GenerationId,
                Path.GetFullPath(context.TemplateDocumentPath),
                request.ExcelProcessId, request.ExcelProcessStartUtcTicks);
            var automation = automationFactory(request.ExcelProcessId, request.ExcelProcessStartUtcTicks)
                ?? throw new InvalidOperationException("Debug preparation workbook automation is unavailable.");
            var execution = await SourceWorkbookReplacement.RunOptionalAsync(
                async token => await materializer.PrepareSourceWorkbookDebugAsync(context,
                    request.SourceSnapshotDirectory, request.WorkingDirectory, token).ConfigureAwait(false),
                context.TemplateDocumentPath, automation, ownershipFactory,
                async (prepared, replacement, session, token) =>
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
                    namePreflight.ThrowIfFailed(prepared!.SourcePreflight,
                        namePreflight.InspectLivePhase(prepared!.SourceSet.SourceFiles,
                            retainedModules, projectName, remainingReferences));
                    // Launch/Restart already requests Debug replacement, even for dirty workbooks.
                    // Retain the callback parameter for callers, but never request consent or Save.
                    var narrowedSession = new SourceWorkbookReplacementSessionView(session);
                    await replacement!.CaptureAsync(narrowedSession, modules, references, token)
                        .ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        using var continuation = CancellationTokenSource.CreateLinkedTokenSource(token);
                        continuation.CancelAfter(prepared!.Timeouts.WorkbookOpen);
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
                        return applied;
                    }
                    catch (Exception operationFailure)
                    {
                        if (replacement.ReplacementStarted
                            && session.SaveState == SourceWorkbookSaveState.NotStarted)
                            await replacement.RestoreAsync(narrowedSession, operationFailure)
                                .ConfigureAwait(false);
                        throw;
                    }
                }, cancellationToken, WorkbookAutomationTimeouts.Default, recoveryBudgetOverride,
                new("Debug preparation", "completion", "source workbook")).ConfigureAwait(false);
            completed = execution.Completed;
            failure = execution.Failure;
            cleanupWarnings = execution.CleanupWarnings;
            importedSourceCount = execution.Prepared?.SourceSet.SourceFiles.Count ?? 0;
        }
        catch (Exception error)
        {
            failure = error;
        }
        if (failure is not null)
        {
            var facts = WorkbookAutomationTerminalFacts.Analyze(failure, cancellationToken.IsCancellationRequested);
            var detail = completed is null ? failure.Message
                : $"Source workbook VBA code was prepared without Save, but a later preparation step failed: {context.TemplateDocumentPath}. Failure: {failure.Message}";
            var result = facts.Disposition == WorkbookAutomationDisposition.Cancelled
                ? CommandResult.Cancelled(detail) : CommandResult.UsageError(detail);
            return !facts.ProcessReleaseProven ? result.MarkOwnedProcessReleaseUnproven() : result;
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
            importedSourceFileCount = importedSourceCount,
            warnings = completed.Warnings.Concat(cleanupWarnings).ToArray()
        }) + Environment.NewLine, VbeImportWarningRenderer.Render(completed.Verification));
    }

}
