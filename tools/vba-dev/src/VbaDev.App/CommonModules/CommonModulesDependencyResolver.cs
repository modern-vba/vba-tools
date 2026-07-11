namespace VbaDev.App.CommonModules;

public static class CommonModulesDependencyResolver
{
    public static IReadOnlyList<CommonModuleManifestEntry> ResolveRequestedEntries(
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

    public static CommonModuleManifestEntry[] MergeEntries(params IReadOnlyList<CommonModuleManifestEntry>[] entryGroups)
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
}
