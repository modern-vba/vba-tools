using VbaLanguageServer.Diagnostics;
using VbaLanguageServer.ProjectModel;

namespace VbaLanguageServer.SourceModel;

/// <summary>
/// Resolves VBA names by applying local, current-module, project, and reference precedence rules.
/// </summary>
public sealed class VbaNameResolutionService
{
    private readonly VbaNameCandidateInventory candidates;
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
        this.resolutionPolicy = resolutionPolicy;
        candidates = new VbaNameCandidateInventory(
            documents,
            referenceSelection,
            referenceCatalogs,
            activeReferenceDefinitions
                ?? referenceCatalogs.GetActiveDefinitions(referenceSelection));
    }

    /// <summary>
    /// Gets unqualified completion definitions visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible and unambiguous completion definitions.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(string uri, VbaPosition position)
        => GetCompletionDefinitions(uri, position, definitionFilter: null);

    internal IReadOnlyList<VbaSourceDefinition> GetCompletionDefinitions(
        string uri,
        VbaPosition position,
        Func<VbaSourceDefinition, bool>? definitionFilter)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return ResolveCompletionCandidates(
                GetUnqualifiedCandidates(currentDocument, position, includeLocals: true),
                definitionFilter)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets active reference qualifier aliases that are visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible qualifier aliases.</returns>
    public IReadOnlyList<string> GetCompletionReferenceQualifiers(string uri, VbaPosition position)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return candidates.GetReferenceQualifiers()
            .Where(qualifier => !HasSourceQualifierShadow(currentDocument, position, qualifier))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(qualifier => qualifier, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets source module qualifier names that are visible at a position.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <param name="position">The source position.</param>
    /// <returns>The visible source module qualifier names.</returns>
    public IReadOnlyList<string> GetCompletionSourceQualifiers(string uri, VbaPosition position)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return [];
        }

        return candidates.GetSourceModuleNames()
            .Where(moduleName => !HasLocalQualifierShadow(currentDocument, position, moduleName))
            .Where(moduleName => GetSourceQualifiedCompletionDefinitions(currentDocument, position, moduleName).Count > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(moduleName => moduleName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets reference definitions exposed through a visible qualifier alias.
    /// </summary>
    /// <param name="currentDocument">The current document.</param>
    /// <param name="position">The source position.</param>
    /// <param name="qualifier">The qualifier alias.</param>
    /// <returns>The qualified reference definitions, or an empty list when a source definition shadows the alias.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetQualifiedCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => HasSourceQualifierShadow(currentDocument, position, qualifier)
            ? []
            : candidates.GetQualifiedReferenceDefinitions(qualifier)
                .Where(IsQualifiedReferenceRootDefinition)
                .ToArray();

    /// <summary>
    /// Gets definitions exposed through a source module qualifier.
    /// </summary>
    /// <param name="currentDocument">The current document.</param>
    /// <param name="position">The source position.</param>
    /// <param name="qualifier">The source module qualifier.</param>
    /// <returns>The qualified source definitions, or an empty list when a local definition shadows the qualifier.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetSourceQualifiedCompletionDefinitions(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => HasLocalQualifierShadow(currentDocument, position, qualifier)
            ? []
            : GetVisibleSourceModuleDefinitions(currentDocument, qualifier);

    /// <summary>
    /// Gets project-level definitions that can participate in document formatting.
    /// </summary>
    /// <param name="uri">The document URI.</param>
    /// <returns>The definitions that can supply canonical casing outside local scope.</returns>
    public IReadOnlyList<VbaSourceDefinition> GetFormattingDefinitions(string uri)
    {
        var currentDocument = candidates.FindDocument(uri);
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
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            definition => !resolutionPolicy.IsTypeDefinition(definition),
            fallbackToUnfiltered: true);

    internal VbaSourceDefinition? ResolveValue(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier)
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            definition => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference
                || !resolutionPolicy.IsTypeDefinition(definition),
            fallbackToUnfiltered: false);

    internal VbaSourceDefinition? ResolvePreferred(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition)
        => ResolvePreferredCore(
            uri,
            position,
            qualifier,
            identifier,
            preferredDefinition,
            fallbackToUnfiltered: true);

    private VbaSourceDefinition? ResolvePreferredCore(
        string uri,
        VbaPosition position,
        string? qualifier,
        string identifier,
        Func<VbaSourceDefinition, bool> preferredDefinition,
        bool fallbackToUnfiltered)
    {
        var currentDocument = candidates.FindDocument(uri);
        if (currentDocument is null)
        {
            return null;
        }

        var rankedCandidates = qualifier is null
            ? GetUnqualifiedCandidates(currentDocument, position, includeLocals: true, identifier)
            : GetQualifiedCandidates(currentDocument, qualifier)
                .Where(candidate => SameName(candidate.Definition.Name, identifier));
        return ResolveBestRankCandidates(
            rankedCandidates,
            preferredDefinition,
            fallbackToUnfiltered);
    }

    private IEnumerable<VbaRankedDefinition> GetUnqualifiedCandidates(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        bool includeLocals,
        string? requestedName = null)
    {
        if (includeLocals)
        {
            foreach (var candidate in candidates.GetSourceCandidates(currentDocument)
                .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
                .Where(candidate => ContainsPosition(candidate.Definition, position))
                .Where(candidate => MatchesRequestedName(candidate, requestedName)))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.LocalRank);
            }
        }

        foreach (var candidate in candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => MatchesRequestedName(candidate, requestedName)))
        {
            yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.CurrentModuleRank);
        }

        foreach (var candidate in candidates.GetSourceCandidates(requestedName)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Public))
        {
            yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.ProjectRank);
        }

        if (candidates.HasReferenceSelection)
        {
            foreach (var candidate in candidates.GetReferenceCandidates(requestedName)
                .Where(candidate => IsUnqualifiedReferenceRootDefinition(candidate.Definition)))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private IEnumerable<VbaRankedDefinition> GetQualifiedCandidates(VbaSourceDocument currentDocument, string qualifier)
    {
        foreach (var candidate in candidates.GetSourceCandidatesByModule(qualifier))
        {
            var allowPrivate = SameUri(currentDocument.Uri, candidate.Uri);
            if (resolutionPolicy.IsReferenceTarget(candidate.Definition)
                && (allowPrivate || candidate.Visibility == VbaSourceDefinitionVisibility.Public))
            {
                yield return new VbaRankedDefinition(candidate.Definition, VbaResolutionPolicy.CurrentModuleRank);
            }
        }

        if (candidates.HasReferenceSelection)
        {
            foreach (var definition in candidates.GetQualifiedReferenceDefinitions(qualifier)
                .Where(IsQualifiedReferenceRootDefinition))
            {
                yield return new VbaRankedDefinition(definition, VbaResolutionPolicy.ReferenceRank);
            }
        }
    }

    private bool HasSourceQualifierShadow(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
    {
        if (candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
            .Where(candidate => ContainsPosition(candidate.Definition, position))
            .Any(candidate => SameName(candidate.Name, qualifier)))
        {
            return true;
        }

        if (candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Any(candidate => SameName(candidate.Name, qualifier)))
        {
            return true;
        }

        return candidates.GetSourceCandidates(qualifier)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Any(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Public);
    }

    private bool HasLocalQualifierShadow(
        VbaSourceDocument currentDocument,
        VbaPosition position,
        string qualifier)
        => candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Local)
            .Where(candidate => ContainsPosition(candidate.Definition, position))
            .Any(candidate => SameName(candidate.Name, qualifier));

    private IReadOnlyList<VbaSourceDefinition> GetVisibleSourceModuleDefinitions(
        VbaSourceDocument currentDocument,
        string qualifier)
    {
        var definitions = candidates.GetSourceCandidatesByModule(qualifier)
            .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
            .Where(candidate => SameUri(currentDocument.Uri, candidate.Uri)
                || candidate.Visibility == VbaSourceDefinitionVisibility.Public)
            .Select(candidate => candidate.Definition)
            .ToArray();
        return definitions;
    }

    private IReadOnlyList<VbaSourceDefinition> ResolveCompletionCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool>? definitionFilter = null)
    {
        return candidates
            .GroupBy(candidate => candidate.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => definitionFilter is null
                ? ResolveRankedCandidates(group)
                : ResolveBestRankCandidates(
                    group,
                    definitionFilter,
                    fallbackToUnfiltered: false))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .ToArray();
    }

    private VbaSourceDefinition? ResolveRankedCandidates(IEnumerable<VbaRankedDefinition> candidates)
        => resolutionPolicy.ResolveRankedCandidates(candidates, this.candidates.ReferenceSelection);

    private VbaSourceDefinition? ResolveBestRankCandidates(
        IEnumerable<VbaRankedDefinition> candidates,
        Func<VbaSourceDefinition, bool> definitionFilter,
        bool fallbackToUnfiltered)
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
        var filteredCandidates = bestCandidates
            .Where(candidate => definitionFilter(candidate.Definition))
            .ToArray();
        return ResolveRankedCandidates(filteredCandidates.Length > 0 || !fallbackToUnfiltered
            ? filteredCandidates
            : bestCandidates);
    }

    internal VbaSourceDefinition? ResolveTypeDefinition(
        VbaSourceDocument currentDocument,
        VbaTypeReference typeReference)
    {
        if (!string.IsNullOrWhiteSpace(typeReference.Qualifier))
        {
            return candidates.HasSourceModule(typeReference.Qualifier)
                ? ResolveSourceTypeDefinition(currentDocument, typeReference.Name, typeReference.Qualifier)
                : ResolveReferenceTypeDefinition(typeReference.Qualifier, typeReference.Name);
        }

        return ResolveSourceTypeDefinition(currentDocument, typeReference.Name, qualifier: null)
            ?? ResolveReferenceCandidates(candidates.GetReferenceCandidates(typeReference.Name)
                .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
                .Where(candidate => candidate.ParentTypeName is null)
                .Select(candidate => candidate.Definition));
    }

    internal IReadOnlyList<VbaSourceDefinition> GetVisibleTypeDefinitions(
        VbaSourceDocument currentDocument,
        string? qualifier = null)
    {
        if (!string.IsNullOrWhiteSpace(qualifier))
        {
            if (candidates.HasSourceModule(qualifier))
            {
                return candidates.GetSourceCandidatesByModule(qualifier)
                    .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
                    .Where(candidate => candidate.Definition.Kind is not (
                        VbaSourceDefinitionKind.Class or VbaSourceDefinitionKind.Form))
                    .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                        || candidate.Visibility == VbaSourceDefinitionVisibility.Public)
                    .Select(candidate => candidate.Definition)
                    .ToArray();
            }

            return candidates.HasReferenceSelection
                ? candidates.GetQualifiedReferenceDefinitions(qualifier)
                    .Where(resolutionPolicy.IsTypeDefinition)
                    .Where(definition => definition.ParentTypeName is null)
                    .ToArray()
                : [];
        }

        var visibleDefinitions = new List<VbaSourceDefinition>();
        visibleDefinitions.AddRange(candidates.GetSourceCandidates(currentDocument)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Select(candidate => candidate.Definition));
        visibleDefinitions.AddRange(candidates.GetSourceCandidates(requestedName: null)
            .Where(candidate => !SameUri(candidate.Uri, currentDocument.Uri))
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => candidate.Visibility == VbaSourceDefinitionVisibility.Public)
            .Select(candidate => candidate.Definition));
        visibleDefinitions.AddRange(candidates.GetReferenceCandidates(requestedName: null)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => candidate.ParentTypeName is null)
            .Select(candidate => candidate.Definition));
        return visibleDefinitions;
    }

    internal IReadOnlyList<VbaSourceDefinition> GetMembersOfType(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName)
    {
        return GetMemberCandidates(currentDocument, typeName, referenceName)
            .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => ResolveMemberCandidateGroup(group.Select(candidate => candidate.Definition)))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal VbaSourceDefinition? ResolveMember(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName,
        string memberName,
        VbaSourceDefinitionKind? requiredKind = null)
    {
        var matchingCandidates = GetMemberCandidates(currentDocument, typeName, referenceName)
            .Where(candidate => SameName(candidate.Name, memberName))
            .Where(candidate => requiredKind is null || candidate.Definition.Kind == requiredKind)
            .Select(candidate => candidate.Definition)
            .ToArray();
        return ResolveMemberCandidateGroup(matchingCandidates);
    }

    internal VbaSourceDefinition? ResolveSourceTypeCompletionGroup(IReadOnlyList<VbaSourceDefinition> definitions)
    {
        var sourceDefinitions = definitions
            .Where(definition => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference)
            .ToArray();
        if (sourceDefinitions.Length == 1)
        {
            return sourceDefinitions[0];
        }

        return sourceDefinitions.Length > 1
            ? null
            : ResolveReferenceCandidates(definitions);
    }

    internal bool IsTypeDefinition(VbaSourceDefinition definition)
        => resolutionPolicy.IsTypeDefinition(definition);

    internal string? GetCanonicalQualifierName(VbaSourceDefinition definition, string qualifier)
        => definition.Identity.Origin != VbaDefinitionOrigin.ProjectReference
            ? definition.ModuleName
            : candidates.GetCanonicalReferenceQualifier(definition.ModuleName, qualifier);

    private VbaSourceDefinition? ResolveReferenceTypeDefinition(string qualifier, string typeName)
        => ResolveReferenceCandidates(candidates.GetQualifiedReferenceDefinitions(qualifier, typeName)
            .Where(resolutionPolicy.IsTypeDefinition)
            .Where(definition => definition.ParentTypeName is null));

    private VbaSourceDefinition? ResolveSourceTypeDefinition(
        VbaSourceDocument currentDocument,
        string typeName,
        string? qualifier)
    {
        var definitions = candidates.GetSourceCandidates(typeName)
            .Where(candidate => resolutionPolicy.IsTypeDefinition(candidate.Definition))
            .Where(candidate => qualifier is null || SameName(candidate.ModuleName, qualifier))
            .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                || candidate.Visibility == VbaSourceDefinitionVisibility.Public)
            .Select(candidate => new VbaRankedDefinition(
                candidate.Definition,
                SameUri(candidate.Uri, currentDocument.Uri)
                    ? VbaResolutionPolicy.CurrentModuleRank
                    : VbaResolutionPolicy.ProjectRank))
            .ToArray();
        return ResolveRankedCandidates(definitions);
    }

    private VbaSourceDefinition? ResolveReferenceCandidates(IEnumerable<VbaSourceDefinition> definitions)
        => resolutionPolicy.ResolveReferenceCandidates(definitions, candidates.ReferenceSelection);

    private bool IsUnqualifiedReferenceRootDefinition(VbaSourceDefinition definition)
    {
        return definition.ReferenceGlobalExposure switch
        {
            ReferenceDefinitionGlobalExposure.LibraryGlobal => true,
            ReferenceDefinitionGlobalExposure.MainHostGlobal =>
                candidates.ReferenceSelection?.MainVbaProjectReference is not null
                && SameName(
                    candidates.ReferenceSelection.MainVbaProjectReference.Name,
                    definition.ModuleName),
            _ => definition.ParentTypeName is null
                && resolutionPolicy.IsTypeDefinition(definition)
        };
    }

    private bool IsQualifiedReferenceRootDefinition(VbaSourceDefinition definition)
        => definition.ReferenceGlobalExposure != ReferenceDefinitionGlobalExposure.None
            || (definition.ParentTypeName is null
                && resolutionPolicy.IsTypeDefinition(definition));

    private static VbaSourceDefinition? ResolveMemberCandidateGroup(
        IEnumerable<VbaSourceDefinition> definitions)
    {
        var coalesced = VbaPropertyAccessorCoalescing.Coalesce(definitions);
        return coalesced.Count == 1 ? coalesced[0] : null;
    }

    private IEnumerable<VbaNameCandidate> GetMemberCandidates(
        VbaSourceDocument currentDocument,
        string typeName,
        string? referenceName)
        => referenceName is not null
            ? candidates.GetReferenceCandidatesByParentType(typeName)
                .Where(candidate => SameName(candidate.ModuleName, referenceName))
            : candidates.GetSourceCandidatesByModule(typeName)
                .Where(candidate => resolutionPolicy.IsReferenceTarget(candidate.Definition))
                .Where(candidate => SameUri(candidate.Uri, currentDocument.Uri)
                    || candidate.Visibility == VbaSourceDefinitionVisibility.Public);

    private static bool MatchesRequestedName(VbaNameCandidate candidate, string? requestedName)
        => requestedName is null || SameName(candidate.Name, requestedName);

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

