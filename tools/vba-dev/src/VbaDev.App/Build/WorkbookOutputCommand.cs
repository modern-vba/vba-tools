using System.Runtime.InteropServices;
using System.Text;
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

    internal WorkbookOutputCommand(WorkbookMaterializer materializer)
    {
        this.materializer = materializer;
    }

    internal CommandResult RunBuild(ResolvedProjectContext context)
        => RunBuildAsync(context, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<CommandResult> RunBuildAsync(
        ResolvedProjectContext context,
        CancellationToken cancellationToken)
        => RunCommandAsync(
            context,
            operationName: "build",
            displayName: "Build",
            completedVerb: "Built",
            () => materializer.MaterializeAsync(
                new WorkbookMaterializationIntent.ProjectBuild(context),
                cancellationToken),
            cancellationToken);

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

    internal async Task<SourceSnapshotBuildCommandResult> RunSnapshotIntentAsync(
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

        return new SourceSnapshotBuildCommandResult(
            execution!.CommandResult,
            execution.Materialization?.CommittedArtifactPath);
    }

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
                    VbeImportWarningRenderer.Render(result.VerificationReport)),
                result);
        }
        catch (WorkbookAutomationCanceledException ex)
        {
            return Failed(PreserveReleaseProof(ex, CommandResult.Cancelled(ex.Message)));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(PreserveReleaseProof(
                ex,
                CommandResult.Cancelled(
                    "Workbook automation was cancelled during the active generation stage.")));
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (BuildCommandException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (CommonModulesManifestException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (InvalidOperationException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (IOException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(CreateFailureResult(ex));
        }
        catch (COMException ex)
        {
            return Failed(CreateFailureResult(
                ex,
                CommandErrorMessages.ExcelComAutomationFailed(operationName, ex)));
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

    private static CommandResult CreateFailureResult(Exception error, string? message = null)
    {
        var result = CommandResult.UsageError(message ?? error.Message);
        return PreserveReleaseProof(error, result);
    }

    private static CommandResult PreserveReleaseProof(Exception error, CommandResult result)
        => WorkbookAutomationFailureClassifier.ContainsCleanupProofFailure(error)
            ? result.MarkOwnedProcessReleaseUnproven()
            : result;

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

    private sealed record WorkbookOutputExecution(
        CommandResult CommandResult,
        WorkbookMaterializationResult? Materialization);
}

internal sealed record SourceSnapshotBuildCommandResult(
    CommandResult CommandResult,
    string? CommittedArtifactPath);
