using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

public sealed class VbaNameResolutionService
{
    private const int LocalRank = 0;
    private const int CurrentModuleRank = 1;
    private const int ProjectRank = 2;
    private const int ReferenceRank = 3;

    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;

    public VbaNameResolutionService(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
    }

    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, VbaPosition position)
    {
        var currentDocument = FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return ResolveCompletionCandidates(GetUnqualifiedCandidates(currentDocument, position, includeLocals: true))
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<VbaSourceDefinition> GetFormattingDefinitions(string uri)
    {
        var currentDocument = FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return ResolveCompletionCandidates(GetUnqualifiedCandidates(currentDocument, new VbaPosition(0, 0), includeLocals: false))
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public VbaSourceDefinition? Resolve(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier)
    {
        var currentDocument = FindDocument(uri);
        if (currentDocument is null)
        {
            return null;
        }

        var candidates = qualifier is null
            ? GetUnqualifiedCandidates(currentDocument, position, includeLocals: true)
                .Where(candidate => SameName(candidate.Definition.Name, identifier))
            : GetQualifiedCandidates(currentDocument, qualifier)
                .Where(candidate => SameName(candidate.Definition.Name, identifier));
        return ResolveRankedCandidates(candidates);
    }

    private IEnumerable<RankedDefinition> GetUnqualifiedCandidates(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        bool includeLocals)
    {
        if (includeLocals)
        {
            foreach (var definition in currentDocument.Definitions
                .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Local)
                .Where(definition => ContainsPosition(definition, position)))
            {
                yield return new RankedDefinition(definition, LocalRank);
            }
        }

        foreach (var definition in currentDocument.Definitions.Where(IsReferenceTarget))
        {
            yield return new RankedDefinition(definition, CurrentModuleRank);
        }

        foreach (var definition in documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(IsReferenceTarget)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public))
        {
            yield return new RankedDefinition(definition, ProjectRank);
        }

        if (referenceSelection is not null)
        {
            foreach (var definition in referenceCatalogs.GetActiveDefinitions(referenceSelection))
            {
                yield return new RankedDefinition(definition, ReferenceRank);
            }
        }
    }

    private IEnumerable<RankedDefinition> GetQualifiedCandidates(VbaSourceDocument currentDocument, string qualifier)
    {
        foreach (var document in documents.Where(document => SameName(document.ModuleName, qualifier)))
        {
            var allowPrivate = SameUri(currentDocument.Uri, document.Uri);
            foreach (var definition in document.Definitions
                .Where(IsReferenceTarget)
                .Where(definition => allowPrivate || definition.Visibility == VbaSourceDefinitionVisibility.Public))
            {
                yield return new RankedDefinition(definition, CurrentModuleRank);
            }
        }

        if (referenceSelection is not null)
        {
            foreach (var definition in referenceCatalogs.GetQualifiedDefinitions(referenceSelection, qualifier))
            {
                yield return new RankedDefinition(definition, ReferenceRank);
            }
        }
    }

    private IReadOnlyList<VbaSourceDefinition> ResolveCompletionCandidates(IEnumerable<RankedDefinition> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveRankedCandidates(group))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();
    }

    private VbaSourceDefinition? ResolveRankedCandidates(IEnumerable<RankedDefinition> candidates)
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
        if (bestCandidates.Length == 1)
        {
            return bestCandidates[0].Definition;
        }

        if (bestRank == ReferenceRank && referenceSelection?.MainVbaProjectReference is not null)
        {
            var mainReferenceCandidates = bestCandidates
                .Where(candidate => candidate.Definition.ModuleName.Equals(
                    referenceSelection.MainVbaProjectReference.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (mainReferenceCandidates.Length == 1)
            {
                return mainReferenceCandidates[0].Definition;
            }
        }

        return null;
    }

    private VbaSourceDocument? FindDocument(string uri)
        => documents.FirstOrDefault(document => SameUri(document.Uri, uri));

    private static bool ContainsPosition(VbaSourceDefinition definition, VbaPosition position)
    {
        if (definition.ParentProcedureRange is null)
        {
            return false;
        }

        return ComparePosition(definition.ParentProcedureRange.Start, position) <= 0
            && ComparePosition(position, definition.ParentProcedureRange.End) <= 0;
    }

    private static bool IsReferenceTarget(VbaSourceDefinition definition)
        => definition.Visibility != VbaSourceDefinitionVisibility.Local
            && definition.Kind != VbaSourceDefinitionKind.Module
            && definition.Kind != VbaSourceDefinitionKind.Class
            && definition.Kind != VbaSourceDefinitionKind.Form;

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }

    private sealed record RankedDefinition(VbaSourceDefinition Definition, int Rank);
}
