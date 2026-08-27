using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Applies shared resolution rules for source and reference definitions.
/// </summary>
internal sealed class VbaResolutionPolicy
{
    private readonly VbaConditionalDeclarationFamilyIndex conditionalFamilies;

    public VbaResolutionPolicy()
        : this(new VbaConditionalDeclarationFamilyIndex([]))
    {
    }

    public VbaResolutionPolicy(
        VbaConditionalDeclarationFamilyIndex conditionalFamilies)
    {
        this.conditionalFamilies = conditionalFamilies;
    }

    public const int LocalRank = 0;
    public const int CurrentModuleRank = 1;
    public const int ProjectRank = 2;
    public const int ReferenceRank = 3;

    public bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

    public bool IsRenameTarget(VbaSourceDefinition definition)
        => !VbaProjectReferenceCatalogSet.IsExternalDefinition(definition)
            && (definition.Visibility == VbaSourceDefinitionVisibility.Local || IsReferenceTarget(definition));

    public bool IsTypeDefinition(VbaSourceDefinition definition)
        => definition.Kind is VbaSourceDefinitionKind.Class
            or VbaSourceDefinitionKind.Form
            or VbaSourceDefinitionKind.Type
            or VbaSourceDefinitionKind.Enum;

    public VbaSourceDefinition? ResolveRankedCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        VbaProjectReferenceSelection? referenceSelection)
        => ResolveRankedCandidatesOutcome(
            candidates,
            referenceSelection).Target?.SelectedDefinition;

    public VbaResolvedNameTarget CreateNameTarget(
        VbaSourceDefinition selectedDefinition)
        => conditionalFamilies.CreateNameTarget(selectedDefinition);

    public VbaNameResolutionOutcome ResolveRankedCandidatesOutcome(
        IEnumerable<VbaRankedDefinition> candidates,
        VbaProjectReferenceSelection? referenceSelection)
    {
        var rankedCandidates = candidates.ToArray();
        if (rankedCandidates.Length == 0)
        {
            return VbaNameResolutionOutcome.Unresolved;
        }

        var bestRank = rankedCandidates.Min(candidate => candidate.Rank);
        var bestCandidates = rankedCandidates
            .Where(candidate => candidate.Rank == bestRank)
            .ToArray();
        var bestDefinitions = VbaPropertyAccessorCoalescing.Coalesce(
            bestCandidates.Select(candidate => candidate.Definition));
        var bestTargets = new List<VbaResolvedNameTarget>(
            bestDefinitions.Count);
        var seenLogicalTargets = new HashSet<
            VbaResolvedNameTargetIdentity>();
        foreach (var definition in bestDefinitions)
        {
            var target = CreateNameTarget(definition);
            if (target is VbaDefinitionNameTarget
                || seenLogicalTargets.Add(target.Identity))
            {
                bestTargets.Add(target);
            }
        }

        if (bestTargets.Count == 1)
        {
            return VbaNameResolutionOutcome.Resolved(
                bestTargets[0]);
        }

        if (bestRank == ReferenceRank && referenceSelection?.MainVbaProjectReference is not null)
        {
            var mainReferenceCandidates = bestTargets
                .Where(target => VbaProjectReferenceName.AreEquivalent(
                    target.SelectedDefinition.ModuleName,
                    referenceSelection.MainVbaProjectReference.Name))
                .ToArray();
            if (mainReferenceCandidates.Length == 1)
            {
                return VbaNameResolutionOutcome.Resolved(
                    mainReferenceCandidates[0]);
            }
        }

        return VbaNameResolutionOutcome.Ambiguous;
    }

    public VbaSourceDefinition? ResolveReferenceCandidates(
        IEnumerable<VbaSourceDefinition> candidates,
        VbaProjectReferenceSelection? referenceSelection)
    {
        return ResolveRankedCandidates(
            candidates.Select(candidate => new VbaRankedDefinition(candidate, ReferenceRank)),
            referenceSelection);
    }
}

