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
public sealed class WorkbookOutputCommand
{
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly WorkbookGenerationPipeline generationPipeline;

    /// <summary>
    /// Creates a workbook output command.
    /// </summary>
    /// <param name="sourcePlanner">The planner that selects source files for the output profile.</param>
    /// <param name="generationPipeline">The pipeline that creates the workbook output.</param>
    public WorkbookOutputCommand(
        WorkbookSourcePlanner sourcePlanner,
        WorkbookGenerationPipeline generationPipeline)
    {
        this.sourcePlanner = sourcePlanner;
        this.generationPipeline = generationPipeline;
    }

    /// <summary>
    /// Generates one workbook output using the supplied profile.
    /// </summary>
    /// <param name="context">The resolved document context.</param>
    /// <param name="profile">The output profile to run.</param>
    /// <returns>The command result for the generated workbook.</returns>
    public CommandResult Run(ResolvedProjectContext context, WorkbookOutputProfile profile)
        => RunAsync(context, profile, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        CancellationToken cancellationToken)
        => RunAsyncCore(
            context,
            profile,
            () => new BorrowedWorkbookGenerationSourceInput(
                profile.ResolveSourceFiles(sourcePlanner, context)),
            () => profile.ResolveTargetDocumentPath(context),
            cancellationToken);

    /// <summary>
    /// Generates one workbook output from an already planned source list.
    /// </summary>
    internal CommandResult Run(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        IReadOnlyList<VbaSourceFile> sourceFiles)
        => RunAsync(context, profile, sourceFiles, CancellationToken.None).GetAwaiter().GetResult();

    internal Task<CommandResult> RunAsync(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        CancellationToken cancellationToken)
        => RunAsyncCore(
            context,
            profile,
            () => new BorrowedWorkbookGenerationSourceInput(sourceFiles),
            () => profile.ResolveTargetDocumentPath(context),
            cancellationToken);

    internal Task<CommandResult> RunWithOwnedSourceAsync(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        Func<IWorkbookGenerationSourceInput> resolveSourceInput,
        Func<string> resolveTargetDocumentPath,
        CancellationToken cancellationToken)
        => RunAsyncCore(
            context,
            profile,
            resolveSourceInput,
            resolveTargetDocumentPath,
            cancellationToken);

    private async Task<CommandResult> RunAsyncCore(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        Func<IWorkbookGenerationSourceInput> resolveSourceInput,
        Func<string> resolveTargetDocumentPath,
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
                return CommandResult.UsageError($"{profile.DisplayName} supports only Excel documents: {context.DocumentName}");
            }

            var targetDocumentPath = resolveTargetDocumentPath();
            var defaultTimeouts = WorkbookAutomationTimeouts.Default;
            var timeouts = defaultTimeouts with
            {
                WorkbookOpen = CommandDefaultResolver.ResolveWorkbookOpenTimeout(context.Manifest),
                WorkbookSave = CommandDefaultResolver.ResolveWorkbookSaveTimeout(context.Manifest)
            };
            var sourceInput = resolveSourceInput();
            var sourceFiles = sourceInput.SourceFiles;
            var generationResult = await generationPipeline.GenerateAsync(
                context.DocumentName,
                context.TemplateDocumentPath,
                targetDocumentPath,
                context.Document.References,
                sourceInput,
                timeouts,
                cancellationToken).ConfigureAwait(false);

            return CommandResult.Success(RenderOutput(profile, targetDocumentPath, sourceFiles, generationResult.Warnings));
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
                CommandErrorMessages.ExcelComAutomationFailed(profile.OperationName, ex));
        }
    }

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
        WorkbookOutputProfile profile,
        string targetDocumentPath,
        IReadOnlyList<VbaSourceFile> sourceFiles,
        IReadOnlyList<string> warnings)
    {
        var output = new StringBuilder();
        output.AppendLine($"{profile.CompletedVerb} {targetDocumentPath}");
        output.AppendLine($"Imported {sourceFiles.Count} source files.");
        foreach (var warning in warnings)
        {
            output.AppendLine(warning);
        }

        return output.ToString();
    }
}

/// <summary>
/// Describes one workbook output command profile.
/// </summary>
/// <param name="OperationName">The lower-case operation name used in diagnostics.</param>
/// <param name="DisplayName">The user-facing operation name used in validation messages.</param>
/// <param name="CompletedVerb">The completed action label printed on success.</param>
/// <param name="ResolveSourceFiles">The source-file planner operation for this output.</param>
/// <param name="ResolveTargetDocumentPath">The target workbook path resolver for this output.</param>
public sealed record WorkbookOutputProfile(
    string OperationName,
    string DisplayName,
    string CompletedVerb,
    Func<WorkbookSourcePlanner, ResolvedProjectContext, IReadOnlyList<VbaSourceFile>> ResolveSourceFiles,
    Func<ResolvedProjectContext, string> ResolveTargetDocumentPath)
{
    /// <summary>
    /// Gets the build output profile.
    /// </summary>
    public static WorkbookOutputProfile Build { get; } = new(
        "build",
        "Build",
        "Built",
        static (planner, context) => planner.ResolveBuildSourceFiles(context),
        static context => context.BinDocumentPath);

    /// <summary>
    /// Gets the publish output profile.
    /// </summary>
    public static WorkbookOutputProfile Publish { get; } = new(
        "publish",
        "Publish",
        "Published",
        static (planner, context) => planner.ResolvePublishSourceFiles(context),
        static context => context.PublishDocumentPath);
}
