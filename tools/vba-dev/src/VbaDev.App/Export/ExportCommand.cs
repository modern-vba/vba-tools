using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Export;

public sealed class ExportCommand
{
    private readonly IWorkbookModuleExporter workbookModuleExporter;

    public ExportCommand(IWorkbookModuleExporter workbookModuleExporter)
    {
        this.workbookModuleExporter = workbookModuleExporter;
    }

    public CommandResult Run(ResolvedProjectContext context, ExportCommandRequest request)
    {
        try
        {
            var sourceWorkbookPath = string.IsNullOrWhiteSpace(request.FromPath)
                ? context.BinDocumentPath
                : ResolveOptionPath(request.WorkingDirectory, request.FromPath);
            var destinationDirectory = string.IsNullOrWhiteSpace(request.ToPath)
                ? context.DocumentSourceSetPath
                : ResolveOptionPath(request.WorkingDirectory, request.ToPath);
            var cleanDestination = true;

            return RunCore(sourceWorkbookPath, destinationDirectory, cleanDestination);
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

    public CommandResult RunExplicit(ExportCommandRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.FromPath))
            {
                return CommandResult.UsageError("--from requires a workbook path.");
            }

            var sourceWorkbookPath = ResolveOptionPath(request.WorkingDirectory, request.FromPath!);
            var destinationDirectory = string.IsNullOrWhiteSpace(request.ToPath)
                ? Path.GetFullPath(request.WorkingDirectory)
                : ResolveOptionPath(request.WorkingDirectory, request.ToPath);
            var cleanDestination = !string.IsNullOrWhiteSpace(request.ToPath);

            return RunCore(sourceWorkbookPath, destinationDirectory, cleanDestination);
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

    private CommandResult RunCore(string sourceWorkbookPath, string destinationDirectory, bool cleanDestination)
    {
        if (!File.Exists(sourceWorkbookPath))
        {
            return CommandResult.UsageError($"Export source workbook was not found: {sourceWorkbookPath}");
        }

        if (File.Exists(destinationDirectory))
        {
            return CommandResult.UsageError($"Export destination is not a directory: {destinationDirectory}");
        }

        Directory.CreateDirectory(destinationDirectory);
        if (cleanDestination)
        {
            ExportWithCleanup(sourceWorkbookPath, destinationDirectory);
        }
        else
        {
            workbookModuleExporter.ExportModules(sourceWorkbookPath, destinationDirectory);
        }

        return CommandResult.Success($"Exported {sourceWorkbookPath} to {destinationDirectory}{Environment.NewLine}");
    }

    private void ExportWithCleanup(string sourceWorkbookPath, string destinationDirectory)
    {
        var existingSourceLayout = DocumentSourceSetLayout.CaptureExistingSourceLayout(destinationDirectory);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vba-dev-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            workbookModuleExporter.ExportModules(sourceWorkbookPath, temporaryDirectory);
            DocumentSourceSetLayout.DeleteVbaSourceAndSidecars(destinationDirectory);
            DocumentSourceSetLayout.RestoreExportedSourceLayout(temporaryDirectory, destinationDirectory, existingSourceLayout);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static void DeleteTemporaryDirectory(string temporaryDirectory)
    {
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
