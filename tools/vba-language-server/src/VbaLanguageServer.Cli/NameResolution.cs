using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Resolves VBA names by applying local, current-module, project, and reference precedence rules.
/// </summary>
public sealed class VbaNameResolutionService
{
    private readonly IReadOnlyList<VbaSourceDocument> documents;
    private readonly VbaProjectReferenceSelection? referenceSelection;
    private readonly VbaProjectReferenceCatalogSet referenceCatalogs;
    private readonly IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<VbaSourceDefinition>> activeReferenceDefinitionsByName;
    private readonly VbaResolutionPolicy resolutionPolicy;

    /// <summary>
    /// Creates a name resolution service over indexed source documents and active references.
    /// </summary>
    /// <param name="documents">The indexed source documents.</param>
    /// <param name="referenceSelection">The active reference selection for the project.</param>
    /// <param name="referenceCatalogs">The available reference catalogs.</param>
    /// <param name="activeReferenceDefinitions">The active reference definitions projected for this index.</param>
    public VbaNameResolutionService(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition>? activeReferenceDefinitions = null)
        : this(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions,
            new VbaResolutionPolicy())
    {
    }

    internal VbaNameResolutionService(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition>? activeReferenceDefinitions,
        VbaResolutionPolicy resolutionPolicy)
    {
        this.documents = documents;
        this.referenceSelection = referenceSelection;
        this.referenceCatalogs = referenceCatalogs;
        this.resolutionPolicy = resolutionPolicy;
        this.activeReferenceDefinitions = activeReferenceDefinitions
            ?? (referenceSelection is null ? [] : referenceCatalogs.GetActiveDefinitions(referenceSelection));
        activeReferenceDefinitionsByName = this.activeReferenceDefinitions
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VbaSourceDefinition>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets unqualified completion definitions visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible and unambiguous completion definitions.</returns>
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

    /// <summary>
    /// Gets project-level definitions that can participate in document formatting.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The definitions that can supply canonical casing outside local scope.</returns>
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

    /// <summary>
    /// Resolves an identifier with an optional qualifier at a source position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position used for local visibility.</param>
    /// <param name="qualifier">The optional qualifier preceding the identifier.</param>
    /// <param name="identifier">The identifier to resolve.</param>
    /// <returns>The resolved definition, or null when unresolved or ambiguous.</returns>
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
            ? GetUnqualifiedCandidates(currentDocument, position, includeLocals: true, identifier)
            : GetQualifiedCandidates(currentDocument, qualifier)
                .Where(candidate => SameName(candidate.Definition.Name, identifier));
        return ResolveRankedCandidates(candidates);
    }

    private IEnumerable<VbaRankedDefinition> GetUnqualifiedCandidates(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        bool includeLocals,
        string? requestedName = null)
    {
        if (includeLocals)
        {
            foreach (var definition in currentDocument.Definitions
                .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Local)
                .Where(definition => ContainsPosition(definition, position))
                .Where(definition => MatchesRequestedName(definition, requestedName)))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.LocalRank);
            }
        }

        foreach (var definition in currentDocument.Definitions
            .Where(resolutionPolicy.IsReferenceTarget)
            .Where(definition => MatchesRequestedName(definition, requestedName)))
        {
            yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.CurrentModuleRank);
        }

        foreach (var definition in documents
            .Where(document => !SameUri(document.Uri, currentDocument.Uri))
            .SelectMany(document => document.Definitions)
            .Where(resolutionPolicy.IsReferenceTarget)
            .Where(definition => definition.Visibility == VbaSourceDefinitionVisibility.Public)
            .Where(definition => MatchesRequestedName(definition, requestedName)))
        {
            yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.ProjectRank);
        }

        if (referenceSelection is not null)
        {
            foreach (var definition in GetActiveReferenceDefinitions(requestedName))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private IEnumerable<VbaRankedDefinition> GetQualifiedCandidates(VbaSourceDocument currentDocument, string qualifier)
    {
        foreach (var document in documents.Where(document => SameName(document.ModuleName, qualifier)))
        {
            var allowPrivate = SameUri(currentDocument.Uri, document.Uri);
            foreach (var definition in document.Definitions
                .Where(resolutionPolicy.IsReferenceTarget)
                .Where(definition => allowPrivate || definition.Visibility == VbaSourceDefinitionVisibility.Public))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.CurrentModuleRank);
            }
        }

        if (referenceSelection is not null)
        {
            foreach (var definition in referenceCatalogs.GetQualifiedDefinitions(referenceSelection, qualifier))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private IReadOnlyList<VbaSourceDefinition> ResolveCompletionCandidates(IEnumerable<VbaRankedDefinition> candidates)
    {
        return candidates
            .GroupBy(candidate => candidate.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveRankedCandidates(group))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();
    }

    private VbaSourceDefinition? ResolveRankedCandidates(IEnumerable<VbaRankedDefinition> candidates)
        => resolutionPolicy.ResolveRankedCandidates(candidates, referenceSelection);

    private IEnumerable<VbaSourceDefinition> GetActiveReferenceDefinitions(string? requestedName)
        => requestedName is null
            ? activeReferenceDefinitions
            : activeReferenceDefinitionsByName.TryGetValue(requestedName, out var definitions)
                ? definitions
                : [];

    private static bool MatchesRequestedName(VbaSourceDefinition definition, string? requestedName)
        => requestedName is null || SameName(definition.Name, requestedName);

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

    private static bool SameUri(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int ComparePosition(VbaPosition left, VbaPosition right)
    {
        var lineComparison = left.Line.CompareTo(right.Line);
        return lineComparison != 0 ? lineComparison : left.Character.CompareTo(right.Character);
    }
}
