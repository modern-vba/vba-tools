using System.Text;
using System.Text.Json;
using VbaDev.App.Projects;
using VbaDev.App.Workbooks;
using VbaDev.Domain;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Applies CommonModules source file copies and manifest updates as a recoverable project transaction.
/// </summary>
public sealed class CommonModulesInstallationTransaction
{
    private const string CommonModulesDirectoryName = "common-modules";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);

    private readonly CommonModulesManifestReader manifestReader;
    private readonly IProjectManifestStore manifestStore;

    /// <summary>
    /// Creates a transaction coordinator for CommonModules installation operations.
    /// </summary>
    /// <param name="manifestReader">The manifest reader for the configured CommonModulesRepository.</param>
    /// <param name="manifestStore">The project manifest store used to persist installed entries.</param>
    public CommonModulesInstallationTransaction(
        CommonModulesManifestReader manifestReader,
        IProjectManifestStore manifestStore)
    {
        this.manifestReader = manifestReader;
        this.manifestStore = manifestStore;
    }

    /// <summary>
    /// Adds requested CommonModules entries to one document source set and records them in the manifest.
    /// </summary>
    /// <param name="context">The resolved project and document context to update.</param>
    /// <param name="requestedModules">The requested module names or file names.</param>
    /// <param name="force">Whether existing target source files may be overwritten.</param>
    /// <returns>A human-readable summary of copied files.</returns>
    public string Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules, bool force)
    {
        var normalizedRequestedModules = requestedModules
            .Select(module => module.Trim())
            .Where(module => !string.IsNullOrWhiteSpace(module))
            .ToArray();
        if (normalizedRequestedModules.Length == 0)
        {
            throw new CommonModulesManifestException("common-module add requires at least one CommonModules module name.");
        }

        var repositoryPath = GetRepositoryPath(context);
        var entries = manifestReader.Load(repositoryPath);
        var orderedEntries = CommonModulesDependencyResolver.ResolveRequestedEntries(entries, normalizedRequestedModules);
        var requestedNames = normalizedRequestedModules
            .Select(GetCommonModuleName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plannedManifest = CloneManifest(context.Manifest);
        var document = GetDocument(plannedManifest, context.DocumentName);
        var installedByName = document.CommonModules.ToDictionary(
            module => module.Name,
            StringComparer.OrdinalIgnoreCase);
        var entriesToCopy = orderedEntries
            .Where(entry => !installedByName.ContainsKey(GetCommonModuleName(entry.ModuleFile)))
            .ToArray();

        var copyPlan = PlanCopyEntries(repositoryPath, context.DocumentSourceSetPath, entriesToCopy, "Copied", force, documentName: null);
        var changed = ApplyInstalledEntries(document, orderedEntries, requestedNames, installedByName);
        ExecuteCopyPlan(copyPlan);

        if (changed)
        {
            SaveManifest(context.ProjectRoot, plannedManifest);
        }

        var copied = BuildCopyOutput(copyPlan);
        return copied.Length == 0
            ? "No CommonModules changes." + Environment.NewLine
            : copied;
    }

    /// <summary>
    /// Refreshes all installed CommonModules source files in a project from the configured repository.
    /// </summary>
    /// <param name="project">The resolved project whose installed CommonModules entries should be updated.</param>
    /// <returns>A human-readable summary of updated files.</returns>
    public string Update(ResolvedProject project)
    {
        var repositoryPath = GetRepositoryPath(project);
        var entries = manifestReader.Load(repositoryPath);
        var plannedManifest = CloneManifest(project.Manifest);
        var copyPlans = new List<CommonModuleCopyPlan>();
        var manifestChanged = false;

        foreach (var (documentName, document) in plannedManifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var documentSourceSetPath = project.ResolvePath(document.SourcePath);
            var installedModuleNames = document.CommonModules
                .Select(module => module.Name)
                .ToArray();
            if (installedModuleNames.Length == 0)
            {
                continue;
            }

            var requestedModuleNames = document.CommonModules
                .Where(module => module.Requested)
                .Select(module => module.Name)
                .ToArray();
            var dependencyClosureEntries = requestedModuleNames.Length == 0
                ? []
                : CommonModulesDependencyResolver.ResolveRequestedEntries(entries, requestedModuleNames);
            var installedEntries = installedModuleNames
                .Select(module => CommonModulesDependencyResolver.ResolveEntry(entries, module))
                .ToArray();
            var orderedEntries = CommonModulesDependencyResolver.MergeEntries(dependencyClosureEntries, installedEntries);
            var installedByName = document.CommonModules.ToDictionary(
                module => module.Name,
                StringComparer.OrdinalIgnoreCase);
            var requestedNames = requestedModuleNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

            copyPlans.AddRange(PlanCopyEntries(repositoryPath, documentSourceSetPath, orderedEntries, "Updated", overwrite: true, documentName));
            if (ApplyInstalledEntries(document, dependencyClosureEntries, requestedNames, installedByName))
            {
                manifestChanged = true;
            }
        }

        ExecuteCopyPlan(copyPlans);

        if (manifestChanged)
        {
            SaveManifest(project.ProjectRoot, plannedManifest);
        }

        var output = BuildCopyOutput(copyPlans);
        return output.Length == 0
            ? "No installed CommonModules entries were found." + Environment.NewLine
            : output;
    }

    private static IReadOnlyList<CommonModuleCopyPlan> PlanCopyEntries(
        string repositoryPath,
        string documentSourceSetPath,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string verb,
        bool overwrite,
        string? documentName = null)
    {
        var plans = new List<CommonModuleCopyPlan>();
        foreach (var entry in entries)
        {
            var sourcePath = Path.Combine(repositoryPath, entry.ModuleFile);
            if (!File.Exists(sourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {sourcePath}");
            }

            var targetPath = ResolveTargetPath(documentSourceSetPath, entry.ModuleFile, overwrite);
            var sidecarDeletePaths = DocumentSourceSetLayout.IsFormFile(entry.ModuleFile)
                ? DocumentSourceSetLayout.FindFormSidecars(documentSourceSetPath, entry.ModuleFile)
                : [];
            var sourceSidecarPath = DocumentSourceSetLayout.IsFormFile(entry.ModuleFile)
                ? DocumentSourceSetLayout.ResolveExistingSidecarPath(sourcePath)
                : null;
            var targetSidecarPath = sourceSidecarPath is null
                ? null
                : Path.ChangeExtension(targetPath, ".frx");
            var relativeTargetPath = NormalizeDisplayPath(Path.GetRelativePath(documentSourceSetPath, targetPath));
            var outputPath = documentName is null ? relativeTargetPath : $"{documentName}/{relativeTargetPath}";
            plans.Add(new CommonModuleCopyPlan(
                SourcePath: sourcePath,
                TargetPath: targetPath,
                SourceSidecarPath: sourceSidecarPath,
                TargetSidecarPath: targetSidecarPath,
                SidecarDeletePaths: sidecarDeletePaths,
                Verb: verb,
                OutputPath: outputPath));
        }

        return plans;
    }

    private static string ResolveTargetPath(
        string documentSourceSetPath,
        string moduleFile,
        bool overwrite)
    {
        var matches = DocumentSourceSetLayout.FindSourceMatches(documentSourceSetPath, moduleFile);
        if (!overwrite && matches.Count > 0)
        {
            throw new CommonModulesManifestException($"CommonModules target source file already exists: {matches[0]}");
        }

        if (overwrite && matches.Count > 1)
        {
            throw new CommonModulesManifestException(
                $"CommonModules target source file has multiple matches for '{moduleFile}': {string.Join(", ", matches)}");
        }

        return matches.Count == 1
            ? matches[0]
            : Path.Combine(documentSourceSetPath, CommonModulesDirectoryName, Path.GetFileName(moduleFile));
    }

    private static void ExecuteCopyPlan(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        try
        {
            foreach (var plan in copyPlan)
            {
                foreach (var sidecarPath in plan.SidecarDeletePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(sidecarPath))
                    {
                        File.Delete(sidecarPath);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetPath)!);
                File.Copy(plan.SourcePath, plan.TargetPath, overwrite: true);
                if (plan.SourceSidecarPath is not null && plan.TargetSidecarPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetSidecarPath)!);
                    File.Copy(plan.SourceSidecarPath, plan.TargetSidecarPath, overwrite: true);
                }
            }
        }
        catch (IOException ex)
        {
            throw new CommonModulesTransactionException(FileOperationFailureMessage(ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CommonModulesTransactionException(FileOperationFailureMessage(ex));
        }
    }

    private void SaveManifest(string projectRoot, ProjectManifest manifest)
    {
        try
        {
            manifestStore.Save(projectRoot, manifest);
        }
        catch (IOException ex)
        {
            throw new CommonModulesTransactionException(WriteManifestRecovery(projectRoot, manifest, ex));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CommonModulesTransactionException(WriteManifestRecovery(projectRoot, manifest, ex));
        }
        catch (ProjectManifestException ex)
        {
            throw new CommonModulesTransactionException(WriteManifestRecovery(projectRoot, manifest, ex));
        }
    }

    private static string FileOperationFailureMessage(Exception ex)
        => $"CommonModules file operation failed before manifest save; manifest was not saved and source files may have been partially updated. {ex.Message}";

    private static string WriteManifestRecovery(string projectRoot, ProjectManifest manifest, Exception manifestSaveException)
    {
        try
        {
            Directory.CreateDirectory(projectRoot);
            var recoveryPath = Path.Combine(projectRoot, $"project.failed-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(recoveryPath, json + Environment.NewLine, Utf16LeWithBom);
            return recoveryPath;
        }
        catch (IOException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return RecoveryFailureMessage(manifestSaveException, ex);
        }
    }

    private static string RecoveryFailureMessage(Exception manifestSaveException, Exception recoveryException)
        => $"Project manifest could not be saved ({manifestSaveException.Message}), and recovery file could not be written: {recoveryException.Message}";

    private static string BuildCopyOutput(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
    {
        var output = new StringBuilder();
        foreach (var plan in copyPlan)
        {
            output.AppendLine($"{plan.Verb} {plan.OutputPath}");
        }

        return output.ToString();
    }

    private static ProjectManifest CloneManifest(ProjectManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        return JsonSerializer.Deserialize<ProjectManifest>(json, JsonOptions)
            ?? throw new CommonModulesManifestException("Project manifest could not be cloned.");
    }

    private static bool ApplyInstalledEntries(
        ProjectDocument document,
        IReadOnlyList<CommonModuleManifestEntry> orderedEntries,
        IReadOnlySet<string> requestedNames,
        IReadOnlyDictionary<string, InstalledCommonModule> installedByName)
    {
        var changed = false;
        foreach (var entry in orderedEntries)
        {
            var name = GetCommonModuleName(entry.ModuleFile);
            var requested = requestedNames.Contains(name);
            if (installedByName.TryGetValue(name, out var installed))
            {
                if (requested && !installed.Requested)
                {
                    var index = document.CommonModules.FindIndex(module => module.Name.Equals(installed.Name, StringComparison.OrdinalIgnoreCase));
                    document.CommonModules[index] = installed with { Requested = true };
                    changed = true;
                }

                continue;
            }

            document.CommonModules.Add(new InstalledCommonModule(name, requested));
            changed = true;
        }

        return changed;
    }

    private static ProjectDocument GetDocument(ProjectManifest manifest, string documentName)
    {
        if (manifest.Documents.TryGetValue(documentName, out var document))
        {
            return document;
        }

        return manifest.Documents
            .First(item => item.Key.Equals(documentName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string GetCommonModuleName(string moduleFile)
        => Path.GetFileNameWithoutExtension(moduleFile);

    private static string NormalizeDisplayPath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string GetRepositoryPath(ResolvedProjectContext context)
        => GetRepositoryPath(context.CommonModulesRepositoryPath);

    private static string GetRepositoryPath(ResolvedProject project)
        => GetRepositoryPath(project.CommonModulesRepositoryPath);

    private static string GetRepositoryPath(string? commonModulesRepositoryPath)
    {
        if (commonModulesRepositoryPath is null)
        {
            throw new CommonModulesManifestException("CommonModulesRepository is not configured in project.json.");
        }

        if (!Directory.Exists(commonModulesRepositoryPath))
        {
            throw new CommonModulesManifestException($"CommonModulesRepository was not found: {commonModulesRepositoryPath}");
        }

        return commonModulesRepositoryPath;
    }

    private sealed record CommonModuleCopyPlan(
        string SourcePath,
        string TargetPath,
        string? SourceSidecarPath,
        string? TargetSidecarPath,
        IReadOnlyList<string> SidecarDeletePaths,
        string Verb,
        string OutputPath);
}
