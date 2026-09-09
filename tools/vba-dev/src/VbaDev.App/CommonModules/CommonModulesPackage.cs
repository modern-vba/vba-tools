using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace VbaDev.App.CommonModules;

/// <summary>
/// Owns the admitted identity and dependency authority for one canonical CommonModules package.
/// </summary>
public sealed class CommonModulesPackage
{
    private readonly FrozenDictionary<string, CommonModuleManifestEntry> byFileName;
    private readonly FrozenDictionary<string, CommonModuleManifestEntry> byName;
    private readonly FrozenDictionary<string, int> declarationOrder;

    private CommonModulesPackage(IReadOnlyList<CommonModuleManifestEntry> entries)
    {
        Entries = Array.AsReadOnly(entries.Select(entry => entry with
        {
            Categories = Array.AsReadOnly(entry.Categories.ToArray()),
            Dependencies = Array.AsReadOnly(entry.Dependencies.ToArray()),
            RequiredReferences = Array.AsReadOnly(entry.RequiredReferences.ToArray())
        }).ToArray());
        byFileName = Entries.ToFrozenDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        byName = Entries.ToFrozenDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        declarationOrder = Entries
            .Select((entry, index) => (entry.ModuleFile, index))
            .ToFrozenDictionary(pair => pair.ModuleFile, pair => pair.index, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets independently owned entries in manifest declaration order.</summary>
    public IReadOnlyList<CommonModuleManifestEntry> Entries { get; }

    /// <summary>
    /// Resolves exact filenames or extensionless module names into an immutable dependency and reference plan.
    /// </summary>
    /// <param name="requestedModules">The requested names in their intended encounter order.</param>
    /// <returns>The dependency-ordered entries and first-seen required-reference union.</returns>
    public CommonModulesSelectionPlan ResolveRequestedPlan(IReadOnlyList<string> requestedModules)
        => CommonModulesSelectionPlan.Create(this, requestedModules);

    internal static CommonModulesPackage AdmitLive(
        CommonModulesPackageReader reader,
        string commonModulesRepositoryPath)
        => new(reader.ReadValidatedLiveEntries(commonModulesRepositoryPath));

    internal static CommonModulesPackage AdmitCaptured(
        CommonModulesPackageReader reader,
        string displayRootPath,
        IReadOnlyDictionary<string, byte[]> capturedFiles)
        => new(reader.ReadValidatedCapturedEntries(displayRootPath, capturedFiles));

    internal bool TryGetEntryByName(
        string name,
        [NotNullWhen(true)] out CommonModuleManifestEntry? entry)
        => byName.TryGetValue(name, out entry);

    internal CommonModuleManifestEntry GetEntryByFileName(string moduleFile)
        => byFileName.TryGetValue(moduleFile, out var entry)
            ? entry
            : throw new CommonModulesManifestException($"CommonModules entry was not found: {moduleFile}");

    internal IReadOnlyList<string> GetRequiredReferences(IReadOnlyList<string> orderedModuleFiles)
    {
        ArgumentNullException.ThrowIfNull(orderedModuleFiles);
        var requiredReferences = new List<string>();
        var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var moduleFile in orderedModuleFiles)
        {
            var entry = GetEntryByFileName(moduleFile);
            foreach (var reference in entry.RequiredReferences)
            {
                if (seenReferences.Add(reference))
                {
                    requiredReferences.Add(reference);
                }
            }
        }

        return Array.AsReadOnly(requiredReferences.ToArray());
    }

    internal IReadOnlyList<CommonModuleManifestEntry> ResolveDependencyClosure(
        IReadOnlyList<string> requestedModules)
    {
        ArgumentNullException.ThrowIfNull(requestedModules);
        var requestedEntries = requestedModules.Select(ResolveEntry).ToArray();
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requestedEntry in requestedEntries)
        {
            CollectReachable(requestedEntry, byFileName, reachable);
        }

        var components = FindDependencyComponents(reachable);
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

        return Array.AsReadOnly(ordered.ToArray());
    }

    private CommonModuleManifestEntry ResolveEntry(string requestedModule)
    {
        if (Path.HasExtension(requestedModule))
        {
            return GetEntryByFileName(requestedModule);
        }

        if (requestedModule is not null && TryGetEntryByName(requestedModule, out var entry))
        {
            return entry;
        }

        throw new CommonModulesManifestException($"CommonModules entry was not found: {requestedModule}");
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

    private IReadOnlyList<DependencyComponent> FindDependencyComponents(
        IReadOnlySet<string> reachable)
    {
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
            components.Add(new DependencyComponent(Array.AsReadOnly(componentEntries.ToArray())));
        }

        foreach (var entry in Entries)
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
