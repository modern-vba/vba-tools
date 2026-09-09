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
    private readonly CommonModulesPackage package;

    private CommonModulesReconciliation(
        CommonModulesPackage package,
        ImmutableArray<CommonModuleManifestEntry> requestedClosure,
        ImmutableArray<CommonModuleManifestEntry> entries,
        ImmutableArray<string> requiredReferences,
        ImmutableArray<ReconciledInstalledCommonModule> installed,
        ImmutableArray<MissingInstalledCommonModuleDependency> missingDependencies,
        ImmutableHashSet<string> reachableNames,
        bool allRequestedRootsCurrent)
    {
        this.package = package;
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

    internal bool BelongsTo(CommonModulesPackage candidate) => ReferenceEquals(package, candidate);

    internal static CommonModulesReconciliation Create(
        CommonModulesPackage package,
        IReadOnlyList<InstalledCommonModule> installedSelection)
    {
        ArgumentNullException.ThrowIfNull(package);
        var installed = installedSelection.ToImmutableArray();
        var installedNames = installed.Select(module => module.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = installed.Where(module => module.Requested).ToArray();
        var availableRoots = roots.Where(module => package.TryGetEntryByName(module.Name, out _)).ToArray();
        var requestedClosure = package.ResolveRequestedPlan(
            availableRoots.Select(module => module.Name).ToArray()).Entries.ToImmutableArray();
        var retainedEntries = installed
            .Select(module => package.TryGetEntryByName(module.Name, out var entry) ? entry : null)
            .OfType<CommonModuleManifestEntry>();
        var orderedEntries = requestedClosure.Concat(retainedEntries)
            .DistinctBy(entry => entry.ModuleFile, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var references = package.GetRequiredReferences(
            orderedEntries.Select(entry => entry.ModuleFile).ToArray()).ToImmutableArray();

        // A reappeared root can be refreshed by Update, but its stored orphan marker
        // withholds current dependency authority from Doctor until refresh commits.
        var allRootsCurrent = roots.All(module => !module.Orphaned && package.TryGetEntryByName(module.Name, out _));
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = ImmutableArray.CreateBuilder<MissingInstalledCommonModuleDependency>();
        foreach (var root in availableRoots.Where(module => !module.Orphaned))
        {
            if (!package.TryGetEntryByName(root.Name, out var entry))
            {
                continue;
            }
            reachable.Add(entry.Name);
            CollectDependencyFacts(root.Name, entry, package, installedNames, reachable,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), missing);
        }

        var reconciled = installed.Select(module =>
        {
            package.TryGetEntryByName(module.Name, out var entry);
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
        return new(package, requestedClosure, orderedEntries, references, reconciled, missing.ToImmutable(),
            reachable.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), allRootsCurrent);
    }

    private static void CollectDependencyFacts(
        string rootName,
        CommonModuleManifestEntry entry,
        CommonModulesPackage package,
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
            var dependencyEntry = package.GetEntryByFileName(dependency);
            reachable.Add(dependencyEntry.Name);
            if (!visited.Contains(dependencyEntry.ModuleFile) && !installedNames.Contains(dependencyEntry.Name))
            {
                missing.Add(new(rootName, dependencyEntry.Name));
            }
            CollectDependencyFacts(rootName, dependencyEntry, package, installedNames, reachable, visited, missing);
        }
    }
}
