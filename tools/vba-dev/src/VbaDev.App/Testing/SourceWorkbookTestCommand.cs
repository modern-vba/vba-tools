using System.Text.RegularExpressions;
using VbaDev.App.Build;
using VbaDev.App.Cli;
using VbaDev.App.FileSystem;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Testing;

/// <summary>Prepares and tests the exact source workbook without tool-initiated Save.</summary>
public sealed class SourceWorkbookTestCommand
{
    private readonly WorkbookMaterializer materializer;
    private readonly ISourceWorkbookAutomation automation;
    private readonly WorkbookReferenceNormalizer referenceNormalizer;
    private readonly IExactFileSystemObjectOwnershipFactory ownershipFactory;
    private readonly SnapshotTestExecutionWorkspaceFactory snapshotWorkspaceFactory;
    private readonly Func<ResolvedProjectContext, string, VbaSourceAnalysisReport, string>? saveFailureEvidence;

    private readonly TimeSpan recoveryBudget;
    private readonly WorkbookMaterializationNamePreflight namePreflight = new();
    private readonly TestProcedureSourceLocator sourceLocator = new();
    private readonly TestResultOutputFormatter outputFormatter = new();

    internal SourceWorkbookTestCommand(WorkbookMaterializer materializer, ISourceWorkbookAutomation automation,
        WorkbookReferenceNormalizer referenceNormalizer, IExactFileSystemObjectOwnershipFactory ownershipFactory,
        SnapshotTestExecutionWorkspaceFactory snapshotWorkspaceFactory,
        TimeSpan? recoveryBudgetOverride = null,
        Func<ResolvedProjectContext, string, VbaSourceAnalysisReport, string>? saveFailureEvidence = null)
    {
        this.materializer = materializer;
        this.automation = automation;
        this.referenceNormalizer = referenceNormalizer;
        this.ownershipFactory = ownershipFactory;
        this.snapshotWorkspaceFactory = snapshotWorkspaceFactory ?? throw new ArgumentNullException(nameof(snapshotWorkspaceFactory));
        this.saveFailureEvidence = saveFailureEvidence;
        recoveryBudget = recoveryBudgetOverride ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>Runs tests on the exact selected source session without a tool-controlled Save.</summary>
    public CommandResult Run(ResolvedProjectContext context, TestCommandRequest request)
        => RunAsync(context, request, CancellationToken.None).GetAwaiter().GetResult();

    public Task<CommandResult> RunAsync(ResolvedProjectContext context, TestCommandRequest request,
        CancellationToken cancellationToken)
        => RunAsync(context, request, null, cancellationToken);

    /// <summary>Runs source tests with mode-specific unsaved-workbook confirmation.</summary>
    public async Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        TestCommandRequest request,
        Func<string, CancellationToken, Task<bool>>? confirmUnsavedChanges,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> scratchPaths = [];
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
            var timeouts = WorkbookAutomationTimeouts.Default with
            {
                WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest),
                WorkbookSave = CommandDefaultResolver.ResolveWorkbookSaveTimeout(context.Manifest)
            };
            var execution = await SourceWorkbookReplacement.RunOptionalAsync(
                async token =>
                {
                    if (request.SourceSnapshotPath is { } snapshot)
                    {
                        inputWorkspace = snapshotWorkspaceFactory.Create(context, snapshot,
                            Path.GetFileName(context.TemplateDocumentPath), token);
                        privateWorkspacePath = inputWorkspace.WorkspacePath;
                        var capture = inputWorkspace.TakeSourceCapture();
                        return await materializer.PrepareSourceWorkbookTestSnapshotAsync(context, capture, token)
                            .ConfigureAwait(false);
                    }
                    return request.BuildFirst
                        ? await materializer.PrepareSourceWorkbookBuildAsync(context, token).ConfigureAwait(false)
                        : null;
                }, context.TemplateDocumentPath, automation, ownershipFactory,
                async (input, replacement, session, token) =>
                {
                    var prepared = (PreparedSourceWorkbookBuild?)input;
                    if (prepared is not null)
                        executedSourceIndex = sourceLocator.CreateIndex(prepared.SourceAdmission,
                            request.SourceSnapshotPath ?? context.DocumentSourceSetPath, context.DocumentSourceSetPath);
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
                                namePreflight.InspectLivePhase(prepared.SourceSet.SourceFiles,
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
                }, cancellationToken, timeouts, recoveryBudget,
                new SourceWorkbookReplacementDescription("Test", "VBA test execution", "already-open source workbook"))
                .ConfigureAwait(false);
            rows = execution.Completed;
            failure = execution.Failure;
            retainScratch = execution.RetainDependentScratch;
            scratchPaths = execution.ScratchPaths;
            cleanupWarnings += string.Concat(execution.CleanupWarnings.Select(warning => warning + Environment.NewLine));
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
                    StandardOutput = SanitizeSnapshotOperationText(result.StandardOutput, privateWorkspacePath, true),
                    StandardError = SanitizeSnapshotOperationText(result.StandardError, privateWorkspacePath, true)
                };
            result = result with { StandardError = result.StandardError + cleanupWarnings };
            if (retainScratch)
            {
                if (!facts.ProcessReleaseProven) result = result.MarkOwnedProcessReleaseUnproven();
                var unproved = new List<string>();
                if (!facts.ProcessReleaseProven) unproved.Add("Excel process release");
                if (!facts.DispatcherRetired) unproved.Add("STA retirement");
                if (!facts.ComReferenceReleaseProven) unproved.Add("COM reference release");
                var retainedPaths = scratchPaths.Concat(new[] { inputWorkspace?.WorkspacePath }.OfType<string>())
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
                Category = SanitizeSnapshotOperationText(result.Category, privateWorkspacePath, false),
                TestName = SanitizeSnapshotOperationText(result.TestName, privateWorkspacePath, false),
                Message = SanitizeSnapshotOperationText(result.Message, privateWorkspacePath, false)
            }).ToArray();
        var located = executedSourceIndex is null ? results : sourceLocator.Locate(executedSourceIndex, results);
        var run = TestRun.FromResults(context.Manifest.ProjectName, context.DocumentName, located);
        var output = outputFormatter.Format(request.Format, run);
        var warnings = preparationWarnings + (!request.BuildFirst && request.SourceSnapshotPath is null
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


    internal static string SanitizeSnapshotOperationText(
        string text,
        string workspacePath,
        bool redactKnownTemporaryRoots)
    {
        var normalizedWorkspacePath = Path.GetFullPath(workspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitized = ReplacePrivatePathForms(
            text,
            normalizedWorkspacePath,
            "<snapshot-test-workspace>");
        if (!redactKnownTemporaryRoots)
        {
            return sanitized;
        }

        sanitized = ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-build-source-snapshot"),
            "<build-source-snapshot>");
        sanitized = ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import"),
            "<vbe-import-staging>");
        return ReplacePrivateGuidRoot(
            sanitized,
            Path.Combine(Path.GetTempPath(), "vba-dev-vbe-import", "source-workbook-build"),
            "<vbe-import-staging>");
    }

    private static string ReplacePrivatePathForms(
        string text,
        string normalizedPath,
        string replacement)
    {
        var sanitized = text.Replace(
            normalizedPath,
            replacement,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        return sanitized.Replace(
            new Uri(normalizedPath).AbsoluteUri.TrimEnd('/'),
            replacement,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplacePrivateGuidRoot(
        string text,
        string privateRoot,
        string replacement)
    {
        var normalizedRoot = Path.GetFullPath(privateRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var sanitized = ReplacePrivateGuidRootForm(
            text,
            normalizedRoot,
            $"[{Regex.Escape(string.Concat(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))}]",
            replacement);
        return ReplacePrivateGuidRootForm(
            sanitized,
            new Uri(normalizedRoot).AbsoluteUri.TrimEnd('/'),
            "/",
            replacement);
    }

    private static string ReplacePrivateGuidRootForm(
        string text,
        string privateRoot,
        string separatorPattern,
        string replacement)
    {
        var pattern = Regex.Escape(privateRoot)
            + separatorPattern
            + "[0-9a-f]{32}";
        return Regex.Replace(
            text,
            pattern,
            replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
