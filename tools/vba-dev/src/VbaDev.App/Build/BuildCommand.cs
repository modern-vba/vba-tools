using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.Build;

public sealed class BuildCommand
{
    private readonly WorkbookSourcePlanner sourcePlanner;
    private readonly WorkbookGenerationPipeline generationPipeline;

    public BuildCommand(
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
                return CommandResult.UsageError($"Build supports only Excel documents: {context.DocumentName}");
            }

            var sourceFiles = sourcePlanner.ResolveBuildSourceFiles(context);
            var generationResult = generationPipeline.Generate(
                context.DocumentName,
                context.TemplateDocumentPath,
                context.BinDocumentPath,
                context.Document.References,
                sourceFiles);

            var output = new StringBuilder();
            output.AppendLine($"Built {context.BinDocumentPath}");
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
    }
}
