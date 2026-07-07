using System.Text;
using VbaDevTools.App.Cli;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Projects;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Domain;

namespace VbaDevTools.App.Build;

public sealed class BuildCommand
{
    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly CommonModulesService commonModulesService;
    private readonly IWorkbookBuildAutomation workbookBuildAutomation;

    public BuildCommand(
        CommonModulesManifestReader commonModulesManifestReader,
        CommonModulesService commonModulesService,
        IWorkbookBuildAutomation workbookBuildAutomation)
    {
        this.commonModulesManifestReader = commonModulesManifestReader;
        this.commonModulesService = commonModulesService;
        this.workbookBuildAutomation = workbookBuildAutomation;
    }

    public CommandResult Run(ResolvedProjectContext context)
    {
        try
        {
            if (!context.Document.Kind.Equals(ProjectDocument.ExcelKind, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.UsageError($"Build supports only Excel documents: {context.DocumentName}");
            }

            var sourceFiles = ResolveSourceFiles(context);
            RebuildWorkbook(context, sourceFiles);

            var output = new StringBuilder();
            output.AppendLine($"Built {context.BinDocumentPath}");
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

    private IReadOnlyList<VbaSourceFile> ResolveSourceFiles(ResolvedProjectContext context)
    {
        if (!File.Exists(context.TemplateDocumentPath))
        {
            throw new BuildCommandException($"Template workbook was not found: {context.TemplateDocumentPath}");
        }

        if (!Directory.Exists(context.DocumentSourceSetPath))
        {
            throw new BuildCommandException($"Document source set was not found: {context.DocumentSourceSetPath}");
        }

        var sourceFilesByName = Directory
            .EnumerateFiles(context.DocumentSourceSetPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsVbaSourceFile)
            .Select(CreateSourceFile)
            .ToDictionary(source => source.FileName, StringComparer.OrdinalIgnoreCase);

        var commonModuleFiles = ResolveInstalledCommonModuleFiles(context, sourceFilesByName);
        var commonModuleSet = new HashSet<string>(commonModuleFiles, StringComparer.OrdinalIgnoreCase);
        var orderedSourceFiles = new List<VbaSourceFile>();
        foreach (var moduleFile in commonModuleFiles)
        {
            if (!sourceFilesByName.TryGetValue(moduleFile, out var sourceFile))
            {
                throw new CommonModulesManifestException($"CommonModules dependency '{moduleFile}' is required but not installed in {context.DocumentSourceSetPath}.");
            }

            orderedSourceFiles.Add(sourceFile);
        }

        orderedSourceFiles.AddRange(sourceFilesByName
            .Values
            .Where(source => !commonModuleSet.Contains(source.FileName))
            .OrderBy(source => source.FileName, StringComparer.OrdinalIgnoreCase));

        return orderedSourceFiles;
    }

    private IReadOnlyList<string> ResolveInstalledCommonModuleFiles(
        ResolvedProjectContext context,
        IReadOnlyDictionary<string, VbaSourceFile> sourceFilesByName)
    {
        if (context.CommonModulesRepositoryPath is null ||
            !Directory.Exists(context.CommonModulesRepositoryPath) ||
            !File.Exists(Path.Combine(context.CommonModulesRepositoryPath, CommonModulesManifestReader.ManifestFileName)))
        {
            return [];
        }

        var entries = commonModulesManifestReader.Load(context.CommonModulesRepositoryPath);
        var installedModuleFiles = entries
            .Where(entry => sourceFilesByName.ContainsKey(entry.ModuleFile))
            .Select(entry => entry.ModuleFile)
            .ToArray();

        return commonModulesService
            .ResolveRequestedEntries(entries, installedModuleFiles)
            .Select(entry => entry.ModuleFile)
            .ToArray();
    }

    private void RebuildWorkbook(ResolvedProjectContext context, IReadOnlyList<VbaSourceFile> sourceFiles)
    {
        var targetDirectory = Path.GetDirectoryName(context.BinDocumentPath)
            ?? throw new BuildCommandException($"Bin workbook path is invalid: {context.BinDocumentPath}");
        Directory.CreateDirectory(targetDirectory);

        var tempWorkbookPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileNameWithoutExtension(context.BinDocumentPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(context.BinDocumentPath)}");

        try
        {
            File.Copy(context.TemplateDocumentPath, tempWorkbookPath, overwrite: false);
            using (var session = workbookBuildAutomation.OpenWorkbook(tempWorkbookPath))
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

            ReplaceTarget(tempWorkbookPath, context.BinDocumentPath);
        }
        finally
        {
            DeleteIfExists(tempWorkbookPath);
        }
    }

    private static void ReplaceTarget(string tempWorkbookPath, string targetWorkbookPath)
    {
        try
        {
            File.Move(tempWorkbookPath, targetWorkbookPath, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new BuildCommandException($"Target workbook is locked or unavailable: {targetWorkbookPath}", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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

    private static VbaSourceFile CreateSourceFile(string path)
    {
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".bas" => VbaSourceKind.StandardModule,
            ".cls" => VbaSourceKind.ClassModule,
            ".frm" => VbaSourceKind.Form,
            _ => throw new BuildCommandException($"Unsupported VBA source file: {path}")
        };

        var binaryPath = kind == VbaSourceKind.Form
            ? Path.ChangeExtension(path, ".frx")
            : null;

        return new VbaSourceFile(
            SourcePath: path,
            Kind: kind,
            BinaryPath: binaryPath is not null && File.Exists(binaryPath) ? binaryPath : null);
    }
}
