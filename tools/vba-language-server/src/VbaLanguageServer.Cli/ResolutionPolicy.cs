using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Applies shared resolution rules for source and reference definitions.
/// </summary>
internal sealed class VbaResolutionPolicy
{
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
            referenceSelection).Definition;

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
        if (bestDefinitions.Count == 1)
        {
            return VbaNameResolutionOutcome.Resolved(bestDefinitions[0]);
        }

        if (bestRank == ReferenceRank && referenceSelection?.MainVbaProjectReference is not null)
        {
            var mainReferenceCandidates = bestDefinitions
                .Where(definition => VbaProjectReferenceName.AreEquivalent(
                    definition.ModuleName,
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
    VbaSourceDefinition? Definition)
{
    public static VbaNameResolutionOutcome Unresolved { get; } =
        new(VbaNameResolutionKind.Unresolved, Definition: null);

    public static VbaNameResolutionOutcome Ambiguous { get; } =
        new(VbaNameResolutionKind.Ambiguous, Definition: null);

    public static VbaNameResolutionOutcome AnalysisIncomplete { get; } =
        new(VbaNameResolutionKind.AnalysisIncomplete, Definition: null);

    public static VbaNameResolutionOutcome NonSemantic { get; } =
        new(VbaNameResolutionKind.NonSemantic, Definition: null);

    public static VbaNameResolutionOutcome Resolved(
        VbaSourceDefinition definition)
        => new(VbaNameResolutionKind.Resolved, definition);
}
