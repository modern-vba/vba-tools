using System.Text;
using VbaDev.App.Cli;
using VbaDev.App.CommonModules;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.Projects;

/// <summary>
/// Creates a new Excel workbook-backed VBA project with default source, bin, and publish layout.
/// </summary>
public sealed class NewProjectCommand
{
    private static readonly string[] StandardInitialReferenceNames =
    [
        "Microsoft Scripting Runtime",
        "Microsoft VBScript Regular Expressions 5.5"
    ];

    private readonly IProjectManifestStore manifestStore;
    private readonly IInitialWorkbookCreator initialWorkbookCreator;
    private readonly CommonModulesManifestReader commonModulesManifestReader;
    private readonly NewProjectAncestorSourceSetIsolation ancestorSourceSetIsolation;

    /// <summary>
    /// Creates the new-project command.
    /// </summary>
    /// <param name="manifestStore">The store used to write the initial project manifest.</param>
    /// <param name="initialWorkbookCreator">The workbook creator used to generate the source template workbook.</param>
    /// <param name="commonModulesManifestReader">The reader used to discover initial CommonModules files.</param>
    public NewProjectCommand(
        IProjectManifestStore manifestStore,
        IInitialWorkbookCreator initialWorkbookCreator,
        CommonModulesManifestReader commonModulesManifestReader)
        : this(
            manifestStore,
            initialWorkbookCreator,
            commonModulesManifestReader,
            new FileSystemPathIdentityResolver())
    {
    }

    internal NewProjectCommand(
        IProjectManifestStore manifestStore,
        IInitialWorkbookCreator initialWorkbookCreator,
        CommonModulesManifestReader commonModulesManifestReader,
        IFileSystemPathIdentityResolver pathIdentityResolver)
    {
        this.manifestStore = manifestStore;
        this.initialWorkbookCreator = initialWorkbookCreator;
        this.commonModulesManifestReader = commonModulesManifestReader;
        ancestorSourceSetIsolation = new NewProjectAncestorSourceSetIsolation(
            manifestStore,
            pathIdentityResolver);
    }

    /// <summary>
    /// Creates the project directory, source template workbook, initial CommonModules files, and project manifest.
    /// </summary>
    /// <param name="request">The new-project command input.</param>
    /// <returns>The command result describing created project state or validation errors.</returns>
    public CommandResult Run(NewProjectCommandRequest request)
    {
        var projectRoot = ResolveProjectRoot(request);
        var projectName = ResolveProjectName(request, projectRoot);
        var documentName = string.IsNullOrWhiteSpace(request.DocumentName) ? projectName : request.DocumentName;
        var manifestPath = Path.Combine(projectRoot, ProjectManifest.ManifestFileName);

        if (File.Exists(manifestPath))
        {
            return CommandResult.UsageError($"vba-project.json already exists: {manifestPath}");
        }

        if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any())
        {
            return CommandResult.UsageError($"Target project directory is not empty: {projectRoot}");
        }

        FileSystemPathIdentity initialProjectIdentity;
        try
        {
            initialProjectIdentity = ancestorSourceSetIsolation.ValidateInitial(projectRoot);
        }
        catch (ProjectManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }

