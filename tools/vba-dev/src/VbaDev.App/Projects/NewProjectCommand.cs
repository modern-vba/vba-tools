using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Projects;

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
        var projectRoot = ResolveProjectRoot(request);
        var projectName = ResolveProjectName(request, projectRoot);
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

        var workbookPath = Path.Combine(sourceSetPath, $"{documentName}.xlsm");
        var referenceNames = initialWorkbookCreator.CreateInitialWorkbook(workbookPath);
        var references = CreateReferenceEntries(referenceNames);
        var commonModules = Array.Empty<InstalledCommonModule>();

        if (commonModulesRepository is not null)
        {
            try
            {
                commonModules = CopyInitialCommonModules(commonModulesRepository, sourceSetPath);
            }
            catch (CommonModulesManifestException ex)
            {
                return CommandResult.UsageError(ex.Message);
            }
        }

        var manifest = ProjectManifest.CreateDefault(
            projectName,
            documentName,
            projectRoot,
            commonModulesRepository,
            commonModules,
            references);
        manifestStore.Save(projectRoot, manifest);

        return new CommandResult(
            0,
            $"Created project '{projectName}' at {projectRoot}.{Environment.NewLine}",
            warnings.ToString());
    }

    private InstalledCommonModule[] CopyInitialCommonModules(string commonModulesRepository, string sourceSetPath)
    {
        var entries = commonModulesManifestReader.Load(commonModulesRepository);
        var requestedEntries = entries
            .Where(entry => entry.HasCategory("runtime-baseline") || entry.HasCategory("test-foundation"))
            .OrderBy(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        var requestedModuleFiles = requestedEntries
            .Select(entry => entry.ModuleFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedEntries = CommonModulesDependencyResolver.ResolveRequestedEntries(entries, requestedModuleFiles.ToArray());

        foreach (var entry in selectedEntries)
        {
            var sourcePath = Path.Combine(commonModulesRepository, entry.ModuleFile);
            if (!File.Exists(sourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {sourcePath}");
            }

            var targetPath = Path.Combine(sourceSetPath, "common-modules", Path.GetFileName(entry.ModuleFile));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);
        }

        return selectedEntries
            .Select(entry => new InstalledCommonModule(
                Path.GetFileNameWithoutExtension(entry.ModuleFile),
                requestedModuleFiles.Contains(entry.ModuleFile)))
            .ToArray();
    }

    private static string ResolveProjectRoot(NewProjectCommandRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            return Path.GetFullPath(Path.IsPathRooted(request.OutputDirectory)
                ? request.OutputDirectory
                : Path.Combine(request.StartDirectory, request.OutputDirectory));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectName))
        {
            return Path.GetFullPath(Path.Combine(request.StartDirectory, request.ProjectName));
        }

        return Path.GetFullPath(request.StartDirectory);
    }

    private static string ResolveProjectName(NewProjectCommandRequest request, string projectRoot)
    {
        if (!string.IsNullOrWhiteSpace(request.ProjectName))
        {
            return request.ProjectName.Trim();
        }

        var trimmedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmedRoot);
    }

    private static VbaProjectReference[] CreateReferenceEntries(IReadOnlyList<string> referenceNames)
        => referenceNames
            .Select(referenceName => referenceName.Trim())
            .Where(referenceName => !string.IsNullOrWhiteSpace(referenceName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(referenceName => new VbaProjectReference(referenceName))
            .ToArray();

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
