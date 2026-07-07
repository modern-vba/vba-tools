using System.Text;
using VbaDevTools.App.Cli;
using VbaDevTools.App.CommonModules;
using VbaDevTools.App.Workbooks;
using VbaDevTools.Domain;

namespace VbaDevTools.App.Projects;

public sealed class NewProjectCommand
{
    private readonly IProjectManifestStore manifestStore;
    private readonly IInitialWorkbookCreator initialWorkbookCreator;
    private readonly CommonModulesManifestReader commonModulesManifestReader;

    public NewProjectCommand(
        IProjectManifestStore manifestStore,
        IInitialWorkbookCreator initialWorkbookCreator,
        CommonModulesManifestReader commonModulesManifestReader)
    {
        this.manifestStore = manifestStore;
        this.initialWorkbookCreator = initialWorkbookCreator;
        this.commonModulesManifestReader = commonModulesManifestReader;
    }

    public CommandResult Run(NewProjectCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectName))
        {
            return CommandResult.UsageError("new requires a project name.");
        }

        var projectRoot = Path.GetFullPath(Path.IsPathRooted(request.ProjectName)
            ? request.ProjectName
            : Path.Combine(request.StartDirectory, request.ProjectName));
        var projectName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var documentName = string.IsNullOrWhiteSpace(request.DocumentName) ? projectName : request.DocumentName;
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);

        if (File.Exists(manifestPath))
        {
            return CommandResult.UsageError($"project.json already exists: {manifestPath}");
        }

        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any())
        {
            return CommandResult.UsageError($"Target project directory is not empty: {projectRoot}");
        }

        var warnings = new StringBuilder();
        Directory.CreateDirectory(projectRoot);
        var sourceSetPath = Path.Combine(projectRoot, "src", documentName);
        var binPath = Path.Combine(projectRoot, "bin", documentName);
        var publishPath = Path.Combine(projectRoot, "publish", documentName);
        Directory.CreateDirectory(sourceSetPath);
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(publishPath);

        var commonModulesRepository = DiscoverCommonModulesRepository(projectRoot);
        if (commonModulesRepository is null)
        {
            warnings.AppendLine("CommonModulesRepository was not found; project creation continued without shared modules.");
        }

        var manifest = ProjectManifest.CreateDefault(projectName, documentName, projectRoot, commonModulesRepository);
        manifestStore.Save(projectRoot, manifest);

        var workbookPath = Path.Combine(sourceSetPath, $"{documentName}.xlsm");
        initialWorkbookCreator.CreateInitialWorkbook(workbookPath);

        if (commonModulesRepository is not null)
        {
            try
            {
                CopyInitialCommonModules(commonModulesRepository, sourceSetPath);
            }
            catch (CommonModulesManifestException ex)
            {
                return CommandResult.UsageError(ex.Message);
            }
        }

        return new CommandResult(
            0,
            $"Created project '{projectName}' at {projectRoot}.{Environment.NewLine}",
            warnings.ToString());
    }

    private void CopyInitialCommonModules(string commonModulesRepository, string sourceSetPath)
    {
        var entries = commonModulesManifestReader.Load(commonModulesRepository);
        var selectedEntries = entries
            .Where(entry => entry.HasCategory("runtime-baseline") || entry.HasCategory("test-foundation"))
            .OrderBy(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in selectedEntries)
        {
            var sourcePath = Path.Combine(commonModulesRepository, entry.ModuleFile);
            if (!File.Exists(sourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {sourcePath}");
            }

            File.Copy(sourcePath, Path.Combine(sourceSetPath, entry.ModuleFile), overwrite: true);
        }
    }

    private static string? DiscoverCommonModulesRepository(string projectRoot)
    {
        var parent = Directory.GetParent(projectRoot);
        if (parent is null)
        {
            return null;
        }

        var candidate = Path.Combine(parent.FullName, "common_modules_repo");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
