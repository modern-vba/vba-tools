namespace VbaDev.App.CommonModules;

/// <summary>
/// Describes a deterministic CommonModules closure and its ordered external-reference union.
/// </summary>
/// <param name="Entries">The dependency-ordered CommonModules entries.</param>
/// <param name="RequiredReferences">The first-seen case-insensitive union of direct requirements.</param>
public sealed record CommonModulesSelectionPlan(
    IReadOnlyList<CommonModuleManifestEntry> Entries,
    IReadOnlyList<string> RequiredReferences);

/// <summary>
/// Resolves requested CommonModules entries into dependency-ordered install plans.
/// </summary>
public static class CommonModulesDependencyResolver
{
    /// <summary>
    /// Resolves requested modules into a dependency-ordered entry and required-reference plan.
    /// </summary>
    public static CommonModulesSelectionPlan ResolveRequestedPlan(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyList<string> requestedModules)
        => CreateSelectionPlan(ResolveRequestedEntries(entries, requestedModules));

    /// <summary>
    /// Creates the ordered required-reference union for an already ordered entry sequence.
    /// </summary>
    public static CommonModulesSelectionPlan CreateSelectionPlan(
        IReadOnlyList<CommonModuleManifestEntry> orderedEntries)
    {
        var requiredReferences = new List<string>();
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in orderedEntries)
        {
            foreach (var reference in entry.RequiredReferences)
            {
                if (seenReferences.Add(reference))
                {
                    requiredReferences.Add(reference);
                }
            }
        }

        return new CommonModulesSelectionPlan(orderedEntries, requiredReferences);
    }

    /// <summary>
    /// Resolves requested modules and their dependencies in copy order.
    /// </summary>
    /// <param name="entries">The complete manifest entry set.</param>
    /// <param name="requestedModules">The requested module names or file names.</param>
    /// <returns>The requested entries and their dependencies with dependencies before dependents.</returns>
    public static IReadOnlyList<CommonModuleManifestEntry> ResolveRequestedEntries(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyList<string> requestedModules)
    {
        var byFileName = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        var requestedEntries = requestedModules
            .Select(requestedModule => ResolveEntry(entries, requestedModule))
            .ToArray();
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedEntry in requestedEntries)
        {
            CollectReachable(requestedEntry, byFileName, reachable);
        }

        var components = FindDependencyComponents(entries, byFileName, reachable);
        var componentByFileName = components
            .SelectMany(component => component.Entries.Select(entry => (entry.ModuleFile, component)))
            .ToDictionary(pair => pair.ModuleFile, pair => pair.component, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<CommonModuleManifestEntry>();
        var visitedComponents = new HashSet<DependencyComponent>();

        foreach (var requestedEntry in requestedEntries)
        {
            VisitComponent(
                componentByFileName[requestedEntry.ModuleFile],
                byFileName,
                componentByFileName,
                ordered,
                visitedComponents);
        }

        return ordered;
    }

    /// <summary>
    /// Finds a single manifest entry by module file name or extensionless CommonModuleName.
    /// </summary>
    /// <param name="entries">The complete manifest entry set.</param>
    /// <param name="requestedModule">The requested module name or module file name.</param>
    /// <returns>The matching manifest entry.</returns>
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

    /// <summary>
    /// Merges ordered entry groups while keeping the first occurrence of each module file.
    /// </summary>
    /// <param name="entryGroups">The ordered entry groups to combine.</param>
    /// <returns>A deduplicated entry array that preserves first-seen order.</returns>
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

    private static void CollectReachable(
        CommonModuleManifestEntry entry,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> byFileName,
        HashSet<string> reachable)
    {
        if (!reachable.Add(entry.ModuleFile))
        {
            return;
        }

        foreach (var dependency in entry.Dependencies)
        {
            if (!byFileName.TryGetValue(dependency, out var dependencyEntry))
            {
                throw new CommonModulesManifestException($"CommonModules manifest references unknown dependency '{dependency}' from '{entry.ModuleFile}'.");
            }

            CollectReachable(dependencyEntry, byFileName, reachable);
        }
    }

    private static IReadOnlyList<DependencyComponent> FindDependencyComponents(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> byFileName,
        IReadOnlySet<string> reachable)
    {
        var declarationOrder = entries
            .Select((entry, index) => (entry.ModuleFile, index))
            .ToDictionary(pair => pair.ModuleFile, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<CommonModuleManifestEntry>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var components = new List<DependencyComponent>();
        var nextIndex = 0;

        void Connect(CommonModuleManifestEntry entry)
        {
            indexes.Add(entry.ModuleFile, nextIndex);
            lowLinks.Add(entry.ModuleFile, nextIndex);
            nextIndex++;
            stack.Push(entry);
            onStack.Add(entry.ModuleFile);

            foreach (var dependency in entry.Dependencies)
            {
                var dependencyEntry = byFileName[dependency];
                if (!indexes.ContainsKey(dependencyEntry.ModuleFile))
                {
                    Connect(dependencyEntry);
                    lowLinks[entry.ModuleFile] = Math.Min(
                        lowLinks[entry.ModuleFile],
                        lowLinks[dependencyEntry.ModuleFile]);
                }
                else if (onStack.Contains(dependencyEntry.ModuleFile))
                {
                    lowLinks[entry.ModuleFile] = Math.Min(
                        lowLinks[entry.ModuleFile],
                        indexes[dependencyEntry.ModuleFile]);
                }
            }

            if (lowLinks[entry.ModuleFile] != indexes[entry.ModuleFile])
            {
                return;
            }

            var componentEntries = new List<CommonModuleManifestEntry>();
            CommonModuleManifestEntry componentEntry;
            do
            {
                componentEntry = stack.Pop();
                onStack.Remove(componentEntry.ModuleFile);
                componentEntries.Add(componentEntry);
            }
            while (!componentEntry.ModuleFile.Equals(entry.ModuleFile, StringComparison.OrdinalIgnoreCase));

            componentEntries.Sort((left, right) =>
                declarationOrder[left.ModuleFile].CompareTo(declarationOrder[right.ModuleFile]));
            components.Add(new DependencyComponent(componentEntries));
        }

        foreach (var entry in entries)
        {
            if (reachable.Contains(entry.ModuleFile) && !indexes.ContainsKey(entry.ModuleFile))
            {
                Connect(entry);
            }
        }

        return components;
    }

    private static void VisitComponent(
        DependencyComponent component,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> byFileName,
        IReadOnlyDictionary<string, DependencyComponent> componentByFileName,
        List<CommonModuleManifestEntry> ordered,
        HashSet<DependencyComponent> visited)
    {
        if (!visited.Add(component))
        {
            return;
        }

        foreach (var entry in component.Entries)
        {
            foreach (var dependency in entry.Dependencies)
            {
                var dependencyEntry = byFileName[dependency];
                var dependencyComponent = componentByFileName[dependencyEntry.ModuleFile];
                if (!ReferenceEquals(component, dependencyComponent))
                {
                    VisitComponent(
                        dependencyComponent,
                        byFileName,
                        componentByFileName,
                        ordered,
                        visited);
                }
            }
        }

        ordered.AddRange(component.Entries);
    }

    private sealed record DependencyComponent(IReadOnlyList<CommonModuleManifestEntry> Entries);
}
