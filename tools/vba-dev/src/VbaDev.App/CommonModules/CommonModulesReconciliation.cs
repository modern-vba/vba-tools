using System.Collections.Immutable;
using VbaDev.Domain;

namespace VbaDev.App.CommonModules;

internal enum CommonModuleRepositoryState
{
    Current,
    MissingOrphanMarker,
    RetainedOrphan,
    StaleOrphanMarker
}

internal sealed record ReconciledInstalledCommonModule(
    InstalledCommonModule Installed,
    CommonModuleManifestEntry? RepositoryEntry,
    CommonModuleRepositoryState State,
    bool IsUnreachableDependency);

internal sealed record MissingInstalledCommonModuleDependency(string RootName, string DependencyName);

/// <summary>Derives immutable reconciliation facts from one validated repository and installed selection.</summary>
internal sealed class CommonModulesReconciliation
{
    private CommonModulesReconciliation(
        ImmutableArray<CommonModuleManifestEntry> requestedClosure,
        ImmutableArray<CommonModuleManifestEntry> entries,
        ImmutableArray<string> requiredReferences,
        ImmutableArray<ReconciledInstalledCommonModule> installed,
        ImmutableArray<MissingInstalledCommonModuleDependency> missingDependencies,
        ImmutableHashSet<string> reachableNames,
        bool allRequestedRootsCurrent)
    {
        RequestedClosure = requestedClosure;
        Entries = entries;
        RequiredReferences = requiredReferences;
        Installed = installed;
        MissingDependencies = missingDependencies;
        ReachableNames = reachableNames;
        AllRequestedRootsCurrent = allRequestedRootsCurrent;
        OrphanedNames = installed.Where(module => module.RepositoryEntry is null)
            .Select(module => module.Installed.Name).ToImmutableArray();
    }

    internal ImmutableArray<CommonModuleManifestEntry> RequestedClosure { get; }
    internal ImmutableArray<CommonModuleManifestEntry> Entries { get; }
    internal ImmutableArray<string> RequiredReferences { get; }
    internal ImmutableArray<ReconciledInstalledCommonModule> Installed { get; }
    internal ImmutableArray<MissingInstalledCommonModuleDependency> MissingDependencies { get; }
    internal ImmutableHashSet<string> ReachableNames { get; }
    internal bool AllRequestedRootsCurrent { get; }
    internal ImmutableArray<string> OrphanedNames { get; }

    internal static CommonModulesReconciliation Create(
        IReadOnlyList<CommonModuleManifestEntry> repository,
        IReadOnlyList<InstalledCommonModule> installedSelection)
    {
        var entries = repository.Select(entry => entry with
        {
            Categories = entry.Categories.ToImmutableArray(),
            Dependencies = entry.Dependencies.ToImmutableArray(),
            RequiredReferences = entry.RequiredReferences.ToImmutableArray()
        }).ToImmutableArray();
        var installed = installedSelection.ToImmutableArray();
        var entriesByName = entries.ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
        var entriesByFile = entries.ToDictionary(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase);
        var installedNames = installed.Select(module => module.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = installed.Where(module => module.Requested).ToArray();
        var availableRoots = roots.Where(module => entriesByName.ContainsKey(module.Name)).ToArray();
        var requestedClosure = CommonModulesDependencyResolver.ResolveRequestedEntries(
            entries, availableRoots.Select(module => module.Name).ToArray()).ToImmutableArray();
        var orderedEntries = CommonModulesDependencyResolver.MergeEntries(requestedClosure,
            installed.Where(module => entriesByName.ContainsKey(module.Name))
                .Select(module => entriesByName[module.Name]).ToArray()).ToImmutableArray();
        var references = CommonModulesDependencyResolver.CreateSelectionPlan(orderedEntries)
            .RequiredReferences.ToImmutableArray();

        // A reappeared root can be refreshed by Update, but its stored orphan marker
        // withholds current dependency authority from Doctor until refresh commits.
        var allRootsCurrent = roots.All(module => !module.Orphaned && entriesByName.ContainsKey(module.Name));
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = ImmutableArray.CreateBuilder<MissingInstalledCommonModuleDependency>();
        foreach (var root in availableRoots.Where(module => !module.Orphaned))
        {
            var entry = entriesByName[root.Name];
            reachable.Add(entry.Name);
            CollectDependencyFacts(root.Name, entry, entriesByFile, installedNames, reachable,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), missing);
        }

        var reconciled = installed.Select(module =>
        {
            entriesByName.TryGetValue(module.Name, out var entry);
            var state = (entry is not null, module.Orphaned) switch
            {
                (false, false) => CommonModuleRepositoryState.MissingOrphanMarker,
                (false, true) => CommonModuleRepositoryState.RetainedOrphan,
                (true, true) => CommonModuleRepositoryState.StaleOrphanMarker,
                _ => CommonModuleRepositoryState.Current
            };
            return new ReconciledInstalledCommonModule(module, entry, state,
                entry is not null && allRootsCurrent && !module.Requested && !reachable.Contains(module.Name));
        }).ToImmutableArray();
        return new(requestedClosure, orderedEntries, references, reconciled, missing.ToImmutable(),
            reachable.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), allRootsCurrent);
    }

    private static void CollectDependencyFacts(
        string rootName,
        CommonModuleManifestEntry entry,
        IReadOnlyDictionary<string, CommonModuleManifestEntry> entriesByFile,
        IReadOnlySet<string> installedNames,
        HashSet<string> reachable,
        HashSet<string> visited,
        ImmutableArray<MissingInstalledCommonModuleDependency>.Builder missing)
    {
        if (!visited.Add(entry.ModuleFile))
        {
            return;
        }
        foreach (var dependency in entry.Dependencies)
        {
            var dependencyEntry = entriesByFile[dependency];
            reachable.Add(dependencyEntry.Name);
            if (!visited.Contains(dependencyEntry.ModuleFile) && !installedNames.Contains(dependencyEntry.Name))
            {
                missing.Add(new(rootName, dependencyEntry.Name));
            }
            CollectDependencyFacts(rootName, dependencyEntry, entriesByFile, installedNames, reachable, visited, missing);
        }
    }
}
