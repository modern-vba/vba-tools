namespace VbaDev.App.CommonModules;

/// <summary>
/// Describes one immutable dependency closure and its ordered external-reference union issued by a Package.
/// </summary>
public sealed class CommonModulesSelectionPlan
{
    private CommonModulesSelectionPlan(
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyList<string> requiredReferences)
    {
        Entries = entries;
        RequiredReferences = requiredReferences;
    }

    /// <summary>Gets canonical Package entries with dependency components before their dependents.</summary>
    public IReadOnlyList<CommonModuleManifestEntry> Entries { get; }

    /// <summary>Gets the first-seen case-insensitive reference union over the selected entry order.</summary>
    public IReadOnlyList<string> RequiredReferences { get; }

    internal static CommonModulesSelectionPlan Create(
        CommonModulesPackage package,
        IReadOnlyList<string> requestedModules)
    {
        ArgumentNullException.ThrowIfNull(package);
        var entries = package.ResolveDependencyClosure(requestedModules);
        var requiredReferences = package.GetRequiredReferences(
            entries.Select(entry => entry.ModuleFile).ToArray());
        return new CommonModulesSelectionPlan(entries, requiredReferences);
    }
}
