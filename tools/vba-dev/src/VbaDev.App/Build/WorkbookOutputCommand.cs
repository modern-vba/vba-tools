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
        => RunCore(
            context,
            profile,
            () => profile.ResolveSourceFiles(sourcePlanner, context));

    /// <summary>
    /// Generates one workbook output from an already planned source list.
    /// </summary>
    internal CommandResult Run(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        IReadOnlyList<VbaSourceFile> sourceFiles)
        => RunCore(context, profile, () => sourceFiles);

    private CommandResult RunCore(
        ResolvedProjectContext context,
        WorkbookOutputProfile profile,
        Func<IReadOnlyList<VbaSourceFile>> resolveSourceFiles)
    {
        try
        {
            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError($"{profile.DisplayName} supports only Excel documents: {context.DocumentName}");
            }

            var sourceFiles = resolveSourceFiles();
            var targetDocumentPath = profile.ResolveTargetDocumentPath(context);
            var generationResult = generationPipeline.Generate(
                context.DocumentName,
                context.TemplateDocumentPath,
                targetDocumentPath,
                context.Document.References,
                sourceFiles);

            return CommandResult.Success(RenderOutput(profile, targetDocumentPath, sourceFiles, generationResult.Warnings));
        }
        catch (BuildCommandException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (IOException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
        catch (COMException ex)
        {
            return CommandResult.UsageError(CommandErrorMessages.ExcelComAutomationFailed(profile.OperationName, ex));
        }
    }

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
