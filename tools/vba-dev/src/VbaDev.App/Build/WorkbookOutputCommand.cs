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
        => RunAsyncCore(
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
        => RunAsyncCore(
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
        => RunAsyncCore(
            context,
            operationName: "build",
            displayName: "Build",
            completedVerb: "Built",
            () =>
            {
                var timeouts = ResolveTimeouts(context);
                var validatedPaths = outputSafetyValidator.Validate(
                    context,
                    sourceSnapshotPath,
                    outputPath);
                return MaterializeCapturedSnapshotAsync(
                    context,
                    captureFactory.Create(
                        validatedPaths.SourceSnapshotPath,
                        cancellationToken),
                    validatedPaths.OutputPath,
                    timeouts,
                    cancellationToken);
            },
            cancellationToken);

    internal Task<CommandResult> RunCapturedSnapshotBuildAsync(
        ResolvedProjectContext context,
        string sourceSnapshotPath,
        AdmittedVbaSourceSet admission,
        string outputPath,
        BuildSourceSnapshotOutputSafetyValidator outputSafetyValidator,
        CancellationToken cancellationToken)
        => RunAsyncCore(
            context,
            operationName: "build",
            displayName: "Build",
            completedVerb: "Built",
            () =>
            {
                var timeouts = ResolveTimeouts(context);
                var validatedPaths = outputSafetyValidator.Validate(
                    context,
                    sourceSnapshotPath,
                    outputPath);
                return MaterializeCapturedSnapshotAsync(
                    context,
                    new AdmittedWorkbookGenerationSourceInput(admission),
                    validatedPaths.OutputPath,
                    timeouts,
                    cancellationToken);
            },
            cancellationToken);

    private async Task<CommandResult> RunAsyncCore(
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
                return CommandResult.Cancelled(
                    "Workbook automation was cancelled during Excel startup.");
            }

            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError($"{displayName} supports only Excel documents: {context.DocumentName}");
            }

            var result = await materialize().ConfigureAwait(false);

            return new CommandResult(
                0,
                RenderOutput(
                    completedVerb,
                    result.CommittedArtifactPath,
                    result.ImportedSourceCount,
                    result.Warnings),
                VbeImportWarningRenderer.Render(result.VerificationReport));
        }
        catch (WorkbookAutomationCanceledException ex)
        {
            return PreserveReleaseProof(ex, CommandResult.Cancelled(ex.Message));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return PreserveReleaseProof(
                ex,
                CommandResult.Cancelled(
                    "Workbook automation was cancelled during the active generation stage."));
        }
        catch (WorkbookAutomationTimeoutException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationProcessLostException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (WorkbookAutomationReleasedProcessCleanupException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (BuildCommandException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (CommonModulesManifestException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (IOException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CreateFailureResult(ex);
        }
        catch (COMException ex)
        {
            return CreateFailureResult(
                ex,
                CommandErrorMessages.ExcelComAutomationFailed(operationName, ex));
        }
    }

    private Task<WorkbookMaterializationResult> MaterializeCapturedSnapshotAsync(
        ResolvedProjectContext context,
        IWorkbookGenerationSourceInput sourceInput,
        string targetDocumentPath,
        WorkbookAutomationTimeouts timeouts,
        CancellationToken cancellationToken)
        => materializer.MaterializeCapturedSnapshotCompatibilityAsync(
            context.DocumentName,
            context.TemplateDocumentPath,
            targetDocumentPath,
            context.Document.References,
            sourceInput,
            timeouts,
            cancellationToken);

    private static WorkbookAutomationTimeouts ResolveTimeouts(ResolvedProjectContext context)
        => WorkbookAutomationTimeouts.Default with
        {
            WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest),
            WorkbookSave = CommandDefaultResolver.ResolveWorkbookSaveTimeout(context.Manifest)
        };

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
}
