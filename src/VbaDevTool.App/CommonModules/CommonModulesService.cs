using System.Text;
using VbaDevTools.App.Cli;
using VbaDevTools.App.Projects;

namespace VbaDevTools.App.CommonModules;

public sealed class CommonModulesService
{
    private readonly CommonModulesManifestReader manifestReader;

    public CommonModulesService(CommonModulesManifestReader manifestReader)
    {
        this.manifestReader = manifestReader;
    }

    public CommandResult Add(ResolvedProjectContext context, IReadOnlyList<string> requestedModules)
    {
        if (requestedModules.Count == 0)
        {
            return CommandResult.UsageError("add requires at least one CommonModules module name.");
        }

        try
        {
            var repositoryPath = GetRepositoryPath(context);
            var entries = manifestReader.Load(repositoryPath);
            var orderedEntries = ResolveRequestedEntries(entries, requestedModules);
            var copied = CopyEntries(repositoryPath, context.DocumentSourceSetPath, orderedEntries, "Copied");
            return CommandResult.Success(copied);
        }
        catch (CommonModulesManifestException ex)
        {
            return CommandResult.UsageError(ex.Message);
        }
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
                var installedEntries = entries
                    .Where(entry => File.Exists(Path.Combine(documentSourceSetPath, entry.ModuleFile)))
                    .Select(entry => entry.ModuleFile)
                    .ToArray();
                if (installedEntries.Length == 0)
                {
                    continue;
                }

                var orderedEntries = ResolveRequestedEntries(entries, installedEntries);
                output.Append(CopyEntries(repositoryPath, documentSourceSetPath, orderedEntries, "Updated", documentName));
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
            Visit(ResolveRequestedEntry(entries, requestedModule), byFileName, ordered, visited, visiting);
        }

        return ordered;
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
            throw new CommonModulesManifestException($"CommonModules dependency cycle includes '{entry.ModuleFile}'.");
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

    private static CommonModuleManifestEntry ResolveRequestedEntry(
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

    private static string CopyEntries(
        string repositoryPath,
        string documentSourceSetPath,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        string verb,
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

            File.Copy(sourcePath, Path.Combine(documentSourceSetPath, entry.ModuleFile), overwrite: true);
            var outputPath = documentName is null ? entry.ModuleFile : $"{documentName}/{entry.ModuleFile}";
            output.AppendLine($"{verb} {outputPath}");
        }

        return output.ToString();
    }

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
}
