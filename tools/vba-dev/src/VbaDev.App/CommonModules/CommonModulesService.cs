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
            var document = GetDocument(context.Manifest, context.DocumentName);
            var installedByName = document.CommonModules.ToDictionary(
                module => module.Name,
                StringComparer.OrdinalIgnoreCase);
            var entriesToCopy = orderedEntries
                .Where(entry => !installedByName.ContainsKey(GetCommonModuleName(entry.ModuleFile)))
                .ToArray();

            EnsureNoUntrackedConflicts(context.DocumentSourceSetPath, entriesToCopy, force);
            var copied = CopyEntries(repositoryPath, context.DocumentSourceSetPath, entriesToCopy, "Copied", overwrite: force);
            var changed = ApplyInstalledEntries(document, orderedEntries, requestedNames, installedByName);
            if (changed)
            {
                manifestStore.Save(context.ProjectRoot, context.Manifest);
            }

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
            var output = new StringBuilder();

            foreach (var (documentName, document) in project.Manifest.Documents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
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

                output.Append(CopyEntries(repositoryPath, documentSourceSetPath, orderedEntries, "Updated", overwrite: true, documentName));
                if (ApplyInstalledEntries(document, dependencyClosureEntries, requestedNames, installedByName))
                {
                    manifestStore.Save(project.ProjectRoot, project.Manifest);
                }
            }

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

    private static string CopyEntries(
        string repositoryPath,
        string documentSourceSetPath,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string verb,
        bool overwrite,
        string? documentName = null)
    {
        Directory.CreateDirectory(documentSourceSetPath);
        var output = new StringBuilder();
        foreach (var entry in entries)
        {
            var sourcePath = Path.Combine(repositoryPath, entry.ModuleFile);
            if (!File.Exists(sourcePath))
            {
                throw new CommonModulesManifestException($"CommonModules source file was not found: {sourcePath}");
            }

            File.Copy(sourcePath, Path.Combine(documentSourceSetPath, entry.ModuleFile), overwrite);
            var outputPath = documentName is null ? entry.ModuleFile : $"{documentName}/{entry.ModuleFile}";
            output.AppendLine($"{verb} {outputPath}");
        }

        return output.ToString();
    }

    private static void EnsureNoUntrackedConflicts(
        string documentSourceSetPath,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        bool force)
    {
        if (force)
        {
            return;
        }

        foreach (var entry in entries)
        {
            var targetPath = Path.Combine(documentSourceSetPath, entry.ModuleFile);
            if (File.Exists(targetPath))
            {
                throw new CommonModulesManifestException($"CommonModules target source file already exists: {targetPath}");
            }
        }
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
}
