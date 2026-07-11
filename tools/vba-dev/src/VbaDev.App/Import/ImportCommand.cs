using System.Runtime.InteropServices;
using VbaDev.App.Cli;
using VbaDev.App.Workbooks;

namespace VbaDev.App.Import;

public sealed class ImportCommand
{
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;

    public ImportCommand(IWorkbookBuildAutomation workbookBuildAutomation)
    {
        this.workbookBuildAutomation = workbookBuildAutomation;
    }

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

        var sourceFiles = Directory
            .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
            .Where(IsVbaSourceFile)
            .Select(CreateSourceFile)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new InvalidOperationException($"No importable VBA source files were found in: {sourceDirectory}");
        }

        ThrowIfDuplicateSourceFileNames(sourceDirectory, sourceFiles);

        return sourceFiles
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ThrowIfDuplicateSourceFileNames(string sourceDirectory, IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        var duplicateGroups = sourceFiles
            .GroupBy(source => source.FileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateGroups.Length == 0)
        {
            return;
        }

        var lines = duplicateGroups.Select(group =>
            $"Duplicate source file name '{group.Key}': {string.Join(", ", group.Select(source => source.SourcePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))}");
        throw new InvalidOperationException(
            $"Duplicate VBA source file names were found under {sourceDirectory}.{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
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

    private static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static VbaSourceFile CreateSourceFile(string path)
    {
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bas" => VbaSourceKind.StandardModule,
            ".cls" => VbaSourceKind.ClassModule,
            ".frm" => VbaSourceKind.Form,
            _ => throw new InvalidOperationException($"Unsupported VBA source file: {path}")
        };

        var binaryPath = kind == VbaSourceKind.Form
            ? Path.ChangeExtension(path, ".frx")
            : null;

        return new VbaSourceFile(
            SourcePath: path,
            Kind: kind,
            BinaryPath: binaryPath is not null && File.Exists(binaryPath) ? binaryPath : null);
    }

    private static string ResolveOptionPath(string workingDirectory, string path)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path));
}
