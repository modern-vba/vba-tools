using VbaDev.App.Cli;
using VbaDev.App.Projects;

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
            CleanDocumentSourceSet(destinationDirectory);
        }

        workbookModuleExporter.ExportModules(sourceWorkbookPath, destinationDirectory);
        return CommandResult.Success($"Exported {sourceWorkbookPath} to {destinationDirectory}{Environment.NewLine}");
    }

    private static void CleanDocumentSourceSet(string destinationDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsVbaSourceOrSidecar(path))
            {
                File.Delete(path);
            }
        }
    }

    private static bool IsVbaSourceOrSidecar(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frx", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