/// <summary>
/// Represents one definition candidate with its name-resolution precedence rank.
/// </summary>
/// <param name="Definition">The candidate definition.</param>
/// <param name="Rank">The lower numeric precedence rank.</param>
internal sealed record VbaRankedDefinition(VbaSourceDefinition Definition, int Rank);

internal abstract record VbaResolvedNameTargetIdentity;

internal sealed record VbaDefinitionNameTargetIdentity(
    VbaDefinitionIdentity DefinitionIdentity)
    : VbaResolvedNameTargetIdentity;

internal sealed record VbaConditionalFamilyNameTargetIdentity(
    ConditionalFamilyIdentity FamilyIdentity)
    : VbaResolvedNameTargetIdentity;

internal sealed record VbaPropertyNameTargetIdentity(
    object ProjectSnapshot,
    string OwnerKey)
    : VbaResolvedNameTargetIdentity;

internal sealed record VbaWithEventsEventNameTargetIdentity(
    VbaDefinitionIdentity HandlerIdentity)
    : VbaResolvedNameTargetIdentity;

internal abstract class VbaResolvedNameTarget
{
    public abstract VbaResolvedNameTargetIdentity Identity { get; }

    public abstract string CanonicalName { get; }

    public abstract VbaSourceDefinition SelectedDefinition { get; }

    public abstract IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions { get; }

    public abstract bool IsConditionalFamily { get; }
}

internal sealed class VbaDefinitionNameTarget : VbaResolvedNameTarget
{
    private readonly IReadOnlyList<VbaSourceDefinition> physicalDefinitions;

    public VbaDefinitionNameTarget(VbaSourceDefinition definition)
    {
        SelectedDefinition = definition;
        Identity = new VbaDefinitionNameTargetIdentity(definition.Identity);
        physicalDefinitions = [definition];
    }

    public override VbaResolvedNameTargetIdentity Identity { get; }

    public override string CanonicalName => SelectedDefinition.Name;

    public override VbaSourceDefinition SelectedDefinition { get; }

    public override IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions
        => physicalDefinitions;

    public override bool IsConditionalFamily => false;
}

internal sealed class VbaConditionalFamilyNameTarget : VbaResolvedNameTarget
{
    public VbaConditionalFamilyNameTarget(
        ConditionalDeclarationFamily family,
        VbaSourceDefinition selectedDefinition)
    {
        Family = family;
        SelectedDefinition = selectedDefinition;
        Identity = new VbaConditionalFamilyNameTargetIdentity(family.Identity);
    }

    public ConditionalDeclarationFamily Family { get; }

    public override VbaResolvedNameTargetIdentity Identity { get; }

    public override string CanonicalName => Family.CanonicalName;

    public override VbaSourceDefinition SelectedDefinition { get; }

    public override IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions
        => Family.Variants;

    public override bool IsConditionalFamily => true;
}

internal sealed class VbaWithEventsEventNameTarget : VbaResolvedNameTarget
{
    private readonly IReadOnlyList<VbaSourceDefinition> physicalDefinitions;

    public VbaWithEventsEventNameTarget(
        VbaSourceDefinition handler,
        string eventName,
        IReadOnlyList<VbaResolvedNameTarget> eventTargets,
        bool isConditionalBinding)
    {
        EventTargets = eventTargets
            .DistinctBy(target => target.Identity)
            .OrderBy(target => target.SelectedDefinition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.SelectedDefinition.Uri, StringComparer.Ordinal)
            .ThenBy(target => target.SelectedDefinition.Range.Start.Line)
            .ThenBy(target => target.SelectedDefinition.Range.Start.Character)
            .ToArray();
        if (EventTargets.Count == 0)
        {
            throw new ArgumentException(
                "A WithEvents Event target requires at least one resolved Event.",
                nameof(eventTargets));
        }

        Identity = new VbaWithEventsEventNameTargetIdentity(handler.Identity);
        CanonicalName = eventName;
        IsConditionalFamily = isConditionalBinding
            || EventTargets.Count > 1
            || EventTargets.Any(target => target.IsConditionalFamily);
        SelectedDefinition = EventTargets[0].SelectedDefinition;
        physicalDefinitions = EventTargets
            .SelectMany(target => target.PhysicalDefinitions)
            .DistinctBy(definition => definition.Identity)
            .OrderBy(definition => definition.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Uri, StringComparer.Ordinal)
            .ThenBy(definition => definition.Range.Start.Line)
            .ThenBy(definition => definition.Range.Start.Character)
            .ToArray();
    }

