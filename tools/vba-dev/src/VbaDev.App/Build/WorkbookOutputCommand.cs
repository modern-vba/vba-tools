using System.Text;
using System.Runtime.ExceptionServices;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Runs workbook output generation for build-like commands.
/// </summary>
internal sealed class WorkbookOutputCommand
{
    private readonly WorkbookMaterializer materializer;
    private readonly Func<ResolvedProjectContext, string, VbaSourceAnalysisReport, string>? saveFailureEvidence;

    internal WorkbookOutputCommand(WorkbookMaterializer materializer,
        Func<ResolvedProjectContext, string, VbaSourceAnalysisReport, string>? saveFailureEvidence = null)
    {
        this.materializer = materializer;
        this.saveFailureEvidence = saveFailureEvidence;
    }

    internal CommandResult RunBuild(ResolvedProjectContext context)
        => RunBuildAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    internal async Task<CommandResult> RunBuildAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => (await RunBuildCoreAsync(context, cancellationToken)
            .ConfigureAwait(false)).CommandResult;

    internal async Task<TestWorkbookBuildCommandResult> RunTestBuildIntentAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => CreateTestWorkbookBuildResult(
            await RunBuildCoreAsync(context, cancellationToken)
                .ConfigureAwait(false));

    internal CommandResult RunPublish(ResolvedProjectContext context)
        => RunPublishAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<CommandResult> RunPublishAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => RunCommandAsync(
            context,
            operationName: "publish",
            displayName: "Publish",
            completedVerb: "Published",
            () => materializer.MaterializeAsync(
                new WorkbookMaterializationIntent.Publish(context),
                cancellationToken),
            cancellationToken);

