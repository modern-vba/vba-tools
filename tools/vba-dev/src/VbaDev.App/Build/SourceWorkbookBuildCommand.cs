using System.Globalization;
using System.Text;
using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaTools.Syntax;

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
        WorkbookExportStaging? recoveryVerification = null;
        var retainRecovery = false;
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

                        var replaceableModules = modules.Where(module => module.Kind.IsImportable()).ToArray();
                        foreach (var module in replaceableModules)
                        {
                            await recovery.WriteModuleAsync(module.Name + SourceExtension(module.Kind),
                                path => session.ExportModuleAsync(module.Name, path, token)).ConfigureAwait(false);
                        }
                        recovery.CompleteProduction();
                        recovery.ProveUnchanged();
                        foreach (var form in replaceableModules.Where(module => module.Kind == WorkbookModuleKind.Form))
                            ValidateCapturedFormRecovery(form.Name, recovery.Path, sourceSet.ActiveCodePage);
                        foreach (var reference in references.Where(reference => reference.IsRemovable))
                        {
                            if (string.IsNullOrWhiteSpace(reference.Guid)
                                || reference.Major is null || reference.Minor is null)
                                throw new BuildCommandException(
                                    $"Build cannot replace workbook references because the existing reference '{reference.Name}' lacks a recoverable GUID/version identity.");
                        }
                        var replacementStarted = false;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            replacementStarted = true;
                            foreach (var module in replaceableModules)
                                await session.RemoveModuleAsync(module.Name, token).ConfigureAwait(false);
                            var warnings = await referenceNormalizer.NormalizeAsync(
                        session, context.DocumentName, context.Document.References,
                        token, prepared.SemanticInputs).ConfigureAwait(false);

                            var finalProjectName = await session.GetProjectNameAsync(token).ConfigureAwait(false);
                            WorkbookMaterializer.VerifyAnalyzedProjectIdentity(prepared.SemanticInputs, finalProjectName);
                            var finalModules = await session.GetModulesAsync(token).ConfigureAwait(false);
                            var finalReferences = await session.GetReferencesAsync(token).ConfigureAwait(false);
                            WorkbookMaterializer.VerifyAnalyzedReferences(prepared.SemanticInputs, finalReferences);
                            namePreflight.ThrowIfFailed(prepared.SourcePreflight,
                        namePreflight.InspectLivePhase(sourceSet.SourceFiles, finalModules,
                            finalProjectName, finalReferences));

                            foreach (var sourceFile in sourceSet.SourceFiles)
                                await session.ImportModuleAsync(sourceFile, token).ConfigureAwait(false);
                            var verification = await session.VerifyAsync(token).ConfigureAwait(false)
                        ?? throw new WorkbookVerificationReportMissingException();
                            var committedProjectName = await session.GetProjectNameAsync(token).ConfigureAwait(false);
                            WorkbookMaterializer.VerifyAnalyzedProjectIdentity(
                        prepared.SemanticInputs, committedProjectName);
                            var committedModules = await session.GetModulesAsync(token).ConfigureAwait(false);
                            var importedNames = sourceSet.SourceFiles
                        .Select(sourceFile => sourceFile.ImportVerification.ComponentName)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var committedRetainedModules = committedModules
                        .Where(module => !importedNames.Contains(module.Name)).ToArray();
                            var committedReferences = await session.GetReferencesAsync(token).ConfigureAwait(false);
                            WorkbookMaterializer.VerifyAnalyzedReferences(
                        prepared.SemanticInputs, committedReferences);
                            namePreflight.ThrowIfFailed(prepared.SourcePreflight,
                        namePreflight.InspectLivePhase(sourceSet.SourceFiles,
                            committedRetainedModules, committedProjectName, committedReferences));
                            token.ThrowIfCancellationRequested();
                            await session.SaveAsync(token).ConfigureAwait(false);
                            if (session.SaveState != SourceWorkbookSaveState.Saved)
                                throw new BuildCommandException(
                            $"Source workbook Save outcome is unknown: {context.TemplateDocumentPath}");
                            completedOperation = (warnings, verification);
                            return completedOperation.Value;
                        }
                        catch (Exception operationFailure)
                        {
                            if (replacementStarted && session.WasAlreadyOpen
                                && session.SaveState == SourceWorkbookSaveState.NotStarted)
                            {
                                using var recoveryDeadline = new CancellationTokenSource(recoveryBudget);
                                var recoveryToken = recoveryDeadline.Token;
                                try
                                {
                                    var currentModules = await session.GetModulesAsync(recoveryToken)
                                        .ConfigureAwait(false);
                                    foreach (var module in currentModules.Where(module => module.Kind.IsImportable()))
                                    {
                                        recoveryToken.ThrowIfCancellationRequested();
                                        await session.RemoveModuleAsync(module.Name, recoveryToken)
                                            .ConfigureAwait(false);
                                    }
                                    recoveryToken.ThrowIfCancellationRequested();
                                    await RestoreReferencesAsync(session, references, recoveryToken)
                                        .ConfigureAwait(false);
                                    foreach (var module in replaceableModules)
                                    {
                                        recoveryToken.ThrowIfCancellationRequested();
                                        var sourcePath = Path.Combine(recovery.Path,
                                            module.Name + SourceExtension(module.Kind));
                                        await session.ImportModuleAsync(new VbeImportSourceFile(
                                            sourcePath, SourceKind(module.Kind),
                                            module.Kind == WorkbookModuleKind.Form &&
                                            File.Exists(Path.ChangeExtension(sourcePath, ".frx"))
                                                ? Path.ChangeExtension(sourcePath, ".frx") : null,
                                            new VbeImportVerification(module.Name, SourceKind(module.Kind),
                                                [], "recovery")), recoveryToken).ConfigureAwait(false);
                                    }
                                    recoveryToken.ThrowIfCancellationRequested();
                                    recoveryVerification = WorkbookExportStaging.Create(ownershipFactory);
                                    await VerifyRestoredWorkbookAsync(session, modules, references,
                                        replaceableModules, recovery, recoveryVerification,
                                        recoveryToken).ConfigureAwait(false);
                                }
                                catch (Exception recoveryFailure)
                                {
                                    retainRecovery = true;
                                    var deadline = recoveryDeadline.IsCancellationRequested
                                        ? $" The {recoveryBudget.TotalMinutes:0.###}-minute recovery deadline expired."
                                        : string.Empty;
                                    throw new BuildCommandException(
                                        $"Build failed before Save and workbook recovery was incomplete.{deadline} The already-open workbook may be partially changed. Recovery files: {recovery.Path}. Restore modules manually from that directory, then inspect workbook references. Original failure: {operationFailure.Message} Recovery failure: {recoveryFailure.Message}",
                                        new AggregateException(operationFailure, recoveryFailure));
                                }
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
                        if (recoveryVerification is not null)
                        {
                            try
                            {
                                var verificationCleanup = recoveryVerification.Cleanup();
                                if (verificationCleanup.Status != InvocationScratchCleanupStatus.Removed)
                                    throw new IOException(
                                        $"Recovery verification staging cleanup was {verificationCleanup.Status}. Retained paths: {string.Join(", ", verificationCleanup.RetainedPaths)}");
                            }
                            catch (Exception cleanupError)
                            {
                                reportError = CombineCleanupFailure(reportError, cleanupError,
                                    "Recovery verification staging cleanup", recoveryVerification.Path);
                            }
                        }
                        if (!retainRecovery)
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
                    $"{error.Message} Excel-dependent Build source mirror and recovery files were retained because workbook automation release or STA retirement could not be proved: {string.Join(", ", sourceMirrorFiles)}, {recovery.Path}{(recoveryVerification is null ? string.Empty : ", " + recoveryVerification.Path)}. Do not remove these paths until the pending Excel automation is no longer using them.",
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
                recoveryVerification?.Dispose();
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
                    : $" Recovery verification staging release failed: {recoveryVerification?.Path}. Failure: {verificationReleaseFailure.Message}";
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

    private static async Task VerifyRestoredWorkbookAsync(
        ISourceWorkbookSession session,
        IReadOnlyList<WorkbookModule> originalModules,
        IReadOnlyList<WorkbookReference> originalReferences,
        IReadOnlyList<WorkbookModule> replaceableModules,
        WorkbookExportStaging originalExport,
        WorkbookExportStaging restoredExport,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restoredModules = await session.GetModulesAsync(cancellationToken)
            .ConfigureAwait(false);
        static string[] ModuleKeys(IReadOnlyList<WorkbookModule> modules)
            => modules.Select(module => $"{(int)module.Kind}:{module.Name}")
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!ModuleKeys(originalModules).SequenceEqual(
                ModuleKeys(restoredModules), StringComparer.OrdinalIgnoreCase))
            throw new BuildCommandException(
                "The restored workbook module identities or kinds differ from the pre-Build capture.");

        cancellationToken.ThrowIfCancellationRequested();
        var restoredReferences = await session.GetReferencesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (restoredReferences.Count != originalReferences.Count ||
            originalReferences.Where((before, index) =>
                !ReferenceIdentityEquals(before, restoredReferences[index])).Any())
            throw new BuildCommandException(
                "The restored workbook reference identities or priority order differ from the pre-Build capture.");

        foreach (var module in replaceableModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = module.Name + SourceExtension(module.Kind);
            await restoredExport.WriteModuleAsync(fileName,
                path => session.ExportModuleAsync(module.Name, path, cancellationToken))
                .ConfigureAwait(false);
        }
        restoredExport.CompleteProduction();
        restoredExport.ProveUnchanged();
        foreach (var module in replaceableModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = module.Name + SourceExtension(module.Kind);
            var before = Path.Combine(originalExport.Path, fileName);
            var after = Path.Combine(restoredExport.Path, fileName);
            if (!File.ReadAllBytes(before).AsSpan().SequenceEqual(File.ReadAllBytes(after)))
                throw new BuildCommandException(
                    $"The restored workbook module '{module.Name}' differs from the pre-Build capture.");
            if (module.Kind != WorkbookModuleKind.Form) continue;
            var beforeSidecar = Path.ChangeExtension(before, ".frx");
            var afterSidecar = Path.ChangeExtension(after, ".frx");
            var beforeExists = File.Exists(beforeSidecar);
            var afterExists = File.Exists(afterSidecar);
            if (beforeExists != afterExists || beforeExists &&
                !File.ReadAllBytes(beforeSidecar).AsSpan()
                    .SequenceEqual(File.ReadAllBytes(afterSidecar)))
                throw new BuildCommandException(
                    $"The restored UserForm '{module.Name}' sidecar differs from the pre-Build capture.");
        }
    }

    private static string SourceExtension(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => ".bas",
            WorkbookModuleKind.ClassModule => ".cls",
            WorkbookModuleKind.Form => ".frm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static VbaSourceKind SourceKind(WorkbookModuleKind kind)
        => kind switch
        {
            WorkbookModuleKind.StandardModule => VbaSourceKind.StandardModule,
            WorkbookModuleKind.ClassModule => VbaSourceKind.ClassModule,
            WorkbookModuleKind.Form => VbaSourceKind.Form,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static async Task RestoreReferencesAsync(
        ISourceWorkbookSession session,
        IReadOnlyList<WorkbookReference> original,
        CancellationToken cancellationToken)
    {
        var current = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reference in current)
        {
            if (original.Any(before => ReferenceIdentityEquals(before, reference))) continue;
            if (!reference.IsRemovable
                || !await session.RemoveReferenceAsync(reference.Name, cancellationToken).ConfigureAwait(false))
                throw new BuildCommandException(
                    $"Workbook reference '{reference.Name}' could not be removed during recovery.");
        }
        var remaining = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var reference in original)
        {
            if (remaining.Any(after => ReferenceIdentityEquals(reference, after))) continue;
            if (string.IsNullOrWhiteSpace(reference.Guid)
                || reference.Major is null || reference.Minor is null)
                throw new BuildCommandException(
                    $"Workbook reference '{reference.Name}' has no recoverable GUID/version identity.");
            await session.AddReferenceAsync(new ResolvedVbaProjectReference(
                reference.Name, reference.Guid, reference.Major.Value, reference.Minor.Value),
                cancellationToken).ConfigureAwait(false);
        }
        var restored = await session.GetReferencesAsync(cancellationToken).ConfigureAwait(false);
        if (restored.Count != original.Count || original.Where((before, index) =>
                !ReferenceIdentityEquals(before, restored[index])).Any())
            throw new BuildCommandException(
                "Workbook reference identities or priority order could not be verified after recovery.");
    }

    private static bool ReferenceIdentityEquals(WorkbookReference left, WorkbookReference right)
        => left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Guid, right.Guid, StringComparison.OrdinalIgnoreCase)
            && left.Major == right.Major
            && left.Minor == right.Minor
            && string.Equals(left.NamespaceName, right.NamespaceName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCapturedFormRecovery(string moduleName, string recoveryPath, int codePage)
    {
        var formPath = Path.Combine(recoveryPath, moduleName + ".frm");
        var sidecarPath = Path.Combine(recoveryPath, moduleName + ".frx");
        string formText;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(codePage,
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            formText = File.ReadAllText(formPath, encoding);
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            throw new BuildCommandException(
                $"The existing UserForm '{moduleName}' could not be captured for recovery: {formPath}", error);
        }

        var designer = VbaSyntaxTree.ParseModule(new Uri(formPath).AbsoluteUri, formText)
            .Module.FormDesignerBlock;
        if (designer is null || designer.EvidenceProblems.Count > 0)
            throw new BuildCommandException(
                $"The existing UserForm '{moduleName}' has incomplete designer recovery evidence: {formPath}");
        foreach (var resource in designer.ResourceReferences)
        {
            if (!resource.FileName.Equals(moduleName + ".frx", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(sidecarPath))
                throw new BuildCommandException(
                    $"The existing UserForm '{moduleName}' declares a required recovery sidecar that was not captured: {sidecarPath}");
            if (!long.TryParse(resource.Offset, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var offset)
                || offset >= new FileInfo(sidecarPath).Length)
                throw new BuildCommandException(
                    $"The captured UserForm sidecar is incomplete at offset {resource.Offset}: {sidecarPath}");
        }
    }
}