/// <summary>
/// Captures immutable, case-insensitive lookup facts used by name and type resolution.
/// </summary>
internal sealed class VbaNameCandidateInventory
{
    private readonly IReadOnlyList<(string ReferenceName, string Qualifier)> activeReferenceQualifiers;
    private readonly IReadOnlyList<VbaNameCandidate> sourceCandidates;
    private readonly IReadOnlyList<VbaNameCandidate> referenceCandidates;
    private readonly ILookup<string, VbaSourceDocument> documentsByUri;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByName;
    private readonly ILookup<string, VbaNameCandidate> sourceCandidatesByModule;
    private readonly ILookup<string, VbaNameCandidate> referenceCandidatesByName;
    private readonly ILookup<string, VbaNameCandidate> referenceCandidatesByParentType;
    private readonly ILookup<string, VbaSourceDefinition> qualifiedReferenceDefinitionsByQualifier;

    public VbaNameCandidateInventory(
        IReadOnlyList<VbaSourceDocument> documents,
        VbaProjectReferenceSelection? referenceSelection,
        VbaProjectReferenceCatalogSet referenceCatalogs,
        IReadOnlyList<VbaSourceDefinition> activeReferenceDefinitions)
    {
        ReferenceSelection = referenceSelection;
        documentsByUri = documents.ToLookup(document => document.Uri, StringComparer.OrdinalIgnoreCase);
        sourceCandidates = documents
            .SelectMany(document => document.Definitions.Select(definition => new VbaNameCandidate(definition, document)))
            .ToArray();
        referenceCandidates = activeReferenceDefinitions
            .Select(definition => new VbaNameCandidate(definition, Document: null))
            .ToArray();
        sourceCandidatesByName = sourceCandidates.ToLookup(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        sourceCandidatesByModule = sourceCandidates.ToLookup(candidate => candidate.ModuleName, StringComparer.OrdinalIgnoreCase);
        referenceCandidatesByName = referenceCandidates.ToLookup(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);
        referenceCandidatesByParentType = referenceCandidates
            .Where(candidate => candidate.ParentTypeName is not null)
            .ToLookup(candidate => candidate.ParentTypeName!, StringComparer.OrdinalIgnoreCase);
        activeReferenceQualifiers = referenceCatalogs.GetActiveQualifierAliases(referenceSelection);
        var qualifiedReferenceDefinitions = activeReferenceQualifiers
            .Select(candidate => candidate.Qualifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(qualifier => referenceCatalogs
                .GetQualifiedDefinitions(referenceSelection, qualifier)
                .Select(definition => new VbaQualifiedReferenceDefinition(qualifier, definition)))
            .ToArray();
        qualifiedReferenceDefinitionsByQualifier = qualifiedReferenceDefinitions.ToLookup(
            candidate => candidate.Qualifier,
            candidate => candidate.Definition,
            StringComparer.OrdinalIgnoreCase);
    }

    public VbaProjectReferenceSelection? ReferenceSelection { get; }

    public bool HasReferenceSelection => referenceCandidates.Count > 0 || activeReferenceQualifiers.Count > 0;

    public VbaSourceDocument? FindDocument(string uri)
        => documentsByUri[uri].FirstOrDefault();

    public IEnumerable<VbaNameCandidate> GetSourceCandidates(VbaSourceDocument document)
        => sourceCandidates.Where(candidate => ReferenceEquals(candidate.Document, document));

    public IEnumerable<VbaNameCandidate> GetSourceCandidates(string? requestedName)
        => requestedName is null ? sourceCandidates : sourceCandidatesByName[requestedName];

    public IEnumerable<VbaNameCandidate> GetSourceCandidatesByModule(string moduleName)
        => sourceCandidatesByModule[moduleName];

    public bool HasSourceModule(string moduleName)
        => sourceCandidatesByModule[moduleName].Any();

    public IEnumerable<VbaNameCandidate> GetReferenceCandidates(string? requestedName)
        => requestedName is null ? referenceCandidates : referenceCandidatesByName[requestedName];

    public IEnumerable<VbaNameCandidate> GetReferenceCandidatesByParentType(string parentTypeName)
        => referenceCandidatesByParentType[parentTypeName];

    public IEnumerable<string> GetSourceModuleNames()
        => sourceCandidates
            .Where(candidate => candidate.Definition.Kind == VbaSourceDefinitionKind.Module)
            .Select(candidate => candidate.Name);

    public IEnumerable<string> GetReferenceQualifiers()
        => activeReferenceQualifiers.Select(candidate => candidate.Qualifier);

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedReferenceDefinitions(string qualifier)
        => qualifiedReferenceDefinitionsByQualifier[qualifier].ToArray();

    public IReadOnlyList<VbaSourceDefinition> GetQualifiedReferenceDefinitions(string qualifier, string name)
        => qualifiedReferenceDefinitionsByQualifier[qualifier]
            .Where(definition => definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public string? GetCanonicalReferenceQualifier(string referenceName, string qualifier)
        => activeReferenceQualifiers
            .Where(candidate => candidate.ReferenceName.Equals(referenceName, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Qualifier)
            .FirstOrDefault(candidate => candidate.Equals(qualifier, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Stores the resolution facts for one definition candidate.
/// </summary>
internal sealed record VbaNameCandidate(VbaSourceDefinition Definition, VbaSourceDocument? Document)
{
    public string Uri => Definition.Uri;

    public string Name => Definition.Name;

    public string ModuleName => Definition.ModuleName;

    public string? ParentTypeName => Definition.ParentTypeName;

    public VbaDefinitionOrigin Origin => Definition.Identity.Origin;

    public VbaSourceDefinitionVisibility Visibility => Definition.Visibility;
}

internal sealed record VbaQualifiedReferenceDefinition(string Qualifier, VbaSourceDefinition Definition);