        var warnings = new StringBuilder();
        var sourceSetPath = Path.Combine(projectRoot, "src", documentName);
        var binPath = Path.Combine(projectRoot, "bin");
        var publishPath = Path.Combine(projectRoot, "publish");
        var artifacts = new NewProjectArtifactTracker();
        try
        {
            artifacts.EnsureDirectory(projectRoot);
            artifacts.EnsureDirectory(sourceSetPath);
            artifacts.EnsureDirectory(binPath);
            artifacts.EnsureDirectory(publishPath);

            var commonModulesRepository = DiscoverCommonModulesRepository(projectRoot);
            if (commonModulesRepository is null)
            {
                warnings.AppendLine("CommonModulesRepository was not found; project creation continued without shared modules.");
            }

            var workbookPath = Path.Combine(sourceSetPath, $"{documentName}.xlsm");
            var referenceNames = initialWorkbookCreator.CreateInitialWorkbook(workbookPath)
                .Concat(StandardInitialReferenceNames)
                .ToArray();
            artifacts.RecordCreatedFile(workbookPath);
            var references = CreateReferenceEntries(referenceNames);
            var commonModules = Array.Empty<InstalledCommonModule>();

            if (commonModulesRepository is not null)
            {
                commonModules = CopyInitialCommonModules(
                    commonModulesRepository,
                    sourceSetPath,
                    artifacts);
            }

            var manifest = ProjectManifest.CreateDefault(
                projectName,
                documentName,
                projectRoot,
                commonModulesRepository,
                commonModules,
                references);
            ancestorSourceSetIsolation.ValidateFinal(
                projectRoot,
                initialProjectIdentity);
            manifestStore.Save(projectRoot, manifest);

            return new CommandResult(
                0,
                $"Created project '{projectName}' at {projectRoot}.{Environment.NewLine}",
                warnings.ToString());
        }
        catch (CommonModulesManifestException ex)
        {
            artifacts.Rollback();
            return CommandResult.UsageError(ex.Message);
        }
        catch (ProjectManifestException ex)
        {
            artifacts.Rollback();
            return CommandResult.UsageError(ex.Message);
        }
        catch
        {
            artifacts.Rollback();
            throw;
        }
    }

    private InstalledCommonModule[] CopyInitialCommonModules(
        string commonModulesRepository,
        string sourceSetPath,
        NewProjectArtifactTracker artifacts)
    {
        var entries = commonModulesManifestReader.Load(commonModulesRepository);
        var requestedEntries = entries
            .Where(entry => entry.HasCategory("runtime-baseline") || entry.HasCategory("test-foundation"))
            .OrderBy(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        var requestedModuleFiles = requestedEntries
            .Select(entry => entry.ModuleFile)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedEntries = CommonModulesDependencyResolver.ResolveRequestedEntries(entries, requestedModuleFiles.ToArray());
        ValidateSelectedEntryIdentities(selectedEntries, sourceSetPath);
        var copyPlan = selectedEntries
            .Select(entry => new
            {
                SourcePath = Path.Combine(commonModulesRepository, entry.ModuleFile),
                TargetPath = Path.Combine(sourceSetPath, "common-modules", entry.InstalledModuleFile)
            })
            .ToArray();
        foreach (var plan in copyPlan)
        {
            if (!File.Exists(plan.SourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {plan.SourcePath}");
            }
        }

        foreach (var plan in copyPlan)
        {
            artifacts.EnsureDirectory(Path.GetDirectoryName(plan.TargetPath)!);
            File.Copy(plan.SourcePath, plan.TargetPath, overwrite: false);
            artifacts.RecordCreatedFile(plan.TargetPath);
        }

        return selectedEntries
            .Select(entry => new InstalledCommonModule(
                entry.Name,
                entry.InstalledModuleFile,
                requestedModuleFiles.Contains(entry.ModuleFile),
                entry.TestOnly))
            .ToArray();
    }

    private static void ValidateSelectedEntryIdentities(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string sourceSetPath)
    {
        var byName = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var byModuleFile = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        var byTargetPath = new Dictionary<string, CommonModuleManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (byName.TryGetValue(entry.Name, out var matchingName))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules selection contains duplicate CommonModules name '{entry.Name}': " +
                    $"'{matchingName.ModuleFile}' and '{entry.ModuleFile}'.");
            }

            if (byModuleFile.TryGetValue(entry.InstalledModuleFile, out var matchingModuleFile))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules selection contains duplicate flat moduleFile '{entry.InstalledModuleFile}': " +
                    $"'{matchingModuleFile.ModuleFile}' and '{entry.ModuleFile}'.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(sourceSetPath, "common-modules", entry.InstalledModuleFile));
            if (byTargetPath.TryGetValue(targetPath, out var matchingTarget))
            {
                throw new CommonModulesManifestException(
                    $"CommonModules entries '{matchingTarget.ModuleFile}' and '{entry.ModuleFile}' " +
                    $"resolve to the same target source file: {targetPath}");
            }

            byName.Add(entry.Name, entry);
            byModuleFile.Add(entry.InstalledModuleFile, entry);
            byTargetPath.Add(targetPath, entry);
        }
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
