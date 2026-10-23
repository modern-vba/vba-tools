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
        ISourceWorkbookSession? observedSession = null;
        var execution = await SourceWorkbookReplacement.RunAsync(
            token => materializer.PrepareSourceWorkbookBuildAsync(context, token),
            context.TemplateDocumentPath, automation, ownershipFactory,
            async (prepared, replacement, session, token) =>
            {
                var sourceSet = prepared.SourceSet;
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
                    return (applied.Warnings, applied.Verification);
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
            }, cancellationToken, recoveryBudget).ConfigureAwait(false);
        if (execution.Failure is { } failure)
        {
            if (observedSession?.SaveState == SourceWorkbookSaveState.Unknown)
                throw new BuildCommandException(
                    $"Source workbook Save outcome is unknown: {context.TemplateDocumentPath}. Inspect the workbook before rebuilding. Failure: {failure.Message}", failure);
            if (observedSession?.SaveState == SourceWorkbookSaveState.Saved)
                throw new BuildCommandException(
                    $"Source workbook Save completed, but a later Build step failed: {context.TemplateDocumentPath}. Failure: {failure.Message}", failure);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (!execution.CallbackReturned)
            throw new BuildCommandException("Source workbook Build produced no verified completion.");
        return new WorkbookMaterializationResult(
            context.TemplateDocumentPath,
            execution.Prepared!.SourceSet.SourceFiles.Count,
            execution.Completed.Item1.Concat(execution.CleanupWarnings).ToArray(),
            execution.Completed.Item2,
            ((PreparedSourceWorkbookBuild)execution.Prepared!).SourceAdmission);
    }
}
