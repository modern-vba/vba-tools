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
        var existingSourceLayout = CaptureExistingSourceLayout(destinationDirectory);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vba-dev-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            workbookModuleExporter.ExportModules(sourceWorkbookPath, temporaryDirectory);
            CleanDocumentSourceSet(destinationDirectory);
            RestoreExportedSourceLayout(temporaryDirectory, destinationDirectory, existingSourceLayout);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryDirectory);
        }
    }

    private static IReadOnlyDictionary<string, string> CaptureExistingSourceLayout(string destinationDirectory)
    {
        var relativePathsByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories)
                     .Where(IsVbaSourceFile)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            relativePathsByFileName.TryAdd(
                Path.GetFileName(path),
                Path.GetRelativePath(destinationDirectory, path));
        }

        return relativePathsByFileName;
    }

    private static void CleanDocumentSourceSet(string destinationDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories))
        {
            if (IsVbaSourceOrSidecar(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void RestoreExportedSourceLayout(
        string temporaryDirectory,
        string destinationDirectory,
        IReadOnlyDictionary<string, string> existingSourceLayout)
    {
        var exportedSourceFiles = Directory
            .EnumerateFiles(temporaryDirectory, "*", SearchOption.AllDirectories)
            .Where(IsVbaSourceFile)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var exportedSourceFile in exportedSourceFiles)
        {
            var exportedFileName = Path.GetFileName(exportedSourceFile);
            var targetRelativePath = existingSourceLayout.TryGetValue(exportedFileName, out var existingRelativePath)
                ? existingRelativePath
                : exportedFileName;
            var targetPath = Path.Combine(destinationDirectory, targetRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(exportedSourceFile, targetPath, overwrite: true);

            if (!Path.GetExtension(exportedSourceFile).Equals(".frm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exportedSidecar = FindMatchingFormSidecar(exportedSourceFile);
            if (exportedSidecar is null)
            {
                continue;
            }

            File.Copy(exportedSidecar, Path.ChangeExtension(targetPath, ".frx"), overwrite: true);
        }
    }

    private static string? FindMatchingFormSidecar(string formPath)
    {
        var directory = Path.GetDirectoryName(formPath)!;
        var formBaseName = Path.GetFileNameWithoutExtension(formPath);
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(path).Equals(formBaseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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

    private static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
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
