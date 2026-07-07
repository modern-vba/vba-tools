using System.Text;
using VbaDevTools.App.Cli;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Projects;
using VbaDevTools.Domain;

namespace VbaDevTools.App.Build;

public sealed class PublishCommand
{
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly WorkbookGenerationPipeline generationPipeline;

    public PublishCommand(
        WorkbookSourcePlanner sourcePlanner,
        WorkbookGenerationPipeline generationPipeline)
    {
        this.sourcePlanner = sourcePlanner;
        this.generationPipeline = generationPipeline;
    }

    public CommandResult Run(ResolvedProjectContext context)
    {
        try
        {
            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError($"Publish supports only Excel documents: {context.DocumentName}");
            }

            var sourceFiles = sourcePlanner.ResolvePublishSourceFiles(context);
            generationPipeline.Generate(
                context.TemplateDocumentPath,
                context.PublishDocumentPath,
                sourceFiles);

            var output = new StringBuilder();
            output.AppendLine($"Published {context.PublishDocumentPath}");
            output.AppendLine($"Imported {sourceFiles.Count} source files.");
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
    }
}
