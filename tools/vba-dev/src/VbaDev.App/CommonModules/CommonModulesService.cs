using System.Text;
using System.Text.Json;
using VbaDev.App.Cli;
using VbaDev.App.Projects;
using VbaDev.Domain;

namespace VbaDev.App.CommonModules;

public sealed class CommonModulesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly UnicodeEncoding Utf16LeWithBom = new(bigEndian: false, byteOrderMark: true);

    private readonly CommonModulesManifestReader manifestReader;
    private readonly IProjectManifestStore manifestStore;

    public CommonModulesService(
        CommonModulesManifestReader manifestReader,
        IProjectManifestStore manifestStore)
    {
        this.manifestReader = manifestReader;
        this.manifestStore = manifestStore;
    }

    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules, bool force)
    {
        var normalizedRequestedModules = requestedModules
            .Select(module => module.Trim())
            .Where(module => !string.IsNullOrWhiteSpace(module))
            .ToArray();
        if (normalizedRequestedModules.Length == 0)
        {
            return CommandResult.UsageError("common-module add requires at least one CommonModules module name.");
        }

        try
        {
            var repositoryPath = GetRepositoryPath(context);
            var entries = manifestReader.Load(repositoryPath);
            var orderedEntries = ResolveRequestedEntries(entries, normalizedRequestedModules);
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
            var fileResult = TryExecuteCopyPlan(copyPlan);
            if (fileResult is not null)
            {
                return fileResult;
            }

            if (changed)
            {
                var saveResult = TrySaveManifest(context.ProjectRoot, plannedManifest);
                if (saveResult is not null)
                {
                    return saveResult;
                }
            }

            var copied = BuildCopyOutput(copyPlan);
            return copied.Length == 0
                ? CommandResult.Success("No CommonModules changes." + Environment.NewLine)
                : CommandResult.Success(copied);
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    public CommandResult List(ResolvedProjectContext context, string format)
    {
        var document = GetDocument(context.Manifest, context.DocumentName);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var output = new CommonModuleListOutput(context.DocumentName, document.CommonModules);
            return CommandResult.Success(JsonSerializer.Serialize(output, JsonOptions) + Environment.NewLine);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Document: {context.DocumentName}");
        builder.AppendLine("CommonModules:");
        if (document.CommonModules.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var module in document.CommonModules)
            {
                builder.AppendLine($"  {module.Name} (requested: {module.Requested.ToString().ToLowerInvariant()})");
            }
        }

        return CommandResult.Success(builder.ToString());
    }

    public CommandResult Update(ResolvedProject project)
    {
        try
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
                    : ResolveRequestedEntries(entries, requestedModuleNames);
                var installedEntries = installedModuleNames
                    .Select(module => ResolveEntry(entries, module))
                    .ToArray();
                var orderedEntries = MergeEntries(dependencyClosureEntries, installedEntries);
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

            var fileResult = TryExecuteCopyPlan(copyPlans);
            if (fileResult is not null)
            {
                return fileResult;
            }

            if (manifestChanged)
            {
                var saveResult = TrySaveManifest(project.ProjectRoot, plannedManifest);
                if (saveResult is not null)
                {
                    return saveResult;
                }
            }

            var output = BuildCopyOutput(copyPlans);
            return output.Length == 0
                ? CommandResult.Success("No installed CommonModules entries were found." + Environment.NewLine)
                : CommandResult.Success(output.ToString());
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
    }

    public IReadOnlyList<CommonModuleManifestEntry> ResolveRequestedEntries(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyList<string> requestedModules)
    {
        var byFileName = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CommonModuleManifestEntry>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var requestedModule in requestedModules)
        {
            Visit(ResolveEntry(entries, requestedModule), byFileName, ordered, visited, visiting);
        }

        return ordered;
    }

    public static CommonModuleManifestEntry ResolveEntry(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string requestedModule)
    {
        var matches = Path.HasExtension(requestedModule)
            ? entries.Where(entry => entry.ModuleFile.Equals(requestedModule, StringComparison.OrdinalIgnoreCase)).ToArray()
            : entries.Where(entry => Path.GetFileNameWithoutExtension(entry.ModuleFile).Equals(requestedModule, StringComparison.OrdinalIgnoreCase)).ToArray();

        return matches.Length switch
        {
            0 => throw new CommonModulesManifestException($"CommonModules entry was not found: {requestedModule}"),
            1 => matches[0],
            _ => throw new CommonModulesManifestException($"CommonModules module name '{requestedModule}' is ambiguous: {string.Join(", ", matches.Select(match => match.ModuleFile))}")
        };
    }

    private static void Visit(
        CommonModuleManifestEntry entry,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> byFileName,
        List<CommonModuleManifestEntry> ordered,
        HashSet<string> visited,
        HashSet<string> visiting)
    {
        if (visited.Contains(entry.ModuleFile))
        {
            return;
        }

        if (!visiting.Add(entry.ModuleFile))
        {
            return;
        }

        foreach (var dependency in entry.Dependencies)
        {
            if (!byFileName.TryGetValue(dependency, out var dependencyEntry))
            {
                throw new CommonModulesManifestException($"CommonModules manifest references unknown dependency '{dependency}' from '{entry.ModuleFile}'.");
            }

            Visit(dependencyEntry, byFileName, ordered, visited, visiting);
        }

        visiting.Remove(entry.ModuleFile);
        visited.Add(entry.ModuleFile);
        ordered.Add(entry);
    }

    private static CommonModuleManifestEntry[] MergeEntries(params IReadOnlyList<CommonModuleManifestEntry>[] entryGroups)
    {
        var entries = new List<CommonModuleManifestEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entryGroup in entryGroups)
        {
            foreach (var entry in entryGroup)
            {
                if (seen.Add(entry.ModuleFile))
                {
                    entries.Add(entry);
                }
            }
        }

        return entries.ToArray();
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
            var sidecarDeletePaths = IsFormFile(entry.ModuleFile)
                ? FindFormSidecars(documentSourceSetPath, entry.ModuleFile)
                : [];
            var sourceSidecarPath = IsFormFile(entry.ModuleFile)
                ? ResolveExistingSidecarPath(sourcePath)
                : null;
            var targetSidecarPath = sourceSidecarPath is null
                ? null
                : Path.ChangeExtension(targetPath, ".frx");
            var outputPath = documentName is null ? entry.ModuleFile : $"{documentName}/{entry.ModuleFile}";
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
        var matches = FindSourceMatches(documentSourceSetPath, moduleFile);
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
            : Path.Combine(documentSourceSetPath, moduleFile);
    }

    private CommandResult? TryExecuteCopyPlan(IReadOnlyList<CommonModuleCopyPlan> copyPlan)
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

            return null;
        }
        catch (IOException ex)
        {
            return FileOperationFailure(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return FileOperationFailure(ex);
        }
    }

    private CommandResult? TrySaveManifest(string projectRoot, ProjectManifest manifest)
    {
        try
        {
            manifestStore.Save(projectRoot, manifest);
            return null;
        }
        catch (IOException ex)
        {
            return WriteManifestRecovery(projectRoot, manifest, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WriteManifestRecovery(projectRoot, manifest, ex);
        }
        catch (ProjectManifestException ex)
        {
            return WriteManifestRecovery(projectRoot, manifest, ex);
        }
    }

    private static CommandResult FileOperationFailure(Exception ex)
        => CommandResult.UsageError(
            $"CommonModules file operation failed before manifest save; manifest was not saved and source files may have been partially updated. {ex.Message}");

    private static CommandResult WriteManifestRecovery(string projectRoot, ProjectManifest manifest, Exception manifestSaveException)
    {
        try
        {
            Directory.CreateDirectory(projectRoot);
            var recoveryPath = Path.Combine(projectRoot, $"project.failed-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(recoveryPath, json + Environment.NewLine, Utf16LeWithBom);
            return CommandResult.UsageError(recoveryPath);
        }
        catch (IOException ex)
        {
            return RecoveryFailure(manifestSaveException, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return RecoveryFailure(manifestSaveException, ex);
        }
    }

    private static CommandResult RecoveryFailure(Exception manifestSaveException, Exception recoveryException)
        => CommandResult.UsageError(
            $"Project manifest could not be saved ({manifestSaveException.Message}), and recovery file could not be written: {recoveryException.Message}");

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

    private static IReadOnlyList<string> FindSourceMatches(string documentSourceSetPath, string moduleFile)
    {
        if (!Directory.Exists(documentSourceSetPath))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(documentSourceSetPath, "*", SearchOption.AllDirectories)
            .Where(path =>
                IsVbaSourceFile(path) &&
                Path.GetFileName(path).Equals(moduleFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> FindFormSidecars(string documentSourceSetPath, string moduleFile)
    {
        if (!Directory.Exists(documentSourceSetPath))
        {
            return [];
        }

        var formName = Path.GetFileNameWithoutExtension(moduleFile);
        return Directory
            .EnumerateFiles(documentSourceSetPath, "*", SearchOption.AllDirectories)
            .Where(path =>
                Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileNameWithoutExtension(path).Equals(formName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ResolveExistingSidecarPath(string formSourcePath)
    {
        var sidecarPath = Path.ChangeExtension(formSourcePath, ".frx");
        return File.Exists(sidecarPath) ? sidecarPath : null;
    }

    private static bool IsVbaSourceFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".bas", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".frm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFormFile(string path)
        => Path.GetExtension(path).Equals(".frm", StringComparison.OrdinalIgnoreCase);

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

    private sealed record CommonModuleListOutput(
        string Document,
        IReadOnlyList<InstalledCommonModule> CommonModules);

    private sealed record CommonModuleCopyPlan(
        string SourcePath,
        string TargetPath,
        string? SourceSidecarPath,
        string? TargetSidecarPath,
        IReadOnlyList<string> SidecarDeletePaths,
        string Verb,
        string OutputPath);
}
