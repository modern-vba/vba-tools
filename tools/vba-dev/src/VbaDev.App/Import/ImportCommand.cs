using System.Runtime.InteropServices;
using VbaDev.App.Cli;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Import;

/// <summary>
/// Imports exported VBA source files into an existing workbook without using vba-project.json.
/// </summary>
public sealed class ImportCommand
{
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;

    /// <summary>
    /// Creates the import command.
    /// </summary>
    /// <param name="workbookBuildAutomation">The workbook automation port used to modify the target workbook.</param>
    public ImportCommand(IWorkbookBuildAutomation workbookBuildAutomation)
    {
        this.workbookBuildAutomation = workbookBuildAutomation;
    }

    /// <summary>
    /// Replaces importable modules in the target workbook with source files from a directory.
    /// </summary>
    /// <param name="request">The import command input containing required --from and --to paths.</param>
    /// <returns>The command result describing the import operation or validation error.</returns>
    public CommandResult Run(ImportCommandRequest request)
    {
        try
        {
            if (request.FromPath is null)
            {
                return CommandResult.UsageError("--from is required.");
            }

            if (string.IsNullOrWhiteSpace(request.FromPath))
            {
                return CommandResult.UsageError("--from requires a source directory path.");
            }

            if (request.ToPath is null)
            {
                return CommandResult.UsageError("--to is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ToPath))
            {
                return CommandResult.UsageError("--to requires a target workbook path.");
            }

            var sourceDirectory = ResolveOptionPath(request.WorkingDirectory, request.FromPath);
            var targetWorkbookPath = ResolveOptionPath(request.WorkingDirectory, request.ToPath);
            var sourceFiles = ResolveSourceFiles(sourceDirectory);
            ValidateTargetWorkbook(targetWorkbookPath);

            using (var session = workbookBuildAutomation.OpenWorkbook(targetWorkbookPath))
            {
                foreach (var component in session.GetModules().Where(component => component.Kind.IsImportable()))
                {
                    session.RemoveModule(component.Name);
                }

                foreach (var sourceFile in sourceFiles)
                {
                    session.ImportModule(sourceFile);
                }

                session.Save();
            }

            var label = sourceFiles.Count == 1 ? "source file" : "source files";
            return CommandResult.Success($"Imported {sourceFiles.Count} {label} from {sourceDirectory} to {targetWorkbookPath}{Environment.NewLine}");
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
            return CommandResult.UsageError(CommandErrorMessages.ExcelComAutomationFailed("import", ex));
        }
    }

    private static IReadOnlyList<VbaSourceFile> ResolveSourceFiles(string sourceDirectory)
    {
        if (File.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Import source path is not a directory: {sourceDirectory}");
        }

        if (!Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Import source directory was not found: {sourceDirectory}");
        }

        var sourceFiles = DocumentSourceSetLayout
            .EnumerateVbaSourceFiles(sourceDirectory)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {sourceDirectory}");
        }

        DocumentSourceSetLayout.ThrowIfDuplicateSourceFileNames(sourceDirectory, sourceFiles);

        return sourceFiles
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateTargetWorkbook(string targetWorkbookPath)
    {
        if (Directory.Exists(targetWorkbookPath))
        {
            throw new InvalidOperationException($"Import target workbook is not a file: {targetWorkbookPath}");
        }

        if (!File.Exists(targetWorkbookPath))
        {
            throw new InvalidOperationException($"Import target workbook was not found: {targetWorkbookPath}");
        }
    }

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