    internal Task<CommandResult> RunSnapshotBuildAsync(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        string outputPath,
        BuildSourceSnapshotCaptureFactory captureFactory,
        BuildSourceSnapshotOutputSafetyValidator outputSafetyValidator,
        CancellationToken cancellationToken)
        => RunCommandAsync(
            context,
            operationName: "build",
            displayName: "Build",
            completedVerb: "Built",
            async () =>
            {
                var validatedPaths = outputSafetyValidator.Validate(
                    context,
                    sourceSnapshotPath,
                    outputPath);
                using var sourceCapture = captureFactory.Create(
                        validatedPaths.SourceSnapshotPath,
                        cancellationToken);
                return await MaterializeSourceSnapshotAsync(
                        context,
                        sourceCapture,
                    validatedPaths.OutputPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    internal async Task<TestWorkbookBuildCommandResult> RunSnapshotIntentAsync(
        ResolvedProjectContext context,
        BuildSourceSnapshotCapture sourceCapture,
        string targetWorkbookPath,
        CancellationToken cancellationToken)
    {
        WorkbookOutputExecution? execution = null;
        try
        {
            using (sourceCapture)
            {
                execution = await RunAsyncCore(
                        context,
                        operationName: "build",
                        displayName: "Build",
                        completedVerb: "Built",
                        () => MaterializeSourceSnapshotAsync(
                            context,
                            sourceCapture,
                            targetWorkbookPath,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException cleanupError) when (execution is not null)
        {
            execution = execution with
            {
                CommandResult = execution.CommandResult with
                {
                    StandardError = execution.CommandResult.StandardError
                        + cleanupError.Message
                        + Environment.NewLine
                }
            };
        }

        return CreateTestWorkbookBuildResult(execution!);
    }

    private Task<WorkbookOutputExecution> RunBuildCoreAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => RunAsyncCore(
            context,
            operationName: "build",
            displayName: "Build",
            completedVerb: "Built",
            () => materializer.MaterializeAsync(
                new WorkbookMaterializationIntent.ProjectBuild(context),
                cancellationToken),
            cancellationToken);

    private async Task<CommandResult> RunCommandAsync(
        ResolvedProjectContext context,
        string operationName,
        string displayName,
        string completedVerb,
        Func<Task<WorkbookMaterializationResult>> materialize,
        CancellationToken cancellationToken)
        => (await RunAsyncCore(
                context,
                operationName,
                displayName,
                completedVerb,
                materialize,
                cancellationToken)
            .ConfigureAwait(false)).CommandResult;

    private async Task<WorkbookOutputExecution> RunAsyncCore(
        ResolvedProjectContext context,
        string operationName,
        string displayName,
        string completedVerb,
        Func<Task<WorkbookMaterializationResult>> materialize,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Failed(CommandResult.Cancelled(
                    "Workbook automation was cancelled during Excel startup."));
            }

            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return Failed(CommandResult.UsageError(
                    $"{displayName} supports only Excel documents: {context.DocumentName}"));
            }

            var result = await materialize().ConfigureAwait(false);

            return new WorkbookOutputExecution(
                new CommandResult(
                    0,
                    RenderOutput(
                        completedVerb,
                        result.CommittedArtifactPath,
                        result.ImportedSourceCount,
                        result.Warnings),
                    VbaSourceAnalysisOutput.Render(result.SourceAdmission.Analysis)
                        + VbeImportWarningRenderer.Render(result.VerificationReport)),
                result);
        }
        catch (VbaSourceAnalysisException ex) when (ex.OperationalFailure is null)
        {
            return Failed(new CommandResult(1, string.Empty, VbaSourceAnalysisOutput.Render(ex.Report)
                + SaveFailureEvidence(context, operationName, ex.Report)));
        }
        catch (Exception caught)
        {
            var sourceAnalysis = caught as VbaSourceAnalysisException;
            var ex = sourceAnalysis?.OperationalFailure ?? caught;
            var facts = WorkbookAutomationTerminalFacts.Analyze(ex, cancellationToken.IsCancellationRequested);
            CommandResult result;
            if (facts.Disposition == WorkbookAutomationDisposition.Cancelled)
            {
                result = CommandResult.Cancelled(facts.TypedCancellation is null
                    ? "Workbook automation was cancelled during the active generation stage."
                    : ex.Message);
            }
            else if (facts.Disposition == WorkbookAutomationDisposition.Failed)
            {
                result = CommandResult.UsageError(facts.PrimaryFailure!.Category == WorkbookAutomationFailureCategory.ComFailure
                    ? CommandErrorMessages.ExcelComAutomationFailed(operationName, ex)
                    : ex.Message);
            }
            else if (sourceAnalysis is not null)
            {
                // Required-input failure must retain its incomplete report even
                // when the cause is unknown or has no trusted cancellation authority.
                result = CommandResult.UsageError(ex.Message);
            }
            else if (facts.IsUntrustedCancellation)
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }
            else if (ex is BuildCommandException or CommonModulesManifestException
                or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                result = CommandResult.UsageError(ex.Message);
            }
            else
            {
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            if (sourceAnalysis is not null)
            {
                result = result with { StandardError = VbaSourceAnalysisOutput.Render(sourceAnalysis.Report)
                    + result.StandardError + SaveFailureEvidence(context, operationName, sourceAnalysis.Report) };
            }
            return Failed(PreserveReleaseProof(facts, result));
        }
    }

    private string SaveFailureEvidence(ResolvedProjectContext context, string operation, VbaSourceAnalysisReport report)
    {
        if (saveFailureEvidence is null || report.Complete) return string.Empty;
        try { return saveFailureEvidence(context, operation, report); }
        catch (Exception error)
        {
            // Evidence is best-effort; the original report and lifecycle disposition remain authoritative.
            return $"Source-analysis failure evidence could not be saved ({error.GetType().Name}). "
                + "Retain the sourceAnalysis record from stderr." + Environment.NewLine;
        }
    }

    private static WorkbookOutputExecution Failed(CommandResult result)
        => new(result, Materialization: null);

    private Task<WorkbookMaterializationResult> MaterializeSourceSnapshotAsync(
        ResolvedProjectContext context,
        BuildSourceSnapshotCapture sourceCapture,
        string targetDocumentPath,
        CancellationToken cancellationToken)
        => materializer.MaterializeAsync(
            new WorkbookMaterializationIntent.SourceSnapshotBuild(
                context,
                sourceCapture,
                targetDocumentPath),
            cancellationToken);

    private static CommandResult PreserveReleaseProof(WorkbookAutomationTerminalFacts facts, CommandResult result)
        => facts.ProcessReleaseProven ? result : result.MarkOwnedProcessReleaseUnproven();

    private static string RenderOutput(
        string completedVerb,
        string targetDocumentPath,
        int importedSourceCount,
        IReadOnlyList<string> warnings)
    {
        var output = new StringBuilder();
        output.AppendLine($"{completedVerb} {targetDocumentPath}");
        output.AppendLine($"Imported {importedSourceCount} source files.");
        foreach (var warning in warnings)
        {
            output.AppendLine(warning);
        }

        return output.ToString();
    }

    private static TestWorkbookBuildCommandResult CreateTestWorkbookBuildResult(
        WorkbookOutputExecution execution)
        => new(
            execution.CommandResult,
            execution.Materialization?.CommittedArtifactPath,
            execution.Materialization?.SourceAdmission);

    private sealed record WorkbookOutputExecution(
        CommandResult CommandResult,
        WorkbookMaterializationResult? Materialization);
}

internal sealed record TestWorkbookBuildCommandResult(
    CommandResult CommandResult,
    string? CommittedArtifactPath,
    AdmittedVbaSourceSet? SourceAdmission);
