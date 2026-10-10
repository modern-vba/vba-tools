using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.Export;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Testing;

/// <summary>Prepares and tests the exact source workbook without tool-initiated Save.</summary>
internal sealed class SourceWorkbookTestCommand(
    WorkbookMaterializer materializer,
    ISourceWorkbookAutomation automation,
    WorkbookReferenceNormalizer referenceNormalizer,
    IExactFileSystemObjectOwnershipFactory ownershipFactory,
    TimeSpan? recoveryBudgetOverride = null,
    SnapshotTestExecutionWorkspaceFactory? snapshotWorkspaceFactory = null,
    Func<ResolvedProjectContext, string, VbaSourceAnalysisReport, string>? saveFailureEvidence = null)
{
    private readonly TimeSpan recoveryBudget = recoveryBudgetOverride ?? TimeSpan.FromMinutes(10);
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();
    private readonly TestProcedureSourceLocator sourceLocator = new();
    private readonly TestResultOutputFormatter outputFormatter = new();

    internal SourceWorkbookTestCommand WithSnapshotWorkspaceFactory(SnapshotTestExecutionWorkspaceFactory factory)
        => new(materializer, automation, referenceNormalizer, ownershipFactory, recoveryBudgetOverride, factory,
            saveFailureEvidence);

    internal async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        TestCommandRequest request,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        CancellationToken cancellationToken)
    {
        VbeImportSourceSet? sourceSet = null;
        WorkbookExportStaging? recovery = null;
        SourceWorkbookReplacement? replacement = null;
        PreparedSourceWorkbookBuild? prepared = null;
        ExecutedSourceIndex? executedSourceIndex = null;
        IReadOnlyList<WorkbookTestResultRow>? rows = null;
        Exception? failure = null;
        var retainScratch = false;
        SnapshotTestExecutionWorkspace? inputWorkspace = null;
        string? privateWorkspacePath = null;
        string cleanupWarnings = string.Empty;
        string preparationWarnings = string.Empty;
        try
        {
            if (request.SourceSnapshotPath is not null || request.BuildFirst)
            {
                if (request.SourceSnapshotPath is { } snapshot)
                {
                    inputWorkspace = snapshotWorkspaceFactory?.Create(context, snapshot,
                        Path.GetFileName(context.TemplateDocumentPath), cancellationToken);
                    privateWorkspacePath = inputWorkspace?.WorkspacePath;
                    var capture = inputWorkspace?.TakeSourceCapture()
                        ?? new BuildSourceSnapshotCaptureFactory(ownershipFactory).Create(snapshot, cancellationToken);
                    prepared = await materializer.PrepareSourceWorkbookTestSnapshotAsync(context,
                        capture, cancellationToken).ConfigureAwait(false);
                }
                else
                    prepared = await materializer.PrepareSourceWorkbookBuildAsync(context, cancellationToken).ConfigureAwait(false);
                sourceSet = prepared.SourceSet;
                executedSourceIndex = sourceLocator.CreateIndex(prepared.SourceAdmission,
                    request.SourceSnapshotPath ?? context.DocumentSourceSetPath, context.DocumentSourceSetPath);
                recovery = WorkbookExportStaging.Create(ownershipFactory);
                replacement = new SourceWorkbookReplacement(sourceSet, prepared.SourcePreflight,
                    recovery, ownershipFactory, recoveryBudget,
                    new SourceWorkbookReplacementDescription("Test", "VBA test execution", "already-open source workbook"));
            }
            var timeouts = prepared?.Timeouts ?? WorkbookAutomationTimeouts.Default with
            {
                WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest),
                WorkbookSave = CommandDefaultResolver.ResolveWorkbookSaveTimeout(context.Manifest)
            };
            rows = await automation.RunAsync(context.TemplateDocumentPath, timeouts,
                async (session, token) =>
                {
                    if (session is not IWorkbookTestExecutionSession tests)
                        throw new InvalidOperationException(
                            "The source workbook session does not support bound Test execution; the workbook was not reopened or changed.");
                    if (session.WasAlreadyOpen && !await session.IsSavedAsync(token).ConfigureAwait(false))
                    {
                        var action = request.BuildFirst
                            ? "replace its VBA code and run tests"
                            : "run its current VBA code without importing external sources";
                        var warning = $"Test will {action} in the already-open source workbook '{context.TemplateDocumentPath}'. Test does not save the workbook; test VBA can change it or explicitly save it. Continue?";
                        if (confirmUnsavedChanges is null)
                            throw new InvalidOperationException(
                                $"The source workbook has unsaved changes: {context.TemplateDocumentPath}. Explicit confirmation is required before Test can continue.");
                        if (!await confirmUnsavedChanges(warning, token).ConfigureAwait(false))
                            throw new InvalidOperationException(
                                $"Test was declined; no VBA import or test execution was attempted: {context.TemplateDocumentPath}");
                    }
                    var executionStarted = false;
                    var replacementSession = new SourceWorkbookReplacementSessionView(session);
                    try
                    {
                        if (prepared is not null)
                        {
                            var projectName = await session.GetProjectNameAsync(token).ConfigureAwait(false);
                            WorkbookMaterializer.VerifyAnalyzedProjectIdentity(prepared.SemanticInputs, projectName);
                            var modules = await session.GetModulesAsync(token).ConfigureAwait(false);
                            var references = await session.GetReferencesAsync(token).ConfigureAwait(false);
                            var desired = context.Document.References.Select(reference => reference.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            namePreflight.ThrowIfFailed(prepared.SourcePreflight,
                                namePreflight.InspectLivePhase(sourceSet!.SourceFiles,
                                    modules.Where(module => !module.Kind.IsImportable()).ToArray(),
                                    projectName, references.Where(reference => !reference.IsRemovable
                                        || desired.Contains(reference.Name)).ToArray()));
                            await replacement!.CaptureAsync(replacementSession, modules, references, token)
                                .ConfigureAwait(false);
                            var applied = await replacement.ReplaceAndVerifyAsync(replacementSession,
                                replacementToken => referenceNormalizer.NormalizeAsync(session,
                                    context.DocumentName, context.Document.References, replacementToken,
                                    prepared.SemanticInputs),
                                name => WorkbookMaterializer.VerifyAnalyzedProjectIdentity(prepared.SemanticInputs, name),
                                liveReferences => WorkbookMaterializer.VerifyAnalyzedReferences(prepared.SemanticInputs, liveReferences),
                                token).ConfigureAwait(false);
                            preparationWarnings = string.Concat(applied.Warnings.Select(warning => warning + Environment.NewLine))
                                + VbeImportWarningRenderer.Render(applied.Verification);
                        }
                        token.ThrowIfCancellationRequested();
                        // From this boundary, VBA may already have performed irreversible side effects.
                        executionStarted = true;
                        return await tests.RunTestsAsync(request.Selector, request.ExecutionTimeout, token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception operationFailure)
                    {
                        if (!executionStarted && replacement?.ReplacementStarted == true
                            && session.WasAlreadyOpen)
                            await replacement.RestoreAsync(replacementSession, operationFailure).ConfigureAwait(false);
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is SourceWorkbookTestInputPreparationException inputFailure)
            {
                cleanupWarnings += inputFailure.CleanupWarning;
                error = inputFailure.PreparationFailure;
            }
            if (error is SnapshotTestWorkspacePreparationException preparationFailure)
            {
                privateWorkspacePath = preparationFailure.WorkspacePath;
                cleanupWarnings += preparationFailure.CleanupWarning;
                error = preparationFailure.PreparationError;
            }
            failure = error;
            var facts = WorkbookAutomationTerminalFacts.Analyze(error, cancellationToken.IsCancellationRequested);
            retainScratch = !facts.ProcessReleaseProven || !facts.DispatcherRetired
                || !facts.ComReferenceReleaseProven;
        }

        foreach (var error in CleanupScratch(sourceSet, recovery, replacement, retainScratch))
            cleanupWarnings += $"Warning: Test staging cleanup failed; inspect retained paths: {error.Message}{Environment.NewLine}";
        if (inputWorkspace is not null)
        {
            if (retainScratch) inputWorkspace.RetainWithoutCleanup();
            else
            {
                try { cleanupWarnings += inputWorkspace.Cleanup().Warning; }
                catch (Exception cleanupError)
                {
                    cleanupWarnings += $"Input workspace cleanup failed: {inputWorkspace.WorkspacePath}. {cleanupError.Message}{Environment.NewLine}";
                    inputWorkspace.RetainWithoutCleanup();
                }
            }
        }
        if (failure is not null)
        {
            var analysis = FindSourceAnalysis(failure);
            var facts = WorkbookAutomationTerminalFacts.Analyze(failure, cancellationToken.IsCancellationRequested);
            var result = facts.Disposition == WorkbookAutomationDisposition.Cancelled
                ? CommandResult.Cancelled(facts.TypedCancellation is not null ? failure.Message
                    : "Workbook automation was cancelled during source Test preparation or execution.")
                : CommandResult.UsageError(facts.PrimaryFailure?.Category == WorkbookAutomationFailureCategory.ComFailure
                    ? CommandErrorMessages.ExcelComAutomationFailed("test", failure) : failure.Message);
            if (analysis is not null)
                result = result with
                {
                    StandardError = VbaSourceAnalysisOutput.Render(analysis.Report)
                        + (failure is VbaSourceAnalysisException { OperationalFailure: null }
                            ? string.Empty : result.StandardError)
                        + SaveFailureEvidence(context, analysis.Report)
                };
            result = result with
            {
                StandardError = result.StandardError + RenderComReleaseEvidence(failure) + preparationWarnings
            };
            if (privateWorkspacePath is not null)
                result = result with
                {
                    StandardOutput = TestCommand.SanitizeSnapshotOperationText(result.StandardOutput, privateWorkspacePath, true),
                    StandardError = TestCommand.SanitizeSnapshotOperationText(result.StandardError, privateWorkspacePath, true)
                };
            result = result with { StandardError = result.StandardError + cleanupWarnings };
            if (retainScratch)
            {
                if (!facts.ProcessReleaseProven) result = result.MarkOwnedProcessReleaseUnproven();
                var unproved = new List<string>();
                if (!facts.ProcessReleaseProven) unproved.Add("Excel process release");
                if (!facts.DispatcherRetired) unproved.Add("STA retirement");
                if (!facts.ComReferenceReleaseProven) unproved.Add("COM reference release");
                var retainedPaths = MirrorPaths(sourceSet)
                    .Concat(new[] { recovery?.Path, replacement?.RecoveryVerification?.Path, inputWorkspace?.WorkspacePath }
                        .OfType<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var retentionMessage = retainedPaths.Length == 0
                    ? $"{string.Join(" and ", unproved)} is unproved. No import or recovery staging was allocated."
                    : $"Test import and recovery staging were retained because {string.Join(" and ", unproved)} is unproved: {string.Join(", ", retainedPaths)}.";
                result = result with
                {
                    StandardError = result.StandardError + retentionMessage + Environment.NewLine
                };
            }
            return result;
        }
        if (rows is null) return CommandResult.UsageError("Test produced no verified completion.");
        var results = rows.Select(row => TestResultRecord.FromWorkbookRow(context.DocumentName, row))
            .Select(result => privateWorkspacePath is null ? result : result with
            {
                Category = TestCommand.SanitizeSnapshotOperationText(result.Category, privateWorkspacePath, false),
                TestName = TestCommand.SanitizeSnapshotOperationText(result.TestName, privateWorkspacePath, false),
                Message = TestCommand.SanitizeSnapshotOperationText(result.Message, privateWorkspacePath, false)
            }).ToArray();
        var located = executedSourceIndex is null ? results : sourceLocator.Locate(executedSourceIndex, results);
        var run = TestRun.FromResults(context.Manifest.ProjectName, context.DocumentName, located);
        var output = outputFormatter.Format(request.Format, run);
        var warnings = preparationWarnings + (prepared is null
            ? $"Warning: Source locations were omitted because --no-build runs an existing workbook without a proved source capture.{Environment.NewLine}"
            : string.Concat(located.Where(result => result.Location is null)
                .Select(result => $"{result.Category}.{result.TestName}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(identity => $"Warning: Source location for '{identity}' was omitted because it could not be mapped safely or unambiguously from the executed source capture to the persistent source set.{Environment.NewLine}")));
        return (run.HasFailures ? CommandResult.Failure(output) : CommandResult.Success(output))
            with { StandardError = warnings + cleanupWarnings };
    }

    private string SaveFailureEvidence(ResolvedProjectContext context, VbaSourceAnalysisReport report)
    {
        if (saveFailureEvidence is null || report.Complete) return string.Empty;
        try { return saveFailureEvidence(context, "test", report); }
        catch (Exception error)
        {
            return $"Source-analysis failure evidence could not be saved ({error.GetType().Name}). Retain the sourceAnalysis record from stderr.{Environment.NewLine}";
        }
    }

    private static string RenderComReleaseEvidence(Exception failure)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var messages = new List<string>();
        pending.Push(failure);
        while (pending.TryPop(out var error))
        {
            if (!seen.Add(error)) continue;
            if (error is WorkbookAutomationComReferenceReleaseException release)
            {
                if (release.OperationError is { } original
                    && !failure.Message.Contains(original.Message, StringComparison.Ordinal))
                    messages.Add($"Original Test operation failure: {original.Message}");
                messages.AddRange(release.ReleaseFailures.Select(item =>
                    $"COM reference release failure ('{item.ReferenceName}'): {item.Error.Message}"));
            }
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Reverse()) pending.Push(inner);
            else if (error.InnerException is { } inner) pending.Push(inner);
        }
        return messages.Count == 0 ? string.Empty : string.Join(Environment.NewLine, messages) + Environment.NewLine;
    }

    private static VbaSourceAnalysisException? FindSourceAnalysis(Exception failure)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.TryPop(out var error))
        {
            if (!seen.Add(error)) continue;
            if (error is VbaSourceAnalysisException analysis) return analysis;
            if (error is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Reverse()) pending.Push(inner);
            else if (error.InnerException is { } inner) pending.Push(inner);
        }
        return null;
    }

    private static IEnumerable<string> MirrorPaths(VbeImportSourceSet? sourceSet)
        => sourceSet?.SourceFiles.SelectMany(file => file.BinaryPath is null
            ? new[] { file.SourcePath } : new[] { file.SourcePath, file.BinaryPath! }) ?? [];

    private static IReadOnlyList<Exception> CleanupScratch(VbeImportSourceSet? sourceSet,
        WorkbookExportStaging? recovery, SourceWorkbookReplacement? replacement, bool retain)
    {
        var failures = new List<Exception>();
        void Release(WorkbookExportStaging? staging, bool keep)
        {
            if (staging is null) return;
            if (!keep)
            {
                try
                {
                    var cleanup = staging.Cleanup();
                    if (cleanup.Status != InvocationScratchCleanupStatus.Removed)
                        throw new IOException($"Test recovery cleanup was {cleanup.Status}: {string.Join(", ", cleanup.RetainedPaths)}");
                }
                catch (Exception error) { failures.Add(error); }
            }
            try { staging.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        }
        Release(replacement?.RecoveryVerification, retain);
        Release(recovery, retain || replacement?.RetainRecovery == true);
        if (sourceSet is not null)
        {
            try
            {
                if (retain) sourceSet.RetainWithoutCleanup();
                else sourceSet.Dispose();
            }
            catch (Exception error) { failures.Add(error); }
        }
        return failures;
    }
}
