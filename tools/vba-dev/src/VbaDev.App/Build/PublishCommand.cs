using System.Text;
using System.Runtime.InteropServices;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.Build;

/// <summary>
/// Publishes a workbook-backed document from its template and publishable source files.
/// </summary>
public sealed class PublishCommand
{
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly WorkbookGenerationPipeline generationPipeline;

    /// <summary>
    /// Creates the publish command.
    /// </summary>
    /// <param name="sourcePlanner">The planner that selects publishable source files.</param>
    /// <param name="generationPipeline">The pipeline that generates the published workbook.</param>
    public PublishCommand(
        WorkbookSourcePlanner sourcePlanner,
        WorkbookGenerationPipeline generationPipeline)
    {
        this.sourcePlanner = sourcePlanner;
        this.generationPipeline = generationPipeline;
    }

    /// <summary>
    /// Generates the document's publish workbook while excluding test-only sources.
    /// </summary>
    /// <param name="context">The resolved project and document context.</param>
    /// <returns>The command result describing the published workbook or any user-facing failure.</returns>
    public CommandResult Run(ResolvedProjectContext context)
    {
        try
        {
            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError($"Publish supports only Excel documents: {context.DocumentName}");
            }

            var sourceFiles = sourcePlanner.ResolvePublishSourceFiles(context);
            var generationResult = generationPipeline.Generate(
                context.DocumentName,
                context.TemplateDocumentPath,
                context.PublishDocumentPath,
                context.Document.References,
                sourceFiles);

            var output = new StringBuilder();
            output.AppendLine($"Published {context.PublishDocumentPath}");
            output.AppendLine($"Imported {sourceFiles.Count} source files.");
            foreach (var warning in generationResult.Warnings)
            {
                output.AppendLine(warning);
            }

            return CommandResult.Success(output.ToString());
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
            return CommandResult.UsageError(CommandErrorMessages.ExcelComAutomationFailed("publish", ex));
        }
    }
}