    public IReadOnlyList<VbaResolvedNameTarget> EventTargets { get; }

    public override VbaResolvedNameTargetIdentity Identity { get; }

    public override string CanonicalName { get; }

    public override VbaSourceDefinition SelectedDefinition { get; }

    public override IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions
        => physicalDefinitions;

    public override bool IsConditionalFamily { get; }
}

internal sealed class VbaPropertyNameTarget : VbaResolvedNameTarget
{
    private readonly VbaResolvedNameTarget selectedAccessorTarget;

    public VbaPropertyNameTarget(
        PropertyNameTargetDescriptor property,
        VbaSourceDefinition selectedDefinition)
    {
        Property = property;
        SelectedDefinition = selectedDefinition;
        selectedAccessorTarget = property.AccessorTargets
            .FirstOrDefault(target => target.PhysicalDefinitions.Any(
                definition => definition.Identity
                    == selectedDefinition.Identity))
            ?? new VbaDefinitionNameTarget(selectedDefinition);
    }

    public PropertyNameTargetDescriptor Property { get; }

    public IReadOnlyList<VbaResolvedNameTarget> AccessorTargets
        => Property.AccessorTargets;

    public override VbaResolvedNameTargetIdentity Identity => Property.Identity;

    public override string CanonicalName
        => Property.IsUnifiedConditionalFamily
            ? Property.CanonicalName
            : selectedAccessorTarget.IsConditionalFamily
                ? selectedAccessorTarget.CanonicalName
                : Property.CanonicalName;

    public override VbaSourceDefinition SelectedDefinition { get; }

    public override IReadOnlyList<VbaSourceDefinition> PhysicalDefinitions
        => Property.IsUnifiedConditionalFamily
            ? Property.UnifiedPhysicalDefinitions
            : selectedAccessorTarget.IsConditionalFamily
                ? selectedAccessorTarget.PhysicalDefinitions
                : Property.PropertyDefinitions;

    public override bool IsConditionalFamily
        => Property.IsUnifiedConditionalFamily
            || selectedAccessorTarget.IsConditionalFamily;
}

internal enum VbaNameResolutionKind
{
    Resolved,
    Unresolved,
    Ambiguous,
    AnalysisIncomplete,
    NonSemantic
}

internal sealed record VbaNameResolutionOutcome(
    VbaNameResolutionKind Kind,
    VbaResolvedNameTarget? Target)
{
    public static VbaNameResolutionOutcome Unresolved { get; } =
        new(VbaNameResolutionKind.Unresolved, Target: null);

    public static VbaNameResolutionOutcome Ambiguous { get; } =
        new(VbaNameResolutionKind.Ambiguous, Target: null);

    public static VbaNameResolutionOutcome AnalysisIncomplete { get; } =
        new(VbaNameResolutionKind.AnalysisIncomplete, Target: null);

    public static VbaNameResolutionOutcome NonSemantic { get; } =
        new(VbaNameResolutionKind.NonSemantic, Target: null);

    public static VbaNameResolutionOutcome Resolved(
        VbaResolvedNameTarget target)
        => new(VbaNameResolutionKind.Resolved, target);
}
