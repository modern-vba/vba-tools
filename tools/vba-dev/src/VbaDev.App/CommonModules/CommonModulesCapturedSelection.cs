namespace VbaDev.App.CommonModules;

/// <summary>Describes ordered captured source units owned by one admitted snapshot.</summary>
public sealed class CommonModulesCapturedSelection
{
    private readonly CommonModulesPackageSnapshot snapshot;
    private readonly IReadOnlyList<CommonModulesCapturedSourceUnit> units;
    private readonly IReadOnlyList<string> requiredReferences;

    private CommonModulesCapturedSelection(
        CommonModulesPackageSnapshot snapshot,
        IReadOnlyList<CommonModulesCapturedSourceUnit> units,
        IReadOnlyList<string> requiredReferences)
    {
        this.snapshot = snapshot;
        this.units = units;
        this.requiredReferences = requiredReferences;
    }

    /// <summary>Gets source units in the Package's selection order.</summary>
    public IReadOnlyList<CommonModulesCapturedSourceUnit> Units
    {
        get
        {
            _ = snapshot.Package;
            return units;
        }
    }

    /// <summary>Gets the ordered reference union established by the Package.</summary>
    public IReadOnlyList<string> RequiredReferences
    {
        get
        {
            _ = snapshot.Package;
            return requiredReferences;
        }
    }

    internal static CommonModulesCapturedSelection Create(
        CommonModulesPackageSnapshot snapshot,
        IReadOnlyList<string> requestedModules)
    {
        var plan = snapshot.ResolveRequestedPlan(requestedModules);
        return Materialize(snapshot, plan.Entries, plan.RequiredReferences);
    }

    internal static CommonModulesCapturedSelection CreateReconciled(
        CommonModulesPackageSnapshot snapshot,
        CommonModulesReconciliation reconciliation)
    {
        var package = snapshot.Package;
        ArgumentNullException.ThrowIfNull(reconciliation);
        if (!reconciliation.BelongsTo(package))
        {
            throw new CommonModulesManifestException(
                "CommonModules reconciliation must belong to the same captured package.");
        }

        return Materialize(snapshot, reconciliation.Entries, reconciliation.RequiredReferences);
    }

    private static CommonModulesCapturedSelection Materialize(
        CommonModulesPackageSnapshot snapshot,
        IReadOnlyList<CommonModuleManifestEntry> entries,
        IReadOnlyList<string> requiredReferences)
    {
        var units = entries
            .Select(entry => CommonModulesCapturedSourceUnit.Create(snapshot, entry.ModuleFile))
            .ToArray();
        return new CommonModulesCapturedSelection(
            snapshot, Array.AsReadOnly(units), Array.AsReadOnly(requiredReferences.ToArray()));
    }
}
