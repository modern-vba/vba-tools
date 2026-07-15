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
            or VbaSourceDefinitionKind.Type;

    public VbaSourceDefinition? ResolveRankedCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        VbaProjectReferenceSelection? referenceSelection)
    {
        var rankedCandidates = candidates.ToArray();
        if (rankedCandidates.Length == 0)
        {
            return null;
        }

        var bestRank = rankedCandidates.Min(candidate => candidate.Rank);
        var bestCandidates = rankedCandidates
            .Where(candidate => candidate.Rank == bestRank)
            .ToArray();
        var bestDefinitions = VbaPropertyAccessorCoalescing.Coalesce(
            bestCandidates.Select(candidate => candidate.Definition));
        if (bestDefinitions.Count == 1)
        {
            return bestDefinitions[0];
        }

        if (bestRank == ReferenceRank && referenceSelection?.MainVbaProjectReference is not null)
        {
            var mainReferenceCandidates = bestDefinitions
                .Where(definition => SameName(
                    definition.ModuleName,
                    referenceSelection.MainVbaProjectReference.Name))
                .ToArray();
            if (mainReferenceCandidates.Length == 1)
            {
                return mainReferenceCandidates[0];
            }
        }

        return null;
    }

    public VbaSourceDefinition? ResolveReferenceCandidates(
        IEnumerable<VbaSourceDefinition> candidates,
        VbaProjectReferenceSelection? referenceSelection)
    {
        return ResolveRankedCandidates(
            candidates.Select(candidate => new VbaRankedDefinition(candidate, ReferenceRank)),
            referenceSelection);
    }

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Represents one definition candidate with its name-resolution precedence rank.
/// </summary>
/// <param name="Definition">The candidate definition.</param>
/// <param name="Rank">The lower numeric precedence rank.</param>
internal sealed record VbaRankedDefinition(VbaSourceDefinition Definition, int Rank);
